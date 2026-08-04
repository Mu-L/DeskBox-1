using System.Collections.Specialized;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// File-widget member that can live inside a persistent Surface window.
/// Standalone legacy file windows remain supported, but grouped file members
/// no longer need to own a top-level HWND.
/// </summary>
public sealed partial class FileSurfaceContent :
    UserControl,
    IWidgetContent,
    ICancellableWidgetContent,
    IWidgetGroupContentCacheable,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private const int StackDuplicateInputWindowMs = 120;
    private static readonly TimeSpan ReconciliationFreshnessWindow =
        TimeSpan.FromSeconds(1);
    private readonly LocalizationService _localizationService;
    private readonly FileService _fileService;
    private readonly SettingsService _settingsService;
    private static readonly QuickLookPreviewService s_quickLookService =
        new();
    private string[] _cutClipboardPaths = [];
    private WidgetItem? _itemRenameTarget;
    private TextBlock? _itemRenameNameText;
    private bool _isCommittingItemRename;
    private bool _isCancellingItemRename;
    private bool _isSurfaceReorderDragActive;
    private string[] _surfaceReorderPaths = [];
    private string? _surfaceReorderStackKey;
    private int _surfaceReorderInsertionIndex = -1;
    private Windows.Foundation.Point _surfaceReorderLastPosition;
    private bool _surfaceReorderHasLastPosition;
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Border? _stackMemberDropTarget;
    private WidgetStackItem? _pressedStack;
    private bool _stackPointerDragStarted;
    private string? _lastStackInputKey;
    private long _lastStackInputTick;
    private bool _isImportBusy;
    private bool _isDisposed;
    private bool _isReadyForReuse;
    private bool _hasBeenWindowVisible;
    private DateTime _lastDiskReconciliationUtc = DateTime.MinValue;
    private int _diskReconciliationQueued;

    public FileSurfaceContent(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        _fileService = fileService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        ViewModel = new WidgetViewModel(
            config,
            fileService,
            organizerService,
            settingsService,
            localizationService,
            dispatcherQueue);

        InitializeComponent();
        ItemsGrid.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        ItemsList.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        Root.DataContext = ViewModel;
        Root.IsTabStop = true;
        EmptyAddButtonText.Text = T("Widget.AddFile");
        OpenSelectionButton.Label = T("Common.Open");
        CopySelectionButton.Label = T("Common.Copy");
        CutSelectionButton.Label = T("Common.Cut");
        DeleteSelectionButton.Label = T("Common.Delete");
        RenameSelectionButton.Label = T("Common.Rename");
        ToolTipService.SetToolTip(OpenSelectionButton, OpenSelectionButton.Label);
        ToolTipService.SetToolTip(CopySelectionButton, CopySelectionButton.Label);
        ToolTipService.SetToolTip(CutSelectionButton, CutSelectionButton.Label);
        ToolTipService.SetToolTip(DeleteSelectionButton, DeleteSelectionButton.Label);
        ToolTipService.SetToolTip(RenameSelectionButton, RenameSelectionButton.Label);
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ActualThemeChanged += FileSurfaceContent_ActualThemeChanged;
        Loaded += OnLoaded;
        UpdateEmptyState();
    }

    public WidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    internal event EventHandler? ExternalFileDragEnded;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy => _isImportBusy;

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.File;

    public FrameworkElement View => this;

    public bool IsReadyForReuse => _isReadyForReuse && !_isDisposed;

    public Task InitializeAsync()
    {
        return InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ViewModel.InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _isReadyForReuse = true;
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.RefreshFolderContentsAsync();
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
        UpdateEmptyState();
    }

    private void FileSurfaceContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            Root.Focus(FocusState.Programmatic);
        }

        QueueDiskReconciliationIfStale("activated");
    }

    public void OnDeactivated()
    {
        // File hydration and folder watchers follow the actual window visibility,
        // rather than foreground activation. Desktop-layer groups intentionally
        // use SW_SHOWNOACTIVATE, so treating their initial inactive state as a
        // deactivation would cancel the first icon hydration pass.
    }

    public object? CaptureTransientState()
    {
        return new FileWidgetTransientState(
            GetSelectedItems()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _cutClipboardPaths.ToArray());
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not FileWidgetTransientState fileState)
        {
            return;
        }

        RestoreSelection(ItemsGrid, fileState.SelectedPaths);
        RestoreSelection(ItemsList, fileState.SelectedPaths);
        _cutClipboardPaths = fileState.CutPaths
            .Where(path => ViewModel.Items.Any(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        ApplyCutState();
        SynchronizeItemSelectionState();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (visible)
        {
            _hasBeenWindowVisible = true;
            ViewModel.ResumeBackgroundActivity();
            UpdateEmptyState();
            QueueDiskReconciliationIfStale("visible");
            return;
        }

        // Content is attached before its host is shown, and the host reports its
        // initial hidden state during that attach. Do not cancel the initial
        // hydration in that case; only a real visible -> hidden transition
        // suspends the file surface.
        if (_hasBeenWindowVisible)
        {
            ViewModel.SuspendBackgroundActivity();
        }
    }

    private void QueueDiskReconciliationIfStale(string reason)
    {
        if (_isDisposed ||
            DateTime.UtcNow - _lastDiskReconciliationUtc <
                ReconciliationFreshnessWindow ||
            Interlocked.Exchange(ref _diskReconciliationQueued, 1) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    await RefreshAsync();
                    App.LogVerbose(
                        $"[FolderRefresh] Reconciled file surface " +
                        $"widget={WidgetId} reason={reason}");
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[FolderRefresh] File surface reconciliation failed " +
                        $"widget={WidgetId} reason={reason}: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _diskReconciliationQueued, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _diskReconciliationQueued, 0);
        }
    }

    public Task AddFromTitleButtonAsync() => PickAndImportFilesAsync();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySelectionRectangleAppearance();
        UpdateEmptyState();
    }

    private void Items_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (!IsLoaded)
        {
            return;
        }

        EmptyState.Visibility =
            !ViewModel.IsLoading && !ViewModel.VisibleItems.Any()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        string[] selectedPaths = GetSelectedItems()
            .Select(item => item.Path)
            .ToArray();
        ViewModel.ToggleViewMode();
        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewBase activeView =
                ViewModel.IconViewVisibility == Visibility.Visible
                    ? ItemsGrid
                    : ItemsList;
            RestoreSelection(activeView, selectedPaths);
            UpdateSelectionCommandBar();
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(RefreshAsync);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(PickAndImportFilesAsync);
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WidgetStackItem stack)
        {
            ToggleStackFromInput(stack);
            return;
        }

        if (e.ClickedItem is WidgetItem item &&
            !_settingsService.Settings.DoubleClickToOpen &&
            !Win32Helper.IsKeyPressed(VirtualKey.Control) &&
            !Win32Helper.IsKeyPressed(VirtualKey.Shift))
        {
            ViewModel.OpenItem(item);
        }
    }
    private void ToggleStackFromInput(WidgetStackItem stack)
    {
        long now = Environment.TickCount64;
        if (string.Equals(
                _lastStackInputKey,
                stack.StackKey,
                StringComparison.Ordinal) &&
            now - _lastStackInputTick < StackDuplicateInputWindowMs)
        {
            return;
        }

        _lastStackInputKey = stack.StackKey;
        _lastStackInputTick = now;
        ViewModel.ToggleStack(stack);
    }


    private void Items_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (!_settingsService.Settings.DoubleClickToOpen ||
            FindItemFromSource(e.OriginalSource) is not { } item)
        {
            return;
        }

        if (item is WidgetStackItem)
        {
            e.Handled = true;
            return;
        }

        ViewModel.OpenItem(item);
        e.Handled = true;
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        WidgetItem? item = FindItemFromSource(e.OriginalSource);
        if (item is null)
        {
            ClearSelection();
            FrameworkElement contentTarget =
                sender as FrameworkElement ?? Root;
            CreateContentAreaFlyout().ShowAt(
                contentTarget,
                e.GetPosition(contentTarget));
            e.Handled = true;
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        if (!activeView.SelectedItems.Contains(item))
        {
            activeView.SelectedItems.Clear();
            activeView.SelectedItems.Add(item);
        }

        MenuFlyout flyout = item is WidgetStackItem stack
            ? CreateStackFlyout(stack)
            : GetSelectedItems().Count > 1
                ? CreateMultiSelectionFlyout()
                : CreateItemFlyout(item);
        FrameworkElement target =
            FindItemElement(e.OriginalSource) ??
            sender as FrameworkElement ??
            Root;
        flyout.ShowAt(target, e.GetPosition(target));
        e.Handled = true;
    }

    private void Items_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        if (_isImportBusy)
        {
            e.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
        _surfaceReorderLastPosition = default;
        _surfaceReorderHasLastPosition = false;
        WidgetStackItem? stack =
            e.Items.OfType<WidgetStackItem>().FirstOrDefault();
        if (stack is not null)
        {
            _stackPointerDragStarted = true;
            e.Data.RequestedOperation = DataPackageOperation.Link;
            e.Data.Properties[
                DeskBoxDragData.SourceWidgetIdProperty] = WidgetId;
            e.Data.Properties[
                DeskBoxDragData.InternalFileDragTokenProperty] =
                DeskBoxDragData.InternalFileDragToken;
            e.Data.Properties[
                DeskBoxDragData.StackReorderKeyProperty] =
                stack.StackKey;
            e.Data.Properties.Title = stack.Name;
            e.Data.SetText(stack.Name);
            return;
        }

        WidgetItem[] selectedItems = e.Items
            .OfType<WidgetItem>()
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .ToArray();
        string[] paths = selectedItems
            .Select(item => item.Path)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        IReadOnlyList<IStorageItem> storageItems =
            _fileService.GetStorageItems(paths);
        if (storageItems.Count > 0)
        {
            e.Data.SetStorageItems(storageItems, readOnly: false);
        }
        _activeDragSourcePaths = paths;
        _activeDragHasStorageItems = storageItems.Count > 0;
        e.Data.SetText(string.Join(Environment.NewLine, paths));
        e.Data.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move |
            DataPackageOperation.Link;
        e.Data.Properties["DeskBoxSourceWidgetId"] = WidgetId;
        e.Data.Properties["DeskBoxSourcePaths"] = paths;
        e.Data.Properties["DeskBoxInternalDragToken"] =
            "DeskBox.WidgetItemDrag.v2";
        e.Data.Properties.Title = paths.Length == 1
            ? Path.GetFileName(paths[0])
            : paths.Length.ToString();
    }

    private async void Items_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs e)
    {
        string[] movedPaths = _activeDragSourcePaths.Length > 0
            ? _activeDragSourcePaths
            : e.Items
                .OfType<WidgetItem>()
                .Where(item => item is not WidgetStackItem)
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        bool hasStorageItems = _activeDragHasStorageItems;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;

        try
        {
            if ((e.DropResult == DataPackageOperation.Move ||
                 (e.DropResult == DataPackageOperation.None && hasStorageItems)) &&
                movedPaths.Length > 0)
            {
                // DropResult describes the target's requested operation, not an
                // item-by-item completion result. Reconcile against a successful
                // parent enumeration so a partial/cancelled Shell move cannot
                // remove every original row.
                _ = ObserveExternalDragOutAsync(
                    movedPaths,
                    _lifetimeCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Drag completion refresh failed " +
                $"id={WidgetId}: {ex}");
        }
        finally
        {
            _pressedStack = null;
            _stackPointerDragStarted = false;
            ClearStackMemberDropTarget();
            if (_isSurfaceReorderDragActive &&
                _surfaceReorderHasLastPosition)
            {
                // WinUI can complete an item drag without raising Drop. The
                // last DragOver position is still the release position, so
                // commit once here instead of losing the reorder.
                CommitSurfaceReorder(_surfaceReorderLastPosition);
            }
            else
            {
                PersistSurfaceReorder();
            }
        }
    }

    private async Task ObserveExternalDragOutAsync(
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var remainingPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remainingPaths.Count == 0)
        {
            return;
        }

        int delayMs = 300;
        const int MaxAttempts = 11;
        try
        {
            for (int attempt = 0;
                 attempt < MaxAttempts &&
                 !_isDisposed &&
                 remainingPaths.Count > 0;
                 attempt++)
            {
                await Task.Delay(delayMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> missingPaths =
                    await ViewModel.GetConfirmedMissingPathsAsync(remainingPaths);
                if (missingPaths.Count > 0)
                {
                    await ViewModel.HandleItemsMovedOutAsync(missingPaths);
                    foreach (string path in missingPaths)
                    {
                        remainingPaths.Remove(path);
                    }

                    // Re-read the directory as a reconciliation step. This covers
                    // batched Shell moves and folder-watcher notifications that were
                    // coalesced while the grouped Surface was inactive.
                    await ViewModel.RefreshFolderContentsAsync();
                    UpdateEmptyState();
                    App.Log(
                        $"[WidgetSurface] External drag-out reconciled " +
                        $"id={WidgetId} removed={missingPaths.Count} " +
                        $"remaining={remainingPaths.Count}");
                }

                delayMs = (int)Math.Min(delayMs * 2, 300_000);
            }
        }
        catch (OperationCanceledException)
        {
            // The Surface was replaced, its group switched member, or the app closed.
        }
        catch (ObjectDisposedException)
        {
            // The content host disposed the member while a Shell move was pending.
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] External drag-out reconciliation failed " +
                $"id={WidgetId}: {ex}");
        }
    }

    private async Task RenameItemAsync(WidgetItem item)
    {
        // Let the MenuFlyout finish closing before taking keyboard focus.
        await Task.Yield();
        await StartItemRenameAsync(item);
    }

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        FrameworkElement? nameElement = FindItemNameElement(item);
        FrameworkElement? target = nameElement ?? FindItemSurface(item);
        UIElement? contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(item);
        _itemRenameTarget = item;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = item.Name;

        if (nameElement is TextBlock nameText)
        {
            _itemRenameNameText = nameText;
            nameText.Visibility = Visibility.Collapsed;
            ItemRenameTextBox.FontSize =
                nameText.FontSize > 0 ? nameText.FontSize : 14;
            ItemRenameTextBox.TextAlignment = nameText.TextAlignment;
            ItemRenameTextBox.HorizontalContentAlignment =
                nameText.HorizontalAlignment switch
                {
                    HorizontalAlignment.Center => HorizontalAlignment.Center,
                    HorizontalAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
            ItemRenameTextBox.TextWrapping = nameText.TextWrapping;
        }
        else
        {
            ItemRenameTextBox.FontSize = ViewModel.IsListMode
                ? ViewModel.ListLabelFontSize
                : ViewModel.IconLabelFontSize;
            ItemRenameTextBox.TextAlignment = ViewModel.IsListMode
                ? TextAlignment.Left
                : TextAlignment.Center;
            ItemRenameTextBox.TextWrapping = TextWrapping.NoWrap;
        }

        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-file-item-rename-opened");

        SelectFilenameWithoutExtension(ItemRenameTextBox);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_itemRenameTarget, item))
            {
                SelectFilenameWithoutExtension(ItemRenameTextBox);
            }
        });

        await Task.CompletedTask;
    }

    private async void ItemRenameTextBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelItemRename();
        }
    }

    private async void ItemRenameTextBox_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_isCancellingItemRename)
        {
            _isCancellingItemRename = false;
            return;
        }

        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename ||
            _itemRenameTarget is null ||
            ItemRenameTextBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = ItemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelItemRename();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            CompleteItemRename();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Inline rename failed id={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                _localizationService.T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "file-rename-error"));
            ItemRenameTextBox.Focus(FocusState.Programmatic);
            ItemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private void CancelItemRename()
    {
        _isCancellingItemRename = true;
        CompleteItemRename();
    }

    private void CompleteItemRename()
    {
        ItemRenameTextBox.Visibility = Visibility.Collapsed;
        ItemRenameTextBox.IsHitTestVisible = false;
        ItemRenameTextBox.Text = string.Empty;
        if (_itemRenameNameText is not null)
        {
            _itemRenameNameText.Visibility = Visibility.Visible;
            _itemRenameNameText = null;
        }

        _itemRenameTarget = null;
        App.Current?.WidgetManager?.EndWidgetInteraction(
            "surface-file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(
        FrameworkElement target,
        UIElement contentHost)
    {
        Windows.Foundation.Point topLeft = target
            .TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        const double border = 1;
        const double horizontalPadding = 2;
        double offsetX = topLeft.X - border - horizontalPadding;
        double offsetY = topLeft.Y - border;
        double hostPaddingHorizontal = 0;
        double hostPaddingVertical = 0;
        if (contentHost is Grid grid)
        {
            hostPaddingHorizontal = grid.Padding.Left + grid.Padding.Right;
            hostPaddingVertical = grid.Padding.Top + grid.Padding.Bottom;
            offsetX -= grid.Padding.Left;
            offsetY -= grid.Padding.Top;
        }

        double height = Math.Max(target.ActualHeight + (2 * border), 20);
        double width;
        if (contentHost is FrameworkElement host)
        {
            double contentWidth =
                Math.Max(60, host.ActualWidth - hostPaddingHorizontal);
            double availableWidth =
                Math.Max(60, contentWidth - offsetX - 8);
            width = ViewModel.IsListMode
                ? Math.Clamp(availableWidth, 80, contentWidth)
                : Math.Clamp(
                    target.ActualWidth +
                    (2 * (border + horizontalPadding)),
                    60,
                    availableWidth);
            double contentHeight =
                Math.Max(20, host.ActualHeight - hostPaddingVertical);
            height = Math.Min(
                height,
                Math.Max(20, contentHeight - offsetY - 4));
        }
        else
        {
            width = Math.Max(
                target.ActualWidth +
                (2 * (border + horizontalPadding)),
                60);
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin =
            new Thickness(offsetX, offsetY, 0, 0);
    }

    private FrameworkElement? FindItemNameElement(WidgetItem item)
    {
        if (GetActiveItemsView().ContainerFromItem(item)
            is not SelectorItem container)
        {
            return null;
        }

        string elementName = ViewModel.IsListMode
            ? "ListItemNameText"
            : "IconItemNameText";
        return FindNamedDescendant<TextBlock>(container, elementName);
    }

    private static TElement? FindNamedDescendant<TElement>(
        DependencyObject parent,
        string name)
        where TElement : FrameworkElement
    {
        int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper
            .GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child =
                Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(
                    parent,
                    index);
            if (child is TElement element &&
                string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            if (FindNamedDescendant<TElement>(child, name)
                is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SelectFilenameWithoutExtension(TextBox textBox)
    {
        textBox.Focus(FocusState.Programmatic);
        string text = textBox.Text;
        int dotIndex = text.LastIndexOf('.');
        if (dotIndex > 0 && text.Length - dotIndex - 1 <= 8)
        {
            textBox.Select(0, dotIndex);
        }
        else
        {
            textBox.SelectAll();
        }
    }

    private async Task DeleteItemAsync(WidgetItem item)
    {
        await RunAsync(() => ViewModel.DeleteItemsAsync([item]));
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format("Widget.MovedToRecycleBin", 1),
            WidgetFeedbackSeverity.Success,
            "file-delete"));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isImportBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            return;
        }

        if (IsInternalReorderDrag(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
            HandleSurfaceRealTimeReorder(
                e.DataView.Properties,
                e.GetPosition(GetActiveItemsView()));
            return;
        }

        if (HasSurfacePathDropData(e.DataView))
        {
            string[] synchronousPaths = GetPackagePaths(e.DataView);
            if (IsUnsafeFolderDrop(synchronousPaths, ViewModel.MappedFolderPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = T("Widget.Error.UnsafeFolderTransfer");
                ApplyDropVisual(FileDropVisualState.None);
                return;
            }

            e.AcceptedOperation = ResolveSurfaceDropOperation(e.DataView);
            e.DragUIOverride.IsGlyphVisible =
                e.AcceptedOperation != DataPackageOperation.None;
            e.DragUIOverride.IsCaptionVisible =
                e.AcceptedOperation != DataPackageOperation.None;
            e.DragUIOverride.Caption =
                GetSurfaceDropCaption(e.AcceptedOperation);
            ApplyDropVisual(FileDropVisualState.None);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
        }
    }

    private static bool IsUnsafeFolderDrop(
        IReadOnlyList<string> sourcePaths,
        string? destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        return sourcePaths.Any(sourcePath =>
            !string.IsNullOrWhiteSpace(sourcePath) &&
            Directory.Exists(sourcePath) &&
            FileService.IsPathUnderDirectory(normalizedDestination, sourcePath));
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        ApplyDropVisual(FileDropVisualState.None);
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        ExternalFileDragEnded?.Invoke(this, EventArgs.Empty);
        if (_isSurfaceReorderDragActive &&
            _surfaceReorderHasLastPosition)
        {
            CommitSurfaceReorder(_surfaceReorderLastPosition);
        }
        else
        {
            PersistSurfaceReorder();
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        ExternalFileDragEnded?.Invoke(this, EventArgs.Empty);
        if (IsInternalReorderDrag(e.DataView))
        {
            _surfaceReorderStackKey ??= TryGetString(
                e.DataView.Properties,
                DeskBoxDragData.StackReorderKeyProperty);
            HandleSurfaceFinalReorder(
                GetPackagePaths(e.DataView),
                e.GetPosition(GetActiveItemsView()));
            PersistSurfaceReorder();
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            IReadOnlyList<DroppedFilePath> droppedFiles = batch.Files;
            string[] paths = droppedFiles
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (droppedFiles.Count > 0)
            {
                DataPackageOperation accepted =
                    e.AcceptedOperation == DataPackageOperation.None
                        ? ResolveSurfaceDropOperation(e.DataView)
                        : e.AcceptedOperation;
                bool mapped = !string.IsNullOrWhiteSpace(
                    ViewModel.MappedFolderPath);
                bool? moveWhenMapped = mapped
                    ? accepted != DataPackageOperation.Copy
                    : null;
                string? sourceWidgetId = TryGetString(
                    e.DataView.Properties,
                    "DeskBoxSourceWidgetId");
                bool showOverlay =
                    DeskBoxDragData.ShouldShowImportOverlay(paths);
                if (showOverlay)
                {
                    SetImportBusy(true);
                }
                try
                {
                    IReadOnlyList<string> completedSourcePaths =
                        await ImportDroppedFilesAsync(
                            droppedFiles,
                            moveWhenMapped);
                    if (moveWhenMapped == true &&
                        sourceWidgetId is { Length: > 0 } &&
                        App.Current?.WidgetManager is { } manager)
                    {
                        await manager.NotifyItemsMovedOutAsync(
                            sourceWidgetId,
                            completedSourcePaths);
                    }
                }
                finally
                {
                    if (showOverlay)
                    {
                        SetImportBusy(false);
                    }
                }

                ShowFeedback(new(
                    _localizationService.Format(
                        moveWhenMapped == true
                            ? "Widget.MovedCount"
                            : "Widget.PastedCount",
                        droppedFiles.Count),
                    WidgetFeedbackSeverity.Success,
                    "file-drop"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] File drop failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-drop-error"));
        }
        finally
        {
            ApplyDropVisual(FileDropVisualState.None);
            deferral.Complete();
        }
    }

    private void SetImportBusy(bool isBusy)
    {
        if (_isImportBusy == isBusy)
        {
            return;
        }

        _isImportBusy = isBusy;
        if (isBusy)
        {
            ImportTitleText.Text = T("Widget.Import.Title");
            ImportDescriptionText.Text =
                T("Widget.Import.Description");
            ApplyDropVisual(FileDropVisualState.None);
        }

        ImportOverlay.Visibility =
            isBusy ? Visibility.Visible : Visibility.Collapsed;
        ImportProgressRing.IsActive = isBusy;
        ItemsGrid.IsHitTestVisible = !isBusy;
        ItemsList.IsHitTestVisible = !isBusy;
        EmptyState.IsHitTestVisible = !isBusy;
        ImportBusyChanged?.Invoke(isBusy);
    }

    internal void SetDesktopOrganizationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetDesktopOrganizationBusy(isBusy));
            return;
        }

        if (_isImportBusy == isBusy)
        {
            return;
        }

        _isImportBusy = isBusy;
        if (isBusy)
        {
            ImportTitleText.Text = T("DesktopOrganization.Busy.Title");
            ImportDescriptionText.Text = T("DesktopOrganization.Busy.Description");
            ApplyDropVisual(FileDropVisualState.None);
        }

        ImportOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ImportProgressRing.IsActive = isBusy;
        ItemsGrid.IsHitTestVisible = !isBusy;
        ItemsList.IsHitTestVisible = !isBusy;
        EmptyState.IsHitTestVisible = !isBusy;
        ImportBusyChanged?.Invoke(isBusy);
    }

    internal bool IsInternalReorderDrag(DataPackageView dataView)
    {
        return string.Equals(
                   TryGetString(
                       dataView.Properties,
                       "DeskBoxInternalDragToken"),
                   "DeskBox.WidgetItemDrag.v2",
                   StringComparison.Ordinal) &&
               string.Equals(
                   TryGetString(
                       dataView.Properties,
                       "DeskBoxSourceWidgetId"),
                   WidgetId,
                   StringComparison.Ordinal) &&
               (GetPackagePaths(dataView).Length > 0 ||
                !string.IsNullOrWhiteSpace(
                    TryGetString(
                        dataView.Properties,
                        DeskBoxDragData.StackReorderKeyProperty)));
    }

    private static bool HasSurfacePathDropData(DataPackageView dataView)
    {
        return GetPackagePaths(dataView).Length > 0 ||
               DeskBoxDragData.HasImportableFileData(dataView);
    }

    private DataPackageOperation ResolveSurfaceDropOperation(
        DataPackageView dataView)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return DataPackageOperation.Link;
        }

        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Control);
        bool copyRequested =
            controlState.HasFlag(CoreVirtualKeyStates.Down);
        DataPackageOperation requested = dataView.RequestedOperation;
        if (requested == DataPackageOperation.None)
        {
            return DataPackageOperation.Move;
        }

        if (copyRequested &&
            requested.HasFlag(DataPackageOperation.Copy))
        {
            return DataPackageOperation.Copy;
        }

        if (requested.HasFlag(DataPackageOperation.Move))
        {
            return DataPackageOperation.Move;
        }

        return requested.HasFlag(DataPackageOperation.Copy)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private string GetSurfaceDropCaption(
        DataPackageOperation operation)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return T("Widget.DragCaption.Reference");
        }

        string operationText = T(
            operation == DataPackageOperation.Copy
                ? "Common.Copy"
                : "Common.Move");
        return _localizationService.Format(
            ViewModel.FollowsDefaultStoragePath
                ? "Widget.DragCaption.Managed"
                : "Widget.DragCaption.Mapped",
            operationText);
    }

    private static async Task<DroppedFileBatch> GetSurfaceDropFilesAsync(
        DataPackageView dataView)
    {
        string[] paths = GetPackagePaths(dataView);
        if (paths.Length > 0)
        {
            DroppedFilePath[] files = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    try
                    {
                        return Path.GetFullPath(path);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                })
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new DroppedFilePath(
                    path,
                    Path.GetFileName(path),
                    ForceManagedCopy: false))
                .ToArray();
            return new DroppedFileBatch(files, temporaryDirectory: null, skippedCount: 0);
        }

        return await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
    }

    private async Task<IReadOnlyList<string>> ImportDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        bool? moveWhenMapped)
    {
        var movedSourcePaths = new List<string>();
        string[] regularPaths = droppedFiles
            .Where(file => !file.ForceManagedCopy)
            .Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (regularPaths.Length > 0)
        {
            IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                regularPaths,
                moveWhenMapped,
                useShellProgress: moveWhenMapped == true);
            if (moveWhenMapped == true)
            {
                movedSourcePaths.AddRange(completed);
            }
        }

        string[] managedCopyPaths = droppedFiles
            .Where(file => file.ForceManagedCopy)
            .Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (managedCopyPaths.Length > 0)
        {
            // Virtual browser files and URL downloads live in a temporary
            // directory owned by DroppedFileBatch. They must always be copied.
            await ViewModel.ImportPathsAsync(
                managedCopyPaths,
                moveWhenMapped: false,
                useShellProgress: false);
        }

        return movedSourcePaths;
    }

    /// <summary>
    /// Imports a file payload received by the owning surface window's native
    /// drag-drop bridge. Grouped file content has no HWND of its own, so this
    /// mirrors the regular surface import pipeline after the host extracts the
    /// native OLE or WM_DROPFILES payload.
    /// </summary>
    internal async Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles)
    {
        if (_isDisposed || _isImportBusy)
        {
            return false;
        }

        DroppedFilePath[] droppedFiles = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(path),
                ForceManagedCopy: containsTemporaryFiles))
            .ToArray();
        if (droppedFiles.Length == 0)
        {
            return false;
        }

        bool mapped = !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
        bool? moveWhenMapped = mapped
            ? containsTemporaryFiles || Win32Helper.IsKeyPressed(VirtualKey.Control)
                ? false
                : true
            : null;
        bool showOverlay = DeskBoxDragData.ShouldShowImportOverlay(
            droppedFiles.Select(file => file.Path).ToArray());
        if (showOverlay)
        {
            SetImportBusy(true);
        }

        try
        {
            await ImportDroppedFilesAsync(droppedFiles, moveWhenMapped);
            ShowFeedback(new(
                _localizationService.Format(
                    moveWhenMapped == true
                        ? "Widget.MovedCount"
                        : "Widget.PastedCount",
                    droppedFiles.Length),
                WidgetFeedbackSeverity.Success,
                "native-file-drop"));
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Native file drop failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "native-file-drop-error"));
            return false;
        }
        finally
        {
            if (showOverlay)
            {
                SetImportBusy(false);
            }
        }
    }

    private void HandleSurfaceRealTimeReorder(
        DataPackagePropertySetView properties,
        Windows.Foundation.Point position)
    {
        string? stackKey = TryGetString(
            properties,
            DeskBoxDragData.StackReorderKeyProperty);
        if (!string.IsNullOrWhiteSpace(stackKey))
        {
            _isSurfaceReorderDragActive = true;
            _surfaceReorderStackKey = stackKey;
            _surfaceReorderPaths = [];
            UpdateSurfaceReorderPreview(position);
            return;
        }

        string[] paths = properties.TryGetValue(
                "DeskBoxSourcePaths",
                out object? value)
            ? value switch
            {
                string[] array => array,
                IEnumerable<string> sequence => sequence.ToArray(),
                _ => []
            }
            : [];
        if (paths.Length == 0)
        {
            return;
        }

        HashSet<string> pathSet = paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = ViewModel.Items.FirstOrDefault(item =>
            pathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        if (!_isSurfaceReorderDragActive)
        {
            if (ViewModel.FileStacksEnabled)
            {
                if (!ViewModel.PrepareVisibleItemReorder(draggedItem))
                {
                    return;
                }
            }
            else if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }

            _isSurfaceReorderDragActive = true;
            _surfaceReorderPaths = paths;
        }

        UpdateSurfaceReorderPreview(position);
    }

    private void HandleSurfaceFinalReorder(
        IReadOnlyList<string> paths,
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            _surfaceReorderPaths = paths.ToArray();
            _isSurfaceReorderDragActive =
                _surfaceReorderPaths.Length > 0;
        }

        CommitSurfaceReorder(position);
    }

    private void UpdateSurfaceReorderPreview(
        Windows.Foundation.Point position)
    {
        _surfaceReorderLastPosition = position;
        _surfaceReorderHasLastPosition = true;
        ListViewBase activeView = GetActiveItemsView();
        _surfaceReorderInsertionIndex =
            ReorderDropIndexCalculator.Compute(
                activeView,
                position,
                _surfaceReorderInsertionIndex);
        UpdateSurfaceReorderInsertionIndicator(position);
    }

    private void UpdateSurfaceReorderInsertionIndicator(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        if (!_isSurfaceReorderDragActive ||
            _surfaceReorderInsertionIndex < 0 ||
            !ReorderDropIndexCalculator.TryGetInsertionIndicatorPlacement(
                activeView,
                SelectionOverlay,
                _surfaceReorderInsertionIndex,
                position,
                out ReorderInsertionIndicatorPlacement placement))
        {
            HideSurfaceReorderInsertionIndicator();
            return;
        }

        bool wasVisible =
            ReorderInsertionIndicator.Visibility == Visibility.Visible;
        ReorderInsertionIndicator.Width = placement.Bounds.Width;
        ReorderInsertionIndicator.Height = placement.Bounds.Height;
        ReorderInsertionLine.Width = placement.IsVertical
            ? 2
            : placement.Bounds.Width;
        ReorderInsertionLine.Height = placement.IsVertical
            ? placement.Bounds.Height
            : 2;
        Canvas.SetLeft(
            ReorderInsertionIndicator,
            placement.Bounds.X);
        Canvas.SetTop(
            ReorderInsertionIndicator,
            placement.Bounds.Y);
        ReorderInsertionIndicator.Opacity = 1;
        ReorderInsertionIndicator.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            ReorderInsertionIndicatorAnimator.Start(
                ReorderInsertionIndicator);
        }
    }

    private void HideSurfaceReorderInsertionIndicator()
    {
        ReorderInsertionIndicatorAnimator.Stop(
            ReorderInsertionIndicator);
        ReorderInsertionIndicator.Visibility = Visibility.Collapsed;
        ReorderInsertionIndicator.Opacity = 0;
        ReorderInsertionIndicator.Width = 0;
        ReorderInsertionIndicator.Height = 0;
    }

    private void ApplySurfaceReorder(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        int targetIndex = ReorderDropIndexCalculator.Compute(
            activeView,
            position,
            _surfaceReorderInsertionIndex);
        _surfaceReorderInsertionIndex = targetIndex;

        if (!string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            ViewModel.MoveStackForReorder(
                _surfaceReorderStackKey,
                targetIndex);
            return;
        }

        if (_surfaceReorderPaths.Length == 0)
        {
            return;
        }

        HashSet<string> pathSet = _surfaceReorderPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = ViewModel.Items.FirstOrDefault(item =>
            pathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.FileStacksEnabled
            ? activeView.Items.IndexOf(draggedItem)
            : ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        if (ViewModel.FileStacksEnabled)
        {
            ViewModel.MoveVisibleItemForReorder(
                draggedItem,
                targetIndex);
            return;
        }

        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(
            draggedItem,
            targetIndex);
    }

    private void PersistSurfaceReorder()
    {
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
    }

    private void CommitSurfaceReorder(
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            return;
        }

        ApplySurfaceReorder(position);
        if (string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            ViewModel.PersistManualOrder();
        }

        PersistSurfaceReorder();
    }

    private void ApplyDropVisual(FileDropVisualState state)
    {
        // Match the standalone file widget: keep content readable and let the
        // native drag caption communicate the operation and destination type.
        DropOverlay.Visibility = Visibility.Collapsed;
        DropOverlay.Opacity = 0;
        ItemsGrid.Opacity = 1;
        ItemsList.Opacity = 1;
        EmptyState.Opacity = 1;
    }

    private static Microsoft.UI.Xaml.Media.Brush? ResolveBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (await TryHandleSpacePreviewAsync(e))
        {
            return;
        }

        if (e.Handled)
        {
            return;
        }

        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool control = controlState.HasFlag(CoreVirtualKeyStates.Down);
        CoreVirtualKeyStates shiftState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool shift = shiftState.HasFlag(CoreVirtualKeyStates.Down);
        if (control && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            ListViewBase activeView =
                ViewModel.IconViewVisibility == Visibility.Visible
                    ? ItemsGrid
                    : ItemsList;
            activeView.SelectAll();
            UpdateSelectionCommandBar();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ClearSelection();
            _cutClipboardPaths = [];
            ApplyCutState();
            return;
        }

        if (control && shift && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            CopySelectedPathsToClipboard();
            return;
        }

        if (control && e.Key is VirtualKey.C or VirtualKey.X)
        {
            e.Handled = true;
            await RunAsync(() => CopySelectionToClipboardAsync(
                cut: e.Key == VirtualKey.X));
            return;
        }

        if (control && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            await RunAsync(PasteFromClipboardAsync);
            return;
        }

        if (e.Key == VirtualKey.F2 &&
            GetSelectedItems().FirstOrDefault() is { } renameTarget)
        {
            e.Handled = true;
            await RenameItemAsync(renameTarget);
            return;
        }

        if (e.Key == VirtualKey.Delete &&
            GetSelectedItems() is { Count: > 0 } deleteTargets)
        {
            e.Handled = true;
            await DeleteItemsAsync(deleteTargets);
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            GetSelectedItems().FirstOrDefault() is { } openTarget)
        {
            e.Handled = true;
            ViewModel.OpenItem(openTarget);
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            await RunAsync(RefreshAsync);
        }
    }

    private async void ItemsView_PreviewKeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        await TryHandleSpacePreviewAsync(e);
    }

    private async Task<bool> TryHandleSpacePreviewAsync(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space ||
            IsTextInputSource(e.OriginalSource))
        {
            return false;
        }

        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 ||
            selectedItems.Any(item => item is WidgetStackItem))
        {
            return false;
        }

        // Match the standalone file widget: ListView/GridView handles Space
        // for selection and otherwise swallows the key before normal KeyDown.
        e.Handled = true;
        WidgetItem previewTarget = selectedItems[0];
        if (s_quickLookService.CanPreview(previewTarget.Path))
        {
            await s_quickLookService.TryToggleAsync(previewTarget.Path);
        }

        return true;
    }

    private static bool IsTextInputSource(object originalSource)
    {
        for (DependencyObject? current = originalSource as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private ListViewBase GetActiveItemsView()
    {
        return ViewModel.IconViewVisibility == Visibility.Visible
            ? ItemsGrid
            : ItemsList;
    }

    private IReadOnlyList<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView().SelectedItems
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .Distinct()
            .ToList();
    }

    private void Items_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        if (e.AddedItems.Count > 0)
        {
            ClearOtherWidgetSelections();
        }

        SynchronizeItemSelectionState();
        UpdateSelectionCommandBar();
    }

    private void UpdateSelectionCommandBar()
    {
        SelectionCommandBar.Visibility = Visibility.Collapsed;
    }

    private void OpenSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            ViewModel.OpenItem(item);
        }
    }

    private async void CopySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: false));
    }

    private async void CutSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: true));
    }

    private async void DeleteSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems() is { Count: > 0 } items)
        {
            await DeleteItemsAsync(items);
        }
    }

    private async void RenameSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await RenameItemAsync(item);
        }
    }

    private static void RestoreSelection(
        ListViewBase view,
        IReadOnlyList<string> selectedPaths)
    {
        view.SelectedItems.Clear();
        foreach (WidgetItem item in view.Items.OfType<WidgetItem>())
        {
            if (selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                view.SelectedItems.Add(item);
            }
        }
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        string[] paths = GetSelectedItems()
            .Select(item => item.Path)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, paths);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: clipboardText,
            paths: paths);
        bool shellClipboardSet =
            ShellClipboardHelper.TrySetFileDropList(paths, cut);
        if (!shellClipboardSet)
        {
            var package = new DataPackage
            {
                RequestedOperation =
                    cut ? DataPackageOperation.Move : DataPackageOperation.Copy
            };
            IReadOnlyList<IStorageItem> storageItems =
                await _fileService.GetStorageItemsAsync(paths);
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }
            else
            {
                package.SetText(clipboardText);
            }
            package.Properties["DeskBoxSourceWidgetId"] = WidgetId;
            package.Properties["DeskBoxSourcePaths"] = paths;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = cut ? paths : [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                cut ? "Widget.CutCount" : "Widget.CopyCount",
                paths.Length),
            WidgetFeedbackSeverity.Success,
            cut ? "file-cut" : "file-copy"));
    }

    private async Task PasteFromClipboardAsync()
    {
        DataPackageView clipboard = Clipboard.GetContent();
        string[] sourcePaths = GetPackagePaths(clipboard);
        if (sourcePaths.Length == 0 &&
            clipboard.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> storageItems =
                await clipboard.GetStorageItemsAsync();
            sourcePaths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (sourcePaths.Length == 0)
        {
            return;
        }

        bool move = clipboard.RequestedOperation.HasFlag(
            DataPackageOperation.Move);
        IReadOnlyList<string> completedSourcePaths = await ViewModel.ImportPathsAsync(
            sourcePaths,
            moveWhenMapped: move,
            useShellProgress: move);
        if (move &&
            TryGetString(clipboard.Properties, "DeskBoxSourceWidgetId")
                is { Length: > 0 } sourceWidgetId &&
            App.Current?.WidgetManager is { } manager)
        {
            await manager.NotifyItemsMovedOutAsync(
                sourceWidgetId,
                completedSourcePaths);
        }

        _cutClipboardPaths = [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                move ? "Widget.MovedCount" : "Widget.PastedCount",
                sourcePaths.Length),
            WidgetFeedbackSeverity.Success,
            move ? "file-move" : "file-paste"));
    }

    private static string[] GetPackagePaths(DataPackageView package)
    {
        if (!package.Properties.TryGetValue(
                "DeskBoxSourcePaths",
                out object? value))
        {
            return [];
        }

        return value switch
        {
            string[] paths => paths,
            IEnumerable<string> paths => paths.ToArray(),
            _ => []
        };
    }

    private static string? TryGetString(
        DataPackagePropertySetView properties,
        string key)
    {
        return properties.TryGetValue(key, out object? value)
            ? value as string
            : null;
    }

    private async Task DeleteItemsAsync(IReadOnlyList<WidgetItem> items)
    {
        await RunAsync(() => ViewModel.DeleteItemsAsync(items));
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.MovedToRecycleBin",
                items.Count),
            WidgetFeedbackSeverity.Success,
            "file-delete"));
    }

    private void ApplyCutState()
    {
        foreach (WidgetItem item in ViewModel.Items)
        {
            item.IsCut = _cutClipboardPaths.Contains(
                item.Path,
                StringComparer.OrdinalIgnoreCase);
        }

        UpdateItemSurfaceVisuals();
    }

    private async Task PickAndImportFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
        InitializeWithWindow.Initialize(
            picker,
            owner == IntPtr.Zero ? foreground : owner);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await ViewModel.ImportPathsAsync(files.Select(file => file.Path));
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] File action failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-action-error"));
        }
        finally
        {
            UpdateEmptyState();
        }
    }

    private string T(string key) => _localizationService.T(key);

    private void ShowFeedback(WidgetFeedbackRequest request)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(request));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        PersistSurfaceReorder();
        _isDisposed = true;
        _isReadyForReuse = false;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        if (_isImportBusy)
        {
            SetImportBusy(false);
        }
        if (_itemRenameTarget is not null)
        {
            CancelItemRename();
        }
        Loaded -= OnLoaded;
        ActualThemeChanged -= FileSurfaceContent_ActualThemeChanged;
        ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
        ViewModel.Dispose();
    }
}

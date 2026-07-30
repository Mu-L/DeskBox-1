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
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private const int StackDuplicateInputWindowMs = 120;
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

    public async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        UpdateEmptyState();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.RefreshFolderContentsAsync();
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
    }

    public void OnDeactivated()
    {
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
            UpdateEmptyState();
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
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
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
            if (e.DropResult == DataPackageOperation.Move)
            {
                await ViewModel.HandleItemsMovedOutAsync(movedPaths);
            }
            else if (e.DropResult == DataPackageOperation.None &&
                     hasStorageItems &&
                     movedPaths.Length > 0)
            {
                // Explorer and the desktop commonly report None for an external
                // Shell/OLE move. Keep the original path snapshot and reconcile
                // only after the source entries actually disappear; a cancelled
                // drag or an external copy therefore leaves the surface untouched.
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
            PersistSurfaceReorder();
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

                string[] missingPaths = remainingPaths
                    .Where(path => !File.Exists(path) && !Directory.Exists(path))
                    .ToArray();
                if (missingPaths.Length > 0)
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
                        $"id={WidgetId} removed={missingPaths.Length} " +
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

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        ApplyDropVisual(FileDropVisualState.None);
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        ExternalFileDragEnded?.Invoke(this, EventArgs.Empty);
        PersistSurfaceReorder();
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
            string[] paths = await GetSurfaceDropPathsAsync(e.DataView);
            if (paths.Length > 0)
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
                    await ViewModel.ImportPathsAsync(
                        paths,
                        moveWhenMapped,
                        useShellProgress: moveWhenMapped == true);
                    if (moveWhenMapped == true &&
                        sourceWidgetId is { Length: > 0 } &&
                        App.Current?.WidgetManager is { } manager)
                    {
                        await manager.NotifyItemsMovedOutAsync(
                            sourceWidgetId,
                            paths);
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
                        paths.Length),
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
        return dataView.Contains(StandardDataFormats.StorageItems) ||
               GetPackagePaths(dataView).Length > 0;
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

    private static async Task<string[]> GetSurfaceDropPathsAsync(
        DataPackageView dataView)
    {
        string[] paths = GetPackagePaths(dataView);
        if (paths.Length > 0 ||
            !dataView.Contains(StandardDataFormats.StorageItems))
        {
            return paths;
        }

        IReadOnlyList<IStorageItem> items =
            await dataView.GetStorageItemsAsync();
        return items
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            MoveSurfaceReorderItem(position);
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

        MoveSurfaceReorderItem(position);
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

        MoveSurfaceReorderItem(position);
    }

    private void MoveSurfaceReorderItem(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        if (!string.IsNullOrWhiteSpace(
                _surfaceReorderStackKey))
        {
            int insertionIndex =
                ComputeSurfaceDropInsertionIndex(
                    activeView,
                    position);
            ViewModel.MoveStackForReorder(
                _surfaceReorderStackKey,
                insertionIndex);
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

        int targetIndex = ComputeSurfaceDropInsertionIndex(
            activeView,
            position);
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

    private static int ComputeSurfaceDropInsertionIndex(
        ListViewBase list,
        Windows.Foundation.Point position)
    {
        if (list.Items.Count == 0)
        {
            return 0;
        }

        bool grid = list is GridView;
        for (int index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is not
                    FrameworkElement container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            Windows.Foundation.Rect bounds =
                container.TransformToVisual(list).TransformBounds(
                    new Windows.Foundation.Rect(
                        0,
                        0,
                        container.ActualWidth,
                        container.ActualHeight));
            if (grid)
            {
                bool aboveRow = position.Y < bounds.Top;
                bool sameRow =
                    position.Y >= bounds.Top &&
                    position.Y < bounds.Bottom;
                bool leftOfCenter =
                    position.X < bounds.X + (bounds.Width / 2);
                if (aboveRow || (sameRow && leftOfCenter))
                {
                    return index;
                }
            }
            else if (position.Y <
                     bounds.Top + (bounds.Height / 2))
            {
                return index;
            }
        }

        return list.Items.Count;
    }

    private void PersistSurfaceReorder()
    {
        if (_isSurfaceReorderDragActive &&
            string.IsNullOrWhiteSpace(
                _surfaceReorderStackKey))
        {
            ViewModel.PersistManualOrder();
        }

        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
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

        if (e.Key == VirtualKey.Space &&
            GetSelectedItems().FirstOrDefault() is { } previewTarget &&
            s_quickLookService.CanPreview(previewTarget.Path))
        {
            e.Handled = true;
            await s_quickLookService.TryToggleAsync(
                previewTarget.Path);
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            await RunAsync(RefreshAsync);
        }
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
        await ViewModel.ImportPathsAsync(
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
                sourcePaths);
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

        _isDisposed = true;
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

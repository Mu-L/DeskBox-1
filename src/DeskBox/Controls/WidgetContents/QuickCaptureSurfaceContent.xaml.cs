using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Window-independent Quick Capture member. All top-level window, DWM,
/// z-order, bounds, capsule, and group navigation behavior stays with the
/// surface host; this control owns only Quick Capture data and interaction.
/// </summary>
public sealed partial class QuickCaptureSurfaceContent :
    UserControl,
    IWidgetContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private readonly LocalizationService _localizationService;
    private string _lastFocusTarget = "Root";
    private string? _pendingFocusTarget;
    private bool _isDisposed;

    public QuickCaptureSurfaceContent(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(quickCaptureService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);

        _localizationService = localizationService;
        ViewModel = new QuickCaptureWidgetViewModel(
            config,
            quickCaptureService,
            settingsService,
            localizationService,
            dispatcherQueue);

        InitializeComponent();
        Root.DataContext = ViewModel;
        Loaded += OnLoaded;
        UpdateSelectedViewVisual();
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.QuickCapture;

    public FrameworkElement View => this;

    public async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        UpdateSelectedViewVisual();
    }

    public Task RefreshAsync() => ViewModel.RefreshItemsAsync();

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        UpdateSelectedViewVisual();
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            ApplyPendingFocus();
        }
    }

    public void OnDeactivated()
    {
        _lastFocusTarget = GetCurrentFocusTarget();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (visible && IsLoaded)
        {
            ViewModel.RefreshAfterViewReady();
        }
    }

    public void RestoreTransientState(string? inputText, string? searchText)
    {
        ViewModel.InputText = inputText ?? string.Empty;
        ViewModel.SearchText = searchText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            ViewModel.ExpandSearch();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        return new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            _lastFocusTarget);
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (state is QuickCaptureWidgetTransientState quickState)
        {
            RestoreTransientState(
                quickState.InputText,
                quickState.SearchText);
            ViewModel.SelectedView = quickState.SelectedView;
            _pendingFocusTarget = quickState.FocusTarget;
            UpdateSelectedViewVisual();
            if (IsLoaded)
            {
                DispatcherQueue.TryEnqueue(ApplyPendingFocus);
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshAfterViewReady();
        UpdateSelectedViewVisual();
        ApplyPendingFocus();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(ViewModel.AddInputAsync);
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !InputTextBox.AcceptsReturn)
        {
            e.Handled = true;
            await RunAsync(ViewModel.AddInputAsync);
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter &&
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift).HasFlag(
                    Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            await RunAsync(ViewModel.AddInputAsync);
        }
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.SearchText = string.Empty;
            Root.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out QuickCaptureViewMode mode))
        {
            return;
        }

        ViewModel.SelectedView = mode;
        UpdateSelectedViewVisual();
    }

    private async void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        var editor = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 120,
            Text = item.Body,
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.Title ?? ViewModel.DisplayName,
            Content = editor,
            PrimaryButtonText = T("Common.Save"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(editor.Text))
        {
            await RunAsync(() => ViewModel.EditItemAsync(item, editor.Text));
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await RunAsync(() => ViewModel.TogglePinnedAsync(item));
        }
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await RunAsync(() => ViewModel.CopyItemAsync(item));
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickCaptureItemViewModel item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Common.Delete"),
            Content = item.DisplayText,
            PrimaryButtonText = T("Common.Delete"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunAsync(async () =>
            {
                await ViewModel.DeleteItemAsync(item);
            });
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            e.DataView.Contains(DeskBoxDragData.TextFormat) ||
            e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DeskBoxDragData.HasDroppedFiles(e.DataView)
                ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                : DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = false;
            e.Handled = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch =
                    await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                QuickCaptureItemViewModel? created =
                    await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
                e.AcceptedOperation = created is null
                    ? DataPackageOperation.None
                    : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
                if (created is not null)
                {
                    RaiseFeedback(
                        T("QuickCapture.Dropped"),
                        WidgetFeedbackSeverity.Success,
                        "quick-drop");
                }
            }
            else
            {
                string? text = await DeskBoxDragData.TryGetTextAsync(
                    e.DataView);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await ViewModel.AddTextAsync(text);
                    e.AcceptedOperation = DataPackageOperation.Copy;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Quick Capture drop failed id={WidgetId}: {ex}");
            RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Error, "quick-drop-error");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void QuickCaptureItem_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (!DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            sender is not Border border)
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation =
            DeskBoxDragData.GetFileAssociationOperation(e.DataView);
        e.DragUIOverride.IsGlyphVisible = true;
        ApplyQuickCaptureItemDropState(border, active: true);
    }

    private void QuickCaptureItem_DragLeave(
        object sender,
        DragEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyQuickCaptureItemDropState(border, active: false);
        }
    }

    private async void QuickCaptureItem_Drop(
        object sender,
        DragEventArgs e)
    {
        if (!DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            sender is not Border
            {
                DataContext: QuickCaptureItemViewModel item
            } border)
        {
            return;
        }

        e.Handled = true;
        ApplyQuickCaptureItemDropState(border, active: false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch =
                await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            QuickCaptureItemViewModel? updated =
                await ViewModel.AddAttachmentsAsync(item, batch.Files);
            e.AcceptedOperation = updated is null
                ? DataPackageOperation.None
                : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
            if (updated is not null)
            {
                RaiseFeedback(
                    T("QuickCapture.Dropped"),
                    WidgetFeedbackSeverity.Success,
                    "quick-attach");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Quick Capture item drop failed " +
                $"id={WidgetId}: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            RaiseFeedback(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "quick-attach-error");
        }
        finally
        {
            ApplyQuickCaptureItemDropState(border, active: false);
            deferral.Complete();
        }
    }

    private static void ApplyQuickCaptureItemDropState(
        Border border,
        bool active)
    {
        border.Background = active
            ? ResolveBrush(
                "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x28, 0x78, 0x9E, 0xFF))
            : new SolidColorBrush(Colors.Transparent);
        border.BorderBrush = active
            ? ResolveBrush(
                "AccentFillColorDefaultBrush",
                Colors.DeepSkyBlue)
            : new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(active ? 1 : 0);
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        return Application.Current.Resources.TryGetValue(
                   key,
                   out object? value) &&
               value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
    }

    private void UpdateSelectedViewVisual()
    {
        if (!IsLoaded)
        {
            return;
        }

        RecordsButton.Opacity =
            ViewModel.SelectedView == QuickCaptureViewMode.Records ? 1 : 0.62;
        PinnedButton.Opacity =
            ViewModel.SelectedView == QuickCaptureViewMode.Pinned ? 1 : 0.62;
        RecentButton.Opacity =
            ViewModel.SelectedView == QuickCaptureViewMode.Recent ? 1 : 0.62;
    }

    private string GetCurrentFocusTarget()
    {
        object? focused = XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(XamlRoot);
        if (ReferenceEquals(focused, InputTextBox))
        {
            return "Input";
        }
        if (ReferenceEquals(focused, SearchTextBox))
        {
            return "Search";
        }
        if (ReferenceEquals(focused, ItemsList))
        {
            return "Items";
        }

        return "Root";
    }

    private void ApplyPendingFocus()
    {
        string target = _pendingFocusTarget ?? _lastFocusTarget;
        _pendingFocusTarget = null;
        FrameworkElement element = target switch
        {
            "Input" => InputTextBox,
            "Search" => SearchTextBox,
            "Items" => ItemsList,
            _ => Root
        };
        element.Focus(FocusState.Programmatic);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Quick Capture action failed id={WidgetId}: {ex}");
            RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Error, "quick-action-error");
        }
    }

    private void RaiseFeedback(
        string message,
        WidgetFeedbackSeverity severity,
        string deduplicationKey)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey)));
    }

    private string T(string key) =>
        _localizationService.T(key);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= OnLoaded;
        ViewModel.Dispose();
    }
}

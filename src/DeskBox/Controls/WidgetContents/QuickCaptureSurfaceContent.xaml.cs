using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

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
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetAddActionContent,
    IDisposable
{
    private const string MasterPaneWidthMetadataKey = "QuickCaptureMasterPaneWidth";
    private const int DetailAutoSaveDelayMs = 600;
    private const double CompactDetailHeaderWidth = 300;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService _settingsService;
    private readonly MasterDetailLayoutPolicy _masterDetailLayoutPolicy = new();
    private readonly MarkdownDocumentService _markdownDocumentService = new();
    private readonly List<DroppedFilePath> _pendingDetailAttachments = [];
    private readonly SemaphoreSlim _detailSaveGate = new(1, 1);
    private DispatcherQueueTimer? _detailAutoSaveTimer;
    private string _lastFocusTarget = "Root";
    private string? _pendingFocusTarget;
    private string? _pendingDetailItemId;
    private bool _pendingDetailEditing;
    private string? _pendingDetailDraft;
    private bool _pendingDetailWasVisibleInSinglePane;
    private QuickCaptureItemViewModel[] _pendingPointerDragItems = [];
    private readonly List<string> _draggedQuickCaptureItemIds = [];
    private bool _isInternalQuickCaptureDrag;
    private bool _isDualPane;
    private bool _showDetailInSinglePane;
    private bool _isDetailEditing;
    private bool _isCreatingDetail;
    private bool _suppressDetailEditorChanges;
    private bool _detailHasUnsavedChanges;
    private bool _isSavingDetail;
    private long _detailEditRevision;
    private long _detailSavedRevision;
    private bool _isSynchronizingViewSelection;
    private long _viewSwitchRevision;
    private QuickCaptureItemViewModel? _detailItem;
    private QuickCaptureAppearancePreset _detailAppearance;
    private TextContentFormat _detailContentFormat = TextContentFormat.Markdown;
    private double? _persistedMasterPaneWidth;
    private double _hostViewportWidth = double.NaN;
    private EventHandler<object>? _segmentedRestoreHandler;
    private int _segmentedStableFrames;
    private double _segmentedCandidateWidth;
    private bool _isResponsiveLayoutTransitionActive;
    private bool _isDisposed;
    private bool _isInitialized;

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
        _settingsService = settingsService;
        ViewModel = new QuickCaptureWidgetViewModel(
            config,
            quickCaptureService,
            settingsService,
            localizationService,
            dispatcherQueue);

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            string details = string.Join(
                ", ",
                ex.GetType().GetProperties()
                    .Where(property => property.GetIndexParameters().Length == 0)
                    .Select(property =>
                    {
                        try
                        {
                            return $"{property.Name}={property.GetValue(ex)}";
                        }
                        catch
                        {
                            return $"{property.Name}=<unavailable>";
                        }
                    }));
            App.Log($"[QuickCaptureSurface] XAML initialization failed: {details}");
            throw;
        }
        ResponsiveContentGrid.DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (config.Metadata.TryGetValue(MasterPaneWidthMetadataKey, out string? persisted) &&
            double.TryParse(persisted, NumberStyles.Float, CultureInfo.InvariantCulture, out double width))
        {
            _persistedMasterPaneWidth = _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(width);
        }
        DetailMarkdownEditor.TextResolver = localizationService.T;
        DetailMarkdownEditor.EditorTextChanged += DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.CommitRequested += DetailMarkdownEditor_CommitRequested;
        DetailMarkdownView.AttachmentResolver = ResolveDetailAttachmentPath;
        DetailMarkdownView.AttachmentOpenRequested += DetailMarkdownView_AttachmentOpenRequested;
        DetailBodyReaderSurface.AddHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler(DetailBodyReaderSurface_DoubleTapped),
            handledEventsToo: true);
        _detailAutoSaveTimer = dispatcherQueue.CreateTimer();
        _detailAutoSaveTimer.Interval = TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs);
        _detailAutoSaveTimer.IsRepeating = false;
        _detailAutoSaveTimer.Tick += DetailAutoSaveTimer_Tick;
        Loaded += OnLoaded;
        ActualThemeChanged += QuickCaptureSurfaceContent_ActualThemeChanged;
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
        _isInitialized = true;
        UpdateSelectedViewVisual();
        ApplyResponsiveLayout();
        if (!RestorePendingDetailState())
        {
            ReconcileDetailSelection();
        }
    }

    public Task RefreshAsync() => ViewModel.RefreshItemsAsync();

    public async Task AddFromTitleButtonAsync()
    {
        await OpenNewDetailAsync();
    }

    internal async Task RevealItemAsync(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        ViewModel.CollapseSearch();
        ViewModel.SelectedView = QuickCaptureViewMode.Records;
        await ViewModel.RefreshItemsAsync();
        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        await OpenDetailAfterSavingAsync(item);
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        UpdateSelectedViewVisual();
        ApplySegmentedStyle();
        ApplyResponsiveLayout();
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            ApplyPendingFocus();
        }
    }

    public async void OnDeactivated()
    {
        _lastFocusTarget = GetCurrentFocusTarget();
        await FlushPendingDetailSaveAsync();
    }

    public async void OnWindowVisibilityChanged(bool visible)
    {
        if (visible && IsLoaded)
        {
            ViewModel.RefreshAfterViewReady();
        }
        else if (!visible)
        {
            await FlushPendingDetailSaveAsync();
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
        bool shouldCaptureDetail =
            QuickCaptureDetailRestorePolicy.ShouldCaptureDetail(
                _isDualPane,
                _showDetailInSinglePane,
                _detailItem is not null);
        return new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            _lastFocusTarget,
            shouldCaptureDetail ? _detailItem?.Id : null,
            shouldCaptureDetail && _isDetailEditing,
            shouldCaptureDetail && _isDetailEditing
                ? DetailMarkdownEditor.Text
                : null,
            shouldCaptureDetail && !_isDualPane && _showDetailInSinglePane);
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
            _pendingDetailItemId = quickState.SelectedDetailItemId;
            _pendingDetailEditing = quickState.IsDetailEditing;
            _pendingDetailDraft = quickState.DetailDraft;
            _pendingDetailWasVisibleInSinglePane =
                quickState.WasDetailVisibleInSinglePane;
            UpdateSelectedViewVisual();
            if (IsLoaded && _isInitialized)
            {
                RestorePendingDetailState();
                DispatcherQueue.TryEnqueue(ApplyPendingFocus);
            }
        }
    }

    private bool RestorePendingDetailState()
    {
        if (string.IsNullOrWhiteSpace(_pendingDetailItemId))
        {
            _pendingDetailWasVisibleInSinglePane = false;
            return false;
        }

        if (!QuickCaptureDetailRestorePolicy.ShouldRestoreDetail(
                _isDualPane,
                _pendingDetailWasVisibleInSinglePane))
        {
            _pendingDetailItemId = null;
            _pendingDetailEditing = false;
            _pendingDetailDraft = null;
            _pendingDetailWasVisibleInSinglePane = false;
            return false;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _pendingDetailItemId, StringComparison.Ordinal));
        _pendingDetailItemId = null;
        _pendingDetailWasVisibleInSinglePane = false;
        if (item is null)
        {
            _pendingDetailEditing = false;
            _pendingDetailDraft = null;
            return false;
        }

        OpenDetail(item);
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        if (_pendingDetailEditing && !item.IsRecent)
        {
            BeginDetailEditing();
            if (_pendingDetailDraft is { } draft &&
                !string.Equals(draft, item.Body, StringComparison.Ordinal))
            {
                SetDetailEditorText(draft);
                MarkDetailDirty();
                _detailAutoSaveTimer?.Start();
            }
            RefreshDetailPresentation();
        }

        _pendingDetailEditing = false;
        _pendingDetailDraft = null;
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshAfterViewReady();
        UpdateSelectedViewVisual();
        ApplyPendingFocus();
        ApplyResponsiveLayout();
        RefreshItemMaterialSurfaces();
        QueueSegmentedRestore();
    }

    private void ResponsiveContentGrid_SizeChanged(
        object sender,
        SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void DetailHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool useTwoRows = e.NewSize.Width < CompactDetailHeaderWidth;
        Grid.SetRow(DetailHeaderActions, useTwoRows ? 1 : 0);
        Grid.SetColumn(DetailHeaderActions, useTwoRows ? 0 : 2);
        Grid.SetColumnSpan(DetailHeaderActions, useTwoRows ? 3 : 1);
        DetailHeaderActions.Margin = useTwoRows
            ? new Thickness(0, 2, 0, 0)
            : new Thickness(0);
        DetailHeaderLayoutGrid.RowSpacing = useTwoRows ? 2 : 0;
        DetailHeader.MinHeight = useTwoRows ? 66 : 36;
    }

    private void ApplyResponsiveLayout()
    {
        if (ResponsiveContentGrid is null ||
            MasterColumn is null ||
            SplitterColumn is null ||
            DetailColumn is null ||
            ListPage is null ||
            DetailPage is null ||
            PaneSplitter is null)
        {
            return;
        }

        double layoutWidth = double.IsFinite(_hostViewportWidth) &&
                             _hostViewportWidth > 0
            ? _hostViewportWidth
            : ResponsiveContentGrid.ActualWidth;
        double availableWidth = Math.Max(
            0,
            layoutWidth -
            ResponsiveContentGrid.Padding.Left -
            ResponsiveContentGrid.Padding.Right);
        string preference = SettingsService.NormalizeQuickCaptureWideLayout(
            _settingsService.Settings.QuickCaptureWideLayout);
        bool forceSinglePane =
            preference == SettingsService.QuickCaptureWideLayoutSinglePane;
        bool forceDualPane =
            preference == SettingsService.QuickCaptureWideLayoutDualPane;
        MasterDetailLayoutSnapshot layout = _masterDetailLayoutPolicy.Resolve(
            availableWidth,
            _isDualPane,
            _persistedMasterPaneWidth,
            forceSinglePane,
            forceDualPane);
        bool enteredDualPane = !_isDualPane && layout.IsDualPane;
        _isDualPane = layout.IsDualPane;

        if (_isDualPane)
        {
            // Keep the grid itself shrinkable. Pixel minimums on the columns
            // become part of the control's desired size and can leave the
            // surface measuring at the normal 588 epx dual-pane width after
            // its host has already become narrower. The policy below still
            // protects both panes at normal widths and proportionally
            // compresses them when DualPane is explicitly selected.
            MasterColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            MasterColumn.Width = new GridLength(layout.MasterWidth);
            SplitterColumn.Width = new GridLength(layout.SplitterWidth);
            DetailColumn.Width = new GridLength(layout.DetailWidth);
            ListPage.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
            DetailPage.Visibility = Visibility.Visible;
            DetailBackButton.Visibility = Visibility.Collapsed;
            DetailBackColumn.Width = new GridLength(8);
            _showDetailInSinglePane = false;
            if (enteredDualPane)
            {
                ReconcileDetailSelection();
            }
        }
        else
        {
            MasterColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            SplitterColumn.Width = new GridLength(0);
            PaneSplitter.Visibility = Visibility.Collapsed;
            if (_showDetailInSinglePane)
            {
                MasterColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ListPage.Visibility = Visibility.Collapsed;
                DetailPage.Visibility = Visibility.Visible;
            }
            else
            {
                MasterColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailColumn.Width = new GridLength(0);
                ListPage.Visibility = Visibility.Visible;
                DetailPage.Visibility = Visibility.Collapsed;
            }
            DetailBackButton.Visibility = Visibility.Visible;
            DetailBackColumn.Width = new GridLength(30);
        }

        RefreshDetailPresentation();
        SynchronizeSegmentedVisibility();
    }

    private void ReconcileDetailSelection()
    {
        if (!_isDualPane)
        {
            return;
        }

        if (_detailItem is not null)
        {
            QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(
                item => item.Id == _detailItem.Id);
            if (refreshed is not null)
            {
                if (_isDetailEditing || _detailHasUnsavedChanges)
                {
                    _detailItem = refreshed;
                    foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
                    {
                        candidate.IsDetailSelected = candidate.Id == refreshed.Id;
                    }
                    RefreshDetailPresentation();
                    return;
                }
                OpenDetail(refreshed);
                return;
            }
        }

        if (ViewModel.Items.FirstOrDefault() is { } first)
        {
            OpenDetail(first);
        }
        else
        {
            _detailItem = null;
            RefreshDetailPresentation();
        }
    }

    private void PaneSplitter_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        PersistMasterPaneWidth();
        ApplyResponsiveLayout();
    }

    private void PaneSplitter_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        _persistedMasterPaneWidth = _masterDetailLayoutPolicy.Options.DefaultMasterWidth;
        PersistMasterPaneWidth();
        ApplyResponsiveLayout();
        e.Handled = true;
    }

    private void PersistMasterPaneWidth()
    {
        if (!_isDualPane || !double.IsFinite(MasterColumn.ActualWidth))
        {
            return;
        }

        double masterWidth = MasterColumn.ActualWidth;
        double minimumDualWidth =
            _masterDetailLayoutPolicy.Options.MinimumMasterWidth +
            _masterDetailLayoutPolicy.Options.SplitterWidth +
            _masterDetailLayoutPolicy.Options.MinimumDetailWidth;
        double layoutWidth = double.IsFinite(_hostViewportWidth) &&
                             _hostViewportWidth > 0
            ? _hostViewportWidth
            : ResponsiveContentGrid.ActualWidth;
        double availableWidth = Math.Max(
            0,
            layoutWidth -
            ResponsiveContentGrid.Padding.Left -
            ResponsiveContentGrid.Padding.Right);
        string preference = SettingsService.NormalizeQuickCaptureWideLayout(
            _settingsService.Settings.QuickCaptureWideLayout);
        if (preference == SettingsService.QuickCaptureWideLayoutDualPane &&
            availableWidth < minimumDualWidth)
        {
            double combinedPaneWidth =
                MasterColumn.ActualWidth + DetailColumn.ActualWidth;
            if (combinedPaneWidth > 0)
            {
                double masterRatio = Math.Clamp(
                    MasterColumn.ActualWidth / combinedPaneWidth,
                    0.01,
                    0.99);
                masterWidth = _masterDetailLayoutPolicy.Options.MinimumDetailWidth *
                              masterRatio /
                              (1 - masterRatio);
            }
        }

        _persistedMasterPaneWidth =
            _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(masterWidth);
        Config.Metadata[MasterPaneWidthMetadataKey] =
            _persistedMasterPaneWidth.Value.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        _settingsService.SaveDebounced();
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _isResponsiveLayoutTransitionActive = true;
        CancelSegmentedRestore();

        // Expansion reveals the live body while the HWND grows. Match the
        // final expanded layout before the first animation frame, just like
        // Search, Music, Weather and Todo, so a capsule-width master/detail
        // surface is never stretched through the intermediate bounds.
        if (!isCollapsing &&
            double.IsFinite(targetContentWidth) &&
            targetContentWidth > 0)
        {
            _hostViewportWidth = targetContentWidth;
            Width = targetContentWidth;
            ApplyResponsiveLayout();
            PrepareSegmentedForExpansion(targetContentWidth);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.TabBarVisibility))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                SynchronizeSegmentedVisibility();
            }
            else
            {
                DispatcherQueue.TryEnqueue(SynchronizeSegmentedVisibility);
            }
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.EditorContentFormat))
        {
            if (_isDetailEditing && _detailItem?.IsRecent != true)
            {
                _detailContentFormat = ViewModel.EditorContentFormat;
                RefreshDetailPresentation();
            }
            return;
        }

        if (e.PropertyName != nameof(QuickCaptureWidgetViewModel.ItemsViewTransitionToken))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ReconcileDetailSelection();
            DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                ReconcileDetailSelection();
                RefreshItemMaterialSurfaces();
            }
        });
    }

    public void OnHostViewportSizeChanged(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        _hostViewportWidth = width;
        Width = width;
        ApplyResponsiveLayout();
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        if (double.IsFinite(finalContentWidth) && finalContentWidth > 0)
        {
            _hostViewportWidth = finalContentWidth;
            Width = finalContentWidth;
        }

        ApplyResponsiveLayout();
        QueueSegmentedRestore();
    }

    public void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        ApplyResponsiveLayout();
        QueueSegmentedRestore();
    }

    private void SuspendSegmented()
    {
        CancelSegmentedRestore();
        if (QuickCaptureViewSegmented is not null)
        {
            QuickCaptureViewSegmented.Visibility = Visibility.Collapsed;
        }
    }

    private void PrepareSegmentedForExpansion(double targetContentWidth)
    {
        if (QuickCaptureViewSegmented is null ||
            ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible ||
            !double.IsFinite(targetContentWidth) ||
            targetContentWidth < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            return;
        }

        // WidgetShell freezes the content presenter at the final expanded
        // width before starting the HWND animation. Realize the Segmented tree
        // against that safe slot now, so the tabs are already present when the
        // compact layer begins to fade instead of appearing after three more
        // stable frames. The initial-load fallback below still protects any
        // genuinely zero-width layout pass.
        CancelSegmentedRestore();
        QuickCaptureViewSegmented.Visibility = Visibility.Visible;
        ApplySegmentedStyle();
        UpdateSelectedViewVisual();
    }

    private void SynchronizeSegmentedVisibility()
    {
        if (_isDisposed || QuickCaptureViewSegmented is null)
        {
            return;
        }

        if (ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible)
        {
            SuspendSegmented();
            return;
        }

        QueueSegmentedRestore();
    }

    private void QueueSegmentedRestore()
    {
        if (!IsLoaded ||
            _isResponsiveLayoutTransitionActive ||
            QuickCaptureViewSegmented is null ||
            ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible ||
            QuickCaptureViewSegmented.Visibility == Visibility.Visible ||
            _segmentedRestoreHandler is not null)
        {
            return;
        }

        _segmentedStableFrames = 0;
        _segmentedCandidateWidth = 0;
        _segmentedRestoreHandler = SegmentedRestore_Rendering;
        CompositionTarget.Rendering += _segmentedRestoreHandler;
    }

    private void SegmentedRestore_Rendering(object? sender, object e)
    {
        if (ViewModel.TabBarVisibility != Visibility.Visible ||
            ListPage.Visibility != Visibility.Visible)
        {
            SuspendSegmented();
            return;
        }

        if (_isResponsiveLayoutTransitionActive)
        {
            _segmentedStableFrames = 0;
            return;
        }

        double width = Math.Min(ListPage.ActualWidth, ResponsiveContentGrid.ActualWidth);
        if (!double.IsFinite(width) ||
            width < WidgetSegmentedLayoutHelper.MinimumSafeWidth)
        {
            _segmentedStableFrames = 0;
            _segmentedCandidateWidth = width;
            return;
        }

        if (Math.Abs(width - _segmentedCandidateWidth) > 0.5)
        {
            _segmentedCandidateWidth = width;
            _segmentedStableFrames = 1;
            return;
        }

        if (++_segmentedStableFrames < 3)
        {
            return;
        }

        CancelSegmentedRestore();
        QuickCaptureViewSegmented.Visibility = Visibility.Visible;
        ApplySegmentedStyle();
        UpdateSelectedViewVisual();
    }

    private void CancelSegmentedRestore()
    {
        if (_segmentedRestoreHandler is not null)
        {
            CompositionTarget.Rendering -= _segmentedRestoreHandler;
            _segmentedRestoreHandler = null;
        }
        _segmentedStableFrames = 0;
        _segmentedCandidateWidth = 0;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(ViewModel.AddInputAsync);
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        bool controlPressed = Win32Helper.IsKeyPressed(
            Windows.System.VirtualKey.Control);
        e.Handled = true;
        if (SettingsService.ShouldSubmitEditorOnEnter(
                _settingsService.Settings.QuickCaptureEditorEnterBehavior,
                controlPressed))
        {
            await RunAsync(ViewModel.AddInputAsync);
            return;
        }

        TextBoxEditorShortcutHelper.InsertLineBreak(InputTextBox);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.SearchText = string.Empty;
            ResponsiveContentGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private async void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out QuickCaptureViewMode mode))
        {
            return;
        }

        await SwitchViewAsync(mode);
    }

    private async void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        await OpenDetailAfterSavingAsync(item);
        await Task.CompletedTask;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        DispatcherQueue.TryEnqueue(() => SearchTextBox.Focus(FocusState.Programmatic));
    }

    private async void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenNewDetailAsync();
    }

    private async Task OpenNewDetailAsync()
    {
        await FlushPendingDetailSaveAsync();
        _detailAutoSaveTimer?.Stop();
        _isCreatingDetail = true;
        _detailItem = null;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _detailContentFormat = ViewModel.EditorContentFormat;
        _pendingDetailAttachments.Clear();
        _isDetailEditing = true;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = false;
        _showDetailInSinglePane = !_isDualPane;
        SetDetailEditorText(string.Empty);
        DetailMarkdownEditor.ShowFormattingToolbar =
            _detailContentFormat == TextContentFormat.Markdown;
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
        DispatcherQueue.TryEnqueue(() =>
            DetailMarkdownEditor.FocusEditor(moveCaretToEnd: false));
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        _detailAutoSaveTimer?.Stop();
        _isCreatingDetail = false;
        _detailItem = item;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments.Clear();
        _isDetailEditing = !item.IsRecent &&
            (!_isDualPane || SettingsService.NormalizeQuickCaptureWideOpenMode(
                _settingsService.Settings.QuickCaptureWideOpenMode) ==
                SettingsService.QuickCaptureWideOpenEditing);
        _detailContentFormat = _isDetailEditing
            ? ViewModel.EditorContentFormat
            : item.ContentFormat;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = false;
        _showDetailInSinglePane = !_isDualPane;
        SetDetailEditorText(item.Body);
        DetailMarkdownEditor.ShowFormattingToolbar =
            _detailContentFormat == TextContentFormat.Markdown;
        DetailMarkdownView.Markdown = item.Body;
        DetailMarkdownView.ContentFormat = _detailContentFormat;
        DetailMarkdownView.AllowRemoteImages =
            _settingsService.Settings.QuickCaptureAllowRemoteImages;
        DetailMarkdownView.AreTaskListsInteractive =
            !item.IsRecent && _detailContentFormat == TextContentFormat.Markdown;
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            item.ToModel().CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));
        DetailPinIcon.Glyph = item.PinGlyph;
        RefreshDetailAttachments();
        ApplyDetailMaterialSurface();
        foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
        {
            candidate.IsDetailSelected = candidate.Id == item.Id;
        }
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private async Task OpenDetailAfterSavingAsync(QuickCaptureItemViewModel item)
    {
        if (_detailItem is not null &&
            string.Equals(_detailItem.Id, item.Id, StringComparison.Ordinal))
        {
            if (!_isDualPane)
            {
                _showDetailInSinglePane = true;
                ApplyResponsiveLayout();
            }
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }

        OpenDetail(item);
    }

    private void RefreshDetailPresentation()
    {
        bool hasDetail = _isCreatingDetail || _detailItem is not null;
        bool isReadOnly = _detailItem?.IsRecent == true;
        DetailEmptyState.Visibility = _isDualPane &&
                                      !hasDetail &&
                                      ViewModel.Items.Count > 0 &&
                                      !ViewModel.IsSwitchingView
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailHeader.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailContent.Visibility = hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailEditButton.Visibility = hasDetail && !_isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDoneButton.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailPinButton.Visibility = hasDetail && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailCopyButton.Visibility = _detailItem is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDeleteButton.Visibility = _detailItem is not null && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailAddFileButton.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMarkdownEditor.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMarkdownView.Visibility = hasDetail && (!_isDetailEditing || isReadOnly)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMaterialPalette.Visibility = hasDetail && _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasDetail)
        {
            DetailMarkdownView.Markdown = DetailMarkdownEditor.Text;
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailMarkdownView.AllowRemoteImages =
                _settingsService.Settings.QuickCaptureAllowRemoteImages;
            DetailMarkdownView.AreTaskListsInteractive =
                !isReadOnly && _detailContentFormat == TextContentFormat.Markdown;
            DetailMarkdownEditor.ShowFormattingToolbar =
                _detailContentFormat == TextContentFormat.Markdown;
            DispatcherQueue.TryEnqueue(DetailMarkdownView.Refresh);
        }
    }

    private void DetailEditButton_Click(object sender, RoutedEventArgs e)
    {
        BeginDetailEditing();
    }

    private void DetailBodyReaderSurface_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (BeginDetailEditing())
        {
            e.Handled = true;
        }
    }

    private bool BeginDetailEditing()
    {
        if (_isDetailEditing ||
            _detailItem?.IsRecent == true ||
            (!_isCreatingDetail && _detailItem is null))
        {
            return false;
        }

        _detailContentFormat = ViewModel.EditorContentFormat;
        _isDetailEditing = true;
        RefreshDetailPresentation();
        DispatcherQueue.TryEnqueue(() =>
            DetailMarkdownEditor.FocusEditor(moveCaretToEnd: false));
        return true;
    }

    private async void DetailDoneButton_Click(object sender, RoutedEventArgs e) =>
        await SaveDetailAsync(completeEditing: true);

    private async void DetailMarkdownEditor_CommitRequested(object? sender, EventArgs e) =>
        await SaveDetailAsync(completeEditing: true);

    private void DetailMarkdownEditor_EditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressDetailEditorChanges ||
            !_isDetailEditing ||
            _detailItem?.IsRecent == true)
        {
            return;
        }

        MarkDetailDirty();
        _detailAutoSaveTimer?.Stop();
        _detailAutoSaveTimer?.Start();
    }

    private void MarkDetailDirty()
    {
        _detailEditRevision++;
        _detailHasUnsavedChanges = _detailEditRevision != _detailSavedRevision;
    }

    private async void DetailAutoSaveTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_detailHasUnsavedChanges)
        {
            await SaveDetailAsync(completeEditing: false);
        }
    }

    private async Task FlushPendingDetailSaveAsync()
    {
        _detailAutoSaveTimer?.Stop();
        if (_detailHasUnsavedChanges || _isSavingDetail)
        {
            await SaveDetailAsync(completeEditing: false);
        }
    }

    private async Task<bool> SaveDetailAsync(bool completeEditing)
    {
        _detailAutoSaveTimer?.Stop();
        await _detailSaveGate.WaitAsync();
        try
        {
            _isSavingDetail = true;
            bool saved;
            do
            {
                saved = await SaveDetailCoreAsync();
                if (!saved)
                {
                    return false;
                }
            }
            while (completeEditing && _detailHasUnsavedChanges);

            if (completeEditing)
            {
                _isDetailEditing = false;
            }

            RefreshDetailAfterSave();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Detail save failed id={WidgetId}: {ex}");
            RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Error, "quick-detail-save-error");
            return false;
        }
        finally
        {
            _isSavingDetail = false;
            _detailSaveGate.Release();
            if (_detailHasUnsavedChanges && _isDetailEditing)
            {
                _detailAutoSaveTimer?.Start();
            }
        }
    }

    private async Task<bool> SaveDetailCoreAsync()
    {
        long revisionAtStart = _detailEditRevision;
        string body = DetailMarkdownEditor.Text;
        if (_detailItem?.IsRecent == true)
        {
            _detailSavedRevision = revisionAtStart;
            _detailHasUnsavedChanges = false;
            return true;
        }

        if (_isCreatingDetail)
        {
            QuickCaptureItem? created = null;
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? attached =
                    await ViewModel.AddItemWithAttachmentsAsync(_pendingDetailAttachments);
                if (attached is null)
                {
                    return false;
                }

                if (!await ViewModel.EditItemDetailsAsync(
                        attached,
                        null,
                        body,
                        _detailAppearance,
                        _detailContentFormat))
                {
                    return false;
                }

                created = attached.ToModel();
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                created = await ViewModel.AddDetailedItemAsync(
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
            }

            if (created is not null)
            {
                await ViewModel.RefreshItemsAsync();
                _detailItem = ViewModel.Items.FirstOrDefault(item => item.Id == created.Id);
                _isCreatingDetail = false;
                _pendingDetailAttachments.Clear();
            }

            _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
            _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
            return true;
        }

        if (_detailItem is not { IsRecent: false } item)
        {
            return !_detailHasUnsavedChanges;
        }

        bool detailsChanged =
            _detailHasUnsavedChanges ||
            _detailAppearance != item.AppearancePreset ||
            _detailContentFormat != item.ContentFormat;
        if (detailsChanged)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                RaiseFeedback(
                    T("QuickCapture.EmptyEdit"),
                    WidgetFeedbackSeverity.Warning,
                    "quick-detail-empty");
                return false;
            }

            if (!await ViewModel.EditItemDetailsAsync(
                    item,
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat))
            {
                return false;
            }

            await ViewModel.RefreshItemsAsync();
            _detailItem = ViewModel.Items.FirstOrDefault(entry => entry.Id == item.Id);
        }

        _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
        _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
        return true;
    }

    private void RefreshDetailAfterSave()
    {
        if (_detailItem is { } refreshed)
        {
            DetailMarkdownView.Markdown = DetailMarkdownEditor.Text;
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailPinIcon.Glyph = refreshed.PinGlyph;
            foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
            {
                candidate.IsDetailSelected = candidate.Id == refreshed.Id;
            }
            RefreshDetailAttachments();
            ApplyDetailMaterialSurface();
            RefreshDetailPresentation();
        }
        else if (!_isCreatingDetail)
        {
            _showDetailInSinglePane = false;
            ApplyResponsiveLayout();
            RefreshDetailPresentation();
        }
    }

    private void SetDetailEditorText(string value)
    {
        _suppressDetailEditorChanges = true;
        try
        {
            DetailMarkdownEditor.Text = value ?? string.Empty;
        }
        finally
        {
            _suppressDetailEditorChanges = false;
        }
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }
        _isCreatingDetail = false;
        _isDetailEditing = false;
        _showDetailInSinglePane = false;
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private async void DetailPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { IsRecent: false } item)
        {
            await FlushPendingDetailSaveAsync();
            await RunAsync(() => ViewModel.TogglePinnedAsync(item));
            await ViewModel.RefreshItemsAsync();
            QuickCaptureItemViewModel? refreshed =
                ViewModel.Items.FirstOrDefault(entry => entry.Id == item.Id);
            if (refreshed is not null)
            {
                _detailItem = refreshed;
                DetailPinIcon.Glyph = refreshed.PinGlyph;
                foreach (QuickCaptureItemViewModel candidate in ViewModel.Items)
                {
                    candidate.IsDetailSelected = candidate.Id == refreshed.Id;
                }
                RefreshDetailPresentation();
            }
        }
    }

    private async void DetailCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is { } item)
        {
            await RunAsync(() => ViewModel.CopyItemAsync(item));
        }
    }

    private void DetailMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, out QuickCaptureAppearancePreset preset))
        {
            _detailAppearance = preset;
            ApplyDetailMaterialSurface();
            MarkDetailDirty();
            RefreshItemMaterialSurfaces();
            _detailAutoSaveTimer?.Stop();
            _detailAutoSaveTimer?.Start();
        }
    }

    private void ApplyDetailMaterialSurface()
    {
        if (DetailMaterialSurface is null)
        {
            return;
        }

        DetailMaterialSurface.Background = ResolveMaterialBrush(_detailAppearance);
        foreach (Button button in GetDetailMaterialButtons())
        {
            bool selected = string.Equals(
                button.Tag as string,
                _detailAppearance.ToString(),
                StringComparison.Ordinal);
            button.BorderBrush = selected
                ? new SolidColorBrush(
                    App.Current.ThemeService?.GetEffectiveAccentColor() ??
                    AccentColorHelper.DefaultAccentColor)
                : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(selected ? 1.5 : 1);
        }
    }

    private IEnumerable<Button> GetDetailMaterialButtons()
    {
        yield return DefaultMaterialButton;
        yield return PaperMaterialButton;
        yield return YellowMaterialButton;
        yield return RoseMaterialButton;
        yield return MintMaterialButton;
        yield return BlueMaterialButton;
    }

    private async void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not { IsRecent: false } item)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("QuickCapture.DeleteConfirm.Title"),
            Content = item.DisplayText,
            PrimaryButtonText = T("Common.Delete"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunAsync(() => ViewModel.DeleteItemAsync(item));
        _detailItem = null;
        _isDetailEditing = false;
        _showDetailInSinglePane = false;
        await ViewModel.RefreshItemsAsync();
        ApplyResponsiveLayout();
        ReconcileDetailSelection();
    }

    private async void DetailMarkdownView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (_detailItem?.IsRecent == true ||
            _detailContentFormat != TextContentFormat.Markdown ||
            !_markdownDocumentService.TryToggleTask(
                DetailMarkdownEditor.Text,
                e.TaskIndex,
                out string updated))
        {
            return;
        }

        SetDetailEditorText(updated);
        MarkDetailDirty();
        DetailMarkdownView.Markdown = updated;
        await SaveDetailAsync(completeEditing: false);
    }

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        try
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
            DroppedFilePath[] paths = files
                .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
                .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            if (_isCreatingDetail || _detailItem is null)
            {
                foreach (DroppedFilePath path in paths)
                {
                    if (!_pendingDetailAttachments.Any(existing =>
                            string.Equals(existing.Path, path.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pendingDetailAttachments.Add(path);
                    }
                }
                MarkDetailDirty();
                _detailAutoSaveTimer?.Stop();
                _detailAutoSaveTimer?.Start();
            }
            else
            {
                _detailItem = await ViewModel.AddAttachmentsAsync(_detailItem, paths) ??
                    _detailItem;
            }

            RefreshDetailAttachments();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Add attachment failed: {ex}");
            RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Error, "quick-attachment-error");
        }
    }

    private async void DetailOpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            !File.Exists(attachment.FilePath))
        {
            return;
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true ||
            sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            int removed = _pendingDetailAttachments.RemoveAll(file =>
                string.Equals(file.Path, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshDetailAttachments();
            if (removed > 0)
            {
                MarkDetailDirty();
                _detailAutoSaveTimer?.Stop();
                _detailAutoSaveTimer?.Start();
            }
            return;
        }

        if (_detailItem.Attachments.Count == 1 &&
            string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text))
        {
            RaiseFeedback(
                T("QuickCapture.EmptyEdit"),
                WidgetFeedbackSeverity.Warning,
                "quick-detail-empty");
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.DeleteAttachmentAsync(
            _detailItem,
            attachment.Id);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachments();
            ApplyDetailMaterialSurface();
        }
    }

    private void RefreshDetailAttachments()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments =
            _detailItem?.Attachments ??
            _pendingDetailAttachments
                .Select(file => new TodoAttachmentViewModel(new TodoAttachment
                {
                    FilePath = file.Path,
                    DisplayName = file.DisplayName,
                    Type = AttachmentStorageService.GetAttachmentType(file.Path)
                }))
                .ToArray();
        DetailAttachmentsList.ItemsSource = attachments;
        DetailAttachmentScroller.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string? ResolveDetailAttachmentPath(string attachmentId) =>
        _detailItem?.Attachments.FirstOrDefault(attachment =>
            string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))?.FilePath;

    private async void DetailMarkdownView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        string? path = ResolveDetailAttachmentPath(e.AttachmentId);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await RunAsync(() => item.IsRecent
                ? ViewModel.PinRecentItemAsync(item)
                : ViewModel.TogglePinnedAsync(item));
        }
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCaptureItemViewModel item })
        {
            await RunAsync(() => ViewModel.CopyItemAsync(item));
        }
    }

    private void QuickCaptureItem_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (sender is not Border
            {
                DataContext: QuickCaptureItemViewModel item
            } anchor)
        {
            return;
        }

        ItemsList.SelectedItem = item;
        MenuFlyout flyout = CreateItemContextFlyout(item);
        flyout.ShowAt(
            anchor,
            new FlyoutShowOptions
            {
                Position = e.GetPosition(anchor),
                ShowMode = FlyoutShowMode.Standard
            });
        e.Handled = true;
    }

    private MenuFlyout CreateItemContextFlyout(QuickCaptureItemViewModel item)
    {
        var flyout = new MenuFlyout();

        if (!item.IsRecent)
        {
            var editItem = CreateContextMenuItem("QuickCapture.Edit", "\uE70F");
            editItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await OpenDetailAfterSavingAsync(item);
                if (_detailItem is not null)
                {
                    BeginDetailEditing();
                }
            };
            flyout.Items.Add(editItem);

            var pinItem = new MenuFlyoutItem
            {
                Text = T(item.IsPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin"),
                Icon = new FontIcon { Glyph = item.IsPinned ? "\uE840" : "\uE718" }
            };
            pinItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.TogglePinnedAsync(item);
            };
            flyout.Items.Add(pinItem);
        }

        var copyItem = CreateContextMenuItem("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(() => ViewModel.CopyItemAsync(item));
        };
        flyout.Items.Add(copyItem);

        if (item.IsRecent)
        {
            var saveItem = CreateContextMenuItem("QuickCapture.SaveToRecords", "\uE74E");
            saveItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.SaveRecentItemAsync(item);
            };
            flyout.Items.Add(saveItem);
        }
        else
        {
            flyout.Items.Add(CreateAppearanceContextSubmenu(item, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var deleteItem = CreateContextMenuItem("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ConfirmDeleteItemAsync(item);
        };
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyoutSubItem CreateAppearanceContextSubmenu(
        QuickCaptureItemViewModel item,
        MenuFlyout owner)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        foreach ((QuickCaptureAppearancePreset preset, string textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var appearanceItem = new ToggleMenuFlyoutItem
            {
                Text = T(textKey),
                IsChecked = item.AppearancePreset == preset
            };
            appearanceItem.Click += async (_, _) =>
            {
                owner.Hide();
                if (!await ViewModel.SetAppearanceAsync(item, preset))
                {
                    return;
                }

                await ViewModel.RefreshItemsAsync();
                QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(
                    candidate => candidate.Id == item.Id);
                if (_detailItem?.Id == item.Id && refreshed is not null)
                {
                    _detailItem = refreshed;
                    _detailAppearance = preset;
                    ApplyDetailMaterialSurface();
                }
            };
            submenu.Items.Add(appearanceItem);
        }

        return submenu;
    }

    private MenuFlyoutItem CreateContextMenuItem(string textKey, string glyph) =>
        new()
        {
            Text = T(textKey),
            Icon = new FontIcon { Glyph = glyph }
        };

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickCaptureItemViewModel item })
        {
            return;
        }

        await ConfirmDeleteItemAsync(item);
    }

    private async Task ConfirmDeleteItemAsync(QuickCaptureItemViewModel item)
    {
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
            await RunAsync(() => ViewModel.DeleteItemAsync(item));
            if (_detailItem?.Id == item.Id)
            {
                _detailItem = null;
                _isDetailEditing = false;
                _detailHasUnsavedChanges = false;
                ReconcileDetailSelection();
            }
        }
    }

    private void QuickCaptureItem_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: QuickCaptureItemViewModel item
            } ||
            !e.GetCurrentPoint(ItemsList).Properties.IsLeftButtonPressed ||
            !ItemsList.SelectedItems.Contains(item))
        {
            _pendingPointerDragItems = [];
            return;
        }

        _pendingPointerDragItems = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
    }

    private void QuickCaptureItem_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _pendingPointerDragItems = [];
    }

    private void ItemsList_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel[] eventItems = e.Items
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
            _pendingPointerDragItems.Length > 1
                ? _pendingPointerDragItems
                : ItemsList.SelectedItems
                    .OfType<QuickCaptureItemViewModel>()
                    .ToArray();
        _pendingPointerDragItems = [];
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            QuickCaptureDragPackage.ResolveDraggedItems(
                eventItems,
                selectedItems);
        if (!QuickCaptureDragPackage.TryPrepare(
                e.Data,
                draggedItems,
                _localizationService))
        {
            e.Cancel = true;
            ResetInternalQuickCaptureDrag();
            return;
        }

        _draggedQuickCaptureItemIds.Clear();
        _draggedQuickCaptureItemIds.AddRange(
            draggedItems.Select(item => item.Id));
        _isInternalQuickCaptureDrag = true;
        e.Data.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move;
    }

    private void ItemsList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        _draggedQuickCaptureItemIds.Clear();
        DispatcherQueue.TryEnqueue(() => _isInternalQuickCaptureDrag = false);
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(GetDraggedQuickCaptureItems(), target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            GetDraggedQuickCaptureItems();
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(draggedItems, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            int changedCount = await ViewModel.ApplyTabDropAsync(
                draggedItems,
                target);
            e.AcceptedOperation = changedCount > 0
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
            if (changedCount > 0)
            {
                ViewModel.SelectedView = target;
                ItemsList.SelectedItems.Clear();
                UpdateSelectedViewVisual();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetDraggedQuickCaptureItems()
    {
        HashSet<string> draggedIds = _draggedQuickCaptureItemIds.ToHashSet(
            StringComparer.Ordinal);
        return ViewModel.Items
            .Where(item => draggedIds.Contains(item.Id))
            .ToList();
    }

    private static bool TryGetQuickCaptureTabTarget(
        string tag,
        out QuickCaptureViewMode target)
    {
        target = tag switch
        {
            "Pinned" => QuickCaptureViewMode.Pinned,
            "Records" => QuickCaptureViewMode.Records,
            _ => QuickCaptureViewMode.Recent
        };
        return target != QuickCaptureViewMode.Recent;
    }

    private void ResetInternalQuickCaptureDrag()
    {
        _draggedQuickCaptureItemIds.Clear();
        _isInternalQuickCaptureDrag = false;
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (controlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            ItemsList.SelectAll();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectedQuickCaptureItemsAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
                GetSelectedQuickCaptureItemsInVisibleOrder();
            if (selectedItems.Count > 0)
            {
                e.Handled = true;
                await DeleteSelectedQuickCaptureItemsAsync(selectedItems);
            }
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ItemsList.SelectedItems.Count > 0)
        {
            ItemsList.SelectedItems.Clear();
            e.Handled = true;
        }
    }

    private IReadOnlyList<QuickCaptureItemViewModel>
        GetSelectedQuickCaptureItemsInVisibleOrder()
    {
        HashSet<QuickCaptureItemViewModel> selectedItems = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .ToHashSet();
        return ViewModel.Items
            .Where(selectedItems.Contains)
            .ToList();
    }

    private Task CopySelectedQuickCaptureItemsAsync()
    {
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
            GetSelectedQuickCaptureItemsInVisibleOrder();
        if (selectedItems.Count == 0)
        {
            return Task.CompletedTask;
        }

        string text = selectedItems.Count == 1
            ? QuickCaptureClipboardFormatter.FormatSingle(
                selectedItems[0],
                _localizationService)
            : QuickCaptureClipboardFormatter.FormatBatch(
                selectedItems,
                _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
        RaiseFeedback(
            _localizationService.Format(
                "QuickCapture.CopiedCount",
                selectedItems.Count),
            WidgetFeedbackSeverity.Success,
            "quick-copy-selected");
        return Task.CompletedTask;
    }

    private async Task DeleteSelectedQuickCaptureItemsAsync(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localizationService.Format(
                "QuickCapture.DeleteSelectedConfirm.Title",
                selectedItems.Count),
            PrimaryButtonText = T("Common.Delete"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<QuickCaptureDeletedItemSnapshot> deletedItems =
            await ViewModel.DeleteItemsAsync(
                selectedItems.Select(item => item.Id),
                selectedItems.All(item => item.IsRecent));
        ItemsList.SelectedItems.Clear();
        if (deletedItems.Count > 0)
        {
            RaiseFeedback(
                _localizationService.Format(
                    "QuickCapture.DeletedCount",
                    deletedItems.Count),
                WidgetFeedbackSeverity.Success,
                "quick-delete-selected");
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

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
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

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
        if (_isInternalQuickCaptureDrag ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView) ||
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
            ? new SolidColorBrush(
                App.Current.ThemeService?.GetEffectiveAccentColor() ??
                AccentColorHelper.DefaultAccentColor)
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
        if (!IsLoaded || QuickCaptureViewSegmented is null)
        {
            return;
        }

        int selectedIndex = ViewModel.SelectedView switch
        {
            QuickCaptureViewMode.Pinned => 1,
            QuickCaptureViewMode.Recent => 2,
            _ => 0
        };
        if (QuickCaptureViewSegmented.SelectedIndex != selectedIndex)
        {
            _isSynchronizingViewSelection = true;
            QuickCaptureViewSegmented.SelectedIndex = selectedIndex;
            _isSynchronizingViewSelection = false;
        }
    }

    private void QuickCaptureItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        SetQuickCaptureItemPinButtonVisible(border, false);
        ApplyQuickCaptureItemMaterialSurface(
            border,
            border.DataContext as QuickCaptureItemViewModel);
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetQuickCaptureItemPinButtonVisible(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetQuickCaptureItemPinButtonVisible(sender as DependencyObject, false);
    }

    private static void SetQuickCaptureItemPinButtonVisible(
        DependencyObject? itemRoot,
        bool isVisible)
    {
        if (itemRoot is null ||
            FindQuickCaptureVisualChild<Button>(itemRoot, "QuickCapturePinItemButton") is not { } button)
        {
            return;
        }

        button.Opacity = isVisible ? 1 : 0;
        button.IsHitTestVisible = isVisible;
    }

    private static T? FindQuickCaptureVisualChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
            {
                return typed;
            }

            if (FindQuickCaptureVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void QuickCaptureItem_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is Border border)
        {
            SetQuickCaptureItemPinButtonVisible(border, false);
            // ListView virtualizes and reuses this Border. Reapply the
            // material for every new item so clipboard entries cannot inherit
            // a colored record background from the previous DataContext.
            ApplyQuickCaptureItemMaterialSurface(
                border,
                args.NewValue as QuickCaptureItemViewModel);
        }
    }

    private void QuickCaptureSurfaceContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        if (_isDisposed)
        {
            return;
        }

        ApplyDetailMaterialSurface();
        RefreshItemMaterialSurfaces();
    }

    private void RefreshItemMaterialSurfaces()
    {
        if (_isDisposed || ItemsList is null)
        {
            return;
        }

        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsList.ContainerFromItem(item) is ListViewItem
                {
                    ContentTemplateRoot: Border border
                })
            {
                ApplyQuickCaptureItemMaterialSurface(border, item);
            }
        }
    }

    private void ApplyQuickCaptureItemMaterialSurface(
        Border border,
        QuickCaptureItemViewModel? item)
    {
        if (item is null)
        {
            border.Background = ResolveMaterialBrush(
                QuickCaptureAppearancePreset.Default);
            return;
        }

        QuickCaptureAppearancePreset requestedPreset =
            _detailHasUnsavedChanges &&
            string.Equals(_detailItem?.Id, item.Id, StringComparison.Ordinal)
                ? _detailAppearance
                : item.AppearancePreset;
        QuickCaptureAppearancePreset preset =
            QuickCaptureAppearancePolicy.ResolveListPreset(
                requestedPreset,
                item.IsRecent);
        border.Background = ResolveMaterialBrush(preset);
    }

    private Brush ResolveMaterialBrush(QuickCaptureAppearancePreset preset)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        return preset switch
        {
            QuickCaptureAppearancePreset.Paper => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x3A, 0x36, 0x30) : Color.FromArgb(0xEC, 0xFA, 0xF5, 0xEA)),
            QuickCaptureAppearancePreset.StickyYellow => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x4A, 0x40, 0x25) : Color.FromArgb(0xEC, 0xFF, 0xF0, 0xB3)),
            QuickCaptureAppearancePreset.Rose => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x47, 0x2E, 0x38) : Color.FromArgb(0xEC, 0xFC, 0xE3, 0xEA)),
            QuickCaptureAppearancePreset.Mint => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x28, 0x42, 0x35) : Color.FromArgb(0xEC, 0xDD, 0xF3, 0xE3)),
            QuickCaptureAppearancePreset.MistBlue => new SolidColorBrush(
                dark ? Color.FromArgb(0xB8, 0x2B, 0x3D, 0x53) : Color.FromArgb(0xEC, 0xDF, 0xEC, 0xF8)),
            _ => ResolveBrush(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
        };
    }

    private async void QuickCaptureViewSegmented_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingViewSelection || !IsLoaded || ItemsList is null)
        {
            return;
        }

        QuickCaptureViewMode mode = QuickCaptureViewSegmented.SelectedIndex switch
        {
            1 => QuickCaptureViewMode.Pinned,
            2 => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };

        await SwitchViewAsync(mode);
    }

    private async Task SwitchViewAsync(QuickCaptureViewMode mode)
    {
        long revision = ++_viewSwitchRevision;
        if (ViewModel.SelectedView == mode)
        {
            UpdateSelectedViewVisual();
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_isDisposed || revision != _viewSwitchRevision)
        {
            return;
        }

        if (_detailHasUnsavedChanges)
        {
            UpdateSelectedViewVisual();
            return;
        }

        ClearDetailForViewChange();
        ViewModel.SelectedView = mode;
        ItemsList.SelectedItems.Clear();
        RefreshDetailPresentation();
        UpdateSelectedViewVisual();
    }

    private void ClearDetailForViewChange()
    {
        _detailAutoSaveTimer?.Stop();
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            item.IsDetailSelected = false;
        }

        _detailItem = null;
        _isCreatingDetail = false;
        _isDetailEditing = false;
        _detailHasUnsavedChanges = false;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _showDetailInSinglePane = false;
        _pendingDetailAttachments.Clear();
        SetDetailEditorText(string.Empty);
        DetailMarkdownView.Markdown = string.Empty;
        RefreshDetailAttachments();
        ApplyResponsiveLayout();
        RefreshDetailPresentation();
    }

    private void QuickCaptureViewSegmented_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplySegmentedStyle();
        }
    }

    private void ApplySegmentedStyle()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(
            QuickCaptureViewSegmented,
            ViewModel.TabStyle);
        if (ViewModel.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(QuickCaptureViewSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(QuickCaptureViewSegmented);
        }
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
            _ => ResponsiveContentGrid
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
        CancelSegmentedRestore();
        Loaded -= OnLoaded;
        ActualThemeChanged -= QuickCaptureSurfaceContent_ActualThemeChanged;
        if (_detailAutoSaveTimer is not null)
        {
            _detailAutoSaveTimer.Stop();
            _detailAutoSaveTimer.Tick -= DetailAutoSaveTimer_Tick;
            _detailAutoSaveTimer = null;
        }
        DetailMarkdownEditor.EditorTextChanged -= DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.CommitRequested -= DetailMarkdownEditor_CommitRequested;
        DetailMarkdownView.AttachmentOpenRequested -= DetailMarkdownView_AttachmentOpenRequested;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }
}

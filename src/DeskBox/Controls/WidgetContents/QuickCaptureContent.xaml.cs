using System.Collections.Specialized;
using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Shared responsive Quick Capture surface used by standalone and grouped
/// widgets. The host owns window chrome; this control owns all note state,
/// navigation, editing, recovery, and responsive presentation.
/// </summary>
public sealed partial class QuickCaptureContent :
    UserControl,
    IWidgetContent,
    IWidgetResponsiveLayoutContent,
    IWidgetTransientStateContent,
    IWidgetFeedbackSource,
    IWidgetAddActionContent,
    IWidgetCommandMenuProvider,
    IDisposable
{
    private const double ExtremeNarrowBreakpoint = 264;
    private const double ComfortableBreakpoint = 560;
    private const double DualPaneEnterBreakpoint = 720;
    private const double DualPaneExitBreakpoint = 680;
    private const double SplitPreviewBreakpoint = 1040;
    private const double DefaultListPaneWidth = 252;
    private const double LegacyDefaultListPaneWidth = 284;
    private const double MinListPaneWidth = 220;
    private const double MaxListPaneWidth = 420;
    private const double MinDetailPaneWidth = 320;
    private const double PaneSplitterGutterWidth = 20;
    private const string LayoutMetadataKey = "quickCapture.layout";
    private const string ListWidthMetadataKey = "quickCapture.listPaneWidth";
    private const string ListWidthVersionMetadataKey = "quickCapture.listPaneWidthVersion";
    private const string CurrentListWidthVersion = "2";
    private const string SplitPreviewMetadataKey = "quickCapture.splitPreview";
    private const string FocusModeMetadataKey = "quickCapture.focusMode";

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly QuickCaptureMarkdownService _markdownService = new();
    private readonly DispatcherQueueTimer _autoSaveTimer;
    private readonly DispatcherQueueTimer _draftTimer;
    private readonly DispatcherQueueTimer _previewTimer;
    private readonly bool _ownsViewModel;
    private readonly List<DroppedFilePath> _pendingAttachments = [];

    private QuickCaptureItemViewModel? _selectedItem;
    private QuickCaptureWidgetTransientState? _pendingTransientState;
    private QuickCaptureLayoutOverride _layoutOverride;
    private QuickCaptureContentFormat _editingFormat = QuickCaptureContentFormat.Markdown;
    private QuickCaptureAppearancePreset _editingAppearance = QuickCaptureAppearancePreset.Default;
    private bool _isDualPane;
    private bool _isEditing;
    private bool _isCreating;
    private bool _showDetailInSinglePane;
    private bool _splitPreviewEnabled;
    private bool _editorPreviewOnly;
    private bool _focusMode;
    private bool _hasUnsavedChanges;
    private bool _suppressEditorChanges;
    private bool _isSaving;
    private bool _revisionCaptured;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _animateNextItemsRefresh;
    private bool _isBulkSelectionMode;
    private bool _suppressSegmentSelectionChange;
    private bool _hasLayoutOverride;
    private bool _hasSplitPreviewOverride;
    private Task? _initializationTask;
    private double _listPaneWidth = DefaultListPaneWidth;
    private double? _frozenLayoutWidth;
    private string _lastFocusTarget = "Root";
    private int _selectionRestoreGeneration;

    public QuickCaptureContent(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
        : this(
            new QuickCaptureWidgetViewModel(
                config,
                quickCaptureService,
                settingsService,
                localizationService,
                dispatcherQueue),
            settingsService,
            localizationService,
            ownsViewModel: true)
    {
    }

    internal QuickCaptureContent(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        bool ownsViewModel = false)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _ownsViewModel = ownsViewModel;

        InitializeComponent();
        Root.DataContext = ViewModel;
        LoadPresentationOverrides();

        _autoSaveTimer = DispatcherQueue.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        _draftTimer = DispatcherQueue.CreateTimer();
        _draftTimer.Interval = TimeSpan.FromSeconds(2);
        _draftTimer.IsRepeating = true;
        _draftTimer.Tick += DraftTimer_Tick;

        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(250);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += PreviewTimer_Tick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnQuickCaptureActualThemeChanged;
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        _settingsService.SettingsChanged += OnQuickCaptureSettingsChanged;
        UpdateSectionVisuals();
        ApplyNoteAppearance(QuickCaptureAppearancePreset.Default);
        UpdateNoteCommandAvailability();
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.QuickCapture;

    public FrameworkElement View => this;

    public Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        return _initializationTask ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        await ViewModel.InitializeAsync();
        ViewModel.ConfigureRevisionRetention(
            _settingsService.Settings.QuickCaptureRevisionRetentionDays,
            _settingsService.Settings.QuickCaptureRevisionLimitPerNote);
        if (_settingsService.Settings.QuickCaptureTrashEnabled)
        {
            await ViewModel.PurgeExpiredTrashAsync(
                _settingsService.Settings.QuickCaptureTrashRetentionDays);
        }
        _isInitialized = true;
        ApplyListPresentationSettings();
        RestorePendingTransientState();
        ApplyResponsiveLayout();
        if (_isDualPane && _selectedItem is null && ViewModel.Items.FirstOrDefault() is { } first)
        {
            await OpenItemAsync(first, edit: false);
        }
    }

    public Task RefreshAsync() => ViewModel.RefreshItemsAsync();

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        ApplyNoteAppearance(_isCreating || _isEditing
            ? _editingAppearance
            : _selectedItem?.AppearancePreset ?? QuickCaptureAppearancePreset.Default);
        ApplyResponsiveLayout();
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            ApplyFocusTarget(_lastFocusTarget);
        }
    }

    public void OnDeactivated()
    {
        _lastFocusTarget = GetFocusTarget();
        _ = ForceCommitAsync(returnToReading: false);
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (visible)
        {
            ViewModel.RefreshAfterViewReady();
        }
        else
        {
            _ = ForceCommitAsync(returnToReading: false);
        }
    }

    public Task AddFromTitleButtonAsync()
    {
        BeginNewNote();
        return Task.CompletedTask;
    }

    internal void FocusInputForNewNote()
    {
        BeginNewNote();
    }

    internal async Task RevealItemAsync(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        ViewModel.CollapseSearch();
        ViewModel.SelectedView = QuickCaptureViewMode.Records;
        await ViewModel.RefreshItemsAsync();
        UpdateSectionVisuals();

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        await OpenItemAsync(item, edit: ShouldOpenExistingInEditMode());
        ItemsList.ScrollIntoView(item);
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _frozenLayoutWidth = isCollapsing ? ActualWidth : targetContentWidth;
        ApplyResponsiveLayout(_frozenLayoutWidth.Value);
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _frozenLayoutWidth = null;
        ApplyResponsiveLayout(finalContentWidth);
    }

    public void CancelResponsiveLayoutTransition()
    {
        _frozenLayoutWidth = null;
        ApplyResponsiveLayout();
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        return new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            GetFocusTarget(),
            _selectedItem?.Id,
            _showDetailInSinglePane,
            _isEditing,
            EditorBodyTextBox.SelectionStart,
            _listPaneWidth,
            GetListScrollOffset(),
            ReadingScrollViewer.VerticalOffset,
            _layoutOverride.ToString());
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (state is not QuickCaptureWidgetTransientState quickState)
        {
            return;
        }

        _pendingTransientState = quickState;
        ViewModel.InputText = quickState.InputText ?? string.Empty;
        ViewModel.SearchText = quickState.SearchText ?? string.Empty;
        ViewModel.SelectedView = quickState.SelectedView;
        _lastFocusTarget = quickState.FocusTarget;
        _listPaneWidth = NormalizeListPaneWidth(quickState.ListPaneWidth);
        if (Enum.TryParse(quickState.LayoutOverride, true, out QuickCaptureLayoutOverride layout))
        {
            _layoutOverride = layout;
        }

        if (_isInitialized)
        {
            RestorePendingTransientState();
        }
    }

    public void RestoreTransientState(string? inputText, string? searchText)
    {
        ViewModel.InputText = inputText ?? string.Empty;
        ViewModel.SearchText = searchText ?? string.Empty;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
        ApplyResponsiveLayout();
        ViewModel.RefreshAfterViewReady();
        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        RestorePendingTransientState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ = ForceCommitAsync(returnToReading: false);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_frozenLayoutWidth is null)
        {
            ApplyResponsiveLayout(e.NewSize.Width);
        }
    }

    private void ApplyResponsiveLayout(double? explicitWidth = null)
    {
        double width = Math.Max(0, explicitWidth ?? _frozenLayoutWidth ?? ActualWidth);
        if (width <= 0)
        {
            return;
        }

        bool canSafelyShowDual = width >= DualPaneExitBreakpoint;
        bool dual = _layoutOverride switch
        {
            QuickCaptureLayoutOverride.Single => false,
            QuickCaptureLayoutOverride.Dual => canSafelyShowDual,
            _ => _isDualPane
                ? width >= DualPaneExitBreakpoint
                : width >= DualPaneEnterBreakpoint
        };
        _isDualPane = dual;

        bool showList = !_focusMode && (dual || !_showDetailInSinglePane);
        bool showDetail = _focusMode || dual || _showDetailInSinglePane;
        ListPane.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        PaneSplitter.Visibility = dual && !_focusMode ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;

        if (dual && !_focusMode)
        {
            double maximumListPaneWidth = GetMaximumListPaneWidth(width);
            ListColumn.MinWidth = MinListPaneWidth;
            ListColumn.MaxWidth = maximumListPaneWidth;
            ListColumn.Width = new GridLength(Math.Min(
                NormalizeListPaneWidth(_listPaneWidth),
                maximumListPaneWidth));
            DividerColumn.Width = new GridLength(PaneSplitterGutterWidth);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else if (showDetail)
        {
            ListColumn.MinWidth = 0;
            ListColumn.MaxWidth = double.PositiveInfinity;
            ListColumn.Width = new GridLength(0);
            DividerColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ListColumn.MinWidth = 0;
            ListColumn.MaxWidth = double.PositiveInfinity;
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
        }

        UpdateNavigationVisibility(width);
        UpdateFormattingToolbarLayout(width);
        BackButton.Visibility = dual && !_focusMode ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewLayout(width);
        UpdateLayoutMenuChecks();
    }

    private void UpdatePreviewLayout(double width)
    {
        bool showSplit = _isEditing && _splitPreviewEnabled && width >= SplitPreviewBreakpoint;
        bool showPreviewOnly = _isEditing && _editorPreviewOnly && !showSplit;
        SourceColumn.Width = showPreviewOnly
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        PreviewColumn.Width = showSplit || showPreviewOnly
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        EditorBodyTextBox.Visibility = showPreviewOnly ? Visibility.Collapsed : Visibility.Visible;
        EditorPreviewPane.Visibility = showSplit || showPreviewOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        FormattingToolbarHost.Visibility = _isEditing && !showPreviewOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditorPreviewModeButton.IsEnabled = !showSplit;
        SplitPreviewMenuItem.IsEnabled = width >= SplitPreviewBreakpoint;
        if (showSplit || showPreviewOnly)
        {
            RefreshMarkdownPreview();
        }
    }

    private void UpdateFormattingToolbarLayout(double width)
    {
        bool showExtendedCommands = width >= ExtremeNarrowBreakpoint;
        Visibility extendedVisibility = showExtendedCommands
            ? Visibility.Visible
            : Visibility.Collapsed;
        FormattingItalicButton.Visibility = extendedVisibility;
        FormattingHeadingButton.Visibility = extendedVisibility;
        FormattingLinkButton.Visibility = extendedVisibility;
    }

    private void EditorPreviewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _editorPreviewOnly = EditorPreviewModeButton.IsChecked == true;
        UpdatePreviewLayout(ActualWidth);
        if (!_editorPreviewOnly)
        {
            EditorBodyTextBox.Focus(FocusState.Programmatic);
            if (_lastEditorViewport is EditorViewportSnapshot viewport)
            {
                int start = Math.Clamp(viewport.SelectionStart, 0, EditorBodyTextBox.Text.Length);
                int length = Math.Clamp(
                    viewport.SelectionLength,
                    0,
                    EditorBodyTextBox.Text.Length - start);
                EditorBodyTextBox.Select(start, length);
                FindDescendant<ScrollViewer>(EditorBodyTextBox)?.ChangeView(
                    viewport.HorizontalOffset,
                    viewport.VerticalOffset,
                    null,
                    disableAnimation: true);
            }
        }
    }

    private void LoadPresentationOverrides()
    {
        if (Config.Metadata.TryGetValue(LayoutMetadataKey, out string? layout) &&
            Enum.TryParse(layout, true, out QuickCaptureLayoutOverride parsedLayout))
        {
            _layoutOverride = parsedLayout;
            _hasLayoutOverride = true;
        }
        else if (Enum.TryParse(
            _settingsService.Settings.QuickCaptureDefaultLayout,
            true,
            out QuickCaptureLayoutOverride defaultLayout))
        {
            _layoutOverride = defaultLayout;
        }

        if (Config.Metadata.TryGetValue(ListWidthMetadataKey, out string? widthText) &&
            double.TryParse(widthText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double width))
        {
            bool isCurrentWidth = Config.Metadata.TryGetValue(
                ListWidthVersionMetadataKey,
                out string? widthVersion) &&
                string.Equals(widthVersion, CurrentListWidthVersion, StringComparison.Ordinal);
            if (!isCurrentWidth && Math.Abs(width - LegacyDefaultListPaneWidth) < 0.5)
            {
                width = DefaultListPaneWidth;
            }
            _listPaneWidth = NormalizeListPaneWidth(width);
            if (!isCurrentWidth)
            {
                Config.Metadata[ListWidthMetadataKey] = _listPaneWidth.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                Config.Metadata[ListWidthVersionMetadataKey] = CurrentListWidthVersion;
                _settingsService.UpdateWidget(Config);
                _settingsService.SaveDebounced();
            }
        }

        _hasSplitPreviewOverride = Config.Metadata.TryGetValue(SplitPreviewMetadataKey, out string? split) &&
            bool.TryParse(split, out bool splitValue);
        _splitPreviewEnabled = _hasSplitPreviewOverride
            ? bool.Parse(split!)
            : _settingsService.Settings.QuickCaptureWideEditorView ==
                SettingsService.QuickCaptureWideEditorSplit;
        _focusMode = Config.Metadata.TryGetValue(FocusModeMetadataKey, out string? focus) &&
            bool.TryParse(focus, out bool focusValue) && focusValue;
    }

    private void PersistPresentationOverrides()
    {
        Config.Metadata[LayoutMetadataKey] = _layoutOverride.ToString();
        Config.Metadata[ListWidthMetadataKey] = _listPaneWidth.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Config.Metadata[ListWidthVersionMetadataKey] = CurrentListWidthVersion;
        Config.Metadata[SplitPreviewMetadataKey] = _splitPreviewEnabled.ToString();
        Config.Metadata[FocusModeMetadataKey] = _focusMode.ToString();
        _settingsService.UpdateWidget(Config);
        _settingsService.SaveDebounced();
    }

    private void RestorePendingTransientState()
    {
        if (_pendingTransientState is not { } state || !_isInitialized)
        {
            return;
        }

        _pendingTransientState = null;
        int generation = ++_selectionRestoreGeneration;
        _showDetailInSinglePane = state.IsDetailVisible;
        ApplyResponsiveLayout();
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (generation != _selectionRestoreGeneration)
            {
                return;
            }

            QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, state.SelectedItemId, StringComparison.Ordinal));
            if (item is not null)
            {
                await OpenItemAsync(item, state.IsEditing);
                EditorBodyTextBox.SelectionStart = Math.Clamp(
                    state.CaretIndex,
                    0,
                    EditorBodyTextBox.Text.Length);
                ReadingScrollViewer.ChangeView(null, state.DetailScrollOffset, null, true);
            }

            SetListScrollOffset(state.ListScrollOffset);
            ApplyFocusTarget(state.FocusTarget);
        });
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplyListPresentationSettings();
        if (_animateNextItemsRefresh)
        {
            _animateNextItemsRefresh = false;
            PlaySurfaceFadeIn(ItemsList);
        }
        if (_selectedItem is null)
        {
            return;
        }

        QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedItem.Id, StringComparison.Ordinal));
        if (refreshed is null)
        {
            return;
        }

        _selectedItem = refreshed;
        ItemsList.SelectedItem = refreshed;
        if (!_isEditing)
        {
            RenderReadingSurface();
        }
    }

    private async void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_isBulkSelectionMode || IsControlPressed() || IsShiftPressed() ||
            e.ClickedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        await OpenItemAsync(item, edit: ShouldOpenExistingInEditMode());
    }

    private async void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateItemSelectionVisuals();
        int selectedCount = ItemsList.SelectedItems.Count;
        if (selectedCount > 1 && !_isBulkSelectionMode)
        {
            SetBulkSelectionMode(enable: true, preserveSelection: true);
        }
        UpdateBulkSelectionState();
        if (_isBulkSelectionMode || selectedCount != 1 ||
            ItemsList.SelectedItem is not QuickCaptureItemViewModel item ||
            ReferenceEquals(item, _selectedItem))
        {
            return;
        }

        await OpenItemAsync(item, edit: ShouldOpenExistingInEditMode());
    }

    private async void QuickCaptureItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuickCaptureItemViewModel item })
        {
            e.Handled = true;
            await OpenItemAsync(item, edit: true);
        }
    }

    private async Task OpenItemAsync(QuickCaptureItemViewModel item, bool edit)
    {
        // Clipboard history is a read-only source. It can be copied, deleted,
        // or promoted to a real note, but it must never enter the note editor.
        edit = edit && !item.IsRecent;
        if (_isEditing && (_selectedItem?.Id != item.Id || !edit))
        {
            await ForceCommitAsync(returnToReading: false);
        }

        _isBulkSelectionMode = false;
        BulkCommandBar.Visibility = Visibility.Collapsed;
        _selectedItem = item;
        _isCreating = false;
        _showDetailInSinglePane = true;
        ItemsList.SelectedItem = item;
        RenderReadingSurface();
        ShowReadingSurface();
        ApplyResponsiveLayout();
        if (!_isDualPane)
        {
            QueueDetailEnterTransition();
        }
        if (edit)
        {
            await EnterEditModeAsync();
        }
    }

    private void RenderReadingSurface()
    {
        if (_selectedItem is not { } item)
        {
            NoSelectionPanel.Visibility = Visibility.Visible;
            ReadingSurface.Visibility = Visibility.Collapsed;
            return;
        }

        NoSelectionPanel.Visibility = Visibility.Collapsed;
        bool hasExplicitTitle = !string.IsNullOrWhiteSpace(item.Title);
        ReadingTitle.Text = hasExplicitTitle ? item.Title!.Trim() : string.Empty;
        ReadingTitle.Visibility = hasExplicitTitle
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReadingUpdatedAt.Text = item.UpdatedAtText;
        PinButton.Label = item.IsRecent
            ? "保存并置顶"
            : item.IsPinned ? "取消置顶" : "置顶";
        _editingAppearance = item.AppearancePreset;
        ApplyNoteAppearance(item.AppearancePreset);
        ReadingMarkdownView.SetContent(
            item.Body,
            item.ContentFormat,
            item.Attachments.Select(attachment => attachment.Attachment).ToArray(),
            allowRemoteImages: _settingsService.Settings.QuickCaptureAllowRemoteImages);
        ReadingMarkdownView.AreTaskListsInteractive = !item.IsRecent;
        ReadingAttachmentsList.ItemsSource = item.Attachments;
        ReadingTagsList.ItemsSource = item.Tags;
    }

    private void ShowReadingSurface()
    {
        bool wasEditing = _isEditing;
        _isEditing = false;
        _editorPreviewOnly = false;
        EditorPreviewModeButton.IsChecked = false;
        EditorSurface.Visibility = Visibility.Collapsed;
        ReadingSurface.Visibility = _selectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionPanel.Visibility = _selectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Visibility = _selectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        DoneEditingButton.Visibility = Visibility.Collapsed;
        FormattingToolbarHost.Visibility = Visibility.Collapsed;
        ApplyNoteAppearance(_selectedItem?.AppearancePreset ?? QuickCaptureAppearancePreset.Default);
        UpdateNoteCommandAvailability();
        UpdatePreviewLayout(ActualWidth);
        if (wasEditing && ReadingSurface.Visibility == Visibility.Visible)
        {
            PlaySurfaceFadeIn(ReadingSurface);
        }
    }

    private void BeginNewNote()
    {
        _autoSaveTimer.Stop();
        _draftTimer.Stop();
        _previewTimer.Stop();
        _selectedItem = null;
        _isBulkSelectionMode = false;
        BulkCommandBar.Visibility = Visibility.Collapsed;
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItem = null;
        _isCreating = true;
        _isEditing = true;
        _editorPreviewOnly = false;
        EditorPreviewModeButton.IsChecked = false;
        _showDetailInSinglePane = true;
        _editingFormat = QuickCaptureContentFormat.Markdown;
        if (_settingsService.Settings.QuickCaptureDefaultFormat ==
            SettingsService.QuickCaptureFormatPlainText)
        {
            _editingFormat = QuickCaptureContentFormat.PlainText;
        }
        _editingAppearance = QuickCaptureAppearancePreset.Default;
        ApplyNoteAppearance(_editingAppearance);
        _pendingAttachments.Clear();
        _hasUnsavedChanges = false;
        _revisionCaptured = true;
        _suppressEditorChanges = true;
        EditorTitleTextBox.Text = string.Empty;
        EditorBodyTextBox.Text = string.Empty;
        _suppressEditorChanges = false;
        NoSelectionPanel.Visibility = Visibility.Collapsed;
        ReadingSurface.Visibility = Visibility.Collapsed;
        EditorSurface.Visibility = Visibility.Visible;
        EditButton.Visibility = Visibility.Collapsed;
        DoneEditingButton.Visibility = Visibility.Visible;
        FormattingToolbarHost.Visibility = Visibility.Visible;
        UpdateNoteCommandAvailability();
        ApplyResponsiveLayout();
        PlaySurfaceFadeIn(EditorSurface);
        if (!_isDualPane)
        {
            QueueDetailEnterTransition();
        }
        EditorBodyTextBox.Focus(FocusState.Programmatic);
    }

    private async void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        await ForceCommitAsync(returnToReading: false);
        BeginNewNote();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await ForceCommitAsync(returnToReading: true);
        if (!_isDualPane && DetailPane.Visibility == Visibility.Visible)
        {
            DetailPane.IsHitTestVisible = false;
            await DetailPageTransitionHelper.PlayExitAsync(DetailPane);
        }
        _showDetailInSinglePane = false;
        ApplyResponsiveLayout();
        DetailPageTransitionHelper.Reset(DetailPane);
        DetailPane.IsHitTestVisible = true;
        ItemsList.Focus(FocusState.Programmatic);
    }

    private void CompactSectionButton_Click(object sender, RoutedEventArgs e)
    {
        CompactSectionButton.Flyout?.ShowAt(CompactSectionButton);
    }

    private void CompactSectionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, true, out QuickCaptureViewMode mode))
        {
            SwitchSection(mode);
        }
    }

    private void SwitchSection(QuickCaptureViewMode mode)
    {
        if (ViewModel.SelectedView == mode)
        {
            return;
        }

        _ = ForceCommitAsync(returnToReading: false);
        _animateNextItemsRefresh = true;
        ViewModel.SelectedView = mode;
        _showDetailInSinglePane = false;
        _selectedItem = null;
        _isBulkSelectionMode = false;
        BulkCommandBar.Visibility = Visibility.Collapsed;
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItem = null;
        ShowReadingSurface();
        UpdateSectionVisuals();
        ApplyResponsiveLayout();
    }

    private void UpdateSectionVisuals()
    {
        if (QuickCaptureViewSegmented is not null)
        {
            _suppressSegmentSelectionChange = true;
            QuickCaptureViewSegmented.SelectedIndex = ViewModel.SelectedView switch
            {
                QuickCaptureViewMode.Pinned => 1,
                QuickCaptureViewMode.Recent => 2,
                _ => 0
            };
            _suppressSegmentSelectionChange = false;
        }
        CompactSectionButton.Content = ViewModel.SelectedView switch
        {
            QuickCaptureViewMode.Pinned => "置顶",
            QuickCaptureViewMode.Recent => "剪贴板",
            _ => "随记"
        };
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        UpdateNavigationVisibility(ActualWidth);
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollapseSearch();
        UpdateNavigationVisibility(ActualWidth);
        ItemsList.Focus(FocusState.Programmatic);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseSearchButton_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key is (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down) &&
            ItemsList.Items.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
            ItemsList.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void QuickCaptureViewSegmented_Loaded(object sender, RoutedEventArgs e) =>
        ApplySegmentedStyle();

    private void QuickCaptureViewSegmented_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplySegmentedLayout();

    private void QuickCaptureViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSegmentSelectionChange)
        {
            return;
        }

        QuickCaptureViewMode mode = QuickCaptureViewSegmented.SelectedIndex switch
        {
            1 => QuickCaptureViewMode.Pinned,
            2 => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };
        SwitchSection(mode);
    }

    private void ApplySegmentedStyle()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(
            QuickCaptureViewSegmented,
            _settingsService.Settings.QuickCaptureTabStyle);
        ApplySegmentedLayout();
        UpdateSectionVisuals();
    }

    private void ApplySegmentedLayout()
    {
        if (_settingsService.Settings.QuickCaptureTabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(QuickCaptureViewSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(QuickCaptureViewSegmented);
        }
    }

    private void UpdateNavigationVisibility(double width)
    {
        bool navigationVisible = _settingsService.Settings.QuickCaptureShowTabBar &&
            !ViewModel.IsSearchExpanded;
        bool extremeNarrow = width > 0 && width < ExtremeNarrowBreakpoint;
        QuickCaptureViewSegmented.Visibility = navigationVisible && !extremeNarrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactSectionButton.Visibility = navigationVisible && extremeNarrow
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SetBulkSelectionMode(!_isBulkSelectionMode);
    }

    private void SetBulkSelectionMode(bool enable, bool preserveSelection = false)
    {
        _isBulkSelectionMode = enable;
        if (!enable)
        {
            ItemsList.SelectedItems.Clear();
            if (_selectedItem is not null)
            {
                ItemsList.SelectedItem = _selectedItem;
            }
        }
        else if (!preserveSelection && _selectedItem is not null &&
            !ItemsList.SelectedItems.Contains(_selectedItem))
        {
            ItemsList.SelectedItems.Add(_selectedItem);
        }

        BulkCommandBar.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;
        UpdateBulkSelectionState();
        UpdateItemSelectionVisuals();
    }

    private void ExitBulkSelectionButton_Click(object sender, RoutedEventArgs e) =>
        SetBulkSelectionMode(enable: false);

    private void UpdateBulkSelectionState()
    {
        int count = ItemsList.SelectedItems.Count;
        BulkSelectionCountText.Text = $"已选择 {count} 项";
    }

    private void UpdateItemSelectionVisuals()
    {
        var selectedIds = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (ItemsList.SelectedItem is QuickCaptureItemViewModel single)
        {
            selectedIds.Add(single.Id);
        }

        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            item.IsListSelected = selectedIds.Contains(item.Id);
        }
    }

    private void QueueDetailEnterTransition()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDualPane && DetailPane.Visibility == Visibility.Visible)
            {
                DetailPageTransitionHelper.PlayEnter(DetailPane);
            }
        });
    }

    private static void PlaySurfaceFadeIn(UIElement surface) =>
        DetailPageTransitionHelper.PlayFadeIn(surface);

    private void LayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, true, out QuickCaptureLayoutOverride layout))
        {
            _layoutOverride = layout;
            _hasLayoutOverride = true;
            PersistPresentationOverrides();
            ApplyResponsiveLayout();
        }
    }

    private void SplitPreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _splitPreviewEnabled = SplitPreviewMenuItem.IsChecked;
        _hasSplitPreviewOverride = true;
        PersistPresentationOverrides();
        ApplyResponsiveLayout();
    }

    private void FocusModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _focusMode = FocusModeMenuItem.IsChecked;
        if (_focusMode && _selectedItem is null && !_isCreating)
        {
            BeginNewNote();
        }

        PersistPresentationOverrides();
        ApplyResponsiveLayout();
    }

    private void UpdateLayoutMenuChecks()
    {
        AutoLayoutMenuItem.IsChecked = _layoutOverride == QuickCaptureLayoutOverride.Auto;
        SingleLayoutMenuItem.IsChecked = _layoutOverride == QuickCaptureLayoutOverride.Single;
        DualLayoutMenuItem.IsChecked = _layoutOverride == QuickCaptureLayoutOverride.Dual;
        SplitPreviewMenuItem.IsChecked = _splitPreviewEnabled;
        FocusModeMenuItem.IsChecked = _focusMode;
    }

    private void PaneSplitter_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        CommitPaneSplitterWidth();
    }

    private void PaneSplitter_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right))
        {
            return;
        }

        CommitPaneSplitterWidth();
        e.Handled = true;
    }

    private void PaneSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _listPaneWidth = DefaultListPaneWidth;
        ListColumn.Width = new GridLength(_listPaneWidth);
        PersistPresentationOverrides();
        e.Handled = true;
    }

    private void CommitPaneSplitterWidth()
    {
        _listPaneWidth = NormalizeListPaneWidth(ListColumn.ActualWidth);
        ListColumn.Width = new GridLength(_listPaneWidth);
        PersistPresentationOverrides();
    }

    private static double NormalizeListPaneWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinListPaneWidth, MaxListPaneWidth)
            : DefaultListPaneWidth;

    private double GetMaximumListPaneWidth(double layoutWidth)
    {
        double availableWidth = Math.Max(
            0,
            layoutWidth - Root.Padding.Left - Root.Padding.Right - PaneSplitterGutterWidth);
        return Math.Clamp(
            availableWidth - MinDetailPaneWidth,
            MinListPaneWidth,
            MaxListPaneWidth);
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F2 && _selectedItem is not null)
        {
            e.Handled = true;
            await EnterEditModeAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.N && IsControlPressed())
        {
            e.Handled = true;
            await ForceCommitAsync(returnToReading: false);
            BeginNewNote();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (ViewModel.IsSearchExpanded)
            {
                CloseSearchButton_Click(sender, e);
                e.Handled = true;
            }
            else if (_isBulkSelectionMode)
            {
                SetBulkSelectionMode(enable: false);
                e.Handled = true;
            }
        }
    }

    private string GetFocusTarget()
    {
        try
        {
            if (XamlRoot is null)
            {
                return _lastFocusTarget;
            }

            object? focused = FocusManager.GetFocusedElement(XamlRoot);
            if (ReferenceEquals(focused, EditorBodyTextBox)) return "Editor";
            if (ReferenceEquals(focused, EditorTitleTextBox)) return "Title";
            if (ReferenceEquals(focused, SearchTextBox)) return "Search";
            if (ReferenceEquals(focused, ItemsList)) return "Items";
            return "Root";
        }
        catch (COMException ex)
        {
            // During window deactivation WinUI may already have detached the
            // focus manager from this XamlRoot. Preserve the last valid target
            // so visibility changes never escape as an unhandled exception.
            App.LogVerbose($"[QuickCapture] Focus snapshot skipped during deactivation: {ex.Message}");
            return _lastFocusTarget;
        }
    }

    private void ApplyFocusTarget(string? target)
    {
        FrameworkElement element = target switch
        {
            "Editor" => EditorBodyTextBox,
            "Title" => EditorTitleTextBox,
            "Input" => ItemsList,
            "Search" => SearchTextBox,
            "Items" => ItemsList,
            _ => Root
        };
        element.Focus(FocusState.Programmatic);
    }

    private double GetListScrollOffset() =>
        FindDescendant<ScrollViewer>(ItemsList)?.VerticalOffset ?? 0;

    private void SetListScrollOffset(double offset) =>
        FindDescendant<ScrollViewer>(ItemsList)?.ChangeView(null, offset, null, true);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool IsControlPressed() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(
                Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsShiftPressed() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift).HasFlag(
                Windows.UI.Core.CoreVirtualKeyStates.Down);

    private bool ShouldOpenExistingInEditMode() =>
        _settingsService.Settings.QuickCaptureExistingNoteOpenMode ==
        SettingsService.QuickCaptureOpenModeEdit;

    private void OnQuickCaptureSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnQuickCaptureSettingsChanged);
            return;
        }

        if (!_hasLayoutOverride && Enum.TryParse(
            _settingsService.Settings.QuickCaptureDefaultLayout,
            true,
            out QuickCaptureLayoutOverride layout))
        {
            _layoutOverride = layout;
        }
        ViewModel.ConfigureRevisionRetention(
            _settingsService.Settings.QuickCaptureRevisionRetentionDays,
            _settingsService.Settings.QuickCaptureRevisionLimitPerNote);
        if (!_hasSplitPreviewOverride)
        {
            _splitPreviewEnabled =
                _settingsService.Settings.QuickCaptureWideEditorView ==
                SettingsService.QuickCaptureWideEditorSplit;
        }
        ApplyListPresentationSettings();
        ApplySegmentedStyle();
        RenderReadingSurface();
        RefreshMarkdownPreview();
        ApplyResponsiveLayout();
    }

    private void ApplyListPresentationSettings()
    {
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            item.UpdateListPresentation(
                _settingsService.Settings.QuickCaptureListDensity,
                _settingsService.Settings.QuickCaptureTimeDisplay);
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
            App.Log($"[QuickCaptureContent] Action failed widget={WidgetId}: {ex}");
            RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Error, "quick-capture-action-error");
        }
    }

    private void RaiseFeedback(
        string message,
        WidgetFeedbackSeverity severity = WidgetFeedbackSeverity.Info,
        string? deduplicationKey = null,
        string? actionText = null,
        Func<Task>? action = null,
        TimeSpan? duration = null)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey,
                    actionText,
                    action,
                    duration)));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _autoSaveTimer.Stop();
        _draftTimer.Stop();
        _previewTimer.Stop();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
        _draftTimer.Tick -= DraftTimer_Tick;
        _previewTimer.Tick -= PreviewTimer_Tick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ActualThemeChanged -= OnQuickCaptureActualThemeChanged;
        ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
        _settingsService.SettingsChanged -= OnQuickCaptureSettingsChanged;
        _ = ForceCommitAsync(returnToReading: false);
        if (_ownsViewModel)
        {
            ViewModel.Dispose();
        }
    }
}

internal enum QuickCaptureLayoutOverride
{
    Auto,
    Single,
    Dual
}

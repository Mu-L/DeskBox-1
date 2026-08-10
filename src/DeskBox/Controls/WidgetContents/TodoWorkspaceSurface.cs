using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

internal enum TodoWorkspaceLayoutMode
{
    Micro,
    Compact,
    Enhanced,
    Split,
    ThreePane
}

internal sealed partial class TodoWorkspaceSurface : Grid, IDisposable
{
    private const double LayoutHysteresis = 24;
    private const double SplitterColumnWidth = 12;
    private const int NavigationColumnIndex = 0;
    private const int MainColumnIndex = 1;
    private const int SplitterColumnIndex = 2;
    private const int DetailColumnIndex = 3;
    private readonly TodoWidgetViewModel _legacyViewModel;
    private readonly TodoWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly SettingsService? _settingsService;
    private readonly WidgetConfig _config;
    private readonly TodoPresentationSettingsStore _presentationStore;
    private readonly TodoCalendarSourceService? _calendarSourceService;
    private readonly TodoQuickAddParser _quickAddParser = new();
    private readonly TodoMarkdownService _markdownService = new();
    private readonly ObservableCollection<TodoWorkspaceTaskRow> _rows = [];
    private readonly List<TodoWorkspaceNavigationItem> _navigationItems = [];
    private readonly Grid _toolbar = new();
    private readonly Button _navigationButton = new();
    private readonly TextBlock _viewTitle = new();
    private readonly TextBlock _periodTitle = new();
    private readonly Button _viewModeButton = new();
    private readonly FontIcon _viewModeIcon = new() { FontSize = 12 };
    private readonly TextBlock _viewModeText = new() { FontSize = 12 };
    private readonly StackPanel _periodButtons = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Button _settingsButton = new();
    private readonly Grid _body = new();
    private readonly Border _navigationPane = new();
    private readonly StackPanel _navigationPanel = new() { Spacing = 2 };
    private readonly Grid _mainHost = new();
    private readonly Border _detailPane = new();
    private readonly Grid _detailHost = new();
    private readonly GridSplitter _splitter = new();
    private readonly Grid _listViewRoot = new();
    private readonly Grid _emptyStateHost = new();
    private readonly ListView _taskList = new();
    private readonly Border _selectionGutter = new();
    private readonly Canvas _lassoCanvas = new();
    private readonly Border _lassoRectangle = new();
    private readonly Border _quickAddPanel = new();
    private readonly TextBox _quickAddTextBox = new();
    private readonly StackPanel _quickAddTokens = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private readonly Button _quickAddButton = new();
    private readonly Grid _feedbackBar = new();
    private readonly TextBlock _feedbackText = new();
    private readonly Button _feedbackAction = new();
    private readonly StackPanel _bulkBar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    private TodoWorkspaceSnapshot _snapshot = new();
    private IReadOnlyList<TodoCalendarEvent> _externalCalendarEvents = [];
    private DateOnly? _calendarSourceRangeStart;
    private DateOnly? _calendarSourceRangeEnd;
    private TodoWidgetPresentationSettings _presentation;
    private TodoWorkspaceNavigationItem? _selectedNavigation;
    private TodoWorkspaceLayoutMode _layoutMode = TodoWorkspaceLayoutMode.Compact;
    private DateOnly _selectedDate;
    private DateOnly _visiblePeriod;
    private string? _selectedTaskId;
    private TodoTask? _selectedTaskOverride;
    private DateOnly? _selectedOccurrenceDate;
    private TodoRecurrenceEditScope _recurrenceEditScope = TodoRecurrenceEditScope.Occurrence;
    private string[] _lastDeletedTaskIds = [];
    private bool _showCollapsedCompleted;
    private bool _disposed;
    private bool _isLoaded;
    private bool _isRefreshing;
    private bool _isBatchSelectionMode;
    private bool _updatingLassoSelection;
    private bool _lassoStarted;
    private int _localMutationDepth;
    private uint? _lassoPointerId;
    private Point _lassoOrigin;
    private HashSet<string> _lassoBaseIds = new(StringComparer.Ordinal);
    private string? _selectionAnchorId;
    private double? _lockedWidth;
    private double? _lockedHeight;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _feedbackCts;

    internal TodoWorkspaceSurface(TodoWidgetViewModel legacyViewModel, DataTemplate taskTemplate)
    {
        _legacyViewModel = legacyViewModel;
        _workspace = legacyViewModel.WorkspaceService ??
            throw new InvalidOperationException("Todo workspace surface requires a shared workspace service.");
        _localization = legacyViewModel.LocalizationService;
        _settingsService = legacyViewModel.SettingsService;
        _config = legacyViewModel.WidgetConfig;
        _presentationStore = new TodoPresentationSettingsStore(_settingsService);
        _calendarSourceService = App.Current?.Services.GetService<TodoCalendarSourceService>();
        _presentation = _presentationStore.Load(_config);
        _selectedDate = _presentation.SelectedDate ?? DateOnly.FromDateTime(DateTime.Today);
        _visiblePeriod = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);

        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BuildVisualTree(taskTemplate);
        AllowDrop = true;
        DragOver += Surface_DragOver;
        Drop += Surface_Drop;
        RightTapped += Surface_RightTapped;
        Loaded += Surface_Loaded;
        Unloaded += Surface_Unloaded;
        SizeChanged += Surface_SizeChanged;
        _localization.LanguageChanged += Localization_LanguageChanged;
        _workspace.Changed += Workspace_Changed;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += SettingsService_SettingsChanged;
        }
    }

    internal TodoWidgetPresentationSettings Presentation => _presentation;

    internal TodoWorkspaceSnapshot Snapshot => _snapshot;

    internal DateOnly SelectedDate => _selectedDate;

    internal TodoWorkspaceLayoutMode LayoutMode => _layoutMode;

    internal TodoTask? SelectedTask => _selectedTaskOverride ??
        (string.IsNullOrWhiteSpace(_selectedTaskId)
            ? null
            : _snapshot.Tasks.FirstOrDefault(task =>
                string.Equals(task.Id, _selectedTaskId, StringComparison.Ordinal)));

    private void BuildVisualTree(DataTemplate taskTemplate)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        BuildToolbar();
        SetRow(_toolbar, 0);
        Children.Add(_toolbar);

        BuildColorMarkerBar();
        SetRow(_colorMarkerBar, 1);
        Children.Add(_colorMarkerBar);

        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.ColumnSpacing = 0;
        SetRow(_body, 2);
        Children.Add(_body);

        _navigationPane.CornerRadius = new CornerRadius(8);
        _navigationPane.BorderThickness = new Thickness(1);
        _navigationPane.BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush");
        _navigationPane.Background = TodoWorkspaceTaskCard.ResourceBrush("CardBackgroundFillColorSecondaryBrush");
        _navigationPane.Padding = new Thickness(6);
        _navigationPane.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _navigationPanel
        };
        SetColumn(_navigationPane, NavigationColumnIndex);
        _body.Children.Add(_navigationPane);

        SetColumn(_mainHost, MainColumnIndex);
        _mainHost.SizeChanged += MainHost_SizeChanged;
        _body.Children.Add(_mainHost);

        // The detail pane is a workspace column, not another card inside the
        // widget. Keeping this surface flat avoids the card-inside-card effect
        // once steps, notes and editors are shown.
        _detailPane.CornerRadius = new CornerRadius(0);
        _detailPane.BorderThickness = new Thickness(0);
        _detailPane.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _detailPane.Child = _detailHost;
        SetColumn(_detailPane, DetailColumnIndex);
        _body.Children.Add(_detailPane);

        _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        _splitter.VerticalAlignment = VerticalAlignment.Stretch;
        _splitter.ResizeDirection = GridSplitter.GridResizeDirection.Columns;
        _splitter.ResizeBehavior = GridSplitter.GridResizeBehavior.PreviousAndNext;
        _splitter.DragIncrement = 1;
        _splitter.KeyboardIncrement = 8;
        _splitter.IsThumbVisible = true;
        _splitter.IsTabStop = true;
        _splitter.UseSystemFocusVisuals = true;
        _splitter.ManipulationCompleted += Splitter_ManipulationCompleted;
        _splitter.KeyUp += Splitter_KeyUp;
        _splitter.DoubleTapped += Splitter_DoubleTapped;
        SetColumn(_splitter, SplitterColumnIndex);
        _body.Children.Add(_splitter);

        BuildQuickAdd();
        SetRow(_quickAddPanel, 3);
        Children.Add(_quickAddPanel);

        BuildFeedbackBar();
        SetRow(_feedbackBar, 4);
        Children.Add(_feedbackBar);

        BuildTaskList(taskTemplate);
    }

    private void BuildToolbar()
    {
        _toolbar.MinHeight = 38;
        _toolbar.Margin = new Thickness(0, 0, 0, 7);
        _toolbar.ColumnSpacing = 5;
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        ConfigureIconButton(_navigationButton, "\uE700", 34);
        AutomationProperties.SetName(_navigationButton, _localization.T("Todo.Workspace.Navigation.Smart"));
        _navigationButton.Click += NavigationButton_Click;
        _toolbar.Children.Add(_navigationButton);

        var titlePanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0
        };
        _viewTitle.FontSize = 15;
        _viewTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _viewTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _periodTitle.FontSize = 11;
        _periodTitle.Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush");
        titlePanel.Children.Add(_viewTitle);
        titlePanel.Children.Add(_periodTitle);
        SetColumn(titlePanel, 1);
        _toolbar.Children.Add(titlePanel);

        _viewModeButton.MinHeight = 32;
        _viewModeButton.MinWidth = 0;
        _viewModeButton.Padding = new Thickness(8, 0, 7, 0);
        _viewModeButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                _viewModeIcon,
                _viewModeText,
                new FontIcon { Glyph = "\uE70D", FontSize = 9 }
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(_viewModeButton, "SubtleButtonStyle");
        _viewModeButton.Click += ViewModeButton_Click;
        SetColumn(_viewModeButton, 2);
        _toolbar.Children.Add(_viewModeButton);

        foreach ((string glyph, int delta) in new[] { ("\uE76B", -1), ("\uE777", 0), ("\uE76C", 1) })
        {
            var button = new Button { Tag = delta };
            ConfigureIconButton(button, glyph, 30);
            AutomationProperties.SetName(button, _localization.T(delta switch
            {
                -1 => "Todo.Workspace.PreviousPeriod",
                0 => "Todo.Workspace.GoToday",
                _ => "Todo.Workspace.NextPeriod"
            }));
            button.Click += PeriodButton_Click;
            _periodButtons.Children.Add(button);
        }
        SetColumn(_periodButtons, 3);
        _toolbar.Children.Add(_periodButtons);

        ConfigureIconButton(_settingsButton, "\uE712", 32);
        ToolTipService.SetToolTip(_settingsButton, _localization.T("Widget.Tooltip.More"));
        AutomationProperties.SetName(_settingsButton, _localization.T("Widget.Tooltip.More"));
        _settingsButton.Click += MoreButton_Click;
        SetColumn(_settingsButton, 4);
        _toolbar.Children.Add(_settingsButton);
    }

    private void BuildQuickAdd()
    {
        _quickAddPanel.Margin = new Thickness(0, 5, 0, 0);
        _quickAddPanel.Padding = new Thickness(3, 5, 1, 1);
        _quickAddPanel.CornerRadius = new CornerRadius(0);
        _quickAddPanel.BorderThickness = new Thickness(0, 1, 0, 0);
        _quickAddPanel.BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush");
        _quickAddPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var panel = new Grid { RowSpacing = 3 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var inputRow = new Grid { ColumnSpacing = 4 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _quickAddTextBox.BorderThickness = new Thickness(0);
        _quickAddTextBox.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _quickAddTextBox.PlaceholderText = _localization.T("Todo.AddPlaceholder");
        _quickAddTextBox.TextChanged += QuickAddTextBox_TextChanged;
        _quickAddTextBox.KeyDown += QuickAddTextBox_KeyDown;
        inputRow.Children.Add(_quickAddTextBox);
        ConfigureIconButton(_quickAddButton, "\uE710", 32);
        AutomationProperties.SetName(_quickAddButton, _localization.T("Common.Add"));
        _quickAddButton.Click += QuickAddButton_Click;
        SetColumn(_quickAddButton, 1);
        inputRow.Children.Add(_quickAddButton);
        panel.Children.Add(inputRow);
        var tokenScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _quickAddTokens
        };
        SetRow(tokenScroll, 1);
        panel.Children.Add(tokenScroll);
        _quickAddPanel.Child = panel;
    }

    private void BuildFeedbackBar()
    {
        _feedbackBar.Visibility = Visibility.Collapsed;
        _feedbackBar.MinHeight = 30;
        _feedbackBar.Margin = new Thickness(0, 5, 0, 0);
        _feedbackBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _feedbackBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _feedbackText.VerticalAlignment = VerticalAlignment.Center;
        _feedbackText.FontSize = 12;
        _feedbackText.TextTrimming = TextTrimming.CharacterEllipsis;
        _feedbackBar.Children.Add(_feedbackText);
        _feedbackAction.MinWidth = 0;
        _feedbackAction.Padding = new Thickness(8, 3, 8, 3);
        TodoWorkspaceTaskCard.ApplyStyle(_feedbackAction, "SubtleButtonStyle");
        SetColumn(_feedbackAction, 1);
        _feedbackBar.Children.Add(_feedbackAction);
    }

    private void BuildTaskList(DataTemplate taskTemplate)
    {
        _listViewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listViewRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _taskList.ItemsSource = _rows;
        _taskList.IsItemClickEnabled = true;
        _taskList.SelectionMode = ListViewSelectionMode.None;
        _taskList.IsMultiSelectCheckBoxEnabled = false;
        _taskList.CanDragItems = true;
        _taskList.AllowDrop = true;
        _taskList.ItemClick += TaskList_ItemClick;
        _taskList.SelectionChanged += TaskList_SelectionChanged;
        _taskList.DragItemsStarting += TaskList_DragItemsStarting;
        _taskList.DragItemsCompleted += TaskList_DragItemsCompleted;
        _taskList.RightTapped += TaskList_RightTapped;
        _taskList.KeyDown += TaskList_KeyDown;
        AutomationProperties.SetName(_taskList, _localization.T("Todo.Workspace.TaskList"));
        _taskList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _taskList.ItemTemplate = taskTemplate;

        SetRow(_taskList, 1);
        _listViewRoot.Children.Add(_bulkBar);
        _listViewRoot.Children.Add(_taskList);

        _selectionGutter.Width = 12;
        _selectionGutter.HorizontalAlignment = HorizontalAlignment.Left;
        _selectionGutter.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(_selectionGutter, _localization.T("Todo.Workspace.SelectTasks"));
        _selectionGutter.PointerPressed += SelectionGutter_PointerPressed;
        _selectionGutter.PointerMoved += SelectionGutter_PointerMoved;
        _selectionGutter.PointerReleased += SelectionGutter_PointerReleased;
        _selectionGutter.PointerCanceled += SelectionGutter_PointerCanceled;
        _selectionGutter.PointerCaptureLost += SelectionGutter_PointerCaptureLost;
        SetRow(_selectionGutter, 1);
        Canvas.SetZIndex(_selectionGutter, 20);
        _listViewRoot.Children.Add(_selectionGutter);

        _lassoCanvas.IsHitTestVisible = false;
        _lassoRectangle.Visibility = Visibility.Collapsed;
        _lassoRectangle.CornerRadius = new CornerRadius(3);
        _lassoRectangle.BorderThickness = new Thickness(1);
        _lassoRectangle.BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush");
        _lassoRectangle.Background = TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush");
        _lassoRectangle.Opacity = 0.22;
        _lassoCanvas.Children.Add(_lassoRectangle);
        SetRowSpan(_lassoCanvas, 2);
        Canvas.SetZIndex(_lassoCanvas, 30);
        _listViewRoot.Children.Add(_lassoCanvas);

        SetRowSpan(_emptyStateHost, 2);
        _listViewRoot.Children.Add(_emptyStateHost);
    }

    private async void Surface_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ApplyResponsiveLayout(ActualWidth, ActualHeight, force: true);
        await RefreshSnapshotAsync(rebuildNavigation: true);
    }

    private void Surface_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _refreshCts?.Cancel();
        _feedbackCts?.Cancel();
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lockedWidth is null)
        {
            ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        }
    }

    internal void BeginResponsiveLayoutTransition(double targetWidth, double targetHeight)
    {
        _lockedWidth = targetWidth;
        _lockedHeight = targetHeight;
        ApplyResponsiveLayout(targetWidth, targetHeight, force: true);
    }

    internal void CompleteResponsiveLayoutTransition(double finalWidth, double finalHeight)
    {
        _lockedWidth = null;
        _lockedHeight = null;
        ApplyResponsiveLayout(finalWidth, finalHeight, force: true);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        _lockedWidth = null;
        _lockedHeight = null;
        ApplyResponsiveLayout(ActualWidth, ActualHeight, force: true);
    }

    private void ApplyResponsiveLayout(double width, double height, bool force = false)
    {
        double scaleCorrection = Math.Max(1.0, _legacyViewModel.TextSize / SettingsService.DefaultTextSize) *
                                 Math.Max(0.9, _presentation.DensityScale);
        double correctedWidth = width / scaleCorrection;
        double correctedHeight = height / scaleCorrection;
        TodoWorkspaceLayoutMode candidate = TodoResponsiveLayoutResolver.Resolve(
            correctedWidth,
            correctedHeight,
            _presentation.ResponsivePreference);
        if (!force &&
            candidate != _layoutMode &&
            !TodoResponsiveLayoutResolver.HasCrossedHysteresis(
                correctedWidth,
                correctedHeight,
                _presentation.ResponsivePreference,
                _layoutMode,
                candidate,
                LayoutHysteresis))
        {
            return;
        }

        bool changed = candidate != _layoutMode;
        _layoutMode = candidate;
        double densityPadding = candidate is TodoWorkspaceLayoutMode.Micro or TodoWorkspaceLayoutMode.Compact
            ? 5
            : Math.Round(5 + (3 * _presentation.DensityScale));
        Padding = new Thickness(densityPadding);
        _body.ColumnDefinitions[NavigationColumnIndex].Width = candidate == TodoWorkspaceLayoutMode.ThreePane
            ? new GridLength(160)
            : new GridLength(0);
        _body.ColumnDefinitions[MainColumnIndex].MinWidth = 0;
        _body.ColumnDefinitions[DetailColumnIndex].MinWidth = 0;
        _navigationPane.Visibility = candidate == TodoWorkspaceLayoutMode.ThreePane
            ? Visibility.Visible
            : Visibility.Collapsed;
        _navigationButton.Visibility = candidate == TodoWorkspaceLayoutMode.ThreePane
            ? Visibility.Collapsed
            : Visibility.Visible;

        bool split = candidate is TodoWorkspaceLayoutMode.Split or TodoWorkspaceLayoutMode.ThreePane;
        bool hasDetail = split || _selectedTaskId is not null;
        bool fullPageDetail = !split && _selectedTaskId is not null;
        _navigationPane.Margin = candidate == TodoWorkspaceLayoutMode.ThreePane
            ? new Thickness(0, 0, 6, 0)
            : new Thickness(0);
        _detailPane.Margin = candidate == TodoWorkspaceLayoutMode.ThreePane
            ? new Thickness(6, 0, 0, 0)
            : new Thickness(0);
        if (candidate == TodoWorkspaceLayoutMode.ThreePane)
        {
            _body.ColumnDefinitions[MainColumnIndex].Width = new GridLength(1, GridUnitType.Star);
            _body.ColumnDefinitions[SplitterColumnIndex].Width = new GridLength(0);
            _body.ColumnDefinitions[DetailColumnIndex].Width = new GridLength(340);
        }
        else if (candidate == TodoWorkspaceLayoutMode.Split)
        {
            double mainRatio = _presentation.DisplayMode == TodoDisplayMode.Month
                ? _presentation.CalendarSplitRatio
                : _presentation.ListSplitRatio;
            (double minimumRatio, double maximumRatio) = GetSplitRatioBounds();
            double available = Math.Max(1, width - (densityPadding * 2) - SplitterColumnWidth);
            double minimumMain = Math.Max(GetSplitMinimumMainWidth(), available * minimumRatio);
            double minimumDetail = Math.Max(GetSplitMinimumDetailWidth(), available * (1 - maximumRatio));
            if (minimumMain + minimumDetail > available)
            {
                double compression = available / (minimumMain + minimumDetail);
                minimumMain *= compression;
                minimumDetail *= compression;
            }

            double maximumMain = Math.Max(minimumMain, available - minimumDetail);
            double mainWidth = Math.Clamp(available * mainRatio, minimumMain, maximumMain);
            _body.ColumnDefinitions[MainColumnIndex].MinWidth = minimumMain;
            _body.ColumnDefinitions[DetailColumnIndex].MinWidth = minimumDetail;
            _body.ColumnDefinitions[MainColumnIndex].Width = new GridLength(mainWidth);
            _body.ColumnDefinitions[SplitterColumnIndex].Width = new GridLength(SplitterColumnWidth);
            _body.ColumnDefinitions[DetailColumnIndex].Width = new GridLength(Math.Max(1, available - mainWidth));
        }
        else
        {
            _body.ColumnDefinitions[MainColumnIndex].Width = hasDetail && _selectedTaskId is not null
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            _body.ColumnDefinitions[SplitterColumnIndex].Width = new GridLength(0);
            _body.ColumnDefinitions[DetailColumnIndex].Width = hasDetail && _selectedTaskId is not null
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }

        _detailPane.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = candidate == TodoWorkspaceLayoutMode.Split && hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        string splitterName = _localization.T(
            _presentation.DisplayMode == TodoDisplayMode.Month
                ? "Todo.Workspace.Settings.CalendarSplit"
                : "Todo.Workspace.Settings.ListSplit");
        AutomationProperties.SetName(_splitter, splitterName);
        ToolTipService.SetToolTip(_splitter, splitterName);
        _viewModeButton.Visibility = candidate is TodoWorkspaceLayoutMode.Micro or TodoWorkspaceLayoutMode.Compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        _periodButtons.Visibility = _presentation.DisplayMode == TodoDisplayMode.List
            ? Visibility.Collapsed
            : Visibility.Visible;
        _quickAddTokens.Visibility = candidate == TodoWorkspaceLayoutMode.Micro
            ? Visibility.Collapsed
            : Visibility.Visible;
        _quickAddPanel.Visibility = fullPageDetail ? Visibility.Collapsed : Visibility.Visible;
        _colorMarkerBar.Visibility = candidate == TodoWorkspaceLayoutMode.Micro || fullPageDetail
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (changed || force)
        {
            RenderCurrentView();
            RenderDetailPane();
        }
    }

    private double GetSplitMinimumMainWidth() =>
        _presentation.DisplayMode == TodoDisplayMode.Month ? 320 : 240;

    private double GetSplitMinimumDetailWidth() =>
        _selectedTaskId is null ? 240 : 300;

    private (double Minimum, double Maximum) GetSplitRatioBounds() =>
        _presentation.DisplayMode == TodoDisplayMode.Month
            ? (0.35, 0.80)
            : (0.25, 0.75);

    private void Splitter_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        CaptureSplitterRatio();
    }

    private void Splitter_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        CaptureSplitterRatio();
        e.Handled = true;
    }

    private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_layoutMode != TodoWorkspaceLayoutMode.Split)
        {
            return;
        }

        if (_presentation.DisplayMode == TodoDisplayMode.Month)
        {
            _presentation.CalendarSplitRatio = 0.58;
        }
        else
        {
            _presentation.ListSplitRatio = 0.40;
        }

        _presentationStore.SaveDebounced(_config, _presentation);
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
        e.Handled = true;
    }

    private void CaptureSplitterRatio()
    {
        if (_layoutMode != TodoWorkspaceLayoutMode.Split || _disposed)
        {
            return;
        }

        double available = _mainHost.ActualWidth + _detailPane.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        (double minimumRatio, double maximumRatio) = GetSplitRatioBounds();
        double ratio = Math.Clamp(_mainHost.ActualWidth / available, minimumRatio, maximumRatio);
        if (_presentation.DisplayMode == TodoDisplayMode.Month)
        {
            _presentation.CalendarSplitRatio = ratio;
        }
        else
        {
            _presentation.ListSplitRatio = ratio;
        }
        _presentationStore.SaveDebounced(_config, _presentation);
    }

    private async Task RefreshSnapshotAsync(bool rebuildNavigation = false)
    {
        if (_isRefreshing || !_isLoaded)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            _snapshot = await _workspace.LoadSnapshotAsync(includeDeleted: true);
            await RefreshCalendarSourcesAsync();
            RefreshSelectedOccurrence();
            if (rebuildNavigation || _navigationItems.Count == 0)
            {
                BuildNavigation();
            }

            RefreshRows();
            RenderCurrentView();
            RenderDetailPane();
            UpdateToolbarText();
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Refresh failed: {ex}");
            ShowFeedback(_localization.T("Todo.Workspace.LoadFailed"));
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void BuildNavigation()
    {
        _navigationItems.Clear();
        _navigationPanel.Children.Clear();
        AddNavigationHeader(_localization.T("Todo.Workspace.Navigation.Smart"));
        TodoOrganizationSettings organization = _settingsService?.Settings.Todo.Organization ?? new TodoOrganizationSettings();
        HashSet<TodoSmartView> primarySmartViews =
        [
            TodoSmartView.Today,
            TodoSmartView.Inbox,
            TodoSmartView.Planned,
            TodoSmartView.Important
        ];
        TodoSmartView[] supportedSmartViews =
        [
            TodoSmartView.Today,
            TodoSmartView.Inbox,
            TodoSmartView.Planned,
            TodoSmartView.Unscheduled,
            TodoSmartView.Important,
            TodoSmartView.Completed
        ];
        var secondarySmartItems = new List<TodoWorkspaceNavigationItem>();
        foreach (TodoSmartView smartView in organization.SmartViewOrder
                     .Concat(supportedSmartViews)
                     .Where(supportedSmartViews.Contains)
                     .Distinct())
        {
            TodoWorkspaceNavigationItem item = CreateSmartNavigationItem(smartView);
            if (primarySmartViews.Contains(smartView))
            {
                AddNavigationItem(item);
            }
            else
            {
                _navigationItems.Add(item);
                secondarySmartItems.Add(item);
            }
        }
        var trash = new TodoWorkspaceNavigationItem(
            "trash",
            _localization.T("Todo.Workspace.Trash"),
            "\uE74D",
            IsTrash: true);
        _navigationItems.Add(trash);
        secondarySmartItems.Add(trash);
        AddNavigationOverflow(secondarySmartItems);

        AddNavigationHeaderWithCommand(
            _localization.T("Todo.Workspace.Navigation.Lists"),
            "\uE710",
            "Todo.Workspace.Navigation.NewList",
            CreateListAsync);
        foreach (TodoList list in _snapshot.Lists
                     .Where(list => !list.IsArchived && !list.IsSystem)
                     .OrderBy(list => list.SortRank))
        {
            AddNavigationItem(new($"list:{list.Id}", GetListDisplayName(list), "\uE8FD", ListId: list.Id));
            foreach (TodoSection section in _snapshot.Sections
                         .Where(section => !section.IsArchived && section.ListId == list.Id)
                         .OrderBy(section => section.SortRank))
            {
                AddNavigationItem(new(
                    $"section:{section.Id}",
                    section.Name,
                    "\uE8B7",
                    ListId: list.Id,
                    SectionId: section.Id,
                IsChild: true));
            }
        }

        // Tags remain valid binding targets and are available from filters, but no longer
        // consume permanent navigation space.
        foreach (TodoTag tag in _snapshot.Tags.OrderBy(tag => tag.SortRank))
        {
            _navigationItems.Add(new($"tag:{tag.Id}", $"#{tag.Name}", "\uE8EC", TagId: tag.Id));
        }

        if (_snapshot.SavedViews.Count > 0)
        {
            AddNavigationHeader(_localization.T("Todo.Workspace.Navigation.SavedViews"));
            foreach (TodoSavedView savedView in _snapshot.SavedViews.OrderBy(view => view.SortRank))
            {
                AddNavigationItem(new($"saved:{savedView.Id}", savedView.Name, savedView.IconGlyph ?? "\uE721"));
            }
        }

        string targetId = _presentation.SectionId is { } sectionId
            ? $"section:{sectionId}"
            : _presentation.TagId is { } tagId
                ? $"tag:{tagId}"
                : _presentation.ListId is { } listId
                    ? string.Equals(listId, TodoWorkspaceDefaults.InboxListId, StringComparison.Ordinal)
                        ? "inbox"
                        : $"list:{listId}"
                    : _presentation.SavedViewId is { } savedViewId
                ? $"saved:{savedViewId}"
                : _presentation.SmartView.ToString().ToLowerInvariant();
        _selectedNavigation = _navigationItems.FirstOrDefault(item =>
                                  string.Equals(item.Id, targetId, StringComparison.Ordinal)) ??
                              _navigationItems.FirstOrDefault(item => item.SmartView == TodoSmartView.Today) ??
                              _navigationItems.First();
        RefreshNavigationButtonStates();
    }

    private TodoWorkspaceNavigationItem CreateSmartNavigationItem(TodoSmartView smartView)
    {
        return smartView switch
        {
            TodoSmartView.Today => new("today", _localization.T("Todo.Workspace.Today"), "\uE823", smartView),
            TodoSmartView.Inbox => new("inbox", _localization.T("Todo.Workspace.Inbox"), "\uE715", smartView),
            TodoSmartView.Planned => new("planned", _localization.T("Todo.Workspace.Planned"), "\uE787", smartView),
            TodoSmartView.Unscheduled => new("unscheduled", _localization.T("Todo.Workspace.Unscheduled"), "\uE8A5", smartView),
            TodoSmartView.Important => new("important", _localization.T("Todo.Workspace.Important"), "\uE735", smartView),
            TodoSmartView.Completed => new("completed", _localization.T("Todo.Workspace.Completed"), "\uE73E", smartView),
            _ => new(smartView.ToString().ToLowerInvariant(), _localization.T($"Todo.Workspace.{smartView}"), "\uE721", smartView)
        };
    }

    private string GetListDisplayName(TodoList list) =>
        string.Equals(list.Id, TodoWorkspaceDefaults.InboxListId, StringComparison.Ordinal)
            ? _localization.T("Todo.Workspace.Inbox")
            : list.Name;

    private void AddNavigationHeader(string text)
    {
        _navigationPanel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(7, 9, 7, 3),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
        });
    }

    private void AddNavigationHeaderWithCommand(
        string text,
        string glyph,
        string tooltipKey,
        Func<Task> action)
    {
        var header = new Grid { Margin = new Thickness(7, 9, 3, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var button = new Button();
        ConfigureIconButton(button, glyph, 26);
        ToolTipService.SetToolTip(button, _localization.T(tooltipKey));
        button.Click += async (_, _) => await action();
        SetColumn(button, 1);
        header.Children.Add(button);
        _navigationPanel.Children.Add(header);
    }

    private void AddNavigationOverflow(IReadOnlyList<TodoWorkspaceNavigationItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var button = new Button
        {
            MinHeight = 32,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children =
                {
                    new FontIcon { Glyph = "\uE712", FontSize = 13 },
                    new TextBlock { Text = _localization.T("Widget.Tooltip.More") }
                }
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
        button.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();
            foreach (TodoWorkspaceNavigationItem item in items)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = item.Name,
                    Icon = new FontIcon { Glyph = item.Glyph },
                    Tag = item
                };
                menuItem.Click += async (_, _) => await SelectNavigationAsync(item);
                flyout.Items.Add(menuItem);
            }
            flyout.ShowAt(button);
        };
        _navigationPanel.Children.Add(button);
    }

    private void AddNavigationItem(TodoWorkspaceNavigationItem item)
    {
        _navigationItems.Add(item);
        var button = new Button
        {
            Tag = item,
            MinHeight = 34,
            Padding = item.IsChild ? new Thickness(24, 3, 8, 3) : new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children =
                {
                    new FontIcon { Glyph = item.Glyph, FontSize = 13 },
                    new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
        AutomationProperties.SetName(button, item.Name);
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
        button.Click += NavigationItem_Click;
        button.RightTapped += NavigationItem_RightTapped;
        _navigationPanel.Children.Add(button);
    }

    private void AddNavigationCommand(string glyph, string textKey, Func<Task> action)
    {
        var button = new Button
        {
            MinHeight = 31,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Opacity = 0.78,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 11 },
                    new TextBlock { Text = _localization.T(textKey), FontSize = 12 }
                }
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
        button.Click += async (_, _) => await action();
        _navigationPanel.Children.Add(button);
    }

    private async Task CreateListAsync()
    {
        string? name = await PromptForNameAsync(
            "Todo.Workspace.Navigation.NewList",
            "Todo.Workspace.Navigation.ListName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TodoList? created = null;
        await RunMutationAsync(async () => created = await _workspace.EnsureListAsync(name), rebuildNavigation: true);
        TodoWorkspaceNavigationItem? item = _navigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.ListId, created?.Id, StringComparison.Ordinal));
        if (item is not null)
        {
            await SelectNavigationAsync(item);
        }
    }

    private async Task CreateTagAsync()
    {
        string? name = await PromptForNameAsync(
            "Todo.Workspace.Navigation.NewTag",
            "Todo.Workspace.Navigation.TagName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TodoTag? created = null;
        await RunMutationAsync(async () => created = await _workspace.EnsureTagAsync(name), rebuildNavigation: true);
        TodoWorkspaceNavigationItem? item = _navigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.TagId, created?.Id, StringComparison.Ordinal));
        if (item is not null)
        {
            await SelectNavigationAsync(item);
        }
    }

    private async Task SaveCurrentViewAsync()
    {
        string? name = await PromptForNameAsync(
            "Todo.Workspace.Navigation.SaveView",
            "Todo.Workspace.Navigation.ViewName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var savedView = new TodoSavedView
        {
            Name = name.Trim(),
            SortRank = _snapshot.SavedViews.Count,
            Query = BuildCurrentQuery()
        };
        await RunMutationAsync(() => _workspace.SaveSavedViewAsync(savedView), rebuildNavigation: true);
        TodoWorkspaceNavigationItem? item = _navigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, $"saved:{savedView.Id}", StringComparison.Ordinal));
        if (item is not null)
        {
            await SelectNavigationAsync(item);
        }
    }

    private async void NavigationItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoWorkspaceNavigationItem item })
        {
            return;
        }

        var flyout = new MenuFlyout();
        if (item.SectionId is { } sectionId)
        {
            TodoSection? section = _snapshot.Sections.FirstOrDefault(candidate => candidate.Id == sectionId);
            if (section is null)
            {
                return;
            }

            var rename = new MenuFlyoutItem { Text = _localization.T("Common.Rename") };
            rename.Click += async (_, _) =>
            {
                string? name = await PromptForNameAsync(
                    "Common.Rename",
                    "Todo.Workspace.Navigation.SectionName",
                    section.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    section.Name = name.Trim();
                    await RunMutationAsync(() => _workspace.SaveSectionAsync(section), rebuildNavigation: true);
                }
            };
            flyout.Items.Add(rename);
            var archive = new MenuFlyoutItem { Text = _localization.T("Todo.Workspace.Navigation.ArchiveSection") };
            archive.Click += async (_, _) =>
            {
                section.IsArchived = true;
                if (string.Equals(_presentation.SectionId, section.Id, StringComparison.Ordinal))
                {
                    _presentation.SectionId = null;
                }
                await RunMutationAsync(() => _workspace.SaveSectionAsync(section), rebuildNavigation: true);
            };
            flyout.Items.Add(archive);
        }
        else if (item.TagId is { } tagId)
        {
            TodoTag? tag = _snapshot.Tags.FirstOrDefault(candidate => candidate.Id == tagId);
            if (tag is null)
            {
                return;
            }

            var rename = new MenuFlyoutItem { Text = _localization.T("Common.Rename") };
            rename.Click += async (_, _) =>
            {
                string? name = await PromptForNameAsync(
                    "Common.Rename",
                    "Todo.Workspace.Navigation.TagName",
                    tag.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tag.Name = name.Trim().TrimStart('#');
                    await RunMutationAsync(() => _workspace.SaveTagAsync(tag), rebuildNavigation: true);
                }
            };
            flyout.Items.Add(rename);
            var delete = new MenuFlyoutItem { Text = _localization.T("Common.Delete") };
            delete.Click += async (_, _) => await ConfirmDeleteTagAsync(tag);
            flyout.Items.Add(delete);
        }
        else if (item.ListId is { } listId)
        {
            TodoList? list = _snapshot.Lists.FirstOrDefault(candidate => candidate.Id == listId);
            if (list is null)
            {
                return;
            }

            var rename = new MenuFlyoutItem { Text = _localization.T("Common.Rename") };
            rename.Click += async (_, _) =>
            {
                string? name = await PromptForNameAsync(
                    "Common.Rename",
                    "Todo.Workspace.Navigation.ListName",
                    list.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    list.Name = name.Trim();
                    await RunMutationAsync(() => _workspace.SaveListAsync(list), rebuildNavigation: true);
                }
            };
            flyout.Items.Add(rename);
            var addSection = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Navigation.NewSection")
            };
            addSection.Click += async (_, _) => await CreateSectionAsync(list);
            flyout.Items.Add(addSection);
            if (!list.IsSystem)
            {
                var archive = new MenuFlyoutItem { Text = _localization.T("Todo.Workspace.Navigation.ArchiveList") };
                archive.Click += async (_, _) =>
                {
                    list.IsArchived = true;
                    await RunMutationAsync(() => _workspace.SaveListAsync(list), rebuildNavigation: true);
                };
                flyout.Items.Add(archive);
            }
        }
        else if (item.Id.StartsWith("saved:", StringComparison.Ordinal))
        {
            string savedViewId = item.Id["saved:".Length..];
            TodoSavedView? view = _snapshot.SavedViews.FirstOrDefault(candidate => candidate.Id == savedViewId);
            if (view is null)
            {
                return;
            }

            var rename = new MenuFlyoutItem { Text = _localization.T("Common.Rename") };
            rename.Click += async (_, _) =>
            {
                string? name = await PromptForNameAsync(
                    "Common.Rename",
                    "Todo.Workspace.Navigation.ViewName",
                    view.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    view.Name = name.Trim();
                    await RunMutationAsync(() => _workspace.SaveSavedViewAsync(view), rebuildNavigation: true);
                }
            };
            flyout.Items.Add(rename);
            var delete = new MenuFlyoutItem { Text = _localization.T("Common.Delete") };
            delete.Click += async (_, _) =>
                await RunMutationAsync(() => _workspace.DeleteSavedViewAsync(savedViewId), rebuildNavigation: true);
            flyout.Items.Add(delete);
        }
        else if (item.SmartView is { } smartView && _settingsService is not null)
        {
            List<TodoSmartView> order = NormalizeSmartViewOrder(
                _settingsService.Settings.Todo.Organization.SmartViewOrder);
            int index = order.IndexOf(smartView);
            var moveUp = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Navigation.MoveUp"),
                IsEnabled = index > 0
            };
            moveUp.Click += async (_, _) => await MoveSmartViewAsync(smartView, -1);
            flyout.Items.Add(moveUp);
            var moveDown = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Navigation.MoveDown"),
                IsEnabled = index >= 0 && index < order.Count - 1
            };
            moveDown.Click += async (_, _) => await MoveSmartViewAsync(smartView, 1);
            flyout.Items.Add(moveDown);
        }

        if (flyout.Items.Count > 0)
        {
            flyout.ShowAt((FrameworkElement)sender);
        }
        e.Handled = true;
    }

    private async Task CreateSectionAsync(TodoList list)
    {
        string? name = await PromptForNameAsync(
            "Todo.Workspace.Navigation.NewSection",
            "Todo.Workspace.Navigation.SectionName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TodoSection? created = null;
        await RunMutationAsync(
            async () => created = await _workspace.EnsureSectionAsync(list.Id, name),
            rebuildNavigation: true);
        TodoWorkspaceNavigationItem? item = _navigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.SectionId, created?.Id, StringComparison.Ordinal));
        if (item is not null)
        {
            await SelectNavigationAsync(item);
        }
    }

    private async Task ConfirmDeleteTagAsync(TodoTag tag)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localization.T("Todo.Workspace.Navigation.DeleteTag"),
            Content = new TextBlock
            {
                Text = _localization.Format("Todo.Workspace.Navigation.DeleteTagDescription", tag.Name),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localization.T("Common.Delete"),
            CloseButtonText = _localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.Equals(_presentation.TagId, tag.Id, StringComparison.Ordinal))
        {
            _presentation.TagId = null;
        }
        await RunMutationAsync(() => _workspace.DeleteTagAsync(tag.Id), rebuildNavigation: true);
    }

    private async Task MoveSmartViewAsync(TodoSmartView view, int delta)
    {
        if (_settingsService is null)
        {
            return;
        }

        List<TodoSmartView> order = NormalizeSmartViewOrder(
            _settingsService.Settings.Todo.Organization.SmartViewOrder);
        int oldIndex = order.IndexOf(view);
        int newIndex = Math.Clamp(oldIndex + delta, 0, order.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        order.RemoveAt(oldIndex);
        order.Insert(newIndex, view);
        _settingsService.Settings.Todo.Organization.SmartViewOrder = order;
        await _settingsService.SaveAsync();
        BuildNavigation();
    }

    private static List<TodoSmartView> NormalizeSmartViewOrder(IEnumerable<TodoSmartView> configured)
    {
        TodoSmartView[] supported =
        [
            TodoSmartView.Today,
            TodoSmartView.Inbox,
            TodoSmartView.Planned,
            TodoSmartView.Unscheduled,
            TodoSmartView.Important,
            TodoSmartView.Completed
        ];
        return configured.Concat(supported).Where(supported.Contains).Distinct().ToList();
    }

    private async Task<string?> PromptForNameAsync(string titleKey, string placeholderKey, string? value = null)
    {
        var textBox = new TextBox
        {
            Text = value ?? string.Empty,
            PlaceholderText = _localization.T(placeholderKey),
            MinWidth = 280,
            MaxLength = 120
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localization.T(titleKey),
            Content = textBox,
            PrimaryButtonText = _localization.T("Common.Save"),
            CloseButtonText = _localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? textBox.Text.Trim()
            : null;
    }

    private void RefreshNavigationButtonStates()
    {
        foreach (Button button in _navigationPanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is TodoWorkspaceNavigationItem item &&
                            string.Equals(item.Id, _selectedNavigation?.Id, StringComparison.Ordinal);
            button.Background = selected
                ? TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush")
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void RefreshRows()
    {
        IEnumerable<TodoTask> tasks;
        TodoQuery effectiveQuery;
        if (_selectedNavigation?.IsTrash == true)
        {
            tasks = _snapshot.Tasks.Where(task => task.DeletedAt is not null)
                .OrderByDescending(task => task.DeletedAt);
            effectiveQuery = new TodoQuery { IncludeDeleted = true };
        }
        else
        {
            effectiveQuery = BuildCurrentQuery();
            tasks = TodoQueryService.Apply(_snapshot, effectiveQuery, DateTimeOffset.Now);
            if (effectiveQuery.SmartView == TodoSmartView.Today)
            {
                tasks = PresentTodayRecurrences(tasks, DateOnly.FromDateTime(DateTime.Today));
            }
        }

        bool explicitlyShowingCompleted = effectiveQuery.Status == TodoTaskStatus.Completed ||
                                          effectiveQuery.SmartView == TodoSmartView.Completed;
        if (_presentation.CompletedVisibility == TodoCompletedVisibility.Hidden &&
            !explicitlyShowingCompleted)
        {
            tasks = tasks.Where(task => task.Status != TodoTaskStatus.Completed);
        }
        else if (_presentation.CompletedVisibility == TodoCompletedVisibility.Collapsed &&
                 !_showCollapsedCompleted &&
                 !explicitlyShowingCompleted)
        {
            tasks = tasks.Where(task => task.Status != TodoTaskStatus.Completed);
        }

        if (_activeColorMarkerFilter is { } colorMarker)
        {
            tasks = tasks.Where(task => string.Equals(
                TodoItem.NormalizeColorMarker(task.ColorMarker),
                colorMarker,
                StringComparison.Ordinal));
        }

        _rows.Clear();
        foreach (TodoTask task in tasks)
        {
            _rows.Add(new TodoWorkspaceTaskRow(
                task,
                _snapshot,
                _localization,
                _presentation,
                ToggleTaskCompletionAsync,
                ApplyTaskColorMarkerAsync)
            {
                IsSelectionMode = _isBatchSelectionMode
            });
        }

        bool canReorder = _selectedNavigation?.IsTrash != true &&
                          (_selectedNavigation?.SmartView == TodoSmartView.Today ||
                           _selectedNavigation?.ListId is not null);
        _taskList.CanReorderItems = canReorder;
        _taskList.ReorderMode = canReorder ? ListViewReorderMode.Enabled : ListViewReorderMode.Disabled;
    }

    private IEnumerable<TodoTask> PresentTodayRecurrences(IEnumerable<TodoTask> queriedTasks, DateOnly today)
    {
        TodoTask[] materialized = queriedTasks.ToArray();
        HashSet<string> fixedSeriesIds = materialized
            .Where(task => task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (fixedSeriesIds.Count == 0)
        {
            return materialized;
        }

        Dictionary<string, TodoTask> occurrences = GetOccurrences(today, today)
            .Where(occurrence => fixedSeriesIds.Contains(occurrence.SeriesTaskId))
            .GroupBy(occurrence => occurrence.SeriesTaskId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Task,
                StringComparer.Ordinal);
        return materialized.SelectMany(task =>
            fixedSeriesIds.Contains(task.Id)
                ? occurrences.TryGetValue(task.Id, out TodoTask? occurrence)
                    ? new[] { occurrence }
                    : []
                : new[] { task });
    }

    private TodoQuery BuildCurrentQuery()
    {
        return _activeFilterQuery is null
            ? BuildNavigationQuery()
            : CloneQuery(_activeFilterQuery);
    }

    private TodoQuery BuildNavigationQuery()
    {
        if (_selectedNavigation?.Id.StartsWith("saved:", StringComparison.Ordinal) == true)
        {
            string id = _selectedNavigation.Id["saved:".Length..];
            TodoSavedView? savedView = _snapshot.SavedViews.FirstOrDefault(view =>
                string.Equals(view.Id, id, StringComparison.Ordinal));
            if (savedView is not null)
            {
                return CloneQuery(savedView.Query);
            }
        }

        return new TodoQuery
        {
            SmartView = _selectedNavigation?.SmartView,
            ListId = _selectedNavigation?.ListId,
            SectionId = _selectedNavigation?.SectionId,
            TagIds = _selectedNavigation?.TagId is { } tagId ? [tagId] : [],
            SortMode = _selectedNavigation?.SmartView == TodoSmartView.Today
                ? TodoSortMode.Smart
                : _selectedNavigation?.ListId is not null || _selectedNavigation?.SectionId is not null
                    ? TodoSortMode.Manual
                    : TodoSortMode.Smart,
            IncludeDeleted = false
        };
    }

    private void RenderCurrentView()
    {
        _mainHost.Children.Clear();
        if (_layoutMode == TodoWorkspaceLayoutMode.Micro)
        {
            _mainHost.Children.Add(BuildMicroView());
            return;
        }

        UIElement content = _presentation.DisplayMode switch
        {
            TodoDisplayMode.Agenda => BuildAgendaView(),
            TodoDisplayMode.Month => BuildMonthView(),
            TodoDisplayMode.Week => BuildTimelineView(7),
            TodoDisplayMode.Day => BuildTimelineView(1),
            _ => BuildListView()
        };
        _mainHost.Children.Add(content);
    }

    private UIElement BuildListView()
    {
        _bulkBar.Children.Clear();
        _bulkBar.Visibility = Visibility.Collapsed;
        _emptyStateHost.Children.Clear();
        if (_rows.Count == 0)
        {
            _emptyStateHost.Children.Add(BuildEmptyState());
            _emptyStateHost.Visibility = Visibility.Visible;
        }
        else
        {
            _emptyStateHost.Visibility = Visibility.Collapsed;
        }

        return _listViewRoot;
    }

    private UIElement BuildEmptyState()
    {
        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 7,
            Children =
            {
                new FontIcon
                {
                    Glyph = _selectedNavigation?.IsTrash == true ? "\uE74D" : "\uE73E",
                    FontSize = 28,
                    Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
                },
                new TextBlock
                {
                    Text = _localization.T(_selectedNavigation?.IsTrash == true
                        ? "Todo.Workspace.EmptyTrash"
                        : "Todo.Workspace.Empty"),
                    Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
                }
            }
        };
    }

    private async Task ToggleTaskCompletionAsync(TodoTask task)
    {
        await RunMutationAsync(async () =>
        {
            if (task.Status != TodoTaskStatus.Completed)
            {
                await _workspace.CompleteTaskAsync(task);
                return;
            }

            TodoTask editable = task.CloneTask();
            editable.Status = TodoTaskStatus.Open;
            editable.IsCompleted = false;
            editable.CompletedAt = null;
            editable.SnoozedUntil = null;
            await _workspace.SaveTaskAsync(editable);
        });
    }

    private async Task RunMutationAsync(Func<Task> mutation, bool rebuildNavigation = false)
    {
        _localMutationDepth++;
        try
        {
            await mutation();
            await RefreshSnapshotAsync(rebuildNavigation);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Mutation failed: {ex}");
            ShowFeedback(_localization.T("Todo.Workspace.SaveFailed"));
        }
        finally
        {
            _localMutationDepth--;
        }
    }

    private void Workspace_Changed(object? sender, TodoWorkspaceChangedEventArgs e)
    {
        if (_disposed || _localMutationDepth > 0 || !_isLoaded)
        {
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        CancellationToken token = _refreshCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(80, token);
                DispatcherQueue.TryEnqueue(async () => await RefreshSnapshotAsync(
                    rebuildNavigation: e.Kind is TodoWorkspaceChangeKind.StructureChanged or TodoWorkspaceChangeKind.Cleared));
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void SettingsService_SettingsChanged()
    {
        if (_disposed || !_isLoaded)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await RefreshCalendarSourcesAsync(force: true);
            BuildNavigation();
            RefreshRows();
            ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
            RenderCurrentView();
            RenderDetailPane();
        });
    }

    private async Task RefreshCalendarSourcesAsync(bool force = false)
    {
        if (_calendarSourceService is null)
        {
            _externalCalendarEvents = [];
            _calendarSourceRangeStart = null;
            _calendarSourceRangeEnd = null;
            return;
        }

        if (!force &&
            _calendarSourceRangeStart is { } cachedStart &&
            _calendarSourceRangeEnd is { } cachedEnd &&
            _selectedDate >= cachedStart.AddMonths(2) &&
            _selectedDate <= cachedEnd.AddMonths(-2))
        {
            return;
        }

        DateOnly start = _selectedDate.AddYears(-1);
        DateOnly end = _selectedDate.AddYears(1);
        _externalCalendarEvents = await _calendarSourceService.LoadEventsAsync(start, end);
        _calendarSourceRangeStart = start;
        _calendarSourceRangeEnd = end;
    }

    private async void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoWorkspaceNavigationItem item })
        {
            return;
        }

        await SelectNavigationAsync(item);
    }

    private Task SelectNavigationAsync(TodoWorkspaceNavigationItem item)
    {
        _selectedNavigation = item;
        _activeFilterQuery = null;
        _showCollapsedCompleted = false;
        _presentation.ListId = item.ListId;
        _presentation.SectionId = item.SectionId;
        _presentation.TagId = item.TagId;
        _presentation.SavedViewId = item.Id.StartsWith("saved:", StringComparison.Ordinal)
            ? item.Id["saved:".Length..]
            : null;
        if (item.SmartView is { } smartView)
        {
            _presentation.SmartView = smartView;
        }
        _presentationStore.SaveDebounced(_config, _presentation);
        RefreshNavigationButtonStates();
        RefreshRows();
        RenderCurrentView();
        UpdateToolbarText();
        return Task.CompletedTask;
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        HashSet<string> primaryIds = ["today", "inbox", "planned", "important"];
        foreach (TodoWorkspaceNavigationItem item in _navigationItems.Where(item => primaryIds.Contains(item.Id)))
        {
            flyout.Items.Add(CreateNavigationMenuItem(item));
        }

        TodoWorkspaceNavigationItem[] secondary = _navigationItems.Where(item =>
            item.IsTrash || item.SmartView is TodoSmartView.Unscheduled or TodoSmartView.Completed).ToArray();
        if (secondary.Length > 0)
        {
            var more = new MenuFlyoutSubItem
            {
                Text = _localization.T("Widget.Tooltip.More"),
                Icon = new FontIcon { Glyph = "\uE712" }
            };
            foreach (TodoWorkspaceNavigationItem item in secondary)
            {
                more.Items.Add(CreateNavigationMenuItem(item));
            }
            flyout.Items.Add(more);
        }

        TodoWorkspaceNavigationItem[] personal = _navigationItems.Where(item =>
            !primaryIds.Contains(item.Id) &&
            !item.IsTrash &&
            item.SmartView is null &&
            item.TagId is null).ToArray();
        if (personal.Length > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            foreach (TodoWorkspaceNavigationItem item in personal)
            {
                flyout.Items.Add(CreateNavigationMenuItem(item));
            }
        }

        flyout.ShowAt(_navigationButton);
    }

    private MenuFlyoutItem CreateNavigationMenuItem(TodoWorkspaceNavigationItem item)
    {
        var menuItem = new MenuFlyoutItem
        {
            Text = item.Name,
            Icon = new FontIcon { Glyph = item.Glyph },
            Tag = item
        };
        menuItem.Click += async (_, _) => await SelectNavigationAsync(item);
        return menuItem;
    }

    private async void DisplayModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoDisplayMode mode })
        {
            return;
        }

        await SetDisplayModeAsync(mode);
    }

    private Task SetDisplayModeAsync(TodoDisplayMode mode)
    {
        if (_presentation.DisplayMode == mode)
        {
            return Task.CompletedTask;
        }

        _presentation.DisplayMode = mode;
        _presentationStore.SaveDebounced(_config, _presentation);
        UpdateToolbarText();
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
        return Task.CompletedTask;
    }

    private async void PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int delta })
        {
            return;
        }

        if (delta == 0)
        {
            _selectedDate = DateOnly.FromDateTime(DateTime.Today);
            _visiblePeriod = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
        }
        else if (_presentation.DisplayMode == TodoDisplayMode.Month)
        {
            _visiblePeriod = _visiblePeriod.AddMonths(delta);
            _selectedDate = _visiblePeriod;
        }
        else
        {
            int days = _presentation.DisplayMode == TodoDisplayMode.Week ? 7 : 1;
            _selectedDate = _selectedDate.AddDays(delta * days);
            _visiblePeriod = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
        }

        _presentation.SelectedDate = _selectedDate;
        _presentationStore.SaveDebounced(_config, _presentation);
        await RefreshCalendarSourcesAsync();
        RenderCurrentView();
        RenderDetailPane();
        UpdateToolbarText();
    }

    private async void QuickAddButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateQuickTaskAsync(openDetails: false);
    }

    private async void QuickAddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        e.Handled = true;
        await CreateQuickTaskAsync(openDetails: shift);
    }

    private void QuickAddTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshQuickAddPresentation();
    }

    private TodoQuickAddResult ParseQuickAddInput()
    {
        TodoQuickRecordSettings? quickRecord = _settingsService?.Settings.Todo.QuickRecord;
        return quickRecord?.NaturalLanguageParsing == false
            ? new TodoQuickAddResult(
                _quickAddTextBox.Text,
                _quickAddTextBox.Text.Trim(),
                null,
                TodoPriority.None,
                null,
                [],
                [])
            : _quickAddParser.Parse(_quickAddTextBox.Text);
    }

    private void RefreshQuickAddPresentation()
    {
        TodoQuickAddResult parsed = ParseQuickAddInput();
        _quickAddTokens.Children.Clear();
        AddQuickAddContextChip();
        foreach (TodoQuickAddToken token in parsed.Tokens)
        {
            var chip = new Button
            {
                Tag = token,
                MinWidth = 0,
                Padding = new Thickness(7, 2, 7, 2),
                Background = TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush"),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = token.DisplayText, FontSize = 11 },
                        new FontIcon { Glyph = "\uE711", FontSize = 8 }
                    }
                }
            };
            chip.CornerRadius = new CornerRadius(10);
            ToolTipService.SetToolTip(chip, _localization.T("Todo.Workspace.QuickAdd.RemoveToken"));
            chip.Click += QuickAddToken_Click;
            _quickAddTokens.Children.Add(chip);
        }

        _quickAddTokens.Visibility = _quickAddTokens.Children.Count == 0 ||
                                     _layoutMode == TodoWorkspaceLayoutMode.Micro
            ? Visibility.Collapsed
            : Visibility.Visible;
        _quickAddButton.IsEnabled = !string.IsNullOrWhiteSpace(parsed.Title);
    }

    private void QuickAddToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoQuickAddToken token })
        {
            return;
        }

        int index = _quickAddTextBox.Text.IndexOf(token.SourceText, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            return;
        }

        _quickAddTextBox.Text = _quickAddTextBox.Text.Remove(index, token.SourceText.Length);
        _quickAddTextBox.SelectionStart = Math.Min(index, _quickAddTextBox.Text.Length);
        _quickAddTextBox.Focus(FocusState.Programmatic);
    }

    private async Task CreateQuickTaskAsync(bool openDetails)
    {
        TodoQuickRecordSettings? quickRecord = _settingsService?.Settings.Todo.QuickRecord;
        TodoQuickAddResult parsed = ParseQuickAddInput();
        if (string.IsNullOrWhiteSpace(parsed.Title))
        {
            return;
        }

        if (parsed.Schedule is null && _quickAddContextDate is { } contextDate)
        {
            parsed = parsed with
            {
                Schedule = new TodoSchedule
                {
                    Date = contextDate,
                    Time = _quickAddContextTime,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DurationMinutes = _quickAddContextTime is null
                        ? null
                        : _presentation.DefaultDurationMinutes
                }
            };
        }

        TodoTask? created = null;
        await RunMutationAsync(async () => created = await _workspace.CreateParsedTaskAsync(parsed), rebuildNavigation: true);
        if (created is null)
        {
            return;
        }

        ClearQuickAddContext(refresh: false);
        _quickAddTextBox.Text = string.Empty;
        if (quickRecord?.ContinuousEntry != false && !openDetails)
        {
            _quickAddTextBox.Focus(FocusState.Programmatic);
        }
        if (openDetails || quickRecord?.ContinuousEntry == false)
        {
            SelectTask(created.Id);
        }
    }

    internal void FocusQuickAdd()
    {
        _quickAddTextBox.Focus(FocusState.Programmatic);
        _quickAddTextBox.SelectAll();
    }

    internal void RevealTask(string? taskId, bool preferToday)
    {
        if (preferToday)
        {
            TodoWorkspaceNavigationItem? today = _navigationItems.FirstOrDefault(item =>
                item.SmartView == TodoSmartView.Today);
            if (today is not null)
            {
                _ = SelectNavigationAsync(today);
            }
        }

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            SelectTask(taskId);
        }
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TodoWorkspaceTaskRow row)
        {
            return;
        }

        bool control = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        if (_isBatchSelectionMode)
        {
            if (shift)
            {
                SelectTaskRange(row, preserveExisting: control);
            }
            else
            {
                _selectionAnchorId = row.Id;
            }
            return;
        }

        if (control || shift)
        {
            EnterBatchSelectionMode(row, selectRange: shift);
        }
        else
        {
            _selectionAnchorId = row.Id;
            SelectTask(row.Id);
        }
    }

    private void EnterBatchSelectionMode(
        TodoWorkspaceTaskRow? row = null,
        bool selectRange = false)
    {
        if (!_isBatchSelectionMode)
        {
            _isBatchSelectionMode = true;
            _taskList.SelectionMode = ListViewSelectionMode.Multiple;
            _taskList.IsMultiSelectCheckBoxEnabled = false;
            foreach (TodoWorkspaceTaskRow taskRow in _rows)
            {
                taskRow.IsSelectionMode = true;
            }
        }

        if (row is not null)
        {
            if (selectRange)
            {
                SelectTaskRange(row, preserveExisting: IsKeyDown(VirtualKey.Control));
            }
            else
            {
                if (!_taskList.SelectedItems.Contains(row))
                {
                    _taskList.SelectedItems.Add(row);
                }
                _selectionAnchorId = row.Id;
            }
        }

        UpdateBulkSelectionBar();
        _taskList.Focus(FocusState.Programmatic);
    }

    private void ExitBatchSelectionMode()
    {
        if (!_isBatchSelectionMode)
        {
            return;
        }

        _isBatchSelectionMode = false;
        _taskList.SelectedItems.Clear();
        _taskList.SelectionMode = ListViewSelectionMode.None;
        _taskList.IsMultiSelectCheckBoxEnabled = false;
        foreach (TodoWorkspaceTaskRow taskRow in _rows)
        {
            taskRow.IsSelectionMode = false;
        }
        _bulkBar.Children.Clear();
        _bulkBar.Visibility = Visibility.Collapsed;
    }

    private void SelectTaskRange(TodoWorkspaceTaskRow row, bool preserveExisting)
    {
        int targetIndex = _rows.IndexOf(row);
        int anchorIndex = string.IsNullOrWhiteSpace(_selectionAnchorId)
            ? targetIndex
            : _rows.ToList().FindIndex(candidate =>
                string.Equals(candidate.Id, _selectionAnchorId, StringComparison.Ordinal));
        if (targetIndex < 0)
        {
            return;
        }
        if (anchorIndex < 0)
        {
            anchorIndex = targetIndex;
        }

        if (!preserveExisting)
        {
            _taskList.SelectedItems.Clear();
        }
        int first = Math.Min(anchorIndex, targetIndex);
        int last = Math.Max(anchorIndex, targetIndex);
        for (int index = first; index <= last; index++)
        {
            TodoWorkspaceTaskRow candidate = _rows[index];
            if (!_taskList.SelectedItems.Contains(candidate))
            {
                _taskList.SelectedItems.Add(candidate);
            }
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SelectionGutter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            !e.GetCurrentPoint(_listViewRoot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _lassoPointerId = e.Pointer.PointerId;
        _lassoOrigin = e.GetCurrentPoint(_listViewRoot).Position;
        _lassoStarted = false;
        _lassoBaseIds = IsKeyDown(VirtualKey.Control)
            ? _taskList.SelectedItems.OfType<TodoWorkspaceTaskRow>()
                .Select(row => row.Id)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _selectionGutter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SelectionGutter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lassoPointerId != e.Pointer.PointerId)
        {
            return;
        }

        Point current = e.GetCurrentPoint(_listViewRoot).Position;
        if (!_lassoStarted &&
            Math.Abs(current.X - _lassoOrigin.X) < 6 &&
            Math.Abs(current.Y - _lassoOrigin.Y) < 6)
        {
            return;
        }

        if (!_lassoStarted)
        {
            _lassoStarted = true;
            EnterBatchSelectionMode();
            if (_lassoBaseIds.Count == 0)
            {
                _taskList.SelectedItems.Clear();
            }
            _lassoRectangle.Visibility = Visibility.Visible;
        }

        double left = Math.Min(_lassoOrigin.X, current.X);
        double top = Math.Min(_lassoOrigin.Y, current.Y);
        double width = Math.Abs(current.X - _lassoOrigin.X);
        double height = Math.Abs(current.Y - _lassoOrigin.Y);
        Canvas.SetLeft(_lassoRectangle, left);
        Canvas.SetTop(_lassoRectangle, top);
        _lassoRectangle.Width = width;
        _lassoRectangle.Height = height;
        UpdateLassoSelection(new Rect(left, top, width, height));
        e.Handled = true;
    }

    private void UpdateLassoSelection(Rect selectionBounds)
    {
        var selectedIds = new HashSet<string>(_lassoBaseIds, StringComparer.Ordinal);
        for (int index = 0; index < _rows.Count; index++)
        {
            if (_taskList.ContainerFromIndex(index) is not ListViewItem container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = container.TransformToVisual(_listViewRoot)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (ArgumentException)
            {
                continue;
            }

            bool intersects = bounds.Left <= selectionBounds.Right &&
                              bounds.Right >= selectionBounds.Left &&
                              bounds.Top <= selectionBounds.Bottom &&
                              bounds.Bottom >= selectionBounds.Top;
            if (intersects)
            {
                selectedIds.Add(_rows[index].Id);
            }
        }

        _updatingLassoSelection = true;
        try
        {
            _taskList.SelectedItems.Clear();
            foreach (TodoWorkspaceTaskRow row in _rows.Where(candidate => selectedIds.Contains(candidate.Id)))
            {
                _taskList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _updatingLassoSelection = false;
        }
        UpdateBulkSelectionBar();
    }

    private void SelectionGutter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_lassoPointerId != e.Pointer.PointerId)
        {
            return;
        }
        _selectionGutter.ReleasePointerCapture(e.Pointer);
        EndLassoSelection();
        e.Handled = true;
    }

    private void SelectionGutter_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndLassoSelection();

    private void SelectionGutter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndLassoSelection();

    private void EndLassoSelection()
    {
        _lassoPointerId = null;
        _lassoStarted = false;
        _lassoRectangle.Visibility = Visibility.Collapsed;
        _lassoRectangle.Width = 0;
        _lassoRectangle.Height = 0;
        _lassoBaseIds.Clear();
    }

    private void SelectTask(string taskId)
    {
        _selectedTaskId = taskId;
        _selectedTaskOverride = null;
        _selectedOccurrenceDate = null;
        _recurrenceEditScope = TodoRecurrenceEditScope.Occurrence;
        RenderDetailPane();
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
    }

    private void SelectTask(TodoTask task, DateOnly occurrenceDate)
    {
        if (task.RecurrenceRule?.GenerationMode != TodoRecurrenceGenerationMode.FixedSchedule)
        {
            SelectTask(task.Id);
            return;
        }

        _selectedTaskId = task.Id;
        _selectedTaskOverride = task;
        _selectedOccurrenceDate = occurrenceDate;
        _recurrenceEditScope = TodoRecurrenceEditScope.Occurrence;
        RenderDetailPane();
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
    }

    private void RefreshSelectedOccurrence()
    {
        if (_selectedOccurrenceDate is not { } date || string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            _selectedTaskOverride = null;
            return;
        }

        TodoOccurrence? occurrence = GetOccurrences(date, date).FirstOrDefault(candidate =>
            string.Equals(candidate.SeriesTaskId, _selectedTaskId, StringComparison.Ordinal) ||
            string.Equals(candidate.Task.Id, _selectedTaskId, StringComparison.Ordinal));
        _selectedTaskOverride = occurrence?.Task;
        if (occurrence is null)
        {
            _selectedOccurrenceDate = null;
        }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLassoSelection)
        {
            return;
        }
        UpdateBulkSelectionBar();
    }

    private void UpdateBulkSelectionBar()
    {
        int count = _taskList.SelectedItems.Count;
        _bulkBar.Children.Clear();
        _bulkBar.Visibility = _isBatchSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_isBatchSelectionMode)
        {
            return;
        }

        _bulkBar.Children.Add(new TextBlock
        {
            Text = _localization.Format("Todo.Workspace.SelectedCount", count),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 6, 0)
        });
        if (_selectedNavigation?.IsTrash == true)
        {
            if (count > 0)
            {
                AddBulkButton("\uE777", "Todo.Workspace.Restore", BulkRestoreAsync);
                AddBulkButton("\uE74D", "Todo.Workspace.DeletePermanently", BulkPurgeAsync);
            }
        }
        else if (count > 0)
        {
            AddBulkButton("\uE73E", "Todo.Workspace.Complete", BulkCompleteAsync);
            AddBulkButton("\uE74D", "Common.Delete", BulkDeleteAsync);
        }
        var close = new Button();
        ConfigureIconButton(close, "\uE711", 30);
        ToolTipService.SetToolTip(close, _localization.T("Common.Close"));
        close.Click += (_, _) => ExitBatchSelectionMode();
        _bulkBar.Children.Add(close);
    }

    private void AddBulkButton(string glyph, string tooltipKey, Func<Task> action)
    {
        var button = new Button();
        ConfigureIconButton(button, glyph, 30);
        ToolTipService.SetToolTip(button, _localization.T(tooltipKey));
        button.Click += async (_, _) => await action();
        _bulkBar.Children.Add(button);
    }

    private async Task BulkCompleteAsync()
    {
        string[] ids = _taskList.SelectedItems.OfType<TodoWorkspaceTaskRow>()
            .Select(row => row.Id).ToArray();
        await RunMutationAsync(async () =>
        {
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            var regularTasks = new List<TodoTask>();
            foreach (string id in ids)
            {
                TodoTask? task = await _workspace.GetTaskAsync(id);
                if (task is null)
                {
                    continue;
                }

                TodoWorkspaceTaskRow? presentedRow = _taskList.SelectedItems
                    .OfType<TodoWorkspaceTaskRow>()
                    .FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.Ordinal));
                TodoTask presented = presentedRow?.Task ?? task;
                if (task.RecurrenceRule is not null)
                {
                    await _workspace.CompleteTaskAsync(presented);
                    continue;
                }

                task.Status = TodoTaskStatus.Completed;
                task.IsCompleted = true;
                task.CompletedAt = completedAt;
                task.SnoozedUntil = null;
                regularTasks.Add(task);
            }
            if (regularTasks.Count > 0)
            {
                await _workspace.SaveTasksAsync(regularTasks);
            }
        });
        ExitBatchSelectionMode();
    }

    private async Task BulkDeleteAsync()
    {
        string[] ids = _taskList.SelectedItems.OfType<TodoWorkspaceTaskRow>()
            .Select(row => row.Id).ToArray();
        await DeleteTasksAsync(ids);
        ExitBatchSelectionMode();
    }

    private async Task BulkRestoreAsync()
    {
        string[] ids = _taskList.SelectedItems.OfType<TodoWorkspaceTaskRow>()
            .Select(row => row.Id).ToArray();
        await RunMutationAsync(() => _workspace.RestoreTasksAsync(ids));
        ExitBatchSelectionMode();
    }

    private async Task BulkPurgeAsync()
    {
        string[] ids = _taskList.SelectedItems.OfType<TodoWorkspaceTaskRow>()
            .Select(row => row.Id).ToArray();
        await ConfirmPurgeTasksAsync(ids);
        ExitBatchSelectionMode();
    }

    private async Task DeleteTasksAsync(IEnumerable<string> taskIds)
    {
        string[] ids = taskIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await RunMutationAsync(() => _workspace.DeleteTasksAsync(ids));
        _lastDeletedTaskIds = ids;
        _selectedTaskId = null;
        ShowFeedback(
            _localization.Format("Todo.Workspace.DeletedCount", ids.Length),
            _localization.T("Common.Undo"),
            UndoDeleteAsync);
    }

    private async Task UndoDeleteAsync()
    {
        string[] ids = _lastDeletedTaskIds;
        _lastDeletedTaskIds = [];
        await RunMutationAsync(() => _workspace.RestoreTasksAsync(ids));
        HideFeedback();
    }

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        string[] ids = e.Items.OfType<TodoWorkspaceTaskRow>().Select(row => row.Id).ToArray();
        if (ids.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.Properties["DeskBox.Todo.TaskIds.v2"] = JsonSerializer.Serialize(ids);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(string.Join(Environment.NewLine, e.Items.OfType<TodoWorkspaceTaskRow>().Select(row => row.Title)));
    }

    private async void TaskList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (!_taskList.CanReorderItems || _rows.Count == 0)
        {
            return;
        }

        bool todayOrder = _selectedNavigation?.SmartView == TodoSmartView.Today;
        TodoTask[] reordered = _rows.Select((row, index) =>
        {
            TodoTask task = row.Task.CloneTask();
            if (todayOrder)
            {
                task.TodaySortRank = index;
            }
            else
            {
                task.SortOrder = index;
            }
            return task;
        }).ToArray();
        await RunMutationAsync(() => _workspace.SaveTasksAsync(reordered));
    }

    private void TaskList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TodoWorkspaceTaskRow row)
        {
            return;
        }
        ShowTaskContextMenu(row.Task, row, null, _taskList, e.GetPosition(_taskList));
        e.Handled = true;
    }

    private async void TaskList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsKeyDown(VirtualKey.Control);
        if (control && e.Key == VirtualKey.A)
        {
            EnterBatchSelectionMode();
            _taskList.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_isBatchSelectionMode)
            {
                ExitBatchSelectionMode();
                e.Handled = true;
                return;
            }
            if (_selectedTaskId is not null)
            {
                CloseDetailPane();
            }
            e.Handled = true;
            return;
        }

        if (!_isBatchSelectionMode &&
            e.Key == VirtualKey.Enter &&
            ((e.OriginalSource as FrameworkElement)?.DataContext as TodoWorkspaceTaskRow ??
             _taskList.SelectedItem as TodoWorkspaceTaskRow) is { } selected)
        {
            SelectTask(selected.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Delete && _taskList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            if (_selectedNavigation?.IsTrash == true)
            {
                await BulkPurgeAsync();
            }
            else
            {
                await BulkDeleteAsync();
            }
        }
    }

    private async Task ConfirmPurgeTasksAsync(IReadOnlyCollection<string> taskIds)
    {
        if (taskIds.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localization.T("Todo.Workspace.DeletePermanently"),
            Content = _localization.T("Todo.Workspace.DeletePermanently.Description"),
            PrimaryButtonText = _localization.T("Common.Delete"),
            CloseButtonText = _localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunMutationAsync(() => _workspace.PurgeTasksAsync(taskIds));
        if (_selectedTaskId is not null && taskIds.Contains(_selectedTaskId, StringComparer.Ordinal))
        {
            _selectedTaskId = null;
            _selectedTaskOverride = null;
            RenderDetailPane();
        }
    }

    private async Task ScheduleTaskAsync(TodoTask task, DateOnly date, TimeOnly? time)
    {
        await RunMutationAsync(async () =>
        {
            TodoTask editable = task.CloneTask();
            editable.Schedule = new TodoSchedule
            {
                Date = date,
                Time = time,
                TimeZoneId = TimeZoneInfo.Local.Id,
                DurationMinutes = time is null ? null : _presentation.DefaultDurationMinutes
            };
            await _workspace.SaveTaskAsync(editable);
        });
    }

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            if (TryFindColorDropTarget(
                    e.OriginalSource as DependencyObject,
                    out _,
                    out _))
            {
                e.Handled = true;
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.IsGlyphVisible = true;
            }
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void Surface_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        try
        {
            if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
            {
                string? colorMarker = TodoItem.NormalizeColorMarker(
                    await DeskBoxDragData.TryGetTodoColorMarkerAsync(e.DataView));
                if (colorMarker is not null &&
                    TryFindColorDropTarget(
                        e.OriginalSource as DependencyObject,
                        out TodoTask targetTask,
                        out DateOnly? occurrenceDate))
                {
                    e.Handled = true;
                    e.AcceptedOperation = DataPackageOperation.Link;
                    await ApplyTaskColorMarkerAsync(targetTask, occurrenceDate, colorMarker);
                }
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                if (SelectedTask is { } task)
                {
                    bool copy = string.Equals(
                        _settingsService?.Settings.Todo.NotesAndAttachments.AttachmentStorageMode,
                        "Copy",
                        StringComparison.OrdinalIgnoreCase);
                    await RunMutationAsync(async () =>
                    {
                        foreach (IStorageItem item in items)
                        {
                            await _workspace.AddAttachmentAsync(task.Id, item.Path, copy);
                        }
                    });
                }
                else
                {
                    IStorageItem? first = items.FirstOrDefault();
                    if (first is not null)
                    {
                        bool copy = string.Equals(
                            _settingsService?.Settings.Todo.NotesAndAttachments.AttachmentStorageMode,
                            "Copy",
                            StringComparison.OrdinalIgnoreCase);
                        TodoTask? created = null;
                        await RunMutationAsync(async () =>
                        {
                            created = await _workspace.CreateTaskAsync(first.Name);
                            foreach (IStorageItem item in items)
                            {
                                await _workspace.AddAttachmentAsync(created.Id, item.Path, copy);
                            }
                        });
                        if (created is not null)
                        {
                            SelectTask(created.Id);
                        }
                    }
                }
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                _quickAddTextBox.Text = await e.DataView.GetTextAsync();
                _quickAddTextBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Drop failed: {ex.Message}");
        }
    }

    private void UpdateToolbarText()
    {
        _viewTitle.Text = _selectedNavigation?.Name ?? _localization.T("Todo.Workspace.Today");
        _periodTitle.Text = _presentation.DisplayMode switch
        {
            TodoDisplayMode.Month => _visiblePeriod.ToString("yyyy MMMM"),
            TodoDisplayMode.Week => $"{GetWeekStart(_selectedDate):M/d} – {GetWeekStart(_selectedDate).AddDays(6):M/d}",
            TodoDisplayMode.Day => _selectedDate.ToString("yyyy/M/d dddd"),
            _ => _localization.Format("Todo.Workspace.TaskCount", _rows.Count)
        };
        _periodTitle.Visibility = string.IsNullOrWhiteSpace(_periodTitle.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _periodButtons.Visibility = _presentation.DisplayMode == TodoDisplayMode.List
            ? Visibility.Collapsed
            : Visibility.Visible;
        _viewModeText.Text = GetDisplayModeName(_presentation.DisplayMode);
        _viewModeIcon.Glyph = GetDisplayModeGlyph(_presentation.DisplayMode);
        AutomationProperties.SetName(_viewModeButton, _viewModeText.Text);
    }

    private void Localization_LanguageChanged()
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            RefreshQuickAddContextLocalization();
            await RefreshSnapshotAsync(rebuildNavigation: true);
        });
    }

    private void ShowFeedback(string message, string? actionText = null, Func<Task>? action = null)
    {
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
        _feedbackCts = new CancellationTokenSource();
        _feedbackText.Text = message;
        _feedbackAction.Content = actionText;
        _feedbackAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        _feedbackAction.Click -= FeedbackAction_Click;
        _feedbackAction.Tag = action;
        _feedbackAction.Click += FeedbackAction_Click;
        _feedbackBar.Visibility = Visibility.Visible;
        CancellationToken token = _feedbackCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4500, token);
                DispatcherQueue.TryEnqueue(HideFeedback);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async void FeedbackAction_Click(object sender, RoutedEventArgs e)
    {
        if (_feedbackAction.Tag is Func<Task> action)
        {
            await action();
        }
    }

    private void HideFeedback()
    {
        _feedbackBar.Visibility = Visibility.Collapsed;
        _feedbackAction.Tag = null;
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        MenuFlyout flyout = BuildViewModeFlyout();
        flyout.ShowAt(_viewModeButton);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var view = new MenuFlyoutSubItem
        {
            Text = GetDisplayModeName(_presentation.DisplayMode),
            Icon = new FontIcon { Glyph = GetDisplayModeGlyph(_presentation.DisplayMode) }
        };
        AddViewModeItems(view.Items);
        flyout.Items.Add(view);

        var select = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.SelectTasks"),
            Icon = new FontIcon { Glyph = "\uE762" }
        };
        select.Click += (_, _) => EnterBatchSelectionMode();
        flyout.Items.Add(select);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.WidgetSettings"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settings.Click += (_, _) => ShowWidgetSettingsFlyout();
        flyout.Items.Add(settings);
        flyout.ShowAt(_settingsButton);
    }

    private MenuFlyout BuildViewModeFlyout()
    {
        var flyout = new MenuFlyout();
        AddViewModeItems(flyout.Items);
        return flyout;
    }

    private void AddViewModeItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (TodoDisplayMode mode in Enum.GetValues<TodoDisplayMode>())
        {
            var item = new MenuFlyoutItem
            {
                Text = GetDisplayModeName(mode),
                Icon = new FontIcon
                {
                    Glyph = mode == _presentation.DisplayMode ? "\uE73E" : GetDisplayModeGlyph(mode)
                },
                Tag = mode
            };
            item.Click += async (_, _) => await SetDisplayModeAsync((TodoDisplayMode)item.Tag);
            items.Add(item);
        }
    }

    private string GetDisplayModeName(TodoDisplayMode mode) => _localization.T(mode switch
    {
        TodoDisplayMode.List => "Todo.Workspace.View.List",
        TodoDisplayMode.Agenda => "Todo.Workspace.View.Agenda",
        TodoDisplayMode.Month => "Todo.Workspace.View.Month",
        TodoDisplayMode.Week => "Todo.Workspace.View.Week",
        _ => "Todo.Workspace.View.Day"
    });

    private static string GetDisplayModeGlyph(TodoDisplayMode mode) => mode switch
    {
        TodoDisplayMode.List => "\uE8FD",
        TodoDisplayMode.Agenda => "\uE8A5",
        TodoDisplayMode.Month => "\uE787",
        TodoDisplayMode.Week => "\uE823",
        _ => "\uE8BF"
    };

    private static void ConfigureIconButton(Button button, string glyph, double size)
    {
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
        button.Padding = new Thickness(0);
        button.Content = new FontIcon { Glyph = glyph, FontSize = Math.Max(12, size * 0.42) };
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
    }

    private DateOnly GetWeekStart(DateOnly date)
    {
        DayOfWeek firstDay = _settingsService?.Settings.Todo.Calendar.WeekStart switch
        {
            "Monday" => DayOfWeek.Monday,
            "Sunday" => DayOfWeek.Sunday,
            "Saturday" => DayOfWeek.Saturday,
            _ => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek
        };
        int delta = (7 + (int)date.DayOfWeek - (int)firstDay) % 7;
        return date.AddDays(-delta);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.Changed -= Workspace_Changed;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        }
        _localization.LanguageChanged -= Localization_LanguageChanged;
        _mainHost.SizeChanged -= MainHost_SizeChanged;
        _splitter.ManipulationCompleted -= Splitter_ManipulationCompleted;
        _splitter.KeyUp -= Splitter_KeyUp;
        _splitter.DoubleTapped -= Splitter_DoubleTapped;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
        DisposeDetailState();
    }
}

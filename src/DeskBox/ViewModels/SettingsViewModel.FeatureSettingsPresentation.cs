using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public IReadOnlyList<SettingsOption> VisibleQuickCaptureDefaultViewOptions =>
        AvailableQuickCaptureDefaultViews
            .Select((value, index) => new { value, index })
            .Where(item => IsQuickCaptureTabSelected(item.value))
            .Select(item => new SettingsOption(
                item.value,
                AvailableQuickCaptureDefaultViewDisplayNames[item.index]))
            .ToArray();

    public IReadOnlyList<SettingsOption> VisibleTodoDefaultFilterOptions =>
        AvailableTodoDefaultFilters
            .Select((value, index) => new { value, index })
            .Where(item => IsTodoTabSelected(item.value))
            .Select(item => new SettingsOption(
                item.value,
                AvailableTodoDefaultFilterDisplayNames[item.index]))
            .ToArray();

    public string QuickCaptureVisibleTabsText => JoinSelectedQuickCaptureTabs();

    public string TodoVisibleTabsText => JoinSelectedTodoTabs();

    public string QuickCaptureTabsSummaryText => QuickCaptureShowTabBar
        ? QuickCaptureVisibleTabsText
        : _localizationService.T("Settings.Toggle.Off");

    public string TodoTabsSummaryText => TodoShowTabBar
        ? TodoVisibleTabsText
        : _localizationService.T("Settings.Toggle.Off");

    public string QuickCaptureLayoutSummaryText
    {
        get
        {
            string layout = GetQuickCaptureWideLayoutDisplayName(QuickCaptureWideLayout);
            return QuickCaptureWideLayout == SettingsService.QuickCaptureWideLayoutSinglePane
                ? layout
                : $"{layout} · {GetQuickCaptureWideOpenModeDisplayName(QuickCaptureWideOpenMode)}";
        }
    }

    public string TodoLayoutSummaryText => GetTodoLayoutModeDisplayName(SelectedTodoLayoutMode);

    public string QuickCaptureContentSummaryText => string.Join(
        " · ",
        GetItemPreviewLineCountDisplayName(QuickCaptureItemPreviewLineCount),
        GetQuickCaptureFormatDisplayName(QuickCaptureEditorFormat),
        GetEditorEnterBehaviorDisplayName(QuickCaptureEditorEnterBehavior));

    public string TodoContentSummaryText => string.Join(
        " · ",
        GetItemPreviewLineCountDisplayName(TodoItemPreviewLineCount),
        SelectedTodoNewTaskPositionText,
        GetEditorEnterBehaviorDisplayName(TodoEditorEnterBehavior));

    public string TodoReminderSummaryText => TodoReminderEnabled
        ? SelectedTodoReminderOffsetMinutesText
        : _localizationService.T("Settings.Toggle.Off");

    public string TodoFooterDisplaySummaryText
    {
        get
        {
            var selected = new List<string>(2);
            if (TodoShowFooterStats)
            {
                selected.Add(_localizationService.T("Settings.Todo.ShowFooterStats.Title"));
            }

            if (TodoShowClearCompletedButton)
            {
                selected.Add(_localizationService.T("Settings.Todo.ShowClearCompleted.Title"));
            }

            return selected.Count == 0
                ? _localizationService.T("Settings.Toggle.Off")
                : string.Join(" · ", selected);
        }
    }

    public Visibility QuickCaptureWideOptionsVisibility =>
        QuickCaptureWideLayout == SettingsService.QuickCaptureWideLayoutSinglePane
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility QuickCaptureRemoteImagesVisibility =>
        QuickCaptureEditorFormat == SettingsService.QuickCaptureFormatMarkdown
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility TodoWideOptionsVisibility =>
        SelectedTodoLayoutMode == SettingsService.TodoLayoutModeSinglePane
            ? Visibility.Collapsed
            : Visibility.Visible;

    public int QuickCaptureTabStyleIndex
    {
        get => SelectedQuickCaptureTabStyle == SettingsService.WidgetTabStyleButton ? 1 : 0;
        set => SelectedQuickCaptureTabStyle = value == 1
            ? SettingsService.WidgetTabStyleButton
            : SettingsService.WidgetTabStylePivot;
    }

    public int TodoTabStyleIndex
    {
        get => SelectedTodoTabStyle == SettingsService.WidgetTabStyleButton ? 1 : 0;
        set => SelectedTodoTabStyle = value == 1
            ? SettingsService.WidgetTabStyleButton
            : SettingsService.WidgetTabStylePivot;
    }

    public bool IsQuickCaptureTabSelected(string view) =>
        NormalizeQuickCaptureDefaultView(view) switch
        {
            SettingsService.QuickCaptureDefaultViewPinned => QuickCaptureShowPinnedTab,
            SettingsService.QuickCaptureDefaultViewRecent => QuickCaptureShowRecentTab,
            _ => QuickCaptureShowRecordsTab
        };

    public bool CanToggleQuickCaptureTab(string view) =>
        !IsQuickCaptureTabSelected(view) || CountSelectedQuickCaptureTabs() > 1;

    public void ToggleQuickCaptureTab(string view)
    {
        bool selected = IsQuickCaptureTabSelected(view);
        if (selected && !CanToggleQuickCaptureTab(view))
        {
            return;
        }

        switch (NormalizeQuickCaptureDefaultView(view))
        {
            case SettingsService.QuickCaptureDefaultViewPinned:
                QuickCaptureShowPinnedTab = !selected;
                break;
            case SettingsService.QuickCaptureDefaultViewRecent:
                QuickCaptureShowRecentTab = !selected;
                break;
            default:
                QuickCaptureShowRecordsTab = !selected;
                break;
        }
    }

    public string GetQuickCaptureTabDisplayName(string view) =>
        GetQuickCaptureDefaultViewDisplayName(view);

    public bool IsTodoTabSelected(string filter) =>
        NormalizeTodoDefaultFilter(filter) switch
        {
            SettingsService.TodoDefaultFilterActive => TodoShowActiveTab,
            SettingsService.TodoDefaultFilterToday => TodoShowTodayTab,
            SettingsService.TodoDefaultFilterThisWeek => TodoShowThisWeekTab,
            SettingsService.TodoDefaultFilterThisMonth => TodoShowThisMonthTab,
            SettingsService.TodoDefaultFilterImportant => TodoShowImportantTab,
            SettingsService.TodoDefaultFilterCompleted => TodoShowCompletedTab,
            _ => TodoShowAllTab
        };

    public bool CanToggleTodoTab(string filter) =>
        !IsTodoTabSelected(filter) || CountSelectedTodoTabs() > 1;

    public void ToggleTodoTab(string filter)
    {
        bool selected = IsTodoTabSelected(filter);
        if (selected && !CanToggleTodoTab(filter))
        {
            return;
        }

        switch (NormalizeTodoDefaultFilter(filter))
        {
            case SettingsService.TodoDefaultFilterActive:
                TodoShowActiveTab = !selected;
                break;
            case SettingsService.TodoDefaultFilterToday:
                TodoShowTodayTab = !selected;
                break;
            case SettingsService.TodoDefaultFilterThisWeek:
                TodoShowThisWeekTab = !selected;
                break;
            case SettingsService.TodoDefaultFilterThisMonth:
                TodoShowThisMonthTab = !selected;
                break;
            case SettingsService.TodoDefaultFilterImportant:
                TodoShowImportantTab = !selected;
                break;
            case SettingsService.TodoDefaultFilterCompleted:
                TodoShowCompletedTab = !selected;
                break;
            default:
                TodoShowAllTab = !selected;
                break;
        }
    }

    public string GetTodoTabDisplayName(string filter) =>
        GetTodoDefaultFilterDisplayName(filter);

    public bool IsTodoFooterDisplayOptionSelected(string option) => option switch
    {
        "Stats" => TodoShowFooterStats,
        "ClearCompleted" => TodoShowClearCompletedButton,
        _ => false
    };

    public void ToggleTodoFooterDisplayOption(string option)
    {
        switch (option)
        {
            case "Stats":
                TodoShowFooterStats = !TodoShowFooterStats;
                break;
            case "ClearCompleted":
                TodoShowClearCompletedButton = !TodoShowClearCompletedButton;
                break;
        }
    }

    public string GetTodoFooterDisplayOptionName(string option) => option switch
    {
        "Stats" => _localizationService.T("Settings.Todo.ShowFooterStats.Title"),
        "ClearCompleted" => _localizationService.T("Settings.Todo.ShowClearCompleted.Title"),
        _ => string.Empty
    };

    private string GetItemPreviewLineCountDisplayName(int lineCount) => lineCount == 1
        ? _localizationService.T("Settings.ContentEditor.PreviewLines.Option.Single")
        : _localizationService.Format(
            "Settings.ContentEditor.PreviewLines.Option.Multiple",
            lineCount);

    private string JoinSelectedQuickCaptureTabs() => string.Join(
        " · ",
        AvailableQuickCaptureDefaultViews
            .Where(IsQuickCaptureTabSelected)
            .Select(GetQuickCaptureTabDisplayName));

    private string JoinSelectedTodoTabs() => string.Join(
        " · ",
        AvailableTodoDefaultFilters
            .Where(IsTodoTabSelected)
            .Select(GetTodoTabDisplayName));

    private int CountSelectedQuickCaptureTabs() =>
        AvailableQuickCaptureDefaultViews.Count(IsQuickCaptureTabSelected);

    private int CountSelectedTodoTabs() =>
        AvailableTodoDefaultFilters.Count(IsTodoTabSelected);

    private void RefreshQuickCaptureTabsPresentation()
    {
        OnPropertyChanged(nameof(QuickCaptureVisibleTabsText));
        OnPropertyChanged(nameof(QuickCaptureTabsSummaryText));
        OnPropertyChanged(nameof(VisibleQuickCaptureDefaultViewOptions));
    }

    private void RefreshTodoTabsPresentation()
    {
        OnPropertyChanged(nameof(TodoVisibleTabsText));
        OnPropertyChanged(nameof(TodoTabsSummaryText));
        OnPropertyChanged(nameof(VisibleTodoDefaultFilterOptions));
    }

    private void RefreshQuickCaptureContentPresentation()
    {
        OnPropertyChanged(nameof(QuickCaptureContentSummaryText));
        OnPropertyChanged(nameof(QuickCaptureRemoteImagesVisibility));
    }

    private void RefreshTodoContentPresentation()
    {
        OnPropertyChanged(nameof(TodoContentSummaryText));
    }
}

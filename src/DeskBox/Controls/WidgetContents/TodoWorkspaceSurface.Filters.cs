using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

internal sealed partial class TodoWorkspaceSurface
{
    private readonly Button _filterButton = new();
    private TodoQuery? _activeFilterQuery;

    private void FilterButton_Click(object sender, RoutedEventArgs e) => ShowFilterFlyout(_filterButton);

    private void ShowFilterFlyout(FrameworkElement? anchor = null)
    {
        TodoQuery current = BuildCurrentQuery();
        TodoQuery navigationQuery = BuildNavigationQuery();
        var panel = new StackPanel
        {
            Width = 340,
            Padding = new Thickness(12),
            Spacing = 9
        };
        panel.Children.Add(new TextBlock
        {
            Text = _localization.T("Todo.Workspace.Filter.Title"),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var allTasks = new ToggleSwitch
        {
            Header = _localization.T("Todo.Workspace.Filter.AllTasks"),
            IsOn = current.SmartView == TodoSmartView.All
        };
        panel.Children.Add(allTasks);

        var search = new TextBox
        {
            Header = _localization.T("Todo.Workspace.Filter.Search"),
            PlaceholderText = _localization.T("Todo.Workspace.Filter.SearchPlaceholder"),
            Text = current.SearchText ?? string.Empty
        };
        panel.Children.Add(search);

        var list = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Filter.List"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var anyList = new ComboBoxItem
        {
            Content = _localization.T("Todo.Workspace.Filter.Any"),
            Tag = string.Empty
        };
        list.Items.Add(anyList);
        list.SelectedItem = anyList;
        foreach (TodoList todoList in _snapshot.Lists.Where(item => !item.IsArchived).OrderBy(item => item.SortRank))
        {
            var option = new ComboBoxItem { Content = GetListDisplayName(todoList), Tag = todoList.Id };
            list.Items.Add(option);
            if (string.Equals(current.ListId, todoList.Id, StringComparison.Ordinal))
            {
                list.SelectedItem = option;
            }
        }
        panel.Children.Add(list);

        var status = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Filter.Status"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddFilterOption(status, _localization.T("Todo.Workspace.Filter.Any"), null, current.Status is null);
        foreach (TodoTaskStatus value in Enum.GetValues<TodoTaskStatus>())
        {
            AddFilterOption(
                status,
                _localization.T($"Todo.Workspace.Status.{value}"),
                value,
                current.Status == value);
        }
        panel.Children.Add(status);

        var priority = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Filter.MinimumPriority"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddFilterOption(priority, _localization.T("Todo.Workspace.Filter.Any"), null, current.MinimumPriority is null);
        foreach (TodoPriority value in new[] { TodoPriority.Low, TodoPriority.Medium, TodoPriority.High })
        {
            AddFilterOption(
                priority,
                _localization.T($"Todo.Workspace.Priority.{value}"),
                value,
                current.MinimumPriority == value);
        }
        panel.Children.Add(priority);

        var sort = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Filter.Sort"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (TodoSortMode value in Enum.GetValues<TodoSortMode>())
        {
            AddFilterOption(
                sort,
                _localization.T($"Todo.Workspace.Sort.{value}"),
                value,
                current.SortMode == value);
        }
        panel.Children.Add(sort);

        var range = new Grid { ColumnSpacing = 7 };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rangeStart = new CalendarDatePicker
        {
            Header = _localization.T("Todo.Workspace.Filter.From"),
            Date = current.RangeStart is { } start
                ? new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue))
                : null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        range.Children.Add(rangeStart);
        var rangeEnd = new CalendarDatePicker
        {
            Header = _localization.T("Todo.Workspace.Filter.To"),
            Date = current.RangeEnd is { } end
                ? new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue))
                : null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(rangeEnd, 1);
        range.Children.Add(rangeEnd);
        panel.Children.Add(range);

        var tagChecks = new List<CheckBox>();
        if (_snapshot.Tags.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _localization.T("Todo.Workspace.Filter.Tags"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var tags = new StackPanel { Spacing = 1 };
            foreach (TodoTag tag in _snapshot.Tags.OrderBy(item => item.SortRank))
            {
                var check = new CheckBox
                {
                    Content = $"#{tag.Name}",
                    Tag = tag.Id,
                    IsChecked = current.TagIds.Contains(tag.Id, StringComparer.Ordinal)
                };
                tagChecks.Add(check);
                tags.Children.Add(check);
            }
            panel.Children.Add(tags);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var clear = new Button { Content = _localization.T("Todo.Workspace.Filter.Clear") };
        TodoWorkspaceTaskCard.ApplyStyle(clear, "SubtleButtonStyle");
        var save = new Button { Content = _localization.T("Todo.Workspace.Filter.SaveView") };
        TodoWorkspaceTaskCard.ApplyStyle(save, "SubtleButtonStyle");
        var apply = new Button { Content = _localization.T("Todo.Workspace.Filter.Apply") };
        actions.Children.Add(clear);
        actions.Children.Add(save);
        actions.Children.Add(apply);
        panel.Children.Add(actions);

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new ScrollViewer
            {
                MaxHeight = 590,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            }
        };

        TodoQuery ReadQuery()
        {
            TodoQuery query = CloneQuery(navigationQuery);
            query.SmartView = allTasks.IsOn ? TodoSmartView.All : navigationQuery.SmartView;
            string? selectedListId = (list.SelectedItem as ComboBoxItem)?.Tag as string;
            query.ListId = string.IsNullOrWhiteSpace(selectedListId) ? null : selectedListId;
            if (!string.Equals(query.ListId, navigationQuery.ListId, StringComparison.Ordinal))
            {
                query.SectionId = null;
            }
            query.SearchText = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim();
            query.Status = (status.SelectedItem as ComboBoxItem)?.Tag is TodoTaskStatus selectedStatus
                ? selectedStatus
                : null;
            query.MinimumPriority = (priority.SelectedItem as ComboBoxItem)?.Tag is TodoPriority selectedPriority
                ? selectedPriority
                : null;
            query.SortMode = (sort.SelectedItem as ComboBoxItem)?.Tag is TodoSortMode selectedSort
                ? selectedSort
                : TodoSortMode.Smart;
            query.RangeStart = rangeStart.Date is { } from
                ? DateOnly.FromDateTime(from.LocalDateTime)
                : null;
            query.RangeEnd = rangeEnd.Date is { } to
                ? DateOnly.FromDateTime(to.LocalDateTime)
                : null;
            if (query.RangeStart is { } fromDate && query.RangeEnd is { } toDate && fromDate > toDate)
            {
                (query.RangeStart, query.RangeEnd) = (query.RangeEnd, query.RangeStart);
            }
            query.TagIds = tagChecks.Where(check => check.IsChecked == true)
                .Select(check => (string)check.Tag)
                .ToList();
            query.IncludeDeleted = false;
            return query;
        }

        clear.Click += (_, _) =>
        {
            flyout.Hide();
            ApplyActiveFilter(null);
        };
        apply.Click += (_, _) =>
        {
            flyout.Hide();
            ApplyActiveFilter(ReadQuery());
        };
        save.Click += async (_, _) =>
        {
            ApplyActiveFilter(ReadQuery());
            flyout.Hide();
            await SaveCurrentViewAsync();
        };
        flyout.ShowAt(anchor ?? _filterButton);
    }

    private static void AddFilterOption(ComboBox comboBox, string text, object? value, bool selected)
    {
        var item = new ComboBoxItem { Content = text, Tag = value };
        comboBox.Items.Add(item);
        if (selected)
        {
            comboBox.SelectedItem = item;
        }
    }

    private void ApplyActiveFilter(TodoQuery? query)
    {
        _activeFilterQuery = query is null ? null : CloneQuery(query);
        _showCollapsedCompleted = false;
        RefreshRows();
        RenderCurrentView();
        RenderDetailPane();
        UpdateToolbarText();
    }

    private void UpdateFilterButtonState()
    {
        _filterButton.Opacity = _activeFilterQuery is null ? 0.72 : 1;
        _filterButton.Background = _activeFilterQuery is null
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush");
        string name = _activeFilterQuery is null
            ? _localization.T("Todo.Workspace.Filter.Title")
            : _localization.T("Todo.Workspace.Filter.Active");
        ToolTipService.SetToolTip(_filterButton, name);
        AutomationProperties.SetName(_filterButton, name);
    }

    private static TodoQuery CloneQuery(TodoQuery source) => new()
    {
        SmartView = source.SmartView,
        ListId = source.ListId,
        SectionId = source.SectionId,
        TagIds = [.. source.TagIds],
        MinimumPriority = source.MinimumPriority,
        Status = source.Status,
        RangeStart = source.RangeStart,
        RangeEnd = source.RangeEnd,
        SearchText = source.SearchText,
        SortMode = source.SortMode,
        IncludeDeleted = source.IncludeDeleted
    };
}

using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

internal sealed partial class TodoWorkspaceSurface
{
    private CancellationTokenSource? _notesSaveCts;
    private TextBox? _notesEditor;
    private TodoMarkdownPresenter? _notesPreview;
    private string? _activeNotesTaskId;
    private bool _buildingDetail;

    private void RenderDetailPane()
    {
        _buildingDetail = true;
        try
        {
            _detailHost.Children.Clear();
            TodoTask? task = SelectedTask;
            if (task is null)
            {
                _detailHost.Children.Add(BuildDaySummaryPane());
                return;
            }

            _detailHost.Children.Add(task.DeletedAt is null
                ? BuildTaskDetail(task)
                : BuildTrashTaskDetail(task));
        }
        finally
        {
            _buildingDetail = false;
        }
    }

    private UIElement BuildDaySummaryPane()
    {
        var root = new Grid { Padding = new Thickness(10), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock
        {
            Text = FormatCalendarDate(_selectedDate),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var panel = new StackPanel { Spacing = 4 };
        foreach (TodoOccurrence occurrence in GetOccurrences(_selectedDate, _selectedDate))
        {
            panel.Children.Add(CreateTaskCard(occurrence.Task, occurrence.Date));
        }
        foreach (TodoCalendarEvent calendarEvent in GetExternalEvents(_selectedDate, _selectedDate))
        {
            panel.Children.Add(BuildExternalEventCard(calendarEvent));
        }
        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _localization.T("Todo.Workspace.NoTasksForDay"),
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(3, 12, 3, 3)
            });
        }
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildTaskDetail(TodoTask task)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildDetailHeader(task));
        Thickness bodyPadding = _layoutMode is TodoWorkspaceLayoutMode.Micro or TodoWorkspaceLayoutMode.Compact
            ? new Thickness(5, 5, 5, 8)
            : new Thickness(8, 6, 8, 10);
        var body = new StackPanel { Spacing = 2, Padding = bodyPadding };
        body.Children.Add(BuildTaskSummaryChips(task));
        body.Children.Add(BuildStepsSection(task));
        body.Children.Add(BuildNotesSection(task));
        if (task.Attachments.Count > 0)
        {
            body.Children.Add(BuildAttachmentsSection(task));
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        };
        SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildDetailHeader(TodoTask task)
    {
        var header = new Grid
        {
            Padding = _layoutMode is TodoWorkspaceLayoutMode.Micro or TodoWorkspaceLayoutMode.Compact
                ? new Thickness(2, 4, 2, 4)
                : new Thickness(6, 6, 6, 6),
            ColumnSpacing = _layoutMode is TodoWorkspaceLayoutMode.Micro or TodoWorkspaceLayoutMode.Compact
                ? 2
                : 4,
            BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = new Button();
        ConfigureIconButton(back, "\uE72B", 31);
        back.Click += (_, _) => CloseDetailPane();
        header.Children.Add(back);

        var complete = new CheckBox
        {
            IsChecked = task.Status == TodoTaskStatus.Completed,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = null,
            IsThreeState = false
        };
        ToolTipService.SetToolTip(complete, _localization.T(task.Status == TodoTaskStatus.Completed
            ? "Todo.Menu.MarkActive"
            : "Todo.Menu.MarkCompleted"));
        complete.Click += async (_, _) => await ToggleTaskCompletionAsync(task);
        SetColumn(complete, 1);
        header.Children.Add(complete);

        if (TodoItem.NormalizeColorMarker(task.ColorMarker) is { } colorMarker)
        {
            var colorPill = new Border
            {
                Width = TodoWorkspaceTaskCard.ColorMarkerWidth,
                Height = TodoWorkspaceTaskCard.ColorMarkerHeight,
                Margin = new Thickness(1, 0, 2, 0),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Background = CreateColorMarkerBrush(colorMarker)
            };
            SetColumn(colorPill, 2);
            header.Children.Add(colorPill);
        }

        var title = new TextBox
        {
            Text = task.Title,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxLength = 2048
        };
        title.LostFocus += async (_, _) => await SaveTitleAsync(task.Id, title.Text);
        title.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && !title.AcceptsReturn)
            {
                args.Handled = true;
                await SaveTitleAsync(task.Id, title.Text);
            }
        };
        SetColumn(title, 3);
        header.Children.Add(title);

        var more = new Button();
        ConfigureIconButton(more, "\uE712", 31);
        ToolTipService.SetToolTip(more, _localization.T("Widget.Tooltip.More"));
        more.Click += (_, _) => ShowDetailMoreMenu(more, task);
        SetColumn(more, 4);
        header.Children.Add(more);
        return header;
    }

    private UIElement BuildTaskSummaryChips(TodoTask task)
    {
        var chips = new TodoWorkspaceWrapPanel
        {
            HorizontalSpacing = 4,
            VerticalSpacing = 4
        };

        if (task.Schedule is { } schedule)
        {
            string scheduleText = schedule.Time is { } time
                ? $"{schedule.Date:M/d} {time:HH\\:mm}"
                : schedule.Date.ToString("M/d");
            chips.Children.Add(CreateTaskInfoChip(
                "\uE823",
                $"{_localization.T("Todo.Workspace.Planned")} {scheduleText}",
                anchor => ShowDetailEditorFlyout(anchor, BuildScheduleFlyoutContent(task))));
        }

        if (task.DeadlineAt is { } deadline)
        {
            chips.Children.Add(CreateTaskInfoChip(
                "\uE121",
                $"{_localization.T("Todo.Workspace.Deadline")} {deadline.ToLocalTime():M/d HH:mm}",
                anchor => ShowDetailEditorFlyout(anchor, BuildDeadlineFlyoutContent(task))));
        }

        TodoList? list = _snapshot.Lists.FirstOrDefault(candidate => candidate.Id == task.ListId);
        if (list is not null)
        {
            chips.Children.Add(CreateTaskInfoChip(
                "\uE8FD",
                GetListDisplayName(list),
                anchor => ShowDetailEditorFlyout(anchor, BuildOrganizationSection(task))));
        }

        if (task.Priority != TodoPriority.None)
        {
            chips.Children.Add(CreateTaskInfoChip(
                "\uE735",
                _localization.T($"Todo.Workspace.Priority.{task.Priority}"),
                anchor => ShowDetailEditorFlyout(anchor, BuildOrganizationSection(task))));
        }

        if (task.TagIds.Count > 0)
        {
            string tags = string.Join(" ", task.TagIds
                .Select(tagId => _snapshot.Tags.FirstOrDefault(tag => tag.Id == tagId)?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => $"#{name}"));
            if (!string.IsNullOrWhiteSpace(tags))
            {
                chips.Children.Add(CreateTaskInfoChip(
                    "\uE8EC",
                    tags,
                    anchor => ShowDetailEditorFlyout(anchor, BuildOrganizationSection(task))));
            }
        }

        if (task.Reminders.Count > 0)
        {
            chips.Children.Add(CreateTaskInfoChip(
                "\uE7E7",
                task.Reminders.Count == 1
                    ? _localization.T("Todo.Workspace.Detail.Reminders")
                    : $"{_localization.T("Todo.Workspace.Detail.Reminders")} {task.Reminders.Count}",
                anchor => ShowDetailEditorFlyout(anchor, BuildReminderSection(task))));
        }

        if (task.RecurrenceRule is { } recurrence)
        {
            chips.Children.Add(CreateTaskInfoChip(
                "\uE8EE",
                _localization.T($"Todo.Workspace.Recurrence.{recurrence.Frequency}"),
                anchor => ShowDetailEditorFlyout(anchor, BuildRecurrenceSection(task))));
        }

        chips.Children.Add(CreateTaskInfoChip(
            "\uE710",
            _localization.T("Todo.Workspace.AddInformation"),
            anchor => ShowAddInformationMenu(anchor, task)));

        return chips;
    }

    private Button CreateTaskInfoChip(string glyph, string text, Action<Button> clicked)
    {
        var button = new Button
        {
            MinWidth = 0,
            MinHeight = 28,
            MaxWidth = 190,
            Padding = new Thickness(7, 2, 8, 2),
            CornerRadius = new CornerRadius(14),
            Background = TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 11 },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 11.5,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
        button.Click += (_, _) => clicked(button);
        return button;
    }

    private UIElement BuildScheduleFlyoutContent(TodoTask task) => BuildScheduleEditor(task);

    private UIElement BuildDeadlineFlyoutContent(TodoTask task) => BuildDeadlineEditor(task);

    private void ShowDetailEditorFlyout(FrameworkElement anchor, UIElement content)
    {
        if (content is FrameworkElement element)
        {
            double available = _detailPane.ActualWidth > 0
                ? _detailPane.ActualWidth
                : ActualWidth;
            element.Width = Math.Max(120, Math.Min(320, available - 48));
        }
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = presenterStyle,
            Content = new ScrollViewer
            {
                MaxHeight = Math.Max(180, Math.Min(500, ActualHeight - 72)),
                Padding = new Thickness(6),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            }
        };
        flyout.ShowAt(anchor);
    }

    private void ShowAddInformationMenu(FrameworkElement anchor, TodoTask task)
    {
        var flyout = new MenuFlyout();
        AddInformationMenuItems(flyout.Items, anchor, task);
        flyout.ShowAt(anchor);
    }

    private void AddInformationMenuItems(
        IList<MenuFlyoutItemBase> items,
        FrameworkElement anchor,
        TodoTask task)
    {
        AddInformationMenuItem(items, "\uE823", "Todo.Workspace.Planned", anchor,
            () => BuildScheduleFlyoutContent(task));
        AddInformationMenuItem(items, "\uE121", "Todo.Workspace.Deadline", anchor,
            () => BuildDeadlineFlyoutContent(task));
        AddInformationMenuItem(items, "\uE8B7", "Todo.Workspace.Detail.Organization", anchor,
            () => BuildOrganizationSection(task));
        AddInformationMenuItem(items, "\uE7E7", "Todo.Workspace.Detail.Reminders", anchor,
            () => BuildReminderSection(task));
        AddInformationMenuItem(items, "\uE8EE", "Todo.Workspace.Detail.Recurrence", anchor,
            () => BuildRecurrenceSection(task));

        var attachment = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Detail.AddFile"),
            Icon = new FontIcon { Glyph = "\uE723" }
        };
        attachment.Click += async (_, _) => await PickAttachmentsAsync(task.Id);
        items.Add(attachment);
    }

    private void AddInformationMenuItem(
        IList<MenuFlyoutItemBase> items,
        string glyph,
        string textKey,
        FrameworkElement anchor,
        Func<UIElement> contentFactory)
    {
        var item = new MenuFlyoutItem
        {
            Text = _localization.T(textKey),
            Icon = new FontIcon { Glyph = glyph }
        };
        item.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
            ShowDetailEditorFlyout(anchor, contentFactory()));
        items.Add(item);
    }

    private void ShowDetailMoreMenu(Button anchor, TodoTask task)
    {
        var flyout = new MenuFlyout();
        var information = new MenuFlyoutSubItem
        {
            Text = _localization.T("Todo.Workspace.AddInformation"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        AddInformationMenuItems(information.Items, anchor, task);
        flyout.Items.Add(information);

        var history = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Detail.History"),
            Icon = new FontIcon { Glyph = "\uE81C" }
        };
        history.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
            ShowDetailEditorFlyout(anchor, BuildHistorySection(task)));
        flyout.Items.Add(history);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem
        {
            Text = _localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        delete.Click += async (_, _) => await DeleteDetailTaskAsync(task);
        flyout.Items.Add(delete);
        flyout.ShowAt(anchor);
    }

    private UIElement BuildOrganizationSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Organization");
        var priority = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Priority"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddLocalizedEnumItems(priority, Enum.GetValues<TodoPriority>(), "Todo.Workspace.Priority", task.Priority);
        priority.SelectionChanged += async (_, _) =>
        {
            if (!_buildingDetail && priority.SelectedItem is ComboBoxItem { Tag: TodoPriority value })
            {
                await SaveTaskFieldAsync(task.Id, editable =>
                {
                    editable.Priority = value;
                    editable.IsImportant = value == TodoPriority.High;
                });
            }
        };
        panel.Children.Add(priority);

        var list = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.List"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (TodoList todoList in _snapshot.Lists.Where(item => !item.IsArchived).OrderBy(item => item.SortRank))
        {
            var option = new ComboBoxItem
            {
                Content = GetListDisplayName(todoList),
                Tag = todoList
            };
            list.Items.Add(option);
            if (string.Equals(todoList.Id, task.ListId, StringComparison.Ordinal))
            {
                list.SelectedItem = option;
            }
        }
        list.SelectionChanged += async (_, _) =>
        {
            if (!_buildingDetail && list.SelectedItem is ComboBoxItem { Tag: TodoList value })
            {
                await SaveTaskFieldAsync(task.Id, editable =>
                {
                    editable.ListId = value.Id;
                    if (_snapshot.Sections.All(section =>
                            section.Id != editable.SectionId || section.ListId != value.Id))
                    {
                        editable.SectionId = null;
                    }
                }, rebuildNavigation: true);
            }
        };
        panel.Children.Add(list);

        var sectionPicker = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Section"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var noSection = new ComboBoxItem
        {
            Content = _localization.T("Todo.Workspace.NoSection")
        };
        sectionPicker.Items.Add(noSection);
        sectionPicker.SelectedItem = noSection;
        foreach (TodoSection section in _snapshot.Sections
                     .Where(item => item.ListId == task.ListId && !item.IsArchived)
                     .OrderBy(item => item.SortRank))
        {
            var option = new ComboBoxItem { Content = section.Name, Tag = section };
            sectionPicker.Items.Add(option);
            if (section.Id == task.SectionId)
            {
                sectionPicker.SelectedItem = option;
            }
        }
        sectionPicker.SelectionChanged += async (_, _) =>
        {
            if (!_buildingDetail && sectionPicker.SelectedItem is ComboBoxItem option)
            {
                string? sectionId = (option.Tag as TodoSection)?.Id;
                await SaveTaskFieldAsync(task.Id, editable => editable.SectionId = sectionId, rebuildNavigation: true);
            }
        };
        panel.Children.Add(sectionPicker);

        var tags = new TextBox
        {
            Header = _localization.T("Todo.Workspace.Tags"),
            PlaceholderText = _localization.T("Todo.Workspace.Tags.Placeholder"),
            Text = string.Join(", ", task.TagIds
                .Select(tagId => _snapshot.Tags.FirstOrDefault(tag => tag.Id == tagId)?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)))
        };
        tags.LostFocus += async (_, _) => await SaveTagsAsync(task.Id, tags.Text);
        panel.Children.Add(tags);
        return WrapSection(panel);
    }

    private UIElement BuildTimingSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Timing");
        panel.Children.Add(BuildScheduleEditor(task));
        panel.Children.Add(BuildDeadlineEditor(task));
        return WrapSection(panel);
    }

    private UIElement BuildScheduleEditor(TodoTask task)
    {
        var root = new StackPanel { Spacing = 5 };
        root.Children.Add(CreateFieldLabel("Todo.Workspace.Planned"));
        var row = new Grid { ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var date = new CalendarDatePicker
        {
            Date = task.Schedule is { } schedule
                ? new DateTimeOffset(schedule.Date.ToDateTime(TimeOnly.MinValue))
                : null,
            PlaceholderText = _localization.T("Todo.Workspace.NoSchedule")
        };
        row.Children.Add(date);
        var clear = new Button();
        ConfigureIconButton(clear, "\uE711", 32);
        clear.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable => editable.Schedule = null);
        SetColumn(clear, 1);
        row.Children.Add(clear);
        root.Children.Add(row);

        var timeRow = new Grid { ColumnSpacing = 6 };
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        var allDay = new ToggleSwitch
        {
            Header = _localization.T("Todo.Workspace.AllDay"),
            IsOn = task.Schedule?.Time is null
        };
        timeRow.Children.Add(allDay);
        var time = new TimePicker
        {
            Time = task.Schedule?.Time?.ToTimeSpan() ?? new TimeSpan(9, 0, 0),
            ClockIdentifier = "24HourClock",
            IsEnabled = !allDay.IsOn
        };
        allDay.Toggled += (_, _) => time.IsEnabled = !allDay.IsOn;
        SetColumn(time, 1);
        timeRow.Children.Add(time);
        var duration = new NumberBox
        {
            Header = _localization.T("Todo.Workspace.DurationMinutes"),
            Minimum = 15,
            Maximum = 480,
            SmallChange = 15,
            Value = task.Schedule?.DurationMinutes ?? _presentation.DefaultDurationMinutes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        SetColumn(duration, 2);
        timeRow.Children.Add(duration);
        root.Children.Add(timeRow);
        var save = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = _localization.T("Common.Save")
        };
        save.Click += async (_, _) =>
        {
            if (date.Date is not { } selectedDate)
            {
                return;
            }
            await SaveTaskFieldAsync(task.Id, editable => editable.Schedule = new TodoSchedule
            {
                Date = DateOnly.FromDateTime(selectedDate.LocalDateTime),
                Time = allDay.IsOn ? null : TimeOnly.FromTimeSpan(time.Time),
                TimeZoneId = TimeZoneInfo.Local.Id,
                DurationMinutes = allDay.IsOn ? null : (int)Math.Round(duration.Value)
            });
        };
        root.Children.Add(save);
        return root;
    }

    private UIElement BuildDeadlineEditor(TodoTask task)
    {
        var root = new StackPanel { Spacing = 5 };
        root.Children.Add(CreateFieldLabel("Todo.Workspace.Deadline"));
        var row = new Grid { ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var date = new CalendarDatePicker
        {
            Date = task.DeadlineAt?.ToLocalTime(),
            PlaceholderText = _localization.T("Todo.Workspace.NoDeadline")
        };
        row.Children.Add(date);
        var time = new TimePicker
        {
            Time = task.DeadlineAt?.ToLocalTime().TimeOfDay ?? new TimeSpan(23, 59, 0),
            ClockIdentifier = "24HourClock"
        };
        SetColumn(time, 1);
        row.Children.Add(time);
        var clear = new Button();
        ConfigureIconButton(clear, "\uE711", 32);
        clear.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
        {
            editable.DeadlineAt = null;
            editable.DueDate = null;
        });
        SetColumn(clear, 2);
        row.Children.Add(clear);
        root.Children.Add(row);
        var save = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = _localization.T("Common.Save")
        };
        save.Click += async (_, _) =>
        {
            if (date.Date is not { } selectedDate)
            {
                return;
            }
            DateTime local = selectedDate.LocalDateTime.Date + time.Time;
            await SaveTaskFieldAsync(task.Id, editable =>
            {
                editable.DeadlineAt = new DateTimeOffset(local).ToUniversalTime();
                editable.DueDate = editable.DeadlineAt;
            });
        };
        root.Children.Add(save);
        return root;
    }

    private UIElement BuildReminderSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Reminders");
        foreach (TodoReminderRule reminder in task.Reminders)
        {
            var row = new Grid { ColumnSpacing = 5 };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var target = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AddLocalizedEnumItems(
                target,
                Enum.GetValues<TodoReminderTarget>(),
                "Todo.Workspace.ReminderTarget",
                reminder.Target);
            row.Children.Add(target);
            var offset = new ComboBox
            {
            };
            int selectedOffset = reminder.OffsetMinutes ?? TodoWorkspaceDefaults.DefaultReminderOffsetMinutes;
            foreach (int value in new[] { 0, 5, 10, 30, 60, 1440 })
            {
                var item = new ComboBoxItem
                {
                    Tag = value,
                    Content = value == 0
                        ? _localization.T("Todo.Workspace.Reminder.AtTime")
                        : _localization.Format("Settings.Todo2.Minutes", value)
                };
                offset.Items.Add(item);
                if (value == selectedOffset)
                {
                    offset.SelectedItem = item;
                }
            }
            SetColumn(offset, 1);
            row.Children.Add(offset);
            var remove = new Button();
            ConfigureIconButton(remove, "\uE74D", 30);
            remove.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
                editable.Reminders.RemoveAll(rule => rule.Id == reminder.Id));
            SetColumn(remove, 2);
            row.Children.Add(remove);
            target.SelectionChanged += async (_, _) =>
            {
                if (_buildingDetail || target.SelectedItem is not ComboBoxItem { Tag: TodoReminderTarget value })
                {
                    return;
                }
                await SaveTaskFieldAsync(task.Id, editable =>
                {
                    TodoReminderRule? rule = editable.Reminders.FirstOrDefault(item => item.Id == reminder.Id);
                    if (rule is not null)
                    {
                        rule.Target = value;
                        if (value == TodoReminderTarget.Absolute && rule.AbsoluteAt is null)
                        {
                            rule.AbsoluteAt = DateTimeOffset.Now.AddHours(1).ToUniversalTime();
                        }
                    }
                });
            };
            offset.SelectionChanged += async (_, _) =>
            {
                if (_buildingDetail || offset.SelectedItem is not ComboBoxItem { Tag: int value })
                {
                    return;
                }
                await SaveTaskFieldAsync(task.Id, editable =>
                {
                    TodoReminderRule? rule = editable.Reminders.FirstOrDefault(item => item.Id == reminder.Id);
                    if (rule is not null) rule.OffsetMinutes = value;
                });
            };
            if (reminder.Target == TodoReminderTarget.Absolute)
            {
                DateTimeOffset localAbsolute = (reminder.AbsoluteAt ?? DateTimeOffset.Now.AddHours(1)).ToLocalTime();
                var absoluteRow = new Grid { ColumnSpacing = 5, Margin = new Thickness(0, 5, 0, 0) };
                absoluteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                absoluteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                absoluteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var absoluteDate = new CalendarDatePicker
                {
                    Date = new DateTimeOffset(localAbsolute.Date),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                absoluteRow.Children.Add(absoluteDate);
                var absoluteTime = new TimePicker
                {
                    Time = localAbsolute.TimeOfDay,
                    ClockIdentifier = "24HourClock"
                };
                SetColumn(absoluteTime, 1);
                absoluteRow.Children.Add(absoluteTime);
                var saveAbsolute = new Button();
                ConfigureIconButton(saveAbsolute, "\uE74E", 32);
                saveAbsolute.Click += async (_, _) =>
                {
                    if (absoluteDate.Date is not { } selectedDate)
                    {
                        return;
                    }
                    DateTime local = selectedDate.LocalDateTime.Date + absoluteTime.Time;
                    await SaveTaskFieldAsync(task.Id, editable =>
                    {
                        TodoReminderRule? rule = editable.Reminders.FirstOrDefault(item => item.Id == reminder.Id);
                        if (rule is not null)
                        {
                            rule.AbsoluteAt = new DateTimeOffset(local).ToUniversalTime();
                        }
                    });
                };
                SetColumn(saveAbsolute, 2);
                absoluteRow.Children.Add(saveAbsolute);
                SetRow(absoluteRow, 1);
                SetColumnSpan(absoluteRow, 3);
                row.Children.Add(absoluteRow);
            }
            panel.Children.Add(row);
        }
        var add = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = _localization.T("Todo.Workspace.AddReminder")
        };
        TodoWorkspaceTaskCard.ApplyStyle(add, "SubtleButtonStyle");
        add.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
            editable.Reminders.Add(new TodoReminderRule
            {
                Target = editable.Schedule?.Time is not null
                    ? TodoReminderTarget.Schedule
                    : TodoReminderTarget.Deadline,
                OffsetMinutes = TodoWorkspaceDefaults.DefaultReminderOffsetMinutes
            }));
        panel.Children.Add(add);
        return WrapSection(panel);
    }

    private UIElement BuildRecurrenceSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Recurrence");
        if (_selectedOccurrenceDate is not null &&
            task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
        {
            var editScope = new ComboBox
            {
                Header = _localization.T("Todo.Workspace.Recurrence.EditScope"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (TodoRecurrenceEditScope scope in Enum.GetValues<TodoRecurrenceEditScope>())
            {
                editScope.Items.Add(new ComboBoxItem
                {
                    Tag = scope,
                    Content = _localization.T($"Todo.Workspace.Recurrence.Scope.{scope}")
                });
            }
            editScope.SelectedItem = editScope.Items.OfType<ComboBoxItem>()
                .First(item => (TodoRecurrenceEditScope)item.Tag == _recurrenceEditScope);
            editScope.SelectionChanged += (_, _) =>
            {
                if (editScope.SelectedItem is ComboBoxItem { Tag: TodoRecurrenceEditScope selectedScope })
                {
                    _recurrenceEditScope = selectedScope;
                }
            };
            panel.Children.Add(editScope);
        }
        var frequency = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Recurrence.Frequency"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        frequency.Items.Add(new ComboBoxItem
        {
            Tag = "None",
            Content = _localization.T("Todo.Workspace.Recurrence.None")
        });
        foreach (TodoRecurrenceFrequency value in Enum.GetValues<TodoRecurrenceFrequency>())
        {
            frequency.Items.Add(new ComboBoxItem
            {
                Tag = value,
                Content = _localization.T($"Todo.Workspace.Recurrence.{value}")
            });
        }
        frequency.SelectedItem = frequency.Items.OfType<ComboBoxItem>().First(item =>
            task.RecurrenceRule is null
                ? item.Tag is string
                : item.Tag is TodoRecurrenceFrequency value && value == task.RecurrenceRule.Frequency);
        panel.Children.Add(frequency);
        var interval = new NumberBox
        {
            Header = _localization.T("Todo.Workspace.Recurrence.Interval"),
            Minimum = 1,
            Maximum = 999,
            Value = task.RecurrenceRule?.Interval ?? 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        panel.Children.Add(interval);
        var anchor = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Recurrence.Anchor"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        TodoRecurrenceAnchor selectedAnchor = task.RecurrenceRule?.Anchor ?? (task.Schedule is null
            ? TodoRecurrenceAnchor.Deadline
            : TodoRecurrenceAnchor.Schedule);
        AddLocalizedEnumItems(anchor, Enum.GetValues<TodoRecurrenceAnchor>(), "Todo.Workspace.Recurrence", selectedAnchor);
        panel.Children.Add(anchor);
        var generation = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Recurrence.Mode"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddLocalizedEnumItems(
            generation,
            Enum.GetValues<TodoRecurrenceGenerationMode>(),
            "Todo.Workspace.Recurrence",
            task.RecurrenceRule?.GenerationMode ?? TodoRecurrenceGenerationMode.FixedSchedule);
        panel.Children.Add(generation);
        var weekdays = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var weekdayToggles = new List<ToggleButton>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            var toggle = new ToggleButton
            {
                Tag = day,
                MinWidth = 31,
                Padding = new Thickness(4, 2, 4, 2),
                Content = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)day],
                IsChecked = task.RecurrenceRule?.WeekDays.Contains(day) == true
            };
            weekdayToggles.Add(toggle);
            weekdays.Children.Add(toggle);
        }
        panel.Children.Add(weekdays);
        var endDate = new CalendarDatePicker
        {
            Header = _localization.T("Todo.Workspace.Recurrence.EndDate"),
            Date = task.RecurrenceRule?.EndDate is { } recurrenceEnd
                ? new DateTimeOffset(recurrenceEnd.ToDateTime(TimeOnly.MinValue))
                : null
        };
        panel.Children.Add(endDate);
        var count = new NumberBox
        {
            Header = _localization.T("Todo.Workspace.Recurrence.Count"),
            Minimum = 0,
            Maximum = 10000,
            Value = task.RecurrenceRule?.OccurrenceCount ?? 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        panel.Children.Add(count);
        var save = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = _localization.T("Common.Save")
        };
        save.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
        {
            if (frequency.SelectedItem is not ComboBoxItem { Tag: TodoRecurrenceFrequency selectedFrequency })
            {
                editable.RecurrenceRule = null;
                return;
            }
            editable.RecurrenceRule = new TodoRecurrenceRule
            {
                Id = editable.RecurrenceRule?.Id ?? Guid.NewGuid().ToString("N"),
                Frequency = selectedFrequency,
                Interval = Math.Max(1, (int)Math.Round(interval.Value)),
                WeekDays = weekdayToggles.Where(toggle => toggle.IsChecked == true)
                    .Select(toggle => (DayOfWeek)toggle.Tag).ToList(),
                EndDate = endDate.Date is { } selectedEnd
                    ? DateOnly.FromDateTime(selectedEnd.LocalDateTime)
                    : null,
                OccurrenceCount = count.Value > 0 ? (int)Math.Round(count.Value) : null,
                Anchor = anchor.SelectedItem is ComboBoxItem { Tag: TodoRecurrenceAnchor anchorValue }
                    ? anchorValue
                    : TodoRecurrenceAnchor.Deadline,
                GenerationMode = generation.SelectedItem is ComboBoxItem { Tag: TodoRecurrenceGenerationMode modeValue }
                    ? modeValue
                    : TodoRecurrenceGenerationMode.FixedSchedule
            };
        });
        panel.Children.Add(save);
        return WrapSection(panel);
    }

    private UIElement BuildStepsSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Steps");
        foreach (TodoStep step in task.Steps.OrderBy(step => step.SortOrder))
        {
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = step.IsCompleted, MinWidth = 0 };
            check.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
            {
                TodoStep? target = editable.Steps.FirstOrDefault(item => item.Id == step.Id);
                if (target is not null) target.IsCompleted = check.IsChecked == true;
            });
            row.Children.Add(check);
            var text = new TextBox
            {
                Text = step.Text,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            text.LostFocus += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
            {
                TodoStep? target = editable.Steps.FirstOrDefault(item => item.Id == step.Id);
                if (target is not null && !string.IsNullOrWhiteSpace(text.Text)) target.Text = text.Text.Trim();
            });
            SetColumn(text, 1);
            row.Children.Add(text);
            var remove = new Button();
            ConfigureIconButton(remove, "\uE74D", 28);
            remove.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
                editable.Steps.RemoveAll(item => item.Id == step.Id));
            SetColumn(remove, 2);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }

        var addRow = new Grid { ColumnSpacing = 4 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = new TextBox { PlaceholderText = _localization.T("Todo.Workspace.AddStep") };
        addRow.Children.Add(input);
        var add = new Button();
        ConfigureIconButton(add, "\uE710", 32);
        add.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            string value = input.Text.Trim();
            input.Text = string.Empty;
            await SaveTaskFieldAsync(task.Id, editable => editable.Steps.Add(new TodoStep
            {
                Text = value,
                SortOrder = editable.Steps.Count
            }));
        };
        SetColumn(add, 1);
        addRow.Children.Add(add);
        panel.Children.Add(addRow);
        return WrapSection(panel);
    }

    private UIElement BuildNotesSection(TodoTask task)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = _localization.T("Todo.Workspace.Detail.Notes"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _activeNotesTaskId = task.Id;
        var notesEditor = new TextBox
        {
            Text = task.Notes ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 84,
            MaxLength = TodoMarkdownService.MaxCharacters,
            PlaceholderText = _localization.T("Todo.Workspace.Notes.SimplePlaceholder"),
            Visibility = Visibility.Collapsed
        };
        _notesEditor = notesEditor;
        var notesPreview = new TodoMarkdownPresenter(_markdownService)
        {
            Markdown = task.Notes ?? string.Empty,
            AllowRemoteImages = _settingsService?.Settings.Todo.NotesAndAttachments.AllowRemoteImages == true,
            AttachmentResolver = attachmentId => task.Attachments.FirstOrDefault(attachment =>
                string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))?.FilePath,
            MinWidth = 0,
            Margin = new Thickness(0)
        };
        _notesPreview = notesPreview;
        var emptyHint = new TextBlock
        {
            Text = _localization.T("Todo.Workspace.Notes.SimplePlaceholder"),
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = string.IsNullOrWhiteSpace(task.Notes)
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        var previewContent = new Grid();
        previewContent.Children.Add(notesPreview);
        previewContent.Children.Add(emptyHint);
        var previewHost = new Border
        {
            MinHeight = 58,
            Padding = new Thickness(6, 4, 6, 6),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = previewContent
        };
        ToolTipService.SetToolTip(previewHost, _localization.T("Todo.Workspace.Edit"));

        void ShowPreview()
        {
            notesPreview.Markdown = notesEditor.Text;
            emptyHint.Visibility = string.IsNullOrWhiteSpace(notesEditor.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            notesEditor.Visibility = Visibility.Collapsed;
            previewHost.Visibility = Visibility.Visible;
        }

        void BeginEdit()
        {
            previewHost.Visibility = Visibility.Collapsed;
            notesEditor.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(() =>
            {
                notesEditor.Focus(FocusState.Programmatic);
                notesEditor.Select(notesEditor.Text.Length, 0);
            });
        }

        previewHost.AddHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler((_, args) =>
            {
                args.Handled = true;
                BeginEdit();
            }),
            handledEventsToo: true);
        notesEditor.TextChanged += (_, _) =>
        {
            notesPreview.Markdown = notesEditor.Text;
            emptyHint.Visibility = string.IsNullOrWhiteSpace(notesEditor.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ScheduleNotesSave(task.Id, notesEditor.Text);
        };
        notesEditor.LostFocus += (_, _) => ShowPreview();
        notesEditor.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape ||
                (args.Key == VirtualKey.Enter && IsKeyDown(VirtualKey.Control)))
            {
                args.Handled = true;
                ShowPreview();
            }
        };
        var contentHost = new Grid();
        contentHost.Children.Add(notesEditor);
        contentHost.Children.Add(previewHost);
        panel.Children.Add(contentHost);
        return WrapSection(panel);
    }

    private UIElement BuildAttachmentsSection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.Attachments");
        foreach (TodoAttachment attachment in task.Attachments)
        {
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var open = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE723", FontSize = 13 },
                        new TextBlock
                        {
                            Text = attachment.DisplayName,
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                }
            };
            TodoWorkspaceTaskCard.ApplyStyle(open, "SubtleButtonStyle");
            open.Click += async (_, _) => await OpenAttachmentAsync(attachment);
            row.Children.Add(open);
            var remove = new Button();
            ConfigureIconButton(remove, "\uE74D", 30);
            remove.Click += async (_, _) => await SaveTaskFieldAsync(task.Id, editable =>
                editable.Attachments.RemoveAll(item => item.Id == attachment.Id));
            SetColumn(remove, 1);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
        var add = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = _localization.T("Todo.Detail.AddFile")
        };
        TodoWorkspaceTaskCard.ApplyStyle(add, "SubtleButtonStyle");
        add.Click += async (_, _) => await PickAttachmentsAsync(task.Id);
        panel.Children.Add(add);
        return WrapSection(panel);
    }

    private UIElement BuildHistorySection(TodoTask task)
    {
        var panel = CreateSectionPanel("Todo.Workspace.Detail.History");
        panel.Children.Add(new TextBlock
        {
            Text = _localization.Format("Todo.Detail.Created", task.CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm")),
            FontSize = 11.5,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = _localization.Format("Todo.Workspace.Updated", task.UpdatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm")),
            FontSize = 11.5,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        if (task.CompletedAt is { } completed)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _localization.Format("Todo.Workspace.CompletedAt", completed.ToLocalTime().ToString("yyyy/M/d HH:mm")),
                FontSize = 11.5,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
            });
        }
        return WrapSection(panel);
    }

    private UIElement BuildTrashTaskDetail(TodoTask task)
    {
        var panel = new StackPanel
        {
            Padding = new Thickness(14),
            Spacing = 12
        };
        var close = new Button();
        ConfigureIconButton(close, "\uE72B", 32);
        close.Click += (_, _) => CloseDetailPane();
        panel.Children.Add(close);
        panel.Children.Add(new TextBlock
        {
            Text = task.Title,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = _localization.Format(
                "Todo.Workspace.DeletedAt",
                task.DeletedAt?.ToLocalTime().ToString("yyyy/M/d HH:mm") ?? string.Empty),
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        var restore = new Button { Content = _localization.T("Todo.Workspace.Restore") };
        restore.Click += async (_, _) =>
        {
            await RunMutationAsync(() => _workspace.RestoreTaskAsync(task.Id));
            _selectedTaskId = null;
        };
        panel.Children.Add(restore);
        var purge = new Button { Content = _localization.T("Todo.Workspace.DeletePermanently") };
        purge.Click += async (_, _) => await ConfirmPurgeTaskAsync(task);
        panel.Children.Add(purge);
        return new ScrollViewer { Content = panel };
    }

    private async Task SaveTitleAsync(string taskId, string? title)
    {
        string normalized = title?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return;
        }
        await SaveTaskFieldAsync(taskId, task => task.Title = normalized);
    }

    private async Task SaveTagsAsync(string taskId, string? text)
    {
        string[] names = (text ?? string.Empty)
            .Split([',', '，', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var ids = new List<string>();
        foreach (string name in names)
        {
            TodoTag tag = await _workspace.EnsureTagAsync(name);
            ids.Add(tag.Id);
        }
        await SaveTaskFieldAsync(taskId, task => task.TagIds = ids, rebuildNavigation: true);
    }

    private async Task SaveTaskFieldAsync(
        string taskId,
        Action<TodoTask> update,
        bool rebuildNavigation = false)
    {
        await RunMutationAsync(async () =>
        {
            if (_selectedOccurrenceDate is { } occurrenceDate &&
                SelectedTask?.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule &&
                SelectedTask is { } selectedOccurrence &&
                string.Equals(selectedOccurrence.Id, taskId, StringComparison.Ordinal))
            {
                TodoTask result = await _workspace.ApplyRecurrenceEditAsync(
                    taskId,
                    occurrenceDate,
                    _recurrenceEditScope,
                    update);
                _selectedTaskId = result.Id;
                _selectedTaskOverride = result;
                if (_recurrenceEditScope != TodoRecurrenceEditScope.Series)
                {
                    _selectedOccurrenceDate = occurrenceDate;
                }
                return;
            }

            TodoTask? editable = await _workspace.GetTaskAsync(taskId, includeDeleted: true);
            if (editable is null)
            {
                return;
            }
            update(editable);
            await _workspace.SaveTaskAsync(editable);
        }, rebuildNavigation);
    }

    private void ScheduleNotesSave(string taskId, string notes)
    {
        _notesSaveCts?.Cancel();
        _notesSaveCts?.Dispose();
        _notesSaveCts = new CancellationTokenSource();
        CancellationToken token = _notesSaveCts.Token;
        string normalized = notes.Length <= TodoMarkdownService.MaxCharacters
            ? notes
            : notes[..TodoMarkdownService.MaxCharacters];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                DispatcherQueue.TryEnqueue(async () => await SaveNotesWithoutRefreshAsync(taskId, normalized));
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task SaveNotesWithoutRefreshAsync(string taskId, string notes)
    {
        if (_disposed || !string.Equals(taskId, _activeNotesTaskId, StringComparison.Ordinal))
        {
            return;
        }
        _localMutationDepth++;
        try
        {
            TodoTask? task = await _workspace.GetTaskAsync(taskId);
            if (task is null || string.Equals(task.Notes ?? string.Empty, notes, StringComparison.Ordinal))
            {
                return;
            }
            task.Notes = notes;
            await _workspace.SaveTaskAsync(task);
            TodoTask? local = _snapshot.Tasks.FirstOrDefault(item => item.Id == taskId);
            if (local is not null)
            {
                local.Notes = notes;
                local.UpdatedAt = task.UpdatedAt;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Note autosave failed: {ex.Message}");
        }
        finally
        {
            _localMutationDepth--;
        }
    }

    private async Task PickAttachmentsAsync(string taskId)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            InitializeWithWindow.Initialize(picker, owner == IntPtr.Zero ? foreground : owner);
            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            bool copy = string.Equals(
                _settingsService?.Settings.Todo.NotesAndAttachments.AttachmentStorageMode,
                "Copy",
                StringComparison.OrdinalIgnoreCase);
            await RunMutationAsync(async () =>
            {
                foreach (StorageFile file in files)
                {
                    await _workspace.AddAttachmentAsync(taskId, file.Path, copy);
                }
            });
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Add attachment failed: {ex.Message}");
        }
    }

    private static async Task OpenAttachmentAsync(TodoAttachment attachment)
    {
        if (File.Exists(attachment.FilePath))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        else if (Directory.Exists(attachment.FilePath))
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFolderAsync(folder);
        }
    }

    private async Task ConfirmPurgeTaskAsync(TodoTask task)
    {
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
        await RunMutationAsync(() => _workspace.PurgeTaskAsync(task.Id));
        _selectedTaskId = null;
        RenderDetailPane();
    }

    private async Task DeleteDetailTaskAsync(TodoTask task)
    {
        if (_selectedOccurrenceDate is not { } occurrenceDate ||
            task.RecurrenceRule?.GenerationMode != TodoRecurrenceGenerationMode.FixedSchedule)
        {
            await DeleteTasksAsync([task.Id]);
            return;
        }

        Func<Task>? undo = null;
        await RunMutationAsync(async () =>
        {
            switch (_recurrenceEditScope)
            {
                case TodoRecurrenceEditScope.Occurrence:
                    await _workspace.CancelRecurrenceOccurrenceAsync(task.Id, occurrenceDate);
                    undo = () => _workspace.RestoreRecurrenceOccurrenceAsync(task.Id, occurrenceDate);
                    break;
                case TodoRecurrenceEditScope.Future:
                    TodoTask original = await _workspace.EndRecurrenceBeforeAsync(task.Id, occurrenceDate);
                    undo = () => _workspace.SaveTaskAsync(original);
                    break;
                default:
                    await _workspace.DeleteTaskAsync(task.Id);
                    undo = async () => { await _workspace.RestoreTaskAsync(task.Id); };
                    break;
            }
        });
        _selectedTaskId = null;
        _selectedTaskOverride = null;
        _selectedOccurrenceDate = null;
        ShowFeedback(
            _localization.T("Todo.Workspace.Deleted"),
            _localization.T("Common.Undo"),
            async () =>
            {
                if (undo is not null)
                {
                    await RunMutationAsync(undo);
                }
                HideFeedback();
            });
    }

    private void CloseDetailPane()
    {
        _notesSaveCts?.Cancel();
        _activeNotesTaskId = null;
        _selectedTaskId = null;
        _selectedTaskOverride = null;
        _selectedOccurrenceDate = null;
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
        RenderDetailPane();
    }

    private StackPanel CreateSectionPanel(string titleKey)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new TextBlock
        {
            Text = _localization.T(titleKey),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        return panel;
    }

    private static Border WrapSection(StackPanel panel)
    {
        return new Border
        {
            Padding = new Thickness(1, 5, 1, 8),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = panel
        };
    }

    private TextBlock CreateFieldLabel(string key)
    {
        return new TextBlock
        {
            Text = _localization.T(key),
            FontSize = 11.5,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        };
    }

    private void AddLocalizedEnumItems<T>(
        ComboBox comboBox,
        IEnumerable<T> values,
        string keyPrefix,
        T selectedValue)
        where T : struct, Enum
    {
        foreach (T value in values)
        {
            var item = new ComboBoxItem
            {
                Tag = value,
                Content = _localization.T($"{keyPrefix}.{value}")
            };
            comboBox.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selectedValue))
            {
                comboBox.SelectedItem = item;
            }
        }
    }

    private void ShowWidgetSettingsFlyout()
    {
        var panel = new StackPanel
        {
            Width = 330,
            Padding = new Thickness(12),
            Spacing = 10
        };
        panel.Children.Add(new TextBlock
        {
            Text = _localization.T("Todo.Workspace.WidgetSettings"),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var display = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Settings.DisplayMode"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddLocalizedEnumItems(display, Enum.GetValues<TodoDisplayMode>(), "Todo.Workspace.View", _presentation.DisplayMode);
        display.SelectionChanged += async (_, _) =>
        {
            if (display.SelectedItem is ComboBoxItem { Tag: TodoDisplayMode mode })
            {
                _presentation.DisplayMode = mode;
                await SavePresentationAndRefreshAsync();
            }
        };
        panel.Children.Add(display);

        var responsive = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Settings.Responsive"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddLocalizedEnumItems(
            responsive,
            Enum.GetValues<TodoResponsivePreference>(),
            "Todo.Workspace.Settings.Responsive",
            _presentation.ResponsivePreference);
        responsive.SelectionChanged += async (_, _) =>
        {
            if (responsive.SelectedItem is ComboBoxItem { Tag: TodoResponsivePreference preference })
            {
                _presentation.ResponsivePreference = preference;
                await SavePresentationAndRefreshAsync();
            }
        };
        panel.Children.Add(responsive);
        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(CreateSettingSlider(
            "Todo.Workspace.Settings.ListSplit",
            25,
            75,
            _presentation.ListSplitRatio * 100,
            async value =>
            {
                _presentation.ListSplitRatio = value / 100;
                await SavePresentationAndRefreshAsync();
            }));
        advanced.Children.Add(CreateSettingSlider(
            "Todo.Workspace.Settings.CalendarSplit",
            35,
            80,
            _presentation.CalendarSplitRatio * 100,
            async value =>
            {
                _presentation.CalendarSplitRatio = value / 100;
                await SavePresentationAndRefreshAsync();
            }));
        panel.Children.Add(CreateSettingSlider(
            "Todo.Workspace.Settings.Density",
            75,
            135,
            _presentation.DensityScale * 100,
            async value =>
            {
                _presentation.DensityScale = value / 100;
                await SavePresentationAndRefreshAsync();
            }));

        var completed = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Settings.Completed"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddLocalizedEnumItems(
            completed,
            Enum.GetValues<TodoCompletedVisibility>(),
            "Settings.Todo2.Completed",
            _presentation.CompletedVisibility);
        completed.SelectionChanged += async (_, _) =>
        {
            if (completed.SelectedItem is ComboBoxItem { Tag: TodoCompletedVisibility visibility })
            {
                _presentation.CompletedVisibility = visibility;
                await SavePresentationAndRefreshAsync();
            }
        };
        panel.Children.Add(completed);

        var slot = new ComboBox
        {
            Header = _localization.T("Todo.Workspace.Settings.TimeSlot"),
            ItemsSource = new[] { 15, 30 },
            SelectedItem = _presentation.CalendarSlotMinutes,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        slot.SelectionChanged += async (_, _) =>
        {
            if (slot.SelectedItem is int minutes)
            {
                _presentation.CalendarSlotMinutes = minutes;
                await SavePresentationAndRefreshAsync();
            }
        };
        advanced.Children.Add(slot);
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowSchedule",
            _presentation.ShowSchedule,
            async value => { _presentation.ShowSchedule = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowDeadline",
            _presentation.ShowDeadline,
            async value => { _presentation.ShowDeadline = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowTags",
            _presentation.ShowTags,
            async value => { _presentation.ShowTags = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowStepProgress",
            _presentation.ShowStepProgress,
            async value => { _presentation.ShowStepProgress = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowAttachments",
            _presentation.ShowAttachments,
            async value => { _presentation.ShowAttachments = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.ShowWeekNumbers",
            _presentation.ShowWeekNumbers,
            async value => { _presentation.ShowWeekNumbers = value; await SavePresentationAndRefreshAsync(); }));
        advanced.Children.Add(CreateSettingToggle(
            "Todo.Workspace.Settings.UnscheduledPool",
            _presentation.ShowUnscheduledPool,
            async value => { _presentation.ShowUnscheduledPool = value; await SavePresentationAndRefreshAsync(); }));
        panel.Children.Add(new Expander
        {
            Header = _localization.T("Todo.Workspace.Settings.Advanced"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = advanced
        });

        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            }
        };
        flyout.ShowAt(_settingsButton);
    }

    private FrameworkElement CreateSettingSlider(
        string labelKey,
        double min,
        double max,
        double value,
        Func<double, Task> changed)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(CreateFieldLabel(labelKey));
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            StepFrequency = 1
        };
        slider.ValueChanged += async (_, _) => await changed(slider.Value);
        panel.Children.Add(slider);
        return panel;
    }

    private FrameworkElement CreateSettingToggle(
        string labelKey,
        bool value,
        Func<bool, Task> changed)
    {
        var toggle = new ToggleSwitch
        {
            Header = _localization.T(labelKey),
            IsOn = value
        };
        toggle.Toggled += async (_, _) => await changed(toggle.IsOn);
        return toggle;
    }

    private Task SavePresentationAndRefreshAsync()
    {
        _presentation = TodoPresentationSettingsStore.Normalize(_presentation);
        _presentationStore.SaveDebounced(_config, _presentation);
        RefreshRows();
        ApplyResponsiveLayout(_lockedWidth ?? ActualWidth, _lockedHeight ?? ActualHeight, force: true);
        UpdateToolbarText();
        return Task.CompletedTask;
    }

    private void DisposeDetailState()
    {
        _notesSaveCts?.Cancel();
        _notesSaveCts?.Dispose();
        _notesSaveCts = null;
        _notesEditor = null;
        _notesPreview = null;
        _activeNotesTaskId = null;
    }
}

using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

internal sealed partial class TodoWorkspaceSurface
{
    private DateOnly? _quickAddContextDate;
    private TimeOnly? _quickAddContextTime;

    private void Surface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;

        if (FindDataContext<TodoWorkspaceTaskRow>(source) is { } row)
        {
            ShowTaskContextMenu(row.Task, row, null, this, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (FindTaggedReference<TodoCalendarDragContext>(source) is { } dragContext)
        {
            ShowTaskContextMenu(
                dragContext.Task,
                null,
                dragContext.RecurrenceOccurrenceDate,
                this,
                e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (FindTaggedReference<TodoCalendarEvent>(source) is { } calendarEvent)
        {
            ShowExternalEventContextMenu(calendarEvent, this, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (IsWithin(source, _detailPane) && _detailPane.Visibility == Visibility.Visible)
        {
            MenuFlyout detailMenu = SelectedTask is { } selected
                ? BuildDetailContextMenu(selected)
                : BuildDateContextMenu(_selectedDate, null);
            detailMenu.ShowAt(this, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (TryFindTaggedDate(source, out DateOnly date))
        {
            TimeOnly? time = FindTimelineTime(source, e);
            BuildDateContextMenu(date, time).ShowAt(this, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        MenuFlyout menu;
        if (IsWithin(source, _navigationPane))
        {
            menu = BuildNavigationBackgroundContextMenu();
        }
        else if (IsWithin(source, _quickAddPanel))
        {
            menu = BuildQuickAddContextMenu();
        }
        else
        {
            menu = BuildViewBackgroundContextMenu();
        }

        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private MenuFlyout BuildViewBackgroundContextMenu()
    {
        var menu = new MenuFlyout();
        bool calendarView = _presentation.DisplayMode != TodoDisplayMode.List;
        var add = new MenuFlyoutItem
        {
            Text = calendarView
                ? _localization.Format(
                    "Todo.Workspace.Context.AddTaskOnDate",
                    FormatShortContextDate(_selectedDate))
                : _localization.T("Todo.AddPlaceholder"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        add.Click += (_, _) => PrimeQuickAdd(
            calendarView ? _selectedDate : null,
            null);
        menu.Items.Add(add);

        if (_presentation.DisplayMode == TodoDisplayMode.List)
        {
            var select = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.SelectTasks"),
                Icon = new FontIcon { Glyph = "\uE762" },
                IsEnabled = _rows.Count > 0
            };
            select.Click += (_, _) => EnterBatchSelectionMode();
            menu.Items.Add(select);
        }
        else
        {
            AddCalendarNavigationCommands(menu.Items, _selectedDate);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AppendViewAndFilterCommands(menu.Items);
        return menu;
    }

    private MenuFlyout BuildDateContextMenu(DateOnly date, TimeOnly? time)
    {
        var menu = new MenuFlyout();
        var add = new MenuFlyoutItem
        {
            Text = time is { } selectedTime
                ? _localization.Format(
                    "Todo.Workspace.Context.AddTaskAtTime",
                    FormatShortContextDate(date),
                    selectedTime.ToString("HH\\:mm"))
                : _localization.Format(
                    "Todo.Workspace.Context.AddTaskOnDate",
                    FormatShortContextDate(date)),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        add.Click += (_, _) => PrimeQuickAdd(date, time);
        menu.Items.Add(add);

        TodoOccurrence[] occurrences = GetOccurrences(date, date).Take(12).ToArray();
        if (occurrences.Length > 0)
        {
            var tasks = new MenuFlyoutSubItem
            {
                Text = _localization.Format(
                    "Todo.Workspace.Context.TasksOnDate",
                    occurrences.Length),
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            foreach (TodoOccurrence occurrence in occurrences)
            {
                var task = new MenuFlyoutItem
                {
                    Text = occurrence.Task.Title,
                    Icon = new FontIcon
                    {
                        Glyph = occurrence.Task.Status == TodoTaskStatus.Completed
                            ? "\uE73E"
                            : "\uE73A"
                    }
                };
                task.Click += (_, _) => SelectTask(occurrence.Task, occurrence.Date);
                tasks.Items.Add(task);
            }
            menu.Items.Add(tasks);
        }

        AddCalendarNavigationCommands(menu.Items, date);
        return menu;
    }

    private void AddCalendarNavigationCommands(
        IList<MenuFlyoutItemBase> items,
        DateOnly date)
    {
        var openDay = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Context.OpenDayView"),
            Icon = new FontIcon { Glyph = "\uE8BF" }
        };
        openDay.Click += async (_, _) =>
        {
            SelectCalendarDate(date);
            await SetDisplayModeAsync(TodoDisplayMode.Day);
        };
        items.Add(openDay);

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        var goToday = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.GoToday"),
            Icon = new FontIcon { Glyph = "\uE823" },
            IsEnabled = date != today || _visiblePeriod.Month != today.Month ||
                        _visiblePeriod.Year != today.Year
        };
        goToday.Click += (_, _) => NavigateCalendarToDate(today);
        items.Add(goToday);
    }

    private MenuFlyout BuildDetailContextMenu(TodoTask task)
    {
        var menu = new MenuFlyout();
        var complete = new MenuFlyoutItem
        {
            Text = _localization.T(task.Status == TodoTaskStatus.Completed
                ? "Todo.Menu.MarkActive"
                : "Todo.Menu.MarkCompleted"),
            Icon = new FontIcon { Glyph = "\uE73E" }
        };
        complete.Click += async (_, _) => await ToggleTaskCompletionAsync(task);
        menu.Items.Add(complete);

        var information = new MenuFlyoutSubItem
        {
            Text = _localization.T("Todo.Workspace.AddInformation"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        AddInformationMenuItems(information.Items, _detailPane, task);
        menu.Items.Add(information);
        menu.Items.Add(CreateTaskColorMarkerMenu(task, _selectedOccurrenceDate));

        var history = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Detail.History"),
            Icon = new FontIcon { Glyph = "\uE81C" }
        };
        history.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
            ShowDetailEditorFlyout(_detailPane, BuildHistorySection(task)));
        menu.Items.Add(history);
        menu.Items.Add(CreateCopyTitleMenuItem(task.Title));
        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem
        {
            Text = _localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        delete.Click += async (_, _) => await DeleteDetailTaskAsync(task);
        menu.Items.Add(delete);
        return menu;
    }

    private void ShowTaskContextMenu(
        TodoTask task,
        TodoWorkspaceTaskRow? row,
        DateOnly? occurrenceDate,
        FrameworkElement anchor,
        Point position)
    {
        var menu = new MenuFlyout();
        if (task.DeletedAt is not null || _selectedNavigation?.IsTrash == true)
        {
            var restore = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Restore"),
                Icon = new FontIcon { Glyph = "\uE777" }
            };
            restore.Click += async (_, _) =>
                await RunMutationAsync(() => _workspace.RestoreTaskAsync(task.Id));
            menu.Items.Add(restore);

            var purge = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.DeletePermanently"),
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            purge.Click += async (_, _) => await ConfirmPurgeTasksAsync([task.Id]);
            menu.Items.Add(purge);
            menu.ShowAt(anchor, position);
            return;
        }

        var open = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Context.OpenTask"),
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        open.Click += (_, _) =>
        {
            if (occurrenceDate is { } date)
            {
                SelectTask(task, date);
            }
            else
            {
                SelectTask(task.Id);
            }
        };
        menu.Items.Add(open);

        var complete = new MenuFlyoutItem
        {
            Text = _localization.T(task.Status == TodoTaskStatus.Completed
                ? "Todo.Menu.MarkActive"
                : "Todo.Menu.MarkCompleted"),
            Icon = new FontIcon { Glyph = "\uE73E" }
        };
        complete.Click += async (_, _) => await ToggleTaskCompletionAsync(task);
        menu.Items.Add(complete);

        var plan = new MenuFlyoutSubItem
        {
            Text = _localization.T("Todo.Workspace.Planned"),
            Icon = new FontIcon { Glyph = "\uE823" }
        };
        AddPlanDateMenuItem(
            plan.Items,
            "Todo.Workspace.ScheduleToday",
            DateOnly.FromDateTime(DateTime.Today),
            task,
            occurrenceDate);
        AddPlanDateMenuItem(
            plan.Items,
            "Todo.Workspace.Context.ScheduleTomorrow",
            DateOnly.FromDateTime(DateTime.Today).AddDays(1),
            task,
            occurrenceDate);
        if (task.Schedule is not null)
        {
            plan.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Context.ClearSchedule"),
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            clear.Click += async (_, _) => await ClearTaskScheduleAsync(task, occurrenceDate);
            plan.Items.Add(clear);
        }
        menu.Items.Add(plan);
        menu.Items.Add(CreateTaskColorMarkerMenu(task, occurrenceDate));
        menu.Items.Add(CreateCopyTitleMenuItem(task.Title));

        if (row is not null && _presentation.DisplayMode == TodoDisplayMode.List)
        {
            var select = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.SelectTasks"),
                Icon = new FontIcon { Glyph = "\uE762" }
            };
            select.Click += (_, _) => EnterBatchSelectionMode(row);
            menu.Items.Add(select);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem
        {
            Text = _localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        delete.Click += async (_, _) => await DeleteTaskFromContextAsync(task, occurrenceDate);
        menu.Items.Add(delete);
        menu.ShowAt(anchor, position);
    }

    private void AddPlanDateMenuItem(
        IList<MenuFlyoutItemBase> items,
        string localizationKey,
        DateOnly date,
        TodoTask task,
        DateOnly? occurrenceDate)
    {
        var item = new MenuFlyoutItem { Text = _localization.T(localizationKey) };
        item.Click += async (_, _) =>
            await ScheduleTaskFromContextAsync(task, occurrenceDate, date);
        items.Add(item);
    }

    private async Task ScheduleTaskFromContextAsync(
        TodoTask task,
        DateOnly? occurrenceDate,
        DateOnly targetDate)
    {
        if (occurrenceDate is not { } sourceDate ||
            task.RecurrenceRule?.GenerationMode != TodoRecurrenceGenerationMode.FixedSchedule)
        {
            await ScheduleTaskAsync(task, targetDate, task.Schedule?.Time);
            return;
        }

        await RunMutationAsync(() => _workspace.ApplyRecurrenceEditAsync(
            task.Id,
            sourceDate,
            TodoRecurrenceEditScope.Occurrence,
            editable => editable.Schedule = new TodoSchedule
            {
                Date = targetDate,
                Time = editable.Schedule?.Time,
                TimeZoneId = editable.Schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id,
                DurationMinutes = editable.Schedule?.DurationMinutes
            }));
    }

    private async Task ClearTaskScheduleAsync(TodoTask task, DateOnly? occurrenceDate)
    {
        await RunMutationAsync(async () =>
        {
            if (occurrenceDate is { } date &&
                task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
            {
                await _workspace.ApplyRecurrenceEditAsync(
                    task.Id,
                    date,
                    TodoRecurrenceEditScope.Occurrence,
                    editable => editable.Schedule = null);
                return;
            }

            TodoTask? editable = await _workspace.GetTaskAsync(task.Id);
            if (editable is not null)
            {
                editable.Schedule = null;
                await _workspace.SaveTaskAsync(editable);
            }
        });
    }

    private async Task DeleteTaskFromContextAsync(TodoTask task, DateOnly? occurrenceDate)
    {
        if (occurrenceDate is not { } date ||
            task.RecurrenceRule?.GenerationMode != TodoRecurrenceGenerationMode.FixedSchedule)
        {
            await DeleteTasksAsync([task.Id]);
            return;
        }

        await RunMutationAsync(() => _workspace.CancelRecurrenceOccurrenceAsync(task.Id, date));
        ShowFeedback(
            _localization.T("Todo.Workspace.Deleted"),
            _localization.T("Common.Undo"),
            async () => await RunMutationAsync(() =>
                _workspace.RestoreRecurrenceOccurrenceAsync(task.Id, date)));
    }

    private MenuFlyoutItem CreateCopyTitleMenuItem(string title)
    {
        var copy = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Context.CopyTitle"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copy.Click += (_, _) => CopyContextText(title);
        return copy;
    }

    private void ShowExternalEventContextMenu(
        TodoCalendarEvent calendarEvent,
        FrameworkElement anchor,
        Point position)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = calendarEvent.SourceName,
            Icon = new FontIcon { Glyph = "\uE787" },
            IsEnabled = false
        });
        menu.Items.Add(CreateCopyTitleMenuItem(calendarEvent.Title));
        if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
        {
            var copyLocation = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Context.CopyLocation"),
                Icon = new FontIcon { Glyph = "\uE707" }
            };
            copyLocation.Click += (_, _) => CopyContextText(calendarEvent.Location!);
            menu.Items.Add(copyLocation);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        AddCalendarNavigationCommands(menu.Items, calendarEvent.Date);
        menu.ShowAt(anchor, position);
    }

    private MenuFlyout BuildNavigationBackgroundContextMenu()
    {
        var menu = new MenuFlyout();
        var newList = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Navigation.NewList"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        newList.Click += async (_, _) => await CreateListAsync();
        menu.Items.Add(newList);

        var saveView = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.Workspace.Navigation.SaveView"),
            Icon = new FontIcon { Glyph = "\uE74E" }
        };
        saveView.Click += async (_, _) => await SaveCurrentViewAsync();
        menu.Items.Add(saveView);
        menu.Items.Add(new MenuFlyoutSeparator());
        AppendViewAndFilterCommands(menu.Items);
        return menu;
    }

    private MenuFlyout BuildQuickAddContextMenu()
    {
        var menu = new MenuFlyout();
        var focus = new MenuFlyoutItem
        {
            Text = _localization.T("Todo.AddPlaceholder"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        focus.Click += (_, _) => FocusQuickAdd();
        menu.Items.Add(focus);

        bool canPaste = false;
        try
        {
            canPaste = Clipboard.GetContent().Contains(StandardDataFormats.Text);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Clipboard inspection failed: {ex.Message}");
        }
        var paste = new MenuFlyoutItem
        {
            Text = _localization.T("Common.Paste"),
            Icon = new FontIcon { Glyph = "\uE77F" },
            IsEnabled = canPaste
        };
        paste.Click += async (_, _) =>
        {
            try
            {
                _quickAddTextBox.Text = await Clipboard.GetContent().GetTextAsync();
                _quickAddTextBox.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                App.Log($"[TodoWorkspace] Clipboard paste failed: {ex.Message}");
            }
        };
        menu.Items.Add(paste);

        if (_quickAddContextDate is not null)
        {
            var clear = new MenuFlyoutItem
            {
                Text = _localization.T("Todo.Workspace.Context.ClearQuickAddDate"),
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            clear.Click += (_, _) => ClearQuickAddContext();
            menu.Items.Add(clear);
        }
        return menu;
    }

    private void AppendViewAndFilterCommands(IList<MenuFlyoutItemBase> items)
    {
        var view = new MenuFlyoutSubItem
        {
            Text = GetDisplayModeName(_presentation.DisplayMode),
            Icon = new FontIcon { Glyph = GetDisplayModeGlyph(_presentation.DisplayMode) }
        };
        AddViewModeItems(view.Items);
        items.Add(view);
    }

    private void PrimeQuickAdd(DateOnly? date, TimeOnly? time)
    {
        _quickAddContextDate = date;
        _quickAddContextTime = date is null ? null : time;
        RefreshQuickAddContextLocalization();
        _quickAddTextBox.Focus(FocusState.Programmatic);
        _quickAddTextBox.Select(_quickAddTextBox.Text.Length, 0);
    }

    private void RefreshQuickAddContextLocalization()
    {
        _quickAddTextBox.PlaceholderText = _quickAddContextDate is { } selectedDate
            ? _localization.Format(
                _quickAddContextTime is null
                    ? "Todo.Workspace.Context.QuickAddOnDate"
                    : "Todo.Workspace.Context.QuickAddAtTime",
                FormatShortContextDate(selectedDate),
                _quickAddContextTime?.ToString("HH\\:mm") ?? string.Empty)
            : _localization.T("Todo.AddPlaceholder");
        RefreshQuickAddPresentation();
    }

    private void ClearQuickAddContext(bool refresh = true)
    {
        _quickAddContextDate = null;
        _quickAddContextTime = null;
        if (refresh)
        {
            RefreshQuickAddContextLocalization();
        }
        else
        {
            _quickAddTextBox.PlaceholderText = _localization.T("Todo.AddPlaceholder");
        }
    }

    private void AddQuickAddContextChip()
    {
        if (_quickAddContextDate is not { } date)
        {
            return;
        }

        string text = _quickAddContextTime is { } time
            ? $"{FormatShortContextDate(date)} {time:HH\\:mm}"
            : FormatShortContextDate(date);
        var chip = new Button
        {
            MinWidth = 0,
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(10),
            Background = TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "\uE787", FontSize = 10 },
                    new TextBlock { Text = text, FontSize = 11 },
                    new FontIcon { Glyph = "\uE711", FontSize = 8 }
                }
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(chip, "SubtleButtonStyle");
        ToolTipService.SetToolTip(chip, _localization.T("Todo.Workspace.Context.ClearQuickAddDate"));
        chip.Click += (_, _) => ClearQuickAddContext();
        _quickAddTokens.Children.Add(chip);
    }

    private void NavigateCalendarToDate(DateOnly date)
    {
        _selectedDate = date;
        _visiblePeriod = new DateOnly(date.Year, date.Month, 1);
        _presentation.SelectedDate = date;
        _presentationStore.SaveDebounced(_config, _presentation);
        RenderCurrentView();
        if (SelectedTask is null &&
            (_layoutMode is TodoWorkspaceLayoutMode.Split or TodoWorkspaceLayoutMode.ThreePane ||
             _detailPane.Visibility == Visibility.Visible))
        {
            RenderDetailPane();
        }
        UpdateToolbarText();
    }

    private TimeOnly? FindTimelineTime(
        DependencyObject? source,
        RightTappedRoutedEventArgs e)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not Canvas { Tag: DateOnly } canvas)
            {
                continue;
            }

            double minutesFromStart = e.GetPosition(canvas).Y / 64d * 60;
            int minutes = SnapMinutes(
                (_presentation.WorkdayStartHour * 60) +
                (int)Math.Round(minutesFromStart));
            minutes = Math.Clamp(minutes, 0, (24 * 60) - _presentation.CalendarSlotMinutes);
            return new TimeOnly(minutes / 60, minutes % 60);
        }
        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? source)
        where T : class
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T value })
            {
                return value;
            }
        }
        return null;
    }

    private static T? FindTaggedReference<T>(DependencyObject? source)
        where T : class
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: T value })
            {
                return value;
            }
        }
        return null;
    }

    private static bool TryFindTaggedDate(DependencyObject? source, out DateOnly date)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: DateOnly value })
            {
                date = value;
                return true;
            }
        }
        date = default;
        return false;
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatShortContextDate(DateOnly date) => date.ToString("M/d");

    private void CopyContextText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Clipboard copy failed: {ex.Message}");
            ShowFeedback(_localization.T("Todo.CopyFailed"));
        }
    }
}

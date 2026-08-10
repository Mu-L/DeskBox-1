using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

internal sealed record TodoCalendarDragContext(TodoTask Task, DateOnly? RecurrenceOccurrenceDate);

internal sealed partial class TodoWorkspaceSurface
{
    private readonly TodoRecurrenceExpansionService _recurrenceExpansion = new();
    private readonly Dictionary<DateOnly, Border> _monthCells = [];
    private readonly Dictionary<DateOnly, Border> _monthDayBadges = [];
    private readonly Dictionary<DateOnly, Border> _monthSelectionIndicators = [];
    private StackPanel? _monthSelectedDayPanel;
    private int _monthTaskLineCapacity = int.MinValue;
    private bool _monthLayoutRefreshQueued;

    private UIElement BuildMicroView()
    {
        var root = new Grid { RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        DateOnly start = GetWeekStart(_selectedDate);
        var strip = new Grid { ColumnSpacing = 2 };
        for (int index = 0; index < 7; index++)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DateOnly date = start.AddDays(index);
            int count = GetOccurrences(date, date).Count + GetExternalEvents(date, date).Count;
            var button = new Button
            {
                Tag = date,
                MinWidth = 0,
                MinHeight = 34,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = date.ToString("ddd"),
                            FontSize = 9,
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = count == 0 ? date.Day.ToString() : $"{date.Day} ·{count}",
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontWeight = date == _selectedDate
                                ? Microsoft.UI.Text.FontWeights.SemiBold
                                : Microsoft.UI.Text.FontWeights.Normal
                        }
                    }
                }
            };
            TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
            button.Click += CalendarDateButton_Click;
            SetColumn(button, index);
            strip.Children.Add(button);
        }
        root.Children.Add(strip);

        var list = new StackPanel { Spacing = 3 };
        foreach (TodoOccurrence occurrence in GetOccurrences(_selectedDate, _selectedDate).Take(6))
        {
            list.Children.Add(CreateTaskCard(occurrence.Task, occurrence.Date));
        }
        foreach (TodoCalendarEvent calendarEvent in GetExternalEvents(_selectedDate, _selectedDate)
                     .Take(Math.Max(0, 6 - list.Children.Count)))
        {
            list.Children.Add(BuildExternalEventCard(calendarEvent));
        }
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list
        };
        SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildAgendaView()
    {
        DateOnly start = _selectedNavigation?.SmartView == TodoSmartView.Today
            ? DateOnly.FromDateTime(DateTime.Today)
            : _selectedDate;
        DateOnly end = start.AddDays(30);
        IReadOnlyList<TodoOccurrence> occurrences = GetOccurrences(start, end);
        IReadOnlyList<TodoCalendarEvent> externalEvents = GetExternalEvents(start, end);
        var panel = new StackPanel { Spacing = 5 };
        DateOnly[] dates = occurrences.Select(occurrence => occurrence.Date)
            .Concat(externalEvents.Select(calendarEvent => calendarEvent.Date))
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
        foreach (DateOnly date in dates)
        {
            panel.Children.Add(new TextBlock
            {
                Tag = date,
                Text = FormatCalendarDate(date),
                Margin = new Thickness(5, 9, 5, 2),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = date == DateOnly.FromDateTime(DateTime.Today)
                    ? TodoWorkspaceTaskCard.ResourceBrush("AccentTextFillColorPrimaryBrush")
                    : TodoWorkspaceTaskCard.ResourceBrush("TextFillColorPrimaryBrush")
            });
            foreach (TodoOccurrence occurrence in occurrences.Where(occurrence => occurrence.Date == date))
            {
                panel.Children.Add(CreateTaskCard(occurrence.Task, occurrence.Date));
            }
            foreach (TodoCalendarEvent calendarEvent in externalEvents.Where(calendarEvent => calendarEvent.Date == date))
            {
                panel.Children.Add(BuildExternalEventCard(calendarEvent));
            }
        }

        if (occurrences.Count == 0 && externalEvents.Count == 0)
        {
            panel.Children.Add(BuildEmptyState());
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 3, 8),
            Content = panel
        };
    }

    private UIElement BuildMonthView()
    {
        _monthCells.Clear();
        _monthDayBadges.Clear();
        _monthSelectionIndicators.Clear();
        _monthSelectedDayPanel = null;
        DateOnly first = new(_visiblePeriod.Year, _visiblePeriod.Month, 1);
        DateOnly gridStart = GetWeekStart(first);
        DateOnly gridEnd = gridStart.AddDays(41);
        IReadOnlyList<TodoOccurrence> allOccurrences = GetOccurrences(gridStart, gridEnd);
        var byDate = allOccurrences.GroupBy(occurrence => occurrence.Date)
            .ToDictionary(group => group.Key, group => group.ToList());
        var externalByDate = GetExternalEvents(gridStart, gridEnd)
            .GroupBy(calendarEvent => calendarEvent.Date)
            .ToDictionary(group => group.Key, group => group.ToList());
        var monthGrid = new Grid { RowSpacing = 2, ColumnSpacing = 2 };
        int dayColumnOffset = _presentation.ShowWeekNumbers ? 1 : 0;
        if (_presentation.ShowWeekNumbers)
        {
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        }
        for (int column = 0; column < 7; column++)
        {
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        monthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int row = 0; row < 6; row++)
        {
            monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        if (_presentation.ShowWeekNumbers)
        {
            monthGrid.Children.Add(new TextBlock
            {
                Text = _localization.T("Todo.Workspace.WeekNumber.Short"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
            });
        }

        for (int column = 0; column < 7; column++)
        {
            DateOnly date = gridStart.AddDays(column);
            var header = new TextBlock
            {
                Text = date.ToString("ddd"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
            };
            SetColumn(header, column + dayColumnOffset);
            monthGrid.Children.Add(header);
        }

        if (_presentation.ShowWeekNumbers)
        {
            for (int row = 0; row < 6; row++)
            {
                var week = new TextBlock
                {
                    Text = GetWeekNumber(gridStart.AddDays(row * 7)).ToString(),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
                };
                SetRow(week, row + 1);
                monthGrid.Children.Add(week);
            }
        }

        double monthHostWidth = _mainHost.ActualWidth > 0
            ? _mainHost.ActualWidth
            : Math.Max(1, ActualWidth - Padding.Left - Padding.Right);
        double monthHostHeight = _mainHost.ActualHeight > 0
            ? _mainHost.ActualHeight
            : Math.Max(1, ActualHeight - 120);
        int taskLineCapacity = TodoResponsiveLayoutResolver.ResolveMonthTaskLineCapacity(
            monthHostWidth,
            monthHostHeight,
            _presentation.ShowWeekNumbers,
            _layoutMode == TodoWorkspaceLayoutMode.Enhanced);
        _monthTaskLineCapacity = taskLineCapacity;
        for (int index = 0; index < 42; index++)
        {
            DateOnly date = gridStart.AddDays(index);
            IReadOnlyList<TodoOccurrence> dayTasks = byDate.GetValueOrDefault(date) ?? [];
            IReadOnlyList<TodoCalendarEvent> dayEvents = externalByDate.GetValueOrDefault(date) ?? [];
            FrameworkElement cell = BuildMonthCell(date, dayTasks, dayEvents, taskLineCapacity);
            SetRow(cell, (index / 7) + 1);
            SetColumn(cell, (index % 7) + dayColumnOffset);
            monthGrid.Children.Add(cell);
        }

        var monthSurface = new Border
        {
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = monthGrid
        };

        if (_layoutMode == TodoWorkspaceLayoutMode.Enhanced)
        {
            var root = new Grid { RowSpacing = 6 };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.62, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.38, GridUnitType.Star) });
            root.Children.Add(monthSurface);
            var selectedDay = new StackPanel { Spacing = 3 };
            _monthSelectedDayPanel = selectedDay;
            PopulateMonthSelectedDayPanel(selectedDay);
            var dayScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = selectedDay
            };
            SetRow(dayScroll, 1);
            root.Children.Add(dayScroll);
            return root;
        }

        return monthSurface;
    }

    private void MainHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isLoaded ||
            _presentation.DisplayMode != TodoDisplayMode.Month ||
            _layoutMode == TodoWorkspaceLayoutMode.Micro ||
            _monthTaskLineCapacity == int.MinValue)
        {
            return;
        }

        int candidate = TodoResponsiveLayoutResolver.ResolveMonthTaskLineCapacity(
            e.NewSize.Width,
            e.NewSize.Height,
            _presentation.ShowWeekNumbers,
            _layoutMode == TodoWorkspaceLayoutMode.Enhanced);
        if (candidate == _monthTaskLineCapacity || _monthLayoutRefreshQueued)
        {
            return;
        }

        _monthLayoutRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _monthLayoutRefreshQueued = false;
                if (!_isLoaded ||
                    _presentation.DisplayMode != TodoDisplayMode.Month ||
                    _layoutMode == TodoWorkspaceLayoutMode.Micro)
                {
                    return;
                }

                int current = TodoResponsiveLayoutResolver.ResolveMonthTaskLineCapacity(
                    _mainHost.ActualWidth,
                    _mainHost.ActualHeight,
                    _presentation.ShowWeekNumbers,
                    _layoutMode == TodoWorkspaceLayoutMode.Enhanced);
                if (current != _monthTaskLineCapacity)
                {
                    RenderCurrentView();
                }
            }))
        {
            _monthLayoutRefreshQueued = false;
        }
    }

    private static int GetWeekNumber(DateOnly date)
    {
        System.Globalization.DateTimeFormatInfo format =
            System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
        return format.Calendar.GetWeekOfYear(
            date.ToDateTime(TimeOnly.MinValue),
            format.CalendarWeekRule,
            format.FirstDayOfWeek);
    }

    private FrameworkElement BuildMonthCell(
        DateOnly date,
        IReadOnlyList<TodoOccurrence> occurrences,
        IReadOnlyList<TodoCalendarEvent> externalEvents,
        int taskLineCapacity)
    {
        bool dateOnly = taskLineCapacity < 0;
        bool compact = taskLineCapacity <= 0;
        var cell = new Border
        {
            Tag = date,
            AllowDrop = true,
            MinHeight = 0,
            Padding = new Thickness(dateOnly ? 0 : compact ? 2 : taskLineCapacity < 3 ? 3 : 5),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Opacity = 1
        };
        cell.Tapped += CalendarCell_Tapped;
        cell.PointerEntered += MonthCell_PointerEntered;
        cell.PointerExited += MonthCell_PointerExited;
        cell.DragOver += CalendarCell_DragOver;
        cell.Drop += CalendarCell_Drop;
        var panel = new StackPanel { Spacing = dateOnly ? 0 : compact ? 1 : 2 };
        var dayLabel = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = dateOnly ? 9.5 : compact ? 10.5 : 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorPrimaryBrush")
        };
        var dayBadge = new Border
        {
            Width = dateOnly ? 18 : compact ? 20 : 21,
            Height = dateOnly ? 18 : compact ? 20 : 21,
            CornerRadius = new CornerRadius(10.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = dayLabel
        };
        panel.Children.Add(dayBadge);

        if (!dateOnly && compact)
        {
            int totalCount = occurrences.Count + externalEvents.Count;
            if (totalCount > 0)
            {
                var dots = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 2
                };
                foreach (TodoOccurrence occurrence in occurrences.Take(3))
                {
                    dots.Children.Add(new Border
                    {
                        Width = 4,
                        Height = 4,
                        CornerRadius = new CornerRadius(2),
                        Background = GetCalendarTaskBrush(occurrence.Task)
                    });
                }
                foreach (TodoCalendarEvent calendarEvent in externalEvents.Take(Math.Max(0, 3 - occurrences.Count)))
                {
                    dots.Children.Add(new Border
                    {
                        Width = 4,
                        Height = 4,
                        CornerRadius = new CornerRadius(2),
                        Background = GetExternalEventBrush(calendarEvent)
                    });
                }
                if (totalCount > 3)
                {
                    dots.Children.Add(new TextBlock { Text = $"+{totalCount - 3}", FontSize = 8 });
                }
                panel.Children.Add(dots);
            }
        }
        else if (!dateOnly)
        {
            int totalCount = occurrences.Count + externalEvents.Count;
            int regularSlots = totalCount > taskLineCapacity
                ? Math.Max(0, taskLineCapacity - 1)
                : taskLineCapacity;
            int renderedCount = 0;
            foreach (TodoOccurrence occurrence in occurrences.Take(regularSlots))
            {
                panel.Children.Add(BuildMonthTaskBar(occurrence));
                renderedCount++;
            }
            foreach (TodoCalendarEvent calendarEvent in externalEvents.Take(Math.Max(0, regularSlots - renderedCount)))
            {
                panel.Children.Add(BuildMonthExternalEventBar(calendarEvent));
                renderedCount++;
            }
            if (totalCount > renderedCount)
            {
                panel.Children.Add(BuildMonthOverflowBar(totalCount - renderedCount));
            }
        }

        var content = new Grid();
        content.Children.Add(panel);
        var selectionIndicator = new Border
        {
            Height = 2,
            Margin = new Thickness(compact ? 6 : 10, 0, compact ? 6 : 10, 0),
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        content.Children.Add(selectionIndicator);
        cell.Child = content;
        _monthCells[date] = cell;
        _monthDayBadges[date] = dayBadge;
        _monthSelectionIndicators[date] = selectionIndicator;
        ApplyMonthCellSelectionVisual(cell, date, date == _selectedDate);
        return cell;
    }

    private FrameworkElement BuildMonthOverflowBar(int count)
    {
        var overflow = new Border
        {
            Height = 17,
            MinHeight = 17,
            Padding = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(3),
            Background = TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorTertiaryBrush"),
            Child = new TextBlock
            {
                Text = $"+{count}",
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
            }
        };
        ToolTipService.SetToolTip(
            overflow,
            _localization.Format("Todo.Workspace.MoreCount", count));
        return overflow;
    }

    private FrameworkElement BuildMonthTaskBar(TodoOccurrence occurrence)
    {
        TodoTask task = occurrence.Task;
        bool planned = task.Schedule is not null;
        bool overdue = task.DeadlineAt is { } deadline && deadline < DateTimeOffset.Now &&
                       task.Status != TodoTaskStatus.Completed;
        var button = new Button
        {
            Tag = new TodoCalendarDragContext(
                task,
                task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule
                    ? occurrence.Date
                    : null),
            CanDrag = true,
            AllowDrop = true,
            MinWidth = 0,
            MinHeight = 17,
            Height = 17,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = planned ? new Thickness(0) : new Thickness(1),
            BorderBrush = overdue
                ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
                : GetCalendarTaskBrush(task),
            Background = planned
                ? (overdue
                    ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
                    : GetCalendarTaskBrush(task))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = planned
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : TodoWorkspaceTaskCard.ResourceBrush("TextFillColorPrimaryBrush"),
            Content = new TextBlock
            {
                Text = task.Title,
                FontSize = 9.5,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        AttachColorMarkerDropTarget(button);
        button.DragStarting += TaskButton_DragStarting;
        button.Click += (_, _) => SelectTask(task, occurrence.Date);
        return button;
    }

    private FrameworkElement BuildMonthExternalEventBar(TodoCalendarEvent calendarEvent)
    {
        var border = new Border
        {
            Tag = calendarEvent,
            MinHeight = 17,
            Height = 17,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = GetExternalEventBrush(calendarEvent),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new TextBlock
            {
                Text = calendarEvent.Title,
                FontSize = 9.5,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        ToolTipService.SetToolTip(border, $"{calendarEvent.SourceName} · {_localization.T("Todo.Workspace.ReadOnlyCalendar")}");
        return border;
    }

    private UIElement BuildTimelineView(int dayCount)
    {
        DateOnly start = dayCount == 7 ? GetWeekStart(_selectedDate) : _selectedDate;
        int startHour = _presentation.WorkdayStartHour;
        int endHour = _presentation.WorkdayEndHour;
        const double pixelsPerHour = 64;
        double canvasHeight = Math.Max(1, endHour - startHour) * pixelsPerHour;

        var root = new Grid { RowSpacing = 5 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (_presentation.ShowUnscheduledPool)
        {
            root.Children.Add(BuildUnscheduledPool());
        }

        var timelineGrid = new Grid();
        timelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        for (int day = 0; day < dayCount; day++)
        {
            timelineGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = dayCount == 7 ? 72 : 180
            });
        }
        timelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timelineGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(canvasHeight) });

        for (int day = 0; day < dayCount; day++)
        {
            DateOnly date = start.AddDays(day);
            var header = new Button
            {
                Tag = date,
                MinWidth = 0,
                Padding = new Thickness(4, 2, 4, 2),
                Content = new TextBlock
                {
                    Text = date.ToString("ddd M/d"),
                    FontSize = dayCount == 7 ? 10 : 11,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = date == DateOnly.FromDateTime(DateTime.Today)
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal
                }
            };
            TodoWorkspaceTaskCard.ApplyStyle(header, "SubtleButtonStyle");
            header.Click += CalendarDateButton_Click;
            SetColumn(header, day + 1);
            timelineGrid.Children.Add(header);

            var canvas = new Canvas
            {
                Tag = date,
                Height = canvasHeight,
                AllowDrop = true,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            canvas.DragOver += Timeline_DragOver;
            canvas.Drop += Timeline_Drop;
            for (int hour = startHour; hour <= endHour; hour++)
            {
                double y = (hour - startHour) * pixelsPerHour;
                canvas.Children.Add(new Border
                {
                    Height = 1,
                    Width = 2000,
                    Background = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush"),
                    Opacity = 0.55
                });
                Canvas.SetTop(canvas.Children[^1], y);
            }

            foreach (TodoOccurrence occurrence in GetOccurrences(date, date)
                         .Where(occurrence => occurrence.Task.Schedule?.Time is not null))
            {
                TodoTask task = occurrence.Task;
                TimeOnly time = task.Schedule!.Time!.Value;
                double top = ((time.Hour + (time.Minute / 60d)) - startHour) * pixelsPerHour;
                if (top < 0 || top >= canvasHeight)
                {
                    continue;
                }
                double duration = Math.Max(15, task.Schedule.DurationMinutes ?? _presentation.DefaultDurationMinutes);
                double height = Math.Max(22, duration / 60d * pixelsPerHour);
                FrameworkElement block = BuildTimeBlock(occurrence, height, pixelsPerHour);
                Canvas.SetLeft(block, 3);
                Canvas.SetTop(block, top);
                canvas.Children.Add(block);
                block.Width = Math.Max(40, canvas.ActualWidth - 6);
                block.SizeChanged += (_, _) => block.Width = Math.Max(40, canvas.ActualWidth - 6);
            }

            foreach (TodoCalendarEvent calendarEvent in GetExternalEvents(date, date)
                         .Where(calendarEvent => !calendarEvent.IsAllDay && calendarEvent.StartTime is not null))
            {
                TimeOnly time = calendarEvent.StartTime!.Value;
                double top = ((time.Hour + (time.Minute / 60d)) - startHour) * pixelsPerHour;
                if (top < 0 || top >= canvasHeight)
                {
                    continue;
                }
                double height = Math.Max(22, calendarEvent.DurationMinutes / 60d * pixelsPerHour);
                FrameworkElement block = BuildExternalTimeBlock(calendarEvent, height);
                Canvas.SetLeft(block, 3);
                Canvas.SetTop(block, top);
                canvas.Children.Add(block);
                block.Width = Math.Max(40, canvas.ActualWidth - 6);
                block.SizeChanged += (_, _) => block.Width = Math.Max(40, canvas.ActualWidth - 6);
            }

            SetRow(canvas, 1);
            SetColumn(canvas, day + 1);
            timelineGrid.Children.Add(canvas);
        }

        for (int hour = startHour; hour < endHour; hour++)
        {
            var label = new TextBlock
            {
                Text = $"{hour:00}:00",
                FontSize = 10,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
            };
            Canvas.SetTop(label, (hour - startHour) * pixelsPerHour - 7);
            var labelCanvas = timelineGrid.Children.OfType<Canvas>().FirstOrDefault(canvas => GetColumn(canvas) == 0);
            _ = labelCanvas;
        }

        var timeLabels = new Canvas { Height = canvasHeight };
        for (int hour = startHour; hour <= endHour; hour++)
        {
            var label = new TextBlock
            {
                Text = $"{hour:00}:00",
                FontSize = 10,
                Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush")
            };
            Canvas.SetTop(label, Math.Max(0, (hour - startHour) * pixelsPerHour - 7));
            timeLabels.Children.Add(label);
        }
        SetRow(timeLabels, 1);
        timelineGrid.Children.Add(timeLabels);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = dayCount == 7 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = timelineGrid
        };
        SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildUnscheduledPool()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = _localization.T("Todo.Workspace.UnscheduledPool"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 5, 0),
            FontSize = 11,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        foreach (TodoTask task in _snapshot.Tasks
                     .Where(task => task.DeletedAt is null &&
                                    task.Status == TodoTaskStatus.Open &&
                                    task.Schedule is null &&
                                    (_activeColorMarkerFilter is null ||
                                     string.Equals(
                                         TodoItem.NormalizeColorMarker(task.ColorMarker),
                                         _activeColorMarkerFilter,
                                         StringComparison.Ordinal)))
                     .OrderByDescending(task => task.Priority)
                     .Take(12))
        {
            var button = new Button
            {
                Tag = new TodoCalendarDragContext(task, null),
                CanDrag = true,
                AllowDrop = true,
                MinWidth = 0,
                MaxWidth = 150,
                Padding = new Thickness(8, 3, 8, 3),
                Content = new TextBlock
                {
                    Text = task.Title,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            AttachColorMarkerDropTarget(button);
            TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
            button.DragStarting += TaskButton_DragStarting;
            button.Click += (_, _) => SelectTask(task.Id);
            panel.Children.Add(button);
        }
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
        scroll.PointerWheelChanged += HorizontalTaskPool_PointerWheelChanged;
        return scroll;
    }

    private static void HorizontalTaskPool_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.ScrollableWidth <= 0)
        {
            return;
        }

        int delta = e.GetCurrentPoint(scroll).Properties.MouseWheelDelta;
        double target = Math.Clamp(scroll.HorizontalOffset - delta, 0, scroll.ScrollableWidth);
        scroll.ChangeView(target, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private FrameworkElement BuildTimeBlock(TodoOccurrence occurrence, double height, double pixelsPerHour)
    {
        TodoTask task = occurrence.Task;
        DateOnly occurrenceDate = occurrence.Date;
        var dragContext = new TodoCalendarDragContext(
            task,
            task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule
                ? occurrenceDate
                : null);
        var root = new Grid
        {
            Tag = dragContext,
            Height = height,
            MinHeight = 22,
            CanDrag = true,
            AllowDrop = true,
            Background = GetCalendarTaskBrush(task)
        };
        AttachColorMarkerDropTarget(root);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.CornerRadius = new CornerRadius(5);
        root.DragStarting += TaskButton_DragStarting;
        root.Tapped += (_, _) => SelectTask(task, occurrenceDate);
        root.Children.Add(new TextBlock
        {
            Text = task.Title,
            Margin = new Thickness(6, 3, 6, 2),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 11,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        });
        var thumb = new Thumb
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = dragContext
        };
        double initialHeight = height;
        thumb.DragDelta += (_, args) => root.Height = Math.Max(22, root.Height + args.VerticalChange);
        thumb.DragCompleted += async (_, _) =>
        {
            int minutes = SnapMinutes((int)Math.Round(root.Height / pixelsPerHour * 60));
            TodoTask editable = task.CloneTask();
            if (editable.Schedule is not null)
            {
                int duration = Math.Max(_presentation.CalendarSlotMinutes, minutes);
                if (dragContext.RecurrenceOccurrenceDate is { } sourceDate)
                {
                    await RunMutationAsync(() => _workspace.ApplyRecurrenceEditAsync(
                        task.Id,
                        sourceDate,
                        TodoRecurrenceEditScope.Occurrence,
                        occurrenceTask =>
                        {
                            if (occurrenceTask.Schedule is not null)
                            {
                                occurrenceTask.Schedule.DurationMinutes = duration;
                            }
                        }));
                }
                else
                {
                    editable.Schedule.DurationMinutes = duration;
                    await RunMutationAsync(() => _workspace.SaveTaskAsync(editable));
                }
            }
        };
        SetRow(thumb, 1);
        root.Children.Add(thumb);
        return root;
    }

    private FrameworkElement BuildExternalTimeBlock(TodoCalendarEvent calendarEvent, double height)
    {
        var root = new Border
        {
            Tag = calendarEvent,
            Height = height,
            MinHeight = 22,
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = GetExternalEventBrush(calendarEvent),
            Background = TodoWorkspaceTaskCard.ResourceBrush("CardBackgroundFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = calendarEvent.Title,
                FontSize = 11,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap
            }
        };
        ToolTipService.SetToolTip(root, $"{calendarEvent.SourceName} · {_localization.T("Todo.Workspace.ReadOnlyCalendar")}");
        return root;
    }

    private void TaskButton_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        TodoCalendarDragContext? context = (sender as FrameworkElement)?.Tag as TodoCalendarDragContext;
        if (context is null)
        {
            args.Cancel = true;
            return;
        }
        args.Data.Properties["DeskBox.Todo.TaskIds.v2"] = JsonSerializer.Serialize(new[] { context.Task.Id });
        if (context.RecurrenceOccurrenceDate is { } occurrenceDate)
        {
            args.Data.Properties["DeskBox.Todo.OccurrenceDate.v2"] = occurrenceDate.ToString("yyyy-MM-dd");
        }
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(context.Task.Title);
    }

    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDraggedTaskIds(e.DataView, out _))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
    }

    private async void Timeline_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Canvas { Tag: DateOnly date } canvas ||
            !TryGetDraggedTaskIds(e.DataView, out string[] ids))
        {
            return;
        }
        double position = e.GetPosition(canvas).Y;
        double minutesFromStart = position / 64d * 60;
        int minutes = SnapMinutes((_presentation.WorkdayStartHour * 60) + (int)Math.Round(minutesFromStart));
        minutes = Math.Clamp(minutes, 0, (24 * 60) - _presentation.CalendarSlotMinutes);
        TimeOnly time = new(minutes / 60, minutes % 60);
        await RunMutationAsync(async () =>
        {
            DateOnly? recurrenceDate = TryGetDraggedOccurrenceDate(e.DataView);
            var changedTasks = new List<TodoTask>();
            foreach (string id in ids)
            {
                TodoTask? task = await _workspace.GetTaskAsync(id);
                if (task is null)
                {
                    continue;
                }
                var schedule = new TodoSchedule
                {
                    Date = date,
                    Time = time,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    DurationMinutes = task.Schedule?.DurationMinutes ?? _presentation.DefaultDurationMinutes
                };
                if (recurrenceDate is { } sourceDate &&
                    task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
                {
                    await _workspace.ApplyRecurrenceEditAsync(
                        task.Id,
                        sourceDate,
                        TodoRecurrenceEditScope.Occurrence,
                        occurrenceTask => occurrenceTask.Schedule = schedule.Clone());
                }
                else
                {
                    task.Schedule = schedule;
                    changedTasks.Add(task);
                }
            }
            if (changedTasks.Count > 0)
            {
                await _workspace.SaveTasksAsync(changedTasks);
            }
        });
    }

    private void CalendarCell_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDraggedTaskIds(e.DataView, out _))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
    }

    private async void CalendarCell_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DateOnly date } ||
            !TryGetDraggedTaskIds(e.DataView, out string[] ids))
        {
            return;
        }
        await RunMutationAsync(async () =>
        {
            DateOnly? recurrenceDate = TryGetDraggedOccurrenceDate(e.DataView);
            var changedTasks = new List<TodoTask>();
            foreach (string id in ids)
            {
                TodoTask? task = await _workspace.GetTaskAsync(id);
                if (task is null)
                {
                    continue;
                }
                var schedule = new TodoSchedule
                {
                    Date = date,
                    Time = task.Schedule?.Time,
                    TimeZoneId = task.Schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id,
                    DurationMinutes = task.Schedule?.DurationMinutes
                };
                if (recurrenceDate is { } sourceDate &&
                    task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
                {
                    await _workspace.ApplyRecurrenceEditAsync(
                        task.Id,
                        sourceDate,
                        TodoRecurrenceEditScope.Occurrence,
                        occurrenceTask => occurrenceTask.Schedule = schedule.Clone());
                }
                else
                {
                    task.Schedule = schedule;
                    changedTasks.Add(task);
                }
            }
            if (changedTasks.Count > 0)
            {
                await _workspace.SaveTasksAsync(changedTasks);
            }
        });
    }

    private void CalendarCell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateOnly date })
        {
            SelectCalendarDate(date);
        }
    }

    private void CalendarDateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateOnly date })
        {
            SelectCalendarDate(date);
        }
    }

    private void SelectCalendarDate(DateOnly date)
    {
        DateOnly previous = _selectedDate;
        _selectedDate = date;
        _presentation.SelectedDate = date;
        _presentationStore.SaveDebounced(_config, _presentation);

        if (_presentation.DisplayMode != TodoDisplayMode.Month ||
            !TryUpdateMonthSelection(previous, date))
        {
            RenderCurrentView();
        }

        if (SelectedTask is null &&
            (_layoutMode is TodoWorkspaceLayoutMode.Split or TodoWorkspaceLayoutMode.ThreePane ||
             _detailPane.Visibility == Visibility.Visible))
        {
            RenderDetailPane();
        }
        UpdateToolbarText();
    }

    private bool TryUpdateMonthSelection(DateOnly previous, DateOnly current)
    {
        if (_monthCells.Count == 0)
        {
            return false;
        }

        if (_monthCells.TryGetValue(previous, out Border? oldCell))
        {
            ApplyMonthCellSelectionVisual(oldCell, previous, selected: false);
        }
        if (_monthCells.TryGetValue(current, out Border? newCell))
        {
            ApplyMonthCellSelectionVisual(newCell, current, selected: true);
        }

        if (_monthSelectedDayPanel is not null)
        {
            PopulateMonthSelectedDayPanel(_monthSelectedDayPanel);
        }
        return true;
    }

    private void PopulateMonthSelectedDayPanel(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = FormatCalendarDate(_selectedDate),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 2)
        });
        foreach (TodoOccurrence occurrence in GetOccurrences(_selectedDate, _selectedDate).Take(8))
        {
            panel.Children.Add(CreateTaskCard(occurrence.Task, occurrence.Date));
        }
        foreach (TodoCalendarEvent calendarEvent in GetExternalEvents(_selectedDate, _selectedDate)
                     .Take(Math.Max(0, 9 - panel.Children.Count)))
        {
            panel.Children.Add(BuildExternalEventCard(calendarEvent));
        }
    }

    private void ApplyMonthCellSelectionVisual(Border cell, DateOnly date, bool selected)
    {
        bool today = date == DateOnly.FromDateTime(DateTime.Today);
        bool inVisibleMonth = date.Year == _visiblePeriod.Year && date.Month == _visiblePeriod.Month;
        cell.Opacity = 1;
        cell.BorderThickness = new Thickness(0);
        cell.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        cell.Background = selected
            ? TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        if (_monthDayBadges.TryGetValue(date, out Border? badge))
        {
            badge.Background = today
                ? TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (badge.Child is TextBlock label)
            {
                label.Foreground = today
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : TodoWorkspaceTaskCard.ResourceBrush("TextFillColorPrimaryBrush");
                label.Opacity = today || selected ? 1 : inVisibleMonth ? 0.68 : 0.36;
            }
        }

        if (_monthSelectionIndicators.TryGetValue(date, out Border? indicator))
        {
            indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MonthCell_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: DateOnly date } cell && date != _selectedDate)
        {
            cell.Background = TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorTertiaryBrush");
        }
    }

    private void MonthCell_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: DateOnly date } cell)
        {
            ApplyMonthCellSelectionVisual(cell, date, date == _selectedDate);
        }
    }

    private TodoWorkspaceTaskCard CreateTaskCard(TodoTask task, DateOnly occurrenceDate)
    {
        var card = new TodoWorkspaceTaskCard
        {
            DataContext = new TodoWorkspaceTaskRow(
                task,
                _snapshot,
                _localization,
                _presentation,
                ToggleTaskCompletionAsync,
                ApplyTaskColorMarkerAsync,
                occurrenceDate)
        };
        card.Tapped += (_, _) => SelectTask(task, occurrenceDate);
        return card;
    }

    private IReadOnlyList<TodoOccurrence> GetOccurrences(DateOnly start, DateOnly end)
    {
        IEnumerable<TodoTask> tasks = _snapshot.Tasks.Where(task =>
            task.DeletedAt is null &&
            task.Status != TodoTaskStatus.Cancelled);
        if (_activeColorMarkerFilter is { } colorMarker)
        {
            tasks = tasks.Where(task => string.Equals(
                TodoItem.NormalizeColorMarker(task.ColorMarker),
                colorMarker,
                StringComparison.Ordinal));
        }

        return _recurrenceExpansion.Expand(
            tasks,
            start,
            end,
            _snapshot.RecurrenceExceptions);
    }

    private IReadOnlyList<TodoCalendarEvent> GetExternalEvents(DateOnly start, DateOnly end) =>
        _externalCalendarEvents
            .Where(calendarEvent => calendarEvent.Date >= start && calendarEvent.Date <= end)
            .ToList();

    private FrameworkElement BuildExternalEventCard(TodoCalendarEvent calendarEvent)
    {
        var root = new Grid
        {
            Tag = calendarEvent,
            MinHeight = 34,
            Padding = new Thickness(8, 5, 8, 5),
            ColumnSpacing = 8
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new FontIcon
        {
            Glyph = "\uE787",
            FontSize = 13,
            Foreground = GetExternalEventBrush(calendarEvent),
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(new TextBlock
        {
            Text = calendarEvent.Title,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = calendarEvent.IsAllDay
                ? $"{calendarEvent.SourceName} · {_localization.T("Todo.Workspace.AllDay")}"
                : $"{calendarEvent.StartTime:HH:mm} · {calendarEvent.SourceName}",
            FontSize = 10.5,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorSecondaryBrush")
        });
        SetColumn(text, 1);
        root.Children.Add(text);
        ToolTipService.SetToolTip(root, _localization.T("Todo.Workspace.ReadOnlyCalendar"));
        return new Border
        {
            Tag = calendarEvent,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = TodoWorkspaceTaskCard.ResourceBrush("CardStrokeColorDefaultBrush"),
            Child = root
        };
    }

    private Brush GetExternalEventBrush(TodoCalendarEvent calendarEvent)
    {
        string color = TodoItem.NormalizeColorMarker(calendarEvent.ColorMarker) ?? "#6B69D6";
        try
        {
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                255,
                Convert.ToByte(color.Substring(1, 2), 16),
                Convert.ToByte(color.Substring(3, 2), 16),
                Convert.ToByte(color.Substring(5, 2), 16)));
        }
        catch
        {
            return TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush");
        }
    }

    private Brush GetCalendarTaskBrush(TodoTask task)
    {
        if (task.DeadlineAt is { } deadline &&
            deadline < DateTimeOffset.Now &&
            task.Status != TodoTaskStatus.Completed)
        {
            return new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }
        string color = TodoItem.GetColorMarkerHex(task.ColorMarker);
        try
        {
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                255,
                Convert.ToByte(color.Substring(1, 2), 16),
                Convert.ToByte(color.Substring(3, 2), 16),
                Convert.ToByte(color.Substring(5, 2), 16)));
        }
        catch
        {
            return TodoWorkspaceTaskCard.ResourceBrush("AccentFillColorDefaultBrush");
        }
    }

    private int SnapMinutes(int value)
    {
        int slot = _presentation.CalendarSlotMinutes;
        return (int)Math.Round(value / (double)slot) * slot;
    }

    private static bool TryGetDraggedTaskIds(DataPackageView data, out string[] taskIds)
    {
        taskIds = [];
        if (!data.Properties.TryGetValue("DeskBox.Todo.TaskIds.v2", out object? value) ||
            value is not string json)
        {
            return false;
        }
        try
        {
            taskIds = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return taskIds.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateOnly? TryGetDraggedOccurrenceDate(DataPackageView data)
    {
        return data.Properties.TryGetValue("DeskBox.Todo.OccurrenceDate.v2", out object? value) &&
               value is string text &&
               DateOnly.TryParseExact(text, "yyyy-MM-dd", out DateOnly date)
            ? date
            : null;
    }

    private string FormatCalendarDate(DateOnly date)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (date == today)
        {
            return $"{_localization.T("Todo.Workspace.Today")} · {date:M/d dddd}";
        }
        return date.ToString("M/d dddd");
    }
}

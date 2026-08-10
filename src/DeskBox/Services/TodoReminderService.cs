using DeskBox.Models;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed record TodoReminderNotification(
    string Title,
    string Message,
    int Count,
    string? WidgetId = null,
    string? ItemId = null,
    bool HasTodayDueItem = false,
    string? ReminderRuleId = null,
    string? OccurrenceKey = null);

public sealed class TodoReminderService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MissedReminderGrace = TimeSpan.FromMinutes(1);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Action<TodoReminderNotification> _notify;
    private readonly Func<string, ITodoStore> _storeFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _usesSharedWorkspace;
    private readonly TodoWorkspaceService? _workspaceService;
    private readonly HashSet<string> _sessionNotifiedKeys = new(StringComparer.Ordinal);

    private DispatcherQueueTimer? _timer;
    private bool _isChecking;
    private bool _disposed;

    private enum ReminderTriggerKind
    {
        Due,
        Snooze
    }

    public TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Action<TodoReminderNotification> notify,
        TodoWorkspaceService workspaceService)
        : this(
            settingsService,
            localizationService,
            dispatcherQueue,
            notify,
            _ => new TodoWorkspaceStoreAdapter(workspaceService),
            () => DateTimeOffset.Now,
            usesSharedWorkspace: true,
            workspaceService)
    {
    }

    internal TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue? dispatcherQueue,
        Action<TodoReminderNotification> notify,
        Func<string, TodoWidgetStore> storeFactory,
        Func<DateTimeOffset> clock)
        : this(
            settingsService,
            localizationService,
            dispatcherQueue,
            notify,
            widgetId => storeFactory(widgetId),
            clock,
            usesSharedWorkspace: false,
            workspaceService: null)
    {
    }

    private TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue? dispatcherQueue,
        Action<TodoReminderNotification> notify,
        Func<string, ITodoStore> storeFactory,
        Func<DateTimeOffset> clock,
        bool usesSharedWorkspace,
        TodoWorkspaceService? workspaceService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue;
        _notify = notify;
        _storeFactory = storeFactory;
        _clock = clock;
        _usesSharedWorkspace = usesSharedWorkspace;
        _workspaceService = workspaceService;
    }

    public void Start()
    {
        if (_disposed || _dispatcherQueue is null || _timer is not null)
        {
            return;
        }

        if (!ShouldBeRunning())
        {
            return;
        }

        // Clear notified keys from any previous session so that reminders
        // that were already shown before the app restarted can fire again.
        _sessionNotifiedKeys.Clear();

        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = ScanInterval;
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _ = RunDelayedInitialCheckAsync();
    }

    /// <summary>
    /// Called when settings change. Starts or stops the timer based on whether
    /// the Todo widget and reminder feature are enabled.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (ShouldBeRunning())
        {
            if (_timer is null && _dispatcherQueue is not null)
            {
                // Clear notified keys when the timer is being (re)started
                // after being stopped — old keys from a previous active
                // period are no longer relevant.
                _sessionNotifiedKeys.Clear();
                _timer = _dispatcherQueue.CreateTimer();
                _timer.Interval = ScanInterval;
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
        }
        else if (_timer is not null)
        {
            _timer.Tick -= Timer_Tick;
            _timer.Stop();
            _timer = null;
            // Clear notified keys when the feature is disabled so they
            // don't accumulate indefinitely while inactive.
            _sessionNotifiedKeys.Clear();
        }
    }

    private bool ShouldBeRunning()
    {
        var settings = _settingsService.Settings;
        return settings.TodoReminderEnabled &&
               settings.Todo.RemindersAndRecurrence.Enabled &&
               FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
    }

    public async Task<int> CheckNowAsync(DateTimeOffset now)
    {
        if (_disposed || _isChecking)
        {
            return 0;
        }

        _isChecking = true;
        try
        {
            var settings = _settingsService.Settings;
            if (!settings.TodoReminderEnabled ||
                !settings.Todo.RemindersAndRecurrence.Enabled ||
                !FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
            {
                return 0;
            }

            int defaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                settings.Todo.SchemaVersion >= TodoSettings.CurrentSchemaVersion
                    ? settings.Todo.RemindersAndRecurrence.DefaultOffsetMinutes
                    : settings.TodoDefaultReminderOffsetMinutes);
            var widgets = settings.Widgets
                .Where(widget =>
                    widget.WidgetKind == WidgetKind.Todo &&
                    !widget.IsDisabled &&
                    !settings.DeletedWidgetIds.Contains(widget.Id))
                .ToList();

            if (widgets.Count == 0 && !_usesSharedWorkspace)
            {
                return 0;
            }

            List<TodoReminderCandidate> candidates = [];
            if (_usesSharedWorkspace)
            {
                WidgetConfig target = widgets.FirstOrDefault() ?? new WidgetConfig
                {
                    Id = string.Empty,
                    Name = _localizationService.T("Todo.Title"),
                    WidgetKind = WidgetKind.Todo
                };
                await CollectWidgetCandidatesAsync(target, now, defaultOffsetMinutes, candidates);
            }
            else
            foreach (var widget in widgets)
            {
                await CollectWidgetCandidatesAsync(widget, now, defaultOffsetMinutes, candidates);
            }

            if (candidates.Count == 0)
            {
                return 0;
            }

            _notify(BuildNotification(candidates));
            return candidates.Count;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Check failed: {ex}");
            return 0;
        }
        finally
        {
            _isChecking = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_timer is not null)
        {
            _timer.Tick -= Timer_Tick;
            _timer.Stop();
            _timer = null;
        }
    }

    internal static bool ShouldNotify(TodoItem item, DateTimeOffset now, TimeSpan reminderOffset)
    {
        return ShouldNotifyDue(item, now, reminderOffset);
    }

    internal static bool ShouldNotify(TodoItem item, DateTimeOffset now, int defaultOffsetMinutes)
    {
        return TryGetReminderTrigger(item, now, defaultOffsetMinutes, out _, out _);
    }

    public async Task<bool> SnoozeAsync(
        string? widgetId,
        string? itemId,
        TimeSpan snoozeFor,
        string? reminderRuleId = null,
        string? occurrenceKey = null)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            snoozeFor <= TimeSpan.Zero)
        {
            return false;
        }

        return await SnoozeUntilAsync(
            widgetId,
            itemId,
            _clock().Add(snoozeFor),
            reminderRuleId,
            occurrenceKey);
    }

    public async Task<bool> SnoozeUntilAsync(
        string? widgetId,
        string? itemId,
        DateTimeOffset snoozedUntil,
        string? reminderRuleId = null,
        string? occurrenceKey = null)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            snoozedUntil <= _clock())
        {
            return false;
        }

        try
        {
            if (!TryGetTodoReminderWidget(widgetId, requireReminderEnabled: true, out WidgetConfig? widget))
            {
                return false;
            }

            if (_usesSharedWorkspace && _workspaceService is not null)
            {
                TodoTask? task = await _workspaceService.GetTaskAsync(itemId);
                if (task is null || task.Status == TodoTaskStatus.Completed)
                {
                    return false;
                }
                TodoReminderRule? rule = !string.IsNullOrWhiteSpace(reminderRuleId)
                    ? task.Reminders.FirstOrDefault(candidate =>
                        candidate.IsEnabled &&
                        string.Equals(candidate.Id, reminderRuleId, StringComparison.Ordinal))
                    : task.Reminders
                        .Where(candidate => candidate.IsEnabled)
                        .OrderBy(candidate => ResolveReminderTarget(task, candidate) ?? DateTimeOffset.MaxValue)
                        .FirstOrDefault();
                if (rule is null)
                {
                    return false;
                }
                rule.SnoozedUntil = snoozedUntil;
                rule.SnoozeLastNotifiedAt = null;
                if (!string.IsNullOrWhiteSpace(occurrenceKey))
                {
                    rule.OccurrenceKey = occurrenceKey;
                }
                await _workspaceService.SaveTaskAsync(task);
                return true;
            }

            var store = _storeFactory(widget.Id);
            var data = await store.LoadAsync();
            var item = data.Items.FirstOrDefault(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (item is null ||
                item.IsCompleted ||
                item.DueDate is null ||
                TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
            {
                return false;
            }

            item.SnoozedUntil = snoozedUntil;
            item.SnoozeLastNotifiedAt = null;
            item.ReminderDismissedForDueDate = item.DueDate;
            item.UpdatedAt = _clock().ToUniversalTime();
            await store.SaveAsync(data);
            App.Log($"[TodoReminder] Snoozed widget={widgetId} item={itemId} until={item.SnoozedUntil:O}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Snooze failed: {ex}");
            return false;
        }
    }

    public async Task<bool> CompleteAsync(
        string? widgetId,
        string? itemId,
        string? occurrenceKey = null)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        try
        {
            if (!TryGetTodoReminderWidget(widgetId, requireReminderEnabled: false, out WidgetConfig? widget))
            {
                return false;
            }

            if (_usesSharedWorkspace && _workspaceService is not null)
            {
                TodoTask? task = await _workspaceService.GetTaskAsync(itemId);
                if (task is null)
                {
                    return false;
                }
                if (task.RecurrenceRule is { GenerationMode: TodoRecurrenceGenerationMode.FixedSchedule } &&
                    TryParseOccurrenceDate(task.Id, occurrenceKey, out DateOnly occurrenceDate))
                {
                    await _workspaceService.ApplyRecurrenceEditAsync(
                        task.Id,
                        occurrenceDate,
                        TodoRecurrenceEditScope.Occurrence,
                        editable =>
                        {
                            editable.Status = TodoTaskStatus.Completed;
                            editable.IsCompleted = true;
                            editable.CompletedAt = _clock().ToUniversalTime();
                        });
                    return true;
                }
                await _workspaceService.CompleteTaskAsync(task);
                return true;
            }

            var store = _storeFactory(widget.Id);
            var data = await store.LoadAsync();
            int itemIndex = data.Items.FindIndex(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (itemIndex < 0)
            {
                return false;
            }

            var item = data.Items[itemIndex];
            if (item.IsCompleted)
            {
                return true;
            }

            DateTimeOffset now = _clock().ToUniversalTime();
            if (item.Recurrence is not null)
            {
                item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
            }

            item.IsCompleted = true;
            item.CompletedAt = now;
            item.UpdatedAt = now;
            item.GeneratedNextItemId = null;
            item.SnoozedUntil = null;
            item.SnoozeLastNotifiedAt = null;

            if (TodoRecurrenceService.TryCreateNextOccurrence(item, now, out TodoItem? nextItem) &&
                nextItem is not null)
            {
                item.GeneratedNextItemId = nextItem.Id;
                data.Items.Insert(Math.Clamp(itemIndex + 1, 0, data.Items.Count), nextItem);
            }

            await store.SaveAsync(data);
            App.Log($"[TodoReminder] Completed from notification widget={widgetId} item={itemId}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Complete failed: {ex}");
            return false;
        }
    }

    private async Task RunDelayedInitialCheckAsync()
    {
        try
        {
            await Task.Delay(StartupDelay);
            await CheckNowAsync(_clock());
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Initial check failed: {ex}");
        }
    }

    private bool TryGetTodoReminderWidget(
        string widgetId,
        bool requireReminderEnabled,
        out WidgetConfig widget)
    {
        widget = null!;
        var settings = _settingsService.Settings;
        if ((requireReminderEnabled &&
             (!settings.TodoReminderEnabled || !settings.Todo.RemindersAndRecurrence.Enabled)) ||
            !FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
        {
            return false;
        }

        var match = settings.Widgets.FirstOrDefault(entry =>
            entry.WidgetKind == WidgetKind.Todo &&
            !entry.IsDisabled &&
            !settings.DeletedWidgetIds.Contains(entry.Id) &&
            string.Equals(entry.Id, widgetId, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        widget = match;
        return true;
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        await CheckNowAsync(_clock());
    }

    private async Task CollectWidgetCandidatesAsync(
        WidgetConfig widget,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        List<TodoReminderCandidate> candidates)
    {
        if (_usesSharedWorkspace && _workspaceService is not null)
        {
            await CollectWorkspaceCandidatesAsync(widget, now, defaultOffsetMinutes, candidates);
            return;
        }

        var store = _storeFactory(widget.Id);
        var data = await store.LoadAsync();
        bool changed = false;

        foreach (var item in data.Items)
        {
            if (!TryGetReminderTrigger(item, now, defaultOffsetMinutes, out ReminderTriggerKind triggerKind, out int? effectiveOffsetMinutes))
            {
                continue;
            }

            string reminderKey = GetReminderKey(widget.Id, item, triggerKind, effectiveOffsetMinutes);
            if (!_sessionNotifiedKeys.Add(reminderKey))
            {
                continue;
            }

            if (triggerKind == ReminderTriggerKind.Snooze)
            {
                item.SnoozeLastNotifiedAt = now;
                item.SnoozedUntil = null;
            }
            else
            {
                item.ReminderLastNotifiedAt = now;
                item.ReminderDismissedForDueDate = item.DueDate;
            }

            changed = true;
            candidates.Add(new TodoReminderCandidate(
                widget.Id,
                widget.Name,
                item,
                item.DueDate ?? now,
                null,
                null));
        }

        if (changed)
        {
            await store.SaveAsync(data);
        }
    }

    private async Task CollectWorkspaceCandidatesAsync(
        WidgetConfig widget,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        List<TodoReminderCandidate> candidates)
    {
        TodoWorkspaceSnapshot snapshot = await _workspaceService!.LoadSnapshotAsync();
        var recurrence = new TodoRecurrenceExpansionService();
        DateOnly localDate = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (TodoTask seriesTask in snapshot.Tasks.Where(task =>
                     task.DeletedAt is null && task.Status == TodoTaskStatus.Open))
        {
            IReadOnlyList<TodoOccurrence> occurrences = seriesTask.RecurrenceRule is null
                ? [new TodoOccurrence(
                    TodoRecurrenceExpansionService.BuildOccurrenceKey(seriesTask.Id, localDate),
                    seriesTask.Id,
                    seriesTask,
                    seriesTask.Schedule?.Date ??
                    (seriesTask.DeadlineAt is { } deadline
                        ? DateOnly.FromDateTime(deadline.LocalDateTime)
                        : localDate),
                    false)]
                : recurrence.Expand(
                    snapshot.Tasks,
                    localDate.AddDays(-2),
                    localDate.AddDays(2),
                    snapshot.RecurrenceExceptions)
                    .Where(occurrence => string.Equals(occurrence.SeriesTaskId, seriesTask.Id, StringComparison.Ordinal))
                    .ToList();
            bool changed = false;
            foreach (TodoOccurrence occurrence in occurrences)
            {
                foreach (TodoReminderRule rule in seriesTask.Reminders.Where(rule => rule.IsEnabled))
                {
                    DateTimeOffset? targetAt = ResolveReminderTarget(occurrence.Task, rule);
                    if (targetAt is null)
                    {
                        continue;
                    }

                    bool snoozed = rule.SnoozedUntil is { } snoozedUntil && now >= snoozedUntil;
                    int offsetMinutes = rule.OffsetMinutes ?? defaultOffsetMinutes;
                    DateTimeOffset reminderAt = targetAt.Value.AddMinutes(-Math.Max(0, offsetMinutes));
                    bool isDue = now >= reminderAt && now <= targetAt.Value.AddDays(1);
                    if (!snoozed && !isDue)
                    {
                        continue;
                    }

                    string key = $"workspace:{seriesTask.Id}:{rule.Id}:{occurrence.OccurrenceKey}:{(snoozed ? "snooze" : targetAt.Value.UtcTicks)}";
                    if (!_sessionNotifiedKeys.Add(key))
                    {
                        continue;
                    }
                    if (!snoozed &&
                        string.Equals(rule.OccurrenceKey, occurrence.OccurrenceKey, StringComparison.Ordinal) &&
                        rule.LastNotifiedAt is not null)
                    {
                        continue;
                    }

                    if (snoozed)
                    {
                        rule.SnoozeLastNotifiedAt = now;
                        rule.SnoozedUntil = null;
                    }
                    else
                    {
                        rule.OccurrenceKey = occurrence.OccurrenceKey;
                        rule.LastNotifiedAt = now;
                    }
                    changed = true;
                    TodoTask display = occurrence.Task.CloneTask();
                    display.DueDate = targetAt;
                    candidates.Add(new TodoReminderCandidate(
                        widget.Id,
                        widget.Name,
                        display,
                        targetAt.Value,
                        rule.Id,
                        occurrence.OccurrenceKey));
                }
            }
            if (changed)
            {
                await _workspaceService.SaveTaskAsync(seriesTask);
            }
        }
    }

    private static DateTimeOffset? ResolveReminderTarget(TodoTask task, TodoReminderRule rule)
    {
        if (rule.Target == TodoReminderTarget.Absolute)
        {
            return rule.AbsoluteAt;
        }
        if (rule.Target == TodoReminderTarget.Deadline)
        {
            return task.DeadlineAt ?? task.DueDate;
        }
        if (task.Schedule is not { } schedule)
        {
            return null;
        }

        TimeOnly time = schedule.Time ?? new TimeOnly(9, 0);
        DateTime local = schedule.Date.ToDateTime(time, DateTimeKind.Unspecified);
        try
        {
            TimeZoneInfo zone = string.IsNullOrWhiteSpace(schedule.TimeZoneId)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        catch (InvalidTimeZoneException)
        {
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
    }

    private TodoReminderNotification BuildNotification(IReadOnlyList<TodoReminderCandidate> candidates)
    {
        var first = candidates
            .OrderBy(candidate => candidate.TriggerAt)
            .First();
        string title = _localizationService.T("Todo.Reminder.NotificationTitle");
        string dueText = FormatDueDate(first.TriggerAt);
        string itemText = NormalizeNotificationText(first.Item.Text);
        string message = candidates.Count == 1
            ? _localizationService.Format("Todo.Reminder.NotificationSingle", itemText, dueText)
            : _localizationService.Format("Todo.Reminder.NotificationMultiple", candidates.Count, itemText, dueText);

        bool hasTodayDueItem = candidates.Any(candidate =>
            candidate.TriggerAt.ToLocalTime().Date == _clock().Date);

        return new TodoReminderNotification(
            title,
            message,
            candidates.Count,
            first.WidgetId,
            first.Item.Id,
            hasTodayDueItem,
            first.ReminderRuleId,
            first.OccurrenceKey);
    }

    private string FormatDueDate(DateTimeOffset dueDate)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        var today = _clock().Date;
        string time = localDueDate.Second == 0
            ? localDueDate.ToString("HH:mm")
            : localDueDate.ToString("HH:mm:ss");

        if (localDueDate.Date == today)
        {
            return _localizationService.Format("Todo.Due.TodayAt", time);
        }

        if (localDueDate.Date == today.AddDays(1))
        {
            return _localizationService.Format("Todo.Due.TomorrowAt", time);
        }

        return localDueDate.Second == 0
            ? localDueDate.ToString("yyyy/M/d HH:mm")
            : localDueDate.ToString("yyyy/M/d HH:mm:ss");
    }

    private string NormalizeNotificationText(string? text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty)
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return _localizationService.T("Todo.Reminder.Untitled");
        }

        const int maxLength = 48;
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static bool TryGetReminderTrigger(
        TodoItem item,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        out ReminderTriggerKind triggerKind,
        out int? effectiveOffsetMinutes)
    {
        triggerKind = ReminderTriggerKind.Due;
        effectiveOffsetMinutes = null;

        if (item.IsCompleted || item.DueDate is null)
        {
            return false;
        }

        if (TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
        {
            return false;
        }

        if (item.SnoozedUntil is { } snoozedUntil)
        {
            triggerKind = ReminderTriggerKind.Snooze;
            return now >= snoozedUntil;
        }

        int normalizedDefaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(defaultOffsetMinutes);
        effectiveOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes) ??
                                 normalizedDefaultOffsetMinutes;
        if (TodoReminderOptions.IsReminderOff(effectiveOffsetMinutes))
        {
            return false;
        }

        return ShouldNotifyDue(
            item,
            now,
            TimeSpan.FromMinutes(Math.Max(0, effectiveOffsetMinutes.Value)));
    }

    private static bool ShouldNotifyDue(TodoItem item, DateTimeOffset now, TimeSpan reminderOffset)
    {
        if (item.IsCompleted || item.DueDate is not { } dueDate)
        {
            return false;
        }

        if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
            DateTimeOffset.Equals(dismissedForDueDate, dueDate))
        {
            return false;
        }

        DateTimeOffset reminderAt = dueDate - reminderOffset;
        if (now < reminderAt)
        {
            return false;
        }

        return now <= dueDate + MissedReminderGrace;
    }

    private static string GetReminderKey(
        string widgetId,
        TodoItem item,
        ReminderTriggerKind triggerKind,
        int? effectiveOffsetMinutes)
    {
        string dueKey = item.DueDate?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        string triggerKey = triggerKind == ReminderTriggerKind.Snooze
            ? item.SnoozedUntil?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"
            : effectiveOffsetMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default";
        return $"{widgetId}:{item.Id}:{triggerKind}:{dueKey}:{triggerKey}";
    }

    private sealed record TodoReminderCandidate(
        string WidgetId,
        string WidgetName,
        TodoItem Item,
        DateTimeOffset TriggerAt,
        string? ReminderRuleId,
        string? OccurrenceKey);

    private static bool TryParseOccurrenceDate(
        string seriesTaskId,
        string? occurrenceKey,
        out DateOnly occurrenceDate)
    {
        occurrenceDate = default;
        string prefix = $"{seriesTaskId}:";
        return !string.IsNullOrWhiteSpace(occurrenceKey) &&
               occurrenceKey.StartsWith(prefix, StringComparison.Ordinal) &&
               DateOnly.TryParseExact(
                   occurrenceKey[prefix.Length..],
                   "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None,
                   out occurrenceDate);
    }
}

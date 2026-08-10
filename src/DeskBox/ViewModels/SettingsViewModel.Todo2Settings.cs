using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private TodoSettings Todo2 => _settingsService.Settings.Todo;

    public IReadOnlyList<SettingsOption> AvailableTodo2SmartViewOptions =>
        Enum.GetValues<TodoSmartView>()
            .Where(value => value != TodoSmartView.All)
            .Select(value => new SettingsOption(value, _localizationService.T($"Todo.Workspace.{value}")))
            .ToList();

    public IReadOnlyList<SettingsOption> AvailableTodo2DisplayModeOptions =>
        Enum.GetValues<TodoDisplayMode>()
            .Select(value => new SettingsOption(value, _localizationService.T($"Todo.Workspace.View.{value}")))
            .ToList();

    public IReadOnlyList<SettingsOption> AvailableTodo2CompletedVisibilityOptions =>
    [
        new(TodoCompletedVisibility.Hidden, _localizationService.T("Settings.Todo2.Completed.Hidden")),
        new(TodoCompletedVisibility.Collapsed, _localizationService.T("Settings.Todo2.Completed.Collapsed")),
        new(TodoCompletedVisibility.Inline, _localizationService.T("Settings.Todo2.Completed.Inline"))
    ];

    public IReadOnlyList<SettingsOption> AvailableTodo2WeekStartOptions =>
    [
        new("System", _localizationService.T("Settings.Todo2.WeekStart.System")),
        new("Monday", _localizationService.T("Settings.Todo2.WeekStart.Monday")),
        new("Sunday", _localizationService.T("Settings.Todo2.WeekStart.Sunday")),
        new("Saturday", _localizationService.T("Settings.Todo2.WeekStart.Saturday"))
    ];

    public IReadOnlyList<SettingsOption> AvailableTodo2SlotOptions =>
    [
        new(15, _localizationService.Format("Settings.Todo2.Minutes", 15)),
        new(30, _localizationService.Format("Settings.Todo2.Minutes", 30))
    ];

    public IReadOnlyList<SettingsOption> AvailableTodo2DurationOptions =>
    new[] { 15, 30, 45, 60, 90, 120 }
        .Select(value => new SettingsOption(value, _localizationService.Format("Settings.Todo2.Minutes", value)))
        .ToList();

    public IReadOnlyList<SettingsOption> AvailableTodo2ReminderOffsetOptions =>
        AvailableTodoReminderOffsetMinutes
            .Select(value => new SettingsOption(value, GetTodoReminderOffsetDisplayName(value)))
            .ToList();

    public IReadOnlyList<SettingsOption> AvailableTodo2SnoozeOptions =>
    new[] { 5, 10, 15, 30, 60 }
        .Select(value => new SettingsOption(value, _localizationService.Format("Settings.Todo2.Minutes", value)))
        .ToList();

    public IReadOnlyList<SettingsOption> AvailableTodo2RecurrenceModeOptions =>
    [
        new(TodoRecurrenceGenerationMode.FixedSchedule, _localizationService.T("Settings.Todo2.Recurrence.Fixed")),
        new(TodoRecurrenceGenerationMode.AfterCompletion, _localizationService.T("Settings.Todo2.Recurrence.AfterCompletion"))
    ];

    public IReadOnlyList<SettingsOption> AvailableTodo2AttachmentModeOptions =>
    [
        new(SettingsService.AttachmentStorageModeLink, _localizationService.T("Settings.AttachmentStorageMode.Link")),
        new(SettingsService.AttachmentStorageModeCopy, _localizationService.T("Settings.AttachmentStorageMode.Copy"))
    ];

    public IReadOnlyList<SettingsOption> AvailableTodo2HotkeyOptions =>
    [
        CreateTodoHotkeyOption(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift, 0x54),
        CreateTodoHotkeyOption(HotkeyModifierKeys.Alt | HotkeyModifierKeys.Shift, 0x54),
        CreateTodoHotkeyOption(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt, 0x54),
        CreateTodoHotkeyOption(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift, 0x59)
    ];

    public TodoSmartView Todo2DefaultSmartView
    {
        get => Todo2.QuickRecord.DefaultSmartView;
        set => SetTodo2(value, Todo2.QuickRecord.DefaultSmartView, v => Todo2.QuickRecord.DefaultSmartView = v, nameof(Todo2DefaultSmartView));
    }

    public TodoDisplayMode Todo2DefaultDisplayMode
    {
        get => Todo2.Calendar.DefaultDisplayMode;
        set => SetTodo2(value, Todo2.Calendar.DefaultDisplayMode, v => Todo2.Calendar.DefaultDisplayMode = v, nameof(Todo2DefaultDisplayMode));
    }

    public TodoCompletedVisibility Todo2CompletedVisibility
    {
        get => Todo2.CompletionAndData.CompletedVisibility;
        set => SetTodo2(value, Todo2.CompletionAndData.CompletedVisibility, v => Todo2.CompletionAndData.CompletedVisibility = v, nameof(Todo2CompletedVisibility));
    }

    public string Todo2NewTaskPosition
    {
        get => Todo2.QuickRecord.NewTaskPosition;
        set
        {
            string normalized = string.Equals(value, SettingsService.TodoNewTaskPositionBottom, StringComparison.Ordinal)
                ? SettingsService.TodoNewTaskPositionBottom
                : SettingsService.TodoNewTaskPositionTop;
            SetTodo2(normalized, Todo2.QuickRecord.NewTaskPosition, v => Todo2.QuickRecord.NewTaskPosition = v, nameof(Todo2NewTaskPosition));
        }
    }

    public bool Todo2ContinuousEntry
    {
        get => Todo2.QuickRecord.ContinuousEntry;
        set => SetTodo2(value, Todo2.QuickRecord.ContinuousEntry, v => Todo2.QuickRecord.ContinuousEntry = v, nameof(Todo2ContinuousEntry));
    }

    public bool Todo2NaturalLanguageParsing
    {
        get => Todo2.QuickRecord.NaturalLanguageParsing;
        set => SetTodo2(value, Todo2.QuickRecord.NaturalLanguageParsing, v => Todo2.QuickRecord.NaturalLanguageParsing = v, nameof(Todo2NaturalLanguageParsing));
    }

    public bool Todo2ShowParsedTokens
    {
        get => Todo2.QuickRecord.ShowParsedTokens;
        set => SetTodo2(value, Todo2.QuickRecord.ShowParsedTokens, v => Todo2.QuickRecord.ShowParsedTokens = v, nameof(Todo2ShowParsedTokens));
    }

    public bool Todo2HotkeyEnabled
    {
        get => Todo2.QuickRecord.TodoHotkeyEnabled;
        set
        {
            if (SetTodo2(
                    value,
                    Todo2.QuickRecord.TodoHotkeyEnabled,
                    v => Todo2.QuickRecord.TodoHotkeyEnabled = v,
                    nameof(Todo2HotkeyEnabled)))
            {
                App.Current?.TodoHotkeyService?.RefreshRegistration();
            }
        }
    }

    public string Todo2HotkeyGesture
    {
        get => $"{Todo2.QuickRecord.TodoHotkeyModifiers}:{Todo2.QuickRecord.TodoHotkeyKey}";
        set
        {
            string[] parts = value?.Split(':', 2) ?? [];
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int modifiers) ||
                !int.TryParse(parts[1], out int key))
            {
                return;
            }

            GlobalHotkeyGesture gesture = GlobalHotkeyService.NormalizeGesture(modifiers, key);
            if (!GlobalHotkeyService.IsValidGesture(gesture) ||
                (Todo2.QuickRecord.TodoHotkeyModifiers == (int)gesture.Modifiers &&
                 Todo2.QuickRecord.TodoHotkeyKey == gesture.VirtualKey))
            {
                return;
            }

            Todo2.QuickRecord.TodoHotkeyModifiers = (int)gesture.Modifiers;
            Todo2.QuickRecord.TodoHotkeyKey = gesture.VirtualKey;
            OnPropertyChanged();
            if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
            {
                _settingsService.SaveDebounced();
            }
            App.Current?.TodoHotkeyService?.RefreshRegistration();
        }
    }

    public string Todo2WeekStart
    {
        get => Todo2.Calendar.WeekStart;
        set => SetTodo2(value, Todo2.Calendar.WeekStart, v => Todo2.Calendar.WeekStart = v, nameof(Todo2WeekStart));
    }

    public int Todo2CalendarSlotMinutes
    {
        get => Todo2.Calendar.CalendarSlotMinutes;
        set => SetTodo2(value <= 15 ? 15 : 30, Todo2.Calendar.CalendarSlotMinutes, v => Todo2.Calendar.CalendarSlotMinutes = v, nameof(Todo2CalendarSlotMinutes));
    }

    public int Todo2DefaultDurationMinutes
    {
        get => Todo2.Calendar.DefaultDurationMinutes;
        set => SetTodo2(Math.Clamp(value, 15, 1440), Todo2.Calendar.DefaultDurationMinutes, v => Todo2.Calendar.DefaultDurationMinutes = v, nameof(Todo2DefaultDurationMinutes));
    }

    public int Todo2WorkdayStartHour
    {
        get => Todo2.Calendar.WorkdayStartHour;
        set => SetTodo2(Math.Clamp(value, 0, 22), Todo2.Calendar.WorkdayStartHour, v => Todo2.Calendar.WorkdayStartHour = v, nameof(Todo2WorkdayStartHour));
    }

    public int Todo2WorkdayEndHour
    {
        get => Todo2.Calendar.WorkdayEndHour;
        set => SetTodo2(Math.Clamp(value, Todo2.Calendar.WorkdayStartHour + 1, 24), Todo2.Calendar.WorkdayEndHour, v => Todo2.Calendar.WorkdayEndHour = v, nameof(Todo2WorkdayEndHour));
    }

    public bool Todo2ShowWeekNumbers
    {
        get => Todo2.Calendar.ShowWeekNumbers;
        set => SetTodo2(value, Todo2.Calendar.ShowWeekNumbers, v => Todo2.Calendar.ShowWeekNumbers = v, nameof(Todo2ShowWeekNumbers));
    }

    public bool Todo2ShowUnscheduledPool
    {
        get => Todo2.Calendar.ShowUnscheduledPool;
        set => SetTodo2(value, Todo2.Calendar.ShowUnscheduledPool, v => Todo2.Calendar.ShowUnscheduledPool = v, nameof(Todo2ShowUnscheduledPool));
    }

    public bool Todo2ReminderEnabled
    {
        get => Todo2.RemindersAndRecurrence.Enabled;
        set
        {
            if (SetTodo2(value, Todo2.RemindersAndRecurrence.Enabled, v => Todo2.RemindersAndRecurrence.Enabled = v, nameof(Todo2ReminderEnabled)))
            {
                App.Current?.TodoReminderService?.Refresh();
            }
        }
    }

    public bool Todo2AddDefaultReminder
    {
        get => Todo2.RemindersAndRecurrence.AddDefaultReminder;
        set => SetTodo2(value, Todo2.RemindersAndRecurrence.AddDefaultReminder, v => Todo2.RemindersAndRecurrence.AddDefaultReminder = v, nameof(Todo2AddDefaultReminder));
    }

    public int Todo2DefaultReminderOffsetMinutes
    {
        get => Todo2.RemindersAndRecurrence.DefaultOffsetMinutes;
        set => SetTodo2(SettingsService.NormalizeTodoReminderOffsetMinutes(value), Todo2.RemindersAndRecurrence.DefaultOffsetMinutes, v => Todo2.RemindersAndRecurrence.DefaultOffsetMinutes = v, nameof(Todo2DefaultReminderOffsetMinutes));
    }

    public int Todo2DefaultSnoozeMinutes
    {
        get => Todo2.RemindersAndRecurrence.DefaultSnoozeMinutes;
        set => SetTodo2(Math.Clamp(value, 1, 1440), Todo2.RemindersAndRecurrence.DefaultSnoozeMinutes, v => Todo2.RemindersAndRecurrence.DefaultSnoozeMinutes = v, nameof(Todo2DefaultSnoozeMinutes));
    }

    public TodoRecurrenceGenerationMode Todo2DefaultRecurrenceMode
    {
        get => Todo2.RemindersAndRecurrence.DefaultRecurrenceMode;
        set => SetTodo2(value, Todo2.RemindersAndRecurrence.DefaultRecurrenceMode, v => Todo2.RemindersAndRecurrence.DefaultRecurrenceMode = v, nameof(Todo2DefaultRecurrenceMode));
    }

    public bool Todo2LiveMarkdownPreview
    {
        get => Todo2.NotesAndAttachments.LiveMarkdownPreview;
        set => SetTodo2(value, Todo2.NotesAndAttachments.LiveMarkdownPreview, v => Todo2.NotesAndAttachments.LiveMarkdownPreview = v, nameof(Todo2LiveMarkdownPreview));
    }

    public bool Todo2AllowRemoteImages
    {
        get => Todo2.NotesAndAttachments.AllowRemoteImages;
        set => SetTodo2(value, Todo2.NotesAndAttachments.AllowRemoteImages, v => Todo2.NotesAndAttachments.AllowRemoteImages = v, nameof(Todo2AllowRemoteImages));
    }

    public string Todo2AttachmentStorageMode
    {
        get => Todo2.NotesAndAttachments.AttachmentStorageMode;
        set => SetTodo2(SettingsService.NormalizeAttachmentStorageMode(value), Todo2.NotesAndAttachments.AttachmentStorageMode, v => Todo2.NotesAndAttachments.AttachmentStorageMode = v, nameof(Todo2AttachmentStorageMode));
    }

    public bool Todo2AutoPurgeTrash
    {
        get => Todo2.CompletionAndData.AutoPurgeTrash;
        set => SetTodo2(value, Todo2.CompletionAndData.AutoPurgeTrash, v => Todo2.CompletionAndData.AutoPurgeTrash = v, nameof(Todo2AutoPurgeTrash));
    }

    public int Todo2TrashRetentionDays
    {
        get => Todo2.CompletionAndData.TrashRetentionDays;
        set => SetTodo2(Math.Clamp(value, 1, 3650), Todo2.CompletionAndData.TrashRetentionDays, v => Todo2.CompletionAndData.TrashRetentionDays = v, nameof(Todo2TrashRetentionDays));
    }

    public bool Todo2ConfirmPermanentDelete
    {
        get => Todo2.CompletionAndData.ConfirmPermanentDelete;
        set => SetTodo2(value, Todo2.CompletionAndData.ConfirmPermanentDelete, v => Todo2.CompletionAndData.ConfirmPermanentDelete = v, nameof(Todo2ConfirmPermanentDelete));
    }

    public string Todo2CalendarSourcesSummary => Todo2.Calendar.Sources.Count == 0
        ? _localizationService.T("Settings.Todo2.CalendarSources.None")
        : _localizationService.Format("Settings.Todo2.CalendarSources.Count", Todo2.Calendar.Sources.Count(source => source.IsEnabled));

    public bool Todo2ShowTodayView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Today);
        set => SetTodoSmartViewVisible(TodoSmartView.Today, value, nameof(Todo2ShowTodayView));
    }

    public bool Todo2ShowInboxView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Inbox);
        set => SetTodoSmartViewVisible(TodoSmartView.Inbox, value, nameof(Todo2ShowInboxView));
    }

    public bool Todo2ShowPlannedView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Planned);
        set => SetTodoSmartViewVisible(TodoSmartView.Planned, value, nameof(Todo2ShowPlannedView));
    }

    public bool Todo2ShowUnscheduledView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Unscheduled);
        set => SetTodoSmartViewVisible(TodoSmartView.Unscheduled, value, nameof(Todo2ShowUnscheduledView));
    }

    public bool Todo2ShowImportantView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Important);
        set => SetTodoSmartViewVisible(TodoSmartView.Important, value, nameof(Todo2ShowImportantView));
    }

    public bool Todo2ShowCompletedView
    {
        get => IsTodoSmartViewVisible(TodoSmartView.Completed);
        set => SetTodoSmartViewVisible(TodoSmartView.Completed, value, nameof(Todo2ShowCompletedView));
    }

    private SettingsOption CreateTodoHotkeyOption(HotkeyModifierKeys modifiers, int key)
    {
        var gesture = new GlobalHotkeyGesture(modifiers, key);
        return new SettingsOption(
            $"{(int)modifiers}:{key}",
            GlobalHotkeyService.FormatGesture(gesture, _localizationService));
    }

    private bool IsTodoSmartViewVisible(TodoSmartView view) =>
        !Todo2.Organization.HiddenSmartViews.Contains(view);

    private void SetTodoSmartViewVisible(TodoSmartView view, bool visible, string propertyName)
    {
        bool currentlyVisible = IsTodoSmartViewVisible(view);
        if (visible == currentlyVisible)
        {
            return;
        }

        if (visible)
        {
            Todo2.Organization.HiddenSmartViews.RemoveAll(candidate => candidate == view);
        }
        else if (!Todo2.Organization.HiddenSmartViews.Contains(view))
        {
            Todo2.Organization.HiddenSmartViews.Add(view);
        }
        OnPropertyChanged(propertyName);
        if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
        {
            _settingsService.SaveDebounced();
        }
    }

    private bool SetTodo2<T>(T value, T current, Action<T> apply, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return false;
        }

        apply(value);
        OnPropertyChanged(propertyName);
        if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
        {
            MirrorTodo2CompatibilitySettings();
            _settingsService.SaveDebounced();
        }
        return true;
    }

    private void MirrorTodo2CompatibilitySettings()
    {
        AppSettings settings = _settingsService.Settings;
        settings.TodoReminderEnabled = settings.Todo.RemindersAndRecurrence.Enabled;
        settings.TodoDefaultReminderOffsetMinutes = settings.Todo.RemindersAndRecurrence.DefaultOffsetMinutes;
        settings.TodoNewTaskPosition = settings.Todo.QuickRecord.NewTaskPosition;
        settings.TodoShowCompletedTasks = settings.Todo.CompletionAndData.CompletedVisibility == TodoCompletedVisibility.Inline;
    }

    internal void NotifyTodo2SettingsChanged()
    {
        foreach (string propertyName in new[]
                 {
                     nameof(Todo2DefaultSmartView), nameof(Todo2DefaultDisplayMode), nameof(Todo2CompletedVisibility),
                     nameof(Todo2NewTaskPosition), nameof(Todo2ContinuousEntry), nameof(Todo2NaturalLanguageParsing),
                     nameof(Todo2ShowParsedTokens), nameof(Todo2HotkeyEnabled), nameof(Todo2HotkeyGesture),
                     nameof(Todo2WeekStart), nameof(Todo2CalendarSlotMinutes),
                     nameof(Todo2DefaultDurationMinutes), nameof(Todo2WorkdayStartHour), nameof(Todo2WorkdayEndHour),
                     nameof(Todo2ShowWeekNumbers), nameof(Todo2ShowUnscheduledPool), nameof(Todo2ReminderEnabled),
                     nameof(Todo2AddDefaultReminder), nameof(Todo2DefaultReminderOffsetMinutes), nameof(Todo2DefaultSnoozeMinutes),
                     nameof(Todo2DefaultRecurrenceMode), nameof(Todo2LiveMarkdownPreview), nameof(Todo2AllowRemoteImages),
                     nameof(Todo2AttachmentStorageMode), nameof(Todo2AutoPurgeTrash), nameof(Todo2TrashRetentionDays),
                     nameof(Todo2ConfirmPermanentDelete), nameof(Todo2CalendarSourcesSummary),
                     nameof(Todo2ShowTodayView), nameof(Todo2ShowInboxView), nameof(Todo2ShowPlannedView),
                     nameof(Todo2ShowUnscheduledView), nameof(Todo2ShowImportantView), nameof(Todo2ShowCompletedView),
                     nameof(AvailableTodo2SmartViewOptions), nameof(AvailableTodo2DisplayModeOptions),
                     nameof(AvailableTodo2CompletedVisibilityOptions), nameof(AvailableTodo2WeekStartOptions),
                     nameof(AvailableTodo2SlotOptions), nameof(AvailableTodo2DurationOptions),
                     nameof(AvailableTodo2ReminderOffsetOptions), nameof(AvailableTodo2SnoozeOptions),
                     nameof(AvailableTodo2RecurrenceModeOptions), nameof(AvailableTodo2AttachmentModeOptions),
                     nameof(AvailableTodo2HotkeyOptions)
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }
}

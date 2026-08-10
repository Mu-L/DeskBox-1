namespace DeskBox.Models;

/// <summary>
/// Global Todo defaults. Widget-specific presentation remains in
/// <see cref="TodoWidgetPresentationSettings"/> metadata.
/// </summary>
public sealed class TodoSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Zero identifies settings created before the nested Todo model existed.</summary>
    public int SchemaVersion { get; set; }

    public TodoQuickRecordSettings QuickRecord { get; set; } = new();

    public TodoCalendarSettings Calendar { get; set; } = new();

    public TodoReminderAndRecurrenceSettings RemindersAndRecurrence { get; set; } = new();

    public TodoNotesAndAttachmentsSettings NotesAndAttachments { get; set; } = new();

    public TodoCompletionAndDataSettings CompletionAndData { get; set; } = new();

    public TodoOrganizationSettings Organization { get; set; } = new();

    public static TodoSettings CreateNewUserDefaults() => new()
    {
        SchemaVersion = CurrentSchemaVersion
    };
}

public sealed class TodoQuickRecordSettings
{
    public string DefaultListId { get; set; } = TodoWorkspaceDefaults.InboxListId;

    public TodoSmartView DefaultSmartView { get; set; } = TodoSmartView.Today;

    public string NewTaskPosition { get; set; } = "Top";

    public bool ContinuousEntry { get; set; } = true;

    public bool NaturalLanguageParsing { get; set; } = true;

    public bool ShowParsedTokens { get; set; } = true;

    public bool TodoHotkeyEnabled { get; set; }

    public int TodoHotkeyModifiers { get; set; } = (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift);

    public int TodoHotkeyKey { get; set; } = 0x54;
}

public sealed class TodoCalendarSettings
{
    public TodoDisplayMode DefaultDisplayMode { get; set; } = TodoDisplayMode.List;

    /// <summary>System, Sunday, Monday, or Saturday.</summary>
    public string WeekStart { get; set; } = "System";

    public int CalendarSlotMinutes { get; set; } = 30;

    public int DefaultDurationMinutes { get; set; } = TodoWorkspaceDefaults.DefaultDurationMinutes;

    public int WorkdayStartHour { get; set; } = 8;

    public int WorkdayEndHour { get; set; } = 20;

    public bool ShowWeekNumbers { get; set; }

    public bool ShowUnscheduledPool { get; set; } = true;

    public List<TodoCalendarSourceSettings> Sources { get; set; } = [];
}

public sealed class TodoCalendarSourceSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public string? ColorMarker { get; set; }
}

public sealed class TodoReminderAndRecurrenceSettings
{
    public bool Enabled { get; set; } = true;

    public int DefaultOffsetMinutes { get; set; } = TodoWorkspaceDefaults.DefaultReminderOffsetMinutes;

    /// <summary>ScheduleThenDeadline, Schedule, or Deadline.</summary>
    public string DefaultTarget { get; set; } = "ScheduleThenDeadline";

    public bool AddDefaultReminder { get; set; } = true;

    public int DefaultSnoozeMinutes { get; set; } = 10;

    public TodoRecurrenceGenerationMode DefaultRecurrenceMode { get; set; } = TodoRecurrenceGenerationMode.FixedSchedule;
}

public sealed class TodoNotesAndAttachmentsSettings
{
    public bool LiveMarkdownPreview { get; set; } = true;

    public bool AllowRemoteImages { get; set; }

    public string AttachmentStorageMode { get; set; } = "Link";

    public int MaximumNoteCharacters { get; set; } = TodoWorkspaceDefaults.MaxNotesCharacters;
}

public sealed class TodoCompletionAndDataSettings
{
    public TodoCompletedVisibility CompletedVisibility { get; set; } = TodoCompletedVisibility.Collapsed;

    public bool AutoPurgeTrash { get; set; } = true;

    public int TrashRetentionDays { get; set; } = TodoWorkspaceDefaults.TrashRetentionDays;

    public bool ConfirmPermanentDelete { get; set; } = true;
}

public sealed class TodoOrganizationSettings
{
    public List<TodoSmartView> SmartViewOrder { get; set; } =
    [
        TodoSmartView.Today,
        TodoSmartView.Inbox,
        TodoSmartView.Planned,
        TodoSmartView.Unscheduled,
        TodoSmartView.Important,
        TodoSmartView.Completed
    ];

    public List<TodoSmartView> HiddenSmartViews { get; set; } = [];
}

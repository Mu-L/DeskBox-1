using System.Text.Json.Serialization;

namespace DeskBox.Models;

public static class TodoWorkspaceDefaults
{
    public const string InboxListId = "inbox";
    public const string DefaultListId = InboxListId;
    public const string LegacyDefaultListId = "tasks";
    public const int DefaultDurationMinutes = 30;
    public const int DefaultReminderOffsetMinutes = 5;
    public const int TrashRetentionDays = 30;
    public const int MaxNotesCharacters = 256 * 1024;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoTaskStatus
{
    Open,
    Completed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoPriority
{
    None,
    Low,
    Medium,
    High
}

public sealed class TodoSchedule
{
    public DateOnly Date { get; set; }

    public TimeOnly? Time { get; set; }

    public string? TimeZoneId { get; set; }

    public int? DurationMinutes { get; set; }

    [JsonIgnore]
    public bool IsAllDay => Time is null;

    public TodoSchedule Clone() => new()
    {
        Date = Date,
        Time = Time,
        TimeZoneId = TimeZoneId,
        DurationMinutes = DurationMinutes
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoReminderTarget
{
    Schedule,
    Deadline,
    Absolute
}

public sealed class TodoReminderRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public TodoReminderTarget Target { get; set; } = TodoReminderTarget.Deadline;

    public int? OffsetMinutes { get; set; }

    public DateTimeOffset? AbsoluteAt { get; set; }

    public string? OccurrenceKey { get; set; }

    public DateTimeOffset? LastNotifiedAt { get; set; }

    public DateTimeOffset? SnoozedUntil { get; set; }

    public DateTimeOffset? SnoozeLastNotifiedAt { get; set; }

    public bool IsEnabled { get; set; } = true;

    public TodoReminderRule Clone() => new()
    {
        Id = Id,
        Target = Target,
        OffsetMinutes = OffsetMinutes,
        AbsoluteAt = AbsoluteAt,
        OccurrenceKey = OccurrenceKey,
        LastNotifiedAt = LastNotifiedAt,
        SnoozedUntil = SnoozedUntil,
        SnoozeLastNotifiedAt = SnoozeLastNotifiedAt,
        IsEnabled = IsEnabled
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoRecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoRecurrenceAnchor
{
    Schedule,
    Deadline
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoRecurrenceGenerationMode
{
    FixedSchedule,
    AfterCompletion
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoRecurrenceEditScope
{
    Occurrence,
    Future,
    Series
}

public sealed class TodoRecurrenceRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public TodoRecurrenceFrequency Frequency { get; set; } = TodoRecurrenceFrequency.Daily;

    public int Interval { get; set; } = 1;

    public List<DayOfWeek> WeekDays { get; set; } = [];

    public int? MonthDay { get; set; }

    public int? MonthWeekOrdinal { get; set; }

    public DayOfWeek? MonthWeekDay { get; set; }

    public DateOnly? EndDate { get; set; }

    public int? OccurrenceCount { get; set; }

    public TodoRecurrenceAnchor Anchor { get; set; } = TodoRecurrenceAnchor.Deadline;

    public TodoRecurrenceGenerationMode GenerationMode { get; set; } = TodoRecurrenceGenerationMode.FixedSchedule;

    public TodoRecurrenceRule Clone() => new()
    {
        Id = Id,
        Frequency = Frequency,
        Interval = Interval,
        WeekDays = [.. WeekDays],
        MonthDay = MonthDay,
        MonthWeekOrdinal = MonthWeekOrdinal,
        MonthWeekDay = MonthWeekDay,
        EndDate = EndDate,
        OccurrenceCount = OccurrenceCount,
        Anchor = Anchor,
        GenerationMode = GenerationMode
    };
}

public sealed class TodoRecurrenceException
{
    public string SeriesId { get; set; } = string.Empty;

    public string OccurrenceKey { get; set; } = string.Empty;

    public string? TaskId { get; set; }

    public bool IsCancelled { get; set; }
}

public sealed class TodoList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ColorMarker { get; set; }
    public double SortRank { get; set; }
    public bool IsSystem { get; set; }
    public bool IsArchived { get; set; }
}

public sealed class TodoSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ListId { get; set; } = TodoWorkspaceDefaults.DefaultListId;
    public string Name { get; set; } = string.Empty;
    public double SortRank { get; set; }
    public bool IsArchived { get; set; }
}

public sealed class TodoTag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? ColorMarker { get; set; }
    public double SortRank { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoSmartView
{
    Today,
    Inbox,
    Planned,
    Unscheduled,
    Important,
    Completed,
    All
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoSortMode
{
    Smart,
    Manual,
    Planned,
    Deadline,
    Priority,
    Updated
}

public sealed class TodoQuery
{
    public TodoSmartView? SmartView { get; set; }
    public string? ListId { get; set; }
    public string? SectionId { get; set; }
    public List<string> TagIds { get; set; } = [];
    public TodoPriority? MinimumPriority { get; set; }
    public TodoTaskStatus? Status { get; set; }
    public DateOnly? RangeStart { get; set; }
    public DateOnly? RangeEnd { get; set; }
    public string? SearchText { get; set; }
    public TodoSortMode SortMode { get; set; } = TodoSortMode.Smart;
    public bool IncludeDeleted { get; set; }
}

public sealed class TodoSavedView
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? IconGlyph { get; set; }
    public double SortRank { get; set; }
    public TodoQuery Query { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoDisplayMode
{
    List,
    Agenda,
    Month,
    Week,
    Day
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoResponsivePreference
{
    Auto,
    SingleColumn,
    PreferSplit
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoCompletedVisibility
{
    Hidden,
    Collapsed,
    Inline
}

public sealed class TodoWidgetPresentationSettings
{
    public const string MetadataKey = "todo.presentation.v1";

    public TodoSmartView SmartView { get; set; } = TodoSmartView.Today;
    public string? ListId { get; set; }
    public string? SectionId { get; set; }
    public string? TagId { get; set; }
    public string? SavedViewId { get; set; }
    public TodoDisplayMode DisplayMode { get; set; } = TodoDisplayMode.List;
    public TodoResponsivePreference ResponsivePreference { get; set; } = TodoResponsivePreference.Auto;
    public double ListSplitRatio { get; set; } = 0.40;
    public double CalendarSplitRatio { get; set; } = 0.58;
    public double DensityScale { get; set; } = 1.0;
    public TodoCompletedVisibility CompletedVisibility { get; set; } = TodoCompletedVisibility.Collapsed;
    public bool ShowSchedule { get; set; } = true;
    public bool ShowDeadline { get; set; } = true;
    public bool ShowStepProgress { get; set; } = true;
    public bool ShowTags { get; set; } = true;
    public bool ShowAttachments { get; set; } = true;
    public int CalendarSlotMinutes { get; set; } = 30;
    public int DefaultDurationMinutes { get; set; } = TodoWorkspaceDefaults.DefaultDurationMinutes;
    public int WorkdayStartHour { get; set; } = 8;
    public int WorkdayEndHour { get; set; } = 20;
    public bool ShowWeekNumbers { get; set; }
    public bool ShowUnscheduledPool { get; set; } = true;
    public bool LiveMarkdownPreview { get; set; } = true;
    public DateOnly? SelectedDate { get; set; }
}

public sealed class TodoTask : TodoItem
{
    [JsonIgnore]
    public DateOnly? PresentedOccurrenceDate { get; set; }

    public string Title
    {
        get => Text;
        set => Text = value;
    }

    public TodoTask CloneTask()
    {
        return new TodoTask
        {
            Id = Id,
            Text = Text,
            IsCompleted = IsCompleted,
            IsImportant = IsImportant,
            ColorMarker = ColorMarker,
            DueDate = DueDate,
            Schedule = Schedule?.Clone(),
            DeadlineAt = DeadlineAt,
            Status = Status,
            Priority = Priority,
            ListId = ListId,
            SectionId = SectionId,
            TagIds = [.. TagIds],
            Reminders = Reminders.Select(rule => rule.Clone()).ToList(),
            RecurrenceRule = RecurrenceRule?.Clone(),
            TodaySortRank = TodaySortRank,
            DeletedAt = DeletedAt,
            Recurrence = Recurrence?.Clone(),
            Steps = Steps.Select(step => new TodoStep
            {
                Id = step.Id,
                Text = step.Text,
                IsCompleted = step.IsCompleted,
                SortOrder = step.SortOrder
            }).ToList(),
            Notes = Notes,
            Attachments = Attachments.Select(attachment => attachment.Clone()).ToList(),
            CompletedAt = CompletedAt,
            ReminderLastNotifiedAt = ReminderLastNotifiedAt,
            ReminderDismissedForDueDate = ReminderDismissedForDueDate,
            ReminderOffsetMinutes = ReminderOffsetMinutes,
            SnoozedUntil = SnoozedUntil,
            SnoozeLastNotifiedAt = SnoozeLastNotifiedAt,
            RecurrenceSeriesId = RecurrenceSeriesId,
            PresentedOccurrenceDate = PresentedOccurrenceDate,
            GeneratedNextItemId = GeneratedNextItemId,
            SortOrder = SortOrder,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}

public sealed class TodoWorkspaceSnapshot
{
    public List<TodoTask> Tasks { get; set; } = [];
    public List<TodoList> Lists { get; set; } = [];
    public List<TodoSection> Sections { get; set; } = [];
    public List<TodoTag> Tags { get; set; } = [];
    public List<TodoSavedView> SavedViews { get; set; } = [];
    public List<TodoRecurrenceException> RecurrenceExceptions { get; set; } = [];
}

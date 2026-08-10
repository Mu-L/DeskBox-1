namespace DeskBox.Models;

public class TodoItem
{
    public const string RedColorMarker = "red";
    public const string OrangeColorMarker = "orange";
    public const string YellowColorMarker = "yellow";
    public const string GreenColorMarker = "green";
    public const string BlueColorMarker = "blue";
    public const string PurpleColorMarker = "purple";
    public const string TealColorMarker = "teal";
    public const string PinkColorMarker = "pink";

    public static readonly string[] SupportedColorMarkers =
    [
        RedColorMarker,
        OrangeColorMarker,
        YellowColorMarker,
        GreenColorMarker,
        BlueColorMarker,
        PurpleColorMarker,
        TealColorMarker,
        PinkColorMarker
    ];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public bool IsImportant { get; set; }

    public string? ColorMarker { get; set; }

    public DateTimeOffset? DueDate { get; set; }

    /// <summary>
    /// User-selected time to work on this task. Unlike <see cref="DueDate"/>,
    /// changing this value never changes whether the task is overdue.
    /// </summary>
    public TodoSchedule? Schedule { get; set; }

    /// <summary>
    /// Rich-model deadline. During the compatibility period this mirrors
    /// <see cref="DueDate"/> so the existing Todo surface can continue to work.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    public TodoTaskStatus Status { get; set; } = TodoTaskStatus.Open;

    public TodoPriority Priority { get; set; } = TodoPriority.None;

    public string ListId { get; set; } = TodoWorkspaceDefaults.DefaultListId;

    public string? SectionId { get; set; }

    public List<string> TagIds { get; set; } = [];

    public List<TodoReminderRule> Reminders { get; set; } = [];

    public TodoRecurrenceRule? RecurrenceRule { get; set; }

    public double? TodaySortRank { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public TodoRecurrence? Recurrence { get; set; }

    public List<TodoStep> Steps { get; set; } = [];

    public string? Notes { get; set; }

    public List<TodoAttachment> Attachments { get; set; } = [];

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? ReminderLastNotifiedAt { get; set; }

    public DateTimeOffset? ReminderDismissedForDueDate { get; set; }

    public int? ReminderOffsetMinutes { get; set; }

    public DateTimeOffset? SnoozedUntil { get; set; }

    public DateTimeOffset? SnoozeLastNotifiedAt { get; set; }

    public string? RecurrenceSeriesId { get; set; }

    public string? GeneratedNextItemId { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string? NormalizeColorMarker(string? colorMarker)
    {
        if (string.IsNullOrWhiteSpace(colorMarker))
        {
            return null;
        }

        string normalized = colorMarker.Trim().ToLowerInvariant();
        return SupportedColorMarkers.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    public static string GetColorMarkerHex(string? colorMarker)
    {
        return NormalizeColorMarker(colorMarker) switch
        {
            RedColorMarker => "#E34D4D",
            OrangeColorMarker => "#F08A3C",
            YellowColorMarker => "#F2C94C",
            GreenColorMarker => "#4CAF6D",
            BlueColorMarker => "#4D8FE3",
            PurpleColorMarker => "#9B6BE8",
            TealColorMarker => "#2DB7A3",
            PinkColorMarker => "#E66AA2",
            _ => "#8A8F98"
        };
    }

    public static string GetColorMarkerLocalizationKey(string? colorMarker)
    {
        return NormalizeColorMarker(colorMarker) switch
        {
            RedColorMarker => "Todo.Color.Red",
            OrangeColorMarker => "Todo.Color.Orange",
            YellowColorMarker => "Todo.Color.Yellow",
            GreenColorMarker => "Todo.Color.Green",
            BlueColorMarker => "Todo.Color.Blue",
            PurpleColorMarker => "Todo.Color.Purple",
            TealColorMarker => "Todo.Color.Teal",
            PinkColorMarker => "Todo.Color.Pink",
            _ => "Todo.Color.None"
        };
    }
}

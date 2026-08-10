using DeskBox.Models;

namespace DeskBox.Services;

internal static class TodoTaskMapper
{
    public static TodoTask FromLegacy(TodoItem item, string? defaultListId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is TodoTask task)
        {
            return task.CloneTask();
        }

        var mapped = new TodoTask
        {
            Id = NormalizeId(item.Id),
            Text = item.Text?.Trim() ?? string.Empty,
            IsCompleted = item.IsCompleted,
            IsImportant = item.IsImportant,
            ColorMarker = TodoItem.NormalizeColorMarker(item.ColorMarker),
            DueDate = item.DeadlineAt ?? item.DueDate,
            DeadlineAt = item.DeadlineAt ?? item.DueDate,
            Schedule = item.Schedule?.Clone(),
            Status = item.IsCompleted
                ? TodoTaskStatus.Completed
                : item.Status == TodoTaskStatus.Cancelled ? TodoTaskStatus.Cancelled : TodoTaskStatus.Open,
            Priority = item.IsImportant
                ? TodoPriority.High
                : item.Priority,
            ListId = string.IsNullOrWhiteSpace(item.ListId)
                ? defaultListId ?? TodoWorkspaceDefaults.DefaultListId
                : item.ListId.Trim(),
            SectionId = string.IsNullOrWhiteSpace(item.SectionId) ? null : item.SectionId.Trim(),
            TagIds = item.TagIds?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [],
            Reminders = item.Reminders?.Select(reminder => reminder.Clone()).ToList() ?? [],
            RecurrenceRule = item.RecurrenceRule?.Clone(),
            TodaySortRank = item.TodaySortRank,
            DeletedAt = item.DeletedAt,
            Recurrence = item.Recurrence?.Clone(),
            Steps = item.Steps?.Select(CloneStep).ToList() ?? [],
            Notes = item.Notes,
            Attachments = item.Attachments?.Select(attachment => attachment.Clone()).ToList() ?? [],
            CompletedAt = item.CompletedAt,
            ReminderLastNotifiedAt = item.ReminderLastNotifiedAt,
            ReminderDismissedForDueDate = item.ReminderDismissedForDueDate,
            ReminderOffsetMinutes = item.ReminderOffsetMinutes,
            SnoozedUntil = item.SnoozedUntil,
            SnoozeLastNotifiedAt = item.SnoozeLastNotifiedAt,
            RecurrenceSeriesId = item.RecurrenceSeriesId,
            GeneratedNextItemId = item.GeneratedNextItemId,
            SortOrder = Math.Max(0, item.SortOrder),
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default
                ? item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt
                : item.UpdatedAt
        };

        EnsureRichRules(mapped);
        return mapped;
    }

    public static TodoItem ToLegacy(TodoTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        TodoTask clone = task.CloneTask();
        clone.DueDate = clone.DeadlineAt;
        clone.IsCompleted = clone.Status == TodoTaskStatus.Completed;
        clone.IsImportant = clone.Priority == TodoPriority.High;
        return clone;
    }

    public static TodoTask MergeLegacyState(TodoTask? existing, TodoItem incoming)
    {
        TodoTask mapped = FromLegacy(incoming, existing?.ListId);
        if (existing is null)
        {
            return mapped;
        }

        // Older UI surfaces do not know about every rich field. Preserve those
        // fields unless the incoming compatibility object explicitly carries them.
        mapped.Schedule ??= existing.Schedule?.Clone();
        mapped.ListId = string.IsNullOrWhiteSpace(incoming.ListId) ||
                        string.Equals(incoming.ListId, TodoWorkspaceDefaults.DefaultListId, StringComparison.Ordinal) &&
                        !string.Equals(existing.ListId, TodoWorkspaceDefaults.DefaultListId, StringComparison.Ordinal)
            ? existing.ListId
            : mapped.ListId;
        mapped.SectionId ??= existing.SectionId;
        if (incoming.TagIds is null || incoming.TagIds.Count == 0)
        {
            mapped.TagIds = [.. existing.TagIds];
        }

        if (incoming.Reminders is null || incoming.Reminders.Count == 0)
        {
            mapped.Reminders = existing.Reminders.Select(rule => rule.Clone()).ToList();
        }

        mapped.RecurrenceRule ??= existing.RecurrenceRule?.Clone();
        mapped.TodaySortRank ??= existing.TodaySortRank;
        mapped.DeletedAt ??= existing.DeletedAt;
        EnsureRichRules(mapped);
        return mapped;
    }

    private static void EnsureRichRules(TodoTask task)
    {
        if (task.Reminders.Count == 0 &&
            task.ReminderOffsetMinutes is { } offset &&
            offset != TodoReminderOptions.ReminderOff)
        {
            task.Reminders.Add(new TodoReminderRule
            {
                Id = $"legacy-{task.Id}",
                Target = TodoReminderTarget.Deadline,
                OffsetMinutes = offset,
                LastNotifiedAt = task.ReminderLastNotifiedAt,
                SnoozedUntil = task.SnoozedUntil,
                SnoozeLastNotifiedAt = task.SnoozeLastNotifiedAt
            });
        }

        if (task.RecurrenceRule is null && task.Recurrence is { } recurrence)
        {
            string mode = TodoRecurrenceMode.Normalize(recurrence.Mode);
            if (mode != TodoRecurrenceMode.None)
            {
                task.RecurrenceRule = new TodoRecurrenceRule
                {
                    Id = $"legacy-{task.Id}",
                    Frequency = mode switch
                    {
                        TodoRecurrenceMode.Monthly => TodoRecurrenceFrequency.Monthly,
                        TodoRecurrenceMode.Weekly or TodoRecurrenceMode.Weekdays => TodoRecurrenceFrequency.Weekly,
                        _ => TodoRecurrenceFrequency.Daily
                    },
                    WeekDays = mode == TodoRecurrenceMode.Weekdays
                        ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
                        : [],
                    Anchor = TodoRecurrenceAnchor.Deadline,
                    GenerationMode = TodoRecurrenceGenerationMode.AfterCompletion
                };
            }
        }
    }

    private static TodoStep CloneStep(TodoStep step) => new()
    {
        Id = NormalizeId(step.Id),
        Text = step.Text?.Trim() ?? string.Empty,
        IsCompleted = step.IsCompleted,
        SortOrder = Math.Max(0, step.SortOrder)
    };

    private static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
}

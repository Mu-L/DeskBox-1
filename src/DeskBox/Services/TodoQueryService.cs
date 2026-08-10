using DeskBox.Models;

namespace DeskBox.Services;

public sealed class TodoQueryService(ITodoWorkspaceRepository repository)
{
    private readonly ITodoWorkspaceRepository _repository = repository;

    public async Task<IReadOnlyList<TodoTask>> QueryAsync(
        TodoQuery query,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        TodoWorkspaceSnapshot snapshot = await _repository.LoadSnapshotAsync(
            query.IncludeDeleted,
            cancellationToken);
        return Apply(snapshot, query, now ?? DateTimeOffset.Now);
    }

    public static IReadOnlyList<TodoTask> Apply(
        TodoWorkspaceSnapshot snapshot,
        TodoQuery query,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        var listsById = snapshot.Lists.ToDictionary(list => list.Id, StringComparer.Ordinal);
        var tagsById = snapshot.Tags.ToDictionary(tag => tag.Id, StringComparer.Ordinal);

        HashSet<string> exceptionTaskIds = snapshot.RecurrenceExceptions
            .Where(exception => exception.TaskId is not null)
            .Select(exception => exception.TaskId!)
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<TodoTask> tasks = snapshot.Tasks.Where(task => !exceptionTaskIds.Contains(task.Id));
        if (!query.IncludeDeleted)
        {
            tasks = tasks.Where(task => task.DeletedAt is null);
        }

        tasks = query.SmartView switch
        {
            TodoSmartView.Today => tasks.Where(task =>
                (task.Status == TodoTaskStatus.Open &&
                 ((task.Schedule is { } schedule && schedule.Date <= today) ||
                  (task.DeadlineAt is { } deadline && DateOnly.FromDateTime(deadline.LocalDateTime) <= today))) ||
                (task.Status == TodoTaskStatus.Completed &&
                 ((task.CompletedAt is { } completed && DateOnly.FromDateTime(completed.LocalDateTime) == today) ||
                  (task.Schedule is { } completedSchedule && completedSchedule.Date == today) ||
                  (task.DeadlineAt is { } completedDeadline &&
                   DateOnly.FromDateTime(completedDeadline.LocalDateTime) == today)))),
            TodoSmartView.Inbox => tasks.Where(task =>
                task.Status == TodoTaskStatus.Open &&
                string.Equals(task.ListId, TodoWorkspaceDefaults.InboxListId, StringComparison.Ordinal)),
            TodoSmartView.Planned => tasks.Where(task =>
                task.Status == TodoTaskStatus.Open && task.Schedule is not null),
            TodoSmartView.Unscheduled => tasks.Where(task =>
                task.Status == TodoTaskStatus.Open && task.Schedule is null),
            TodoSmartView.Important => tasks.Where(task =>
                task.Status == TodoTaskStatus.Open && task.Priority == TodoPriority.High),
            TodoSmartView.Completed => tasks.Where(task => task.Status == TodoTaskStatus.Completed),
            TodoSmartView.All => tasks,
            _ => tasks.Where(task => task.Status == TodoTaskStatus.Open)
        };

        if (!string.IsNullOrWhiteSpace(query.ListId))
        {
            tasks = tasks.Where(task => string.Equals(task.ListId, query.ListId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.SectionId))
        {
            tasks = tasks.Where(task => string.Equals(task.SectionId, query.SectionId, StringComparison.Ordinal));
        }

        if (query.TagIds.Count > 0)
        {
            tasks = tasks.Where(task => query.TagIds.All(tagId => task.TagIds.Contains(tagId, StringComparer.Ordinal)));
        }

        if (query.MinimumPriority is { } priority)
        {
            tasks = tasks.Where(task => task.Priority >= priority);
        }

        if (query.Status is { } status)
        {
            tasks = tasks.Where(task => task.Status == status);
        }

        if (query.RangeStart is { } rangeStart)
        {
            tasks = tasks.Where(task => GetRelevantDate(task) is { } date && date >= rangeStart);
        }

        if (query.RangeEnd is { } rangeEnd)
        {
            tasks = tasks.Where(task => GetRelevantDate(task) is { } date && date <= rangeEnd);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            string search = query.SearchText.Trim();
            tasks = tasks.Where(task => MatchesSearch(task, search, listsById, tagsById));
        }

        IOrderedEnumerable<TodoTask> ordered = query.SortMode switch
        {
            TodoSortMode.Manual => tasks.OrderBy(task => task.SortOrder).ThenBy(task => task.UpdatedAt),
            TodoSortMode.Planned => tasks.OrderBy(task => task.Schedule?.Date ?? DateOnly.MaxValue)
                .ThenBy(task => task.Schedule?.Time ?? TimeOnly.MaxValue),
            TodoSortMode.Deadline => tasks.OrderBy(task => task.DeadlineAt ?? DateTimeOffset.MaxValue),
            TodoSortMode.Priority => tasks.OrderByDescending(task => task.Priority).ThenBy(task => task.SortOrder),
            TodoSortMode.Updated => tasks.OrderByDescending(task => task.UpdatedAt),
            _ => ApplySmartOrder(tasks, query.SmartView, today)
        };

        return ordered.ThenBy(task => task.Id, StringComparer.Ordinal).ToList();
    }

    private static IOrderedEnumerable<TodoTask> ApplySmartOrder(
        IEnumerable<TodoTask> tasks,
        TodoSmartView? smartView,
        DateOnly today)
    {
        if (smartView == TodoSmartView.Today)
        {
            return tasks
                .OrderBy(task => GetTodayGroup(task, today))
                .ThenBy(task => task.TodaySortRank ?? double.MaxValue)
                .ThenBy(task => task.Schedule?.Time ?? TimeOnly.MaxValue)
                .ThenByDescending(task => task.Priority)
                .ThenBy(task => task.SortOrder);
        }

        if (smartView == TodoSmartView.Completed)
        {
            return tasks.OrderByDescending(task => task.CompletedAt ?? task.UpdatedAt);
        }

        return tasks
            .OrderBy(task => task.Status == TodoTaskStatus.Completed ? 1 : 0)
            .ThenBy(task => task.Schedule?.Date ?? DateOnly.MaxValue)
            .ThenBy(task => task.DeadlineAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(task => task.Priority)
            .ThenBy(task => task.SortOrder);
    }

    private static int GetTodayGroup(TodoTask task, DateOnly today)
    {
        if (task.Status == TodoTaskStatus.Completed)
        {
            return 4;
        }

        if (task.DeadlineAt is { } deadline && DateOnly.FromDateTime(deadline.LocalDateTime) < today)
        {
            return 0;
        }

        if (task.Schedule is { } oldSchedule && oldSchedule.Date < today)
        {
            return 1;
        }

        if (task.Schedule is { } schedule && schedule.Date == today)
        {
            return 2;
        }

        if (task.DeadlineAt is { } todayDeadline && DateOnly.FromDateTime(todayDeadline.LocalDateTime) == today)
        {
            return 3;
        }

        return task.Status == TodoTaskStatus.Completed ? 4 : 3;
    }

    private static DateOnly? GetRelevantDate(TodoTask task)
    {
        if (task.Schedule is { } schedule)
        {
            return schedule.Date;
        }

        return task.DeadlineAt is { } deadline
            ? DateOnly.FromDateTime(deadline.LocalDateTime)
            : null;
    }

    private static bool MatchesSearch(
        TodoTask task,
        string search,
        IReadOnlyDictionary<string, TodoList> lists,
        IReadOnlyDictionary<string, TodoTag> tags)
    {
        if (task.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
            (task.Notes?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            task.Steps.Any(step => step.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)) ||
            task.Attachments.Any(attachment => attachment.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            return true;
        }

        if (lists.TryGetValue(task.ListId, out TodoList? list) &&
            list.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        return task.TagIds.Any(tagId =>
            tags.TryGetValue(tagId, out TodoTag? tag) &&
            tag.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }
}

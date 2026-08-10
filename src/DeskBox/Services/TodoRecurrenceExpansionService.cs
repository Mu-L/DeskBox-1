using DeskBox.Models;

namespace DeskBox.Services;

public sealed record TodoOccurrence(
    string OccurrenceKey,
    string SeriesTaskId,
    TodoTask Task,
    DateOnly Date,
    bool IsGenerated);

public sealed class TodoRecurrenceExpansionService
{
    private const int MaxExpandedOccurrences = 10000;

    public IReadOnlyList<TodoOccurrence> Expand(
        IEnumerable<TodoTask> tasks,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        IEnumerable<TodoRecurrenceException>? exceptions = null)
    {
        if (rangeEnd < rangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd));
        }

        List<TodoTask> allTasks = tasks.ToList();
        Dictionary<string, TodoTask> tasksById = allTasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var exceptionMap = (exceptions ?? [])
            .ToDictionary(
                exception => $"{exception.SeriesId}\u001f{exception.OccurrenceKey}",
                StringComparer.Ordinal);
        var exceptionsBySeriesId = exceptionMap.Values
            .GroupBy(exception => exception.SeriesId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var exceptionTaskIds = new HashSet<string>(
            exceptionMap.Values.Where(exception => exception.TaskId is not null).Select(exception => exception.TaskId!),
            StringComparer.Ordinal);
        var result = new List<TodoOccurrence>();
        var resolvedExceptionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (TodoTask source in allTasks.Where(task => !exceptionTaskIds.Contains(task.Id)))
        {
            TodoRecurrenceRule? rule = source.RecurrenceRule;
            DateOnly? anchor = GetAnchorDate(source, rule?.Anchor ?? TodoRecurrenceAnchor.Schedule);
            if (rule is null ||
                rule.GenerationMode == TodoRecurrenceGenerationMode.AfterCompletion ||
                anchor is null)
            {
                DateOnly? taskDate = GetRelevantDate(source);
                if (taskDate is { } date && date >= rangeStart && date <= rangeEnd)
                {
                    string occurrenceKey = BuildOccurrenceKey(source.Id, date);
                    if (TryResolveException(source.Id, occurrenceKey, source, date, out TodoOccurrence? resolved))
                    {
                        if (resolved is not null)
                        {
                            result.Add(resolved);
                        }
                        continue;
                    }
                    result.Add(new TodoOccurrence(
                        occurrenceKey,
                        source.Id,
                        source,
                        date,
                        IsGenerated: false));
                }

                continue;
            }

            int emitted = 0;
            int ordinal = 0;
            for (DateOnly cursor = anchor.Value;
                 cursor <= rangeEnd && emitted < MaxExpandedOccurrences;
                 cursor = cursor.AddDays(1))
            {
                if (rule.EndDate is { } endDate && cursor > endDate)
                {
                    break;
                }

                if (!IsOccurrence(anchor.Value, cursor, rule))
                {
                    continue;
                }

                ordinal++;
                if (rule.OccurrenceCount is { } maxCount && ordinal > maxCount)
                {
                    break;
                }

                if (cursor < rangeStart)
                {
                    continue;
                }

                string occurrenceKey = BuildOccurrenceKey(source.Id, cursor);
                TodoTask occurrence = ShiftToDate(source, anchor.Value, cursor);
                if (TryResolveException(source.Id, occurrenceKey, occurrence, cursor, out TodoOccurrence? resolved))
                {
                    if (resolved is not null)
                    {
                        result.Add(resolved);
                    }
                    emitted++;
                    continue;
                }
                result.Add(new TodoOccurrence(
                    occurrenceKey,
                    source.Id,
                    occurrence,
                    cursor,
                    IsGenerated: cursor != anchor.Value));
                emitted++;
            }
        }

        List<TodoRecurrenceException> BuildExceptionChain(TodoRecurrenceException root)
        {
            var chain = new List<TodoRecurrenceException> { root };
            var visited = new HashSet<string>(StringComparer.Ordinal)
            {
                $"{root.SeriesId}\u001f{root.OccurrenceKey}"
            };
            TodoRecurrenceException current = root;
            DateOnly? rootDate = TryParseOccurrenceDate(root.OccurrenceKey);
            while (current.TaskId is { } taskId &&
                   exceptionsBySeriesId.TryGetValue(taskId, out TodoRecurrenceException[]? candidates))
            {
                TodoRecurrenceException? next = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.OccurrenceKey, root.OccurrenceKey, StringComparison.Ordinal));
                next ??= rootDate is { } date
                    ? candidates.FirstOrDefault(candidate => TryParseOccurrenceDate(candidate.OccurrenceKey) == date)
                    : null;
                if (next is null ||
                    !visited.Add($"{next.SeriesId}\u001f{next.OccurrenceKey}"))
                {
                    break;
                }

                chain.Add(next);
                current = next;
            }
            return chain;
        }

        void MarkExceptionChainResolved(IEnumerable<TodoRecurrenceException> chain)
        {
            foreach (TodoRecurrenceException item in chain)
            {
                resolvedExceptionKeys.Add($"{item.SeriesId}\u001f{item.OccurrenceKey}");
            }
        }

        bool TryResolveException(
            string seriesId,
            string occurrenceKey,
            TodoTask fallback,
            DateOnly date,
            out TodoOccurrence? occurrence)
        {
            occurrence = null;
            if (!exceptionMap.TryGetValue($"{seriesId}\u001f{occurrenceKey}", out TodoRecurrenceException? exception))
            {
                return false;
            }
            List<TodoRecurrenceException> chain = BuildExceptionChain(exception);
            MarkExceptionChainResolved(chain);
            if (chain.Any(item => item.IsCancelled))
            {
                return true;
            }
            TodoRecurrenceException effective = chain[^1];
            TodoTask task = effective.TaskId is { } taskId && tasksById.TryGetValue(taskId, out TodoTask? overrideTask)
                ? overrideTask.CloneTask()
                : fallback;
            task.PresentedOccurrenceDate = date;
            DateOnly resolvedDate = GetRelevantDate(task) ?? date;
            if (resolvedDate < rangeStart || resolvedDate > rangeEnd)
            {
                return true;
            }
            occurrence = new TodoOccurrence(occurrenceKey, seriesId, task, resolvedDate, IsGenerated: true);
            return true;
        }

        // An edited occurrence can be moved outside its original date. Its source
        // occurrence may therefore be outside the requested range, so include the
        // exception task independently when its edited date is inside the range.
        foreach (TodoRecurrenceException exception in exceptionMap.Values)
        {
            string mapKey = $"{exception.SeriesId}\u001f{exception.OccurrenceKey}";
            if (exception.IsCancelled ||
                exceptionTaskIds.Contains(exception.SeriesId) ||
                resolvedExceptionKeys.Contains(mapKey) ||
                exception.TaskId is null)
            {
                continue;
            }

            List<TodoRecurrenceException> chain = BuildExceptionChain(exception);
            MarkExceptionChainResolved(chain);
            if (chain.Any(item => item.IsCancelled) ||
                chain[^1].TaskId is not { } taskId ||
                !tasksById.TryGetValue(taskId, out TodoTask? exceptionTask) ||
                GetRelevantDate(exceptionTask) is not { } movedDate ||
                movedDate < rangeStart ||
                movedDate > rangeEnd)
            {
                continue;
            }

            TodoTask presentedException = exceptionTask.CloneTask();
            presentedException.PresentedOccurrenceDate = TryParseOccurrenceDate(exception.OccurrenceKey);
            result.Add(new TodoOccurrence(
                exception.OccurrenceKey,
                exception.SeriesId,
                presentedException,
                movedDate,
                IsGenerated: true));
        }

        return result
            .OrderBy(occurrence => occurrence.Date)
            .ThenBy(occurrence => occurrence.Task.Schedule?.Time ?? TimeOnly.MaxValue)
            .ThenBy(occurrence => occurrence.Task.SortOrder)
            .ToList();
    }

    private static bool IsOccurrence(
        DateOnly anchor,
        DateOnly candidate,
        TodoRecurrenceRule rule)
    {
        int interval = Math.Max(1, rule.Interval);
        int days = candidate.DayNumber - anchor.DayNumber;
        if (days < 0)
        {
            return false;
        }

        return rule.Frequency switch
        {
            TodoRecurrenceFrequency.Daily => days % interval == 0,
            TodoRecurrenceFrequency.Weekly => IsWeeklyOccurrence(anchor, candidate, rule, interval),
            TodoRecurrenceFrequency.Monthly => IsMonthlyOccurrence(anchor, candidate, rule, interval),
            TodoRecurrenceFrequency.Yearly => IsYearlyOccurrence(anchor, candidate, interval),
            _ => false
        };
    }

    private static bool IsWeeklyOccurrence(
        DateOnly anchor,
        DateOnly candidate,
        TodoRecurrenceRule rule,
        int interval)
    {
        DateOnly anchorWeek = StartOfWeek(anchor);
        DateOnly candidateWeek = StartOfWeek(candidate);
        int weeks = (candidateWeek.DayNumber - anchorWeek.DayNumber) / 7;
        IReadOnlyCollection<DayOfWeek> days = rule.WeekDays.Count == 0
            ? [anchor.DayOfWeek]
            : rule.WeekDays;
        return weeks >= 0 && weeks % interval == 0 && days.Contains(candidate.DayOfWeek);
    }

    private static bool IsMonthlyOccurrence(
        DateOnly anchor,
        DateOnly candidate,
        TodoRecurrenceRule rule,
        int interval)
    {
        int months = ((candidate.Year - anchor.Year) * 12) + candidate.Month - anchor.Month;
        if (months < 0 || months % interval != 0)
        {
            return false;
        }

        if (rule.MonthWeekOrdinal is { } ordinal && rule.MonthWeekDay is { } weekDay)
        {
            return candidate.DayOfWeek == weekDay &&
                   GetWeekdayOrdinal(candidate) == ordinal;
        }

        int requestedDay = rule.MonthDay ?? anchor.Day;
        int actualDay = Math.Min(requestedDay, DateTime.DaysInMonth(candidate.Year, candidate.Month));
        return candidate.Day == actualDay;
    }

    private static bool IsYearlyOccurrence(DateOnly anchor, DateOnly candidate, int interval)
    {
        int years = candidate.Year - anchor.Year;
        if (years < 0 || years % interval != 0 || candidate.Month != anchor.Month)
        {
            return false;
        }

        return candidate.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(candidate.Year, candidate.Month));
    }

    private static TodoTask ShiftToDate(TodoTask source, DateOnly anchor, DateOnly date)
    {
        TodoTask clone = source.CloneTask();
        int deltaDays = date.DayNumber - anchor.DayNumber;
        clone.Schedule = clone.Schedule is null
            ? null
            : new TodoSchedule
            {
                Date = clone.Schedule.Date.AddDays(deltaDays),
                Time = clone.Schedule.Time,
                TimeZoneId = clone.Schedule.TimeZoneId,
                DurationMinutes = clone.Schedule.DurationMinutes
            };
        clone.DeadlineAt = clone.DeadlineAt?.AddDays(deltaDays);
        clone.DueDate = clone.DeadlineAt;
        clone.PresentedOccurrenceDate = date;
        return clone;
    }

    private static DateOnly? GetAnchorDate(TodoTask task, TodoRecurrenceAnchor anchor) => anchor switch
    {
        TodoRecurrenceAnchor.Schedule => task.Schedule?.Date,
        TodoRecurrenceAnchor.Deadline when task.DeadlineAt is { } deadline =>
            DateOnly.FromDateTime(deadline.LocalDateTime),
        _ => task.Schedule?.Date ?? (task.DeadlineAt is { } fallback
            ? DateOnly.FromDateTime(fallback.LocalDateTime)
            : null)
    };

    private static DateOnly? GetRelevantDate(TodoTask task) => task.Schedule?.Date ??
        (task.DeadlineAt is { } deadline ? DateOnly.FromDateTime(deadline.LocalDateTime) : null);

    private static DateOnly StartOfWeek(DateOnly date)
    {
        DayOfWeek first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int delta = (7 + (int)date.DayOfWeek - (int)first) % 7;
        return date.AddDays(-delta);
    }

    private static int GetWeekdayOrdinal(DateOnly date) => ((date.Day - 1) / 7) + 1;

    public static string BuildOccurrenceKey(string seriesId, DateOnly date) =>
        $"{seriesId}:{date:yyyy-MM-dd}";

    public static DateOnly? TryParseOccurrenceDate(string? occurrenceKey)
    {
        if (string.IsNullOrWhiteSpace(occurrenceKey))
        {
            return null;
        }
        int separator = occurrenceKey.LastIndexOf(':');
        return separator >= 0 &&
               DateOnly.TryParseExact(
                   occurrenceKey[(separator + 1)..],
                   "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None,
                   out DateOnly date)
            ? date
            : null;
    }
}

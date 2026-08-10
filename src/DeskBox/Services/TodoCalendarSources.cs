using System.Globalization;
using System.Text;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record TodoCalendarEvent(
    string Id,
    string SourceId,
    string SourceName,
    string Title,
    DateOnly Date,
    TimeOnly? StartTime,
    int DurationMinutes,
    bool IsAllDay,
    string? Description = null,
    string? Location = null,
    string? ColorMarker = null);

public interface ITodoCalendarSource
{
    bool CanRead(TodoCalendarSourceSettings source);

    Task<IReadOnlyList<TodoCalendarEvent>> ReadAsync(
        TodoCalendarSourceSettings source,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        CancellationToken cancellationToken = default);
}

public sealed class TodoCalendarSourceService(
    SettingsService settingsService,
    IEnumerable<ITodoCalendarSource> providers)
{
    private readonly SettingsService _settingsService = settingsService;
    private readonly IReadOnlyList<ITodoCalendarSource> _providers = providers.ToList();

    public async Task<IReadOnlyList<TodoCalendarEvent>> LoadEventsAsync(
        DateOnly rangeStart,
        DateOnly rangeEnd,
        CancellationToken cancellationToken = default)
    {
        var result = new List<TodoCalendarEvent>();
        foreach (TodoCalendarSourceSettings source in _settingsService.Settings.Todo.Calendar.Sources
                     .Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.SourcePath)))
        {
            ITodoCalendarSource? provider = _providers.FirstOrDefault(candidate => candidate.CanRead(source));
            if (provider is null)
            {
                continue;
            }

            try
            {
                result.AddRange(await provider.ReadAsync(source, rangeStart, rangeEnd, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                App.Log($"[TodoCalendar] Failed to read '{source.SourcePath}': {ex.Message}");
            }
        }

        return result
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime ?? TimeOnly.MinValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Read-only RFC 5545 reader for local .ics files. It deliberately ignores
/// alarms and executable/HTML content and exposes only calendar display data.
/// </summary>
public sealed class IcsTodoCalendarSource : ITodoCalendarSource
{
    private const int MaximumOccurrencesPerEvent = 10000;

    public bool CanRead(TodoCalendarSourceSettings source) =>
        string.Equals(Path.GetExtension(source.SourcePath), ".ics", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<TodoCalendarEvent>> ReadAsync(
        TodoCalendarSourceSettings source,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        CancellationToken cancellationToken = default)
    {
        if (rangeEnd < rangeStart || !File.Exists(source.SourcePath))
        {
            return [];
        }

        string content = await File.ReadAllTextAsync(source.SourcePath, Encoding.UTF8, cancellationToken);
        IReadOnlyList<string> lines = Unfold(content);
        var templates = new List<IcsEventTemplate>();
        IcsEventTemplate? current = null;
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new IcsEventTemplate();
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current?.Start is not null)
                {
                    templates.Add(current);
                }
                current = null;
                continue;
            }
            if (current is null)
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string descriptor = line[..colon];
            string value = line[(colon + 1)..];
            string name = descriptor.Split(';')[0].ToUpperInvariant();
            switch (name)
            {
                case "UID": current.Uid = Unescape(value); break;
                case "SUMMARY": current.Summary = Unescape(value); break;
                case "DESCRIPTION": current.Description = Unescape(value); break;
                case "LOCATION": current.Location = Unescape(value); break;
                case "DTSTART": current.Start = ParseDate(descriptor, value); break;
                case "DTEND": current.End = ParseDate(descriptor, value); break;
                case "DURATION": current.DurationMinutes = ParseDurationMinutes(value); break;
                case "RRULE": current.Recurrence = ParseRecurrence(value); break;
                case "EXDATE":
                    foreach (string dateValue in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (ParseDate(descriptor, dateValue) is { } excluded)
                        {
                            current.ExcludedDates.Add(excluded.Date);
                        }
                    }
                    break;
            }
        }

        var result = new List<TodoCalendarEvent>();
        foreach (IcsEventTemplate template in templates)
        {
            Expand(template, source, rangeStart, rangeEnd, result);
        }
        return result;
    }

    private static void Expand(
        IcsEventTemplate template,
        TodoCalendarSourceSettings source,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        List<TodoCalendarEvent> output)
    {
        IcsDateValue start = template.Start!.Value;
        int duration = template.DurationMinutes ?? ResolveDuration(template);
        if (template.Recurrence is null)
        {
            AddOccurrence(start.Date, 0);
            return;
        }

        IcsRecurrence recurrence = template.Recurrence;
        int ordinal = 0;
        for (DateOnly cursor = start.Date;
             cursor <= rangeEnd && ordinal < MaximumOccurrencesPerEvent;
             cursor = cursor.AddDays(1))
        {
            if (recurrence.Until is { } until && cursor > until)
            {
                break;
            }
            if (!MatchesRecurrence(start.Date, cursor, recurrence))
            {
                continue;
            }

            ordinal++;
            if (recurrence.Count is { } count && ordinal > count)
            {
                break;
            }
            AddOccurrence(cursor, ordinal);
        }

        void AddOccurrence(DateOnly date, int ordinal)
        {
            if (date < rangeStart || date > rangeEnd || template.ExcludedDates.Contains(date))
            {
                return;
            }
            string uid = string.IsNullOrWhiteSpace(template.Uid)
                ? $"{source.Id}:{template.Summary}:{start.Date:yyyyMMdd}"
                : template.Uid;
            output.Add(new TodoCalendarEvent(
                $"{source.Id}:{uid}:{date:yyyyMMdd}:{ordinal}",
                source.Id,
                string.IsNullOrWhiteSpace(source.Name) ? Path.GetFileNameWithoutExtension(source.SourcePath) : source.Name,
                string.IsNullOrWhiteSpace(template.Summary) ? "(Untitled)" : template.Summary,
                date,
                start.IsAllDay ? null : start.Time,
                Math.Max(1, duration),
                start.IsAllDay,
                template.Description,
                template.Location,
                source.ColorMarker));
        }
    }

    private static bool MatchesRecurrence(DateOnly anchor, DateOnly date, IcsRecurrence recurrence)
    {
        int interval = Math.Max(1, recurrence.Interval);
        int days = date.DayNumber - anchor.DayNumber;
        if (days < 0)
        {
            return false;
        }
        return recurrence.Frequency switch
        {
            "DAILY" => days % interval == 0,
            "WEEKLY" => ((days / 7) % interval == 0) &&
                        (recurrence.ByDays.Count == 0
                            ? date.DayOfWeek == anchor.DayOfWeek
                            : recurrence.ByDays.Contains(date.DayOfWeek)),
            "MONTHLY" => MonthsBetween(anchor, date) is var months && months >= 0 && months % interval == 0 &&
                         (recurrence.ByDays.Count > 0
                             ? recurrence.ByDays.Contains(date.DayOfWeek) && ((date.Day - 1) / 7 == (anchor.Day - 1) / 7)
                             : date.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(date.Year, date.Month))),
            "YEARLY" => date.Year >= anchor.Year && (date.Year - anchor.Year) % interval == 0 &&
                        date.Month == anchor.Month &&
                        date.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(date.Year, date.Month)),
            _ => date == anchor
        };
    }

    private static int MonthsBetween(DateOnly start, DateOnly end) =>
        ((end.Year - start.Year) * 12) + end.Month - start.Month;

    private static int ResolveDuration(IcsEventTemplate template)
    {
        if (template.Start is not { } start || template.End is not { } end)
        {
            return template.Start?.IsAllDay == true ? 1440 : TodoWorkspaceDefaults.DefaultDurationMinutes;
        }
        if (start.IsAllDay)
        {
            return Math.Max(1, end.Date.DayNumber - start.Date.DayNumber) * 1440;
        }
        DateTime startValue = start.Date.ToDateTime(start.Time ?? TimeOnly.MinValue);
        DateTime endValue = end.Date.ToDateTime(end.Time ?? TimeOnly.MinValue);
        return Math.Max(1, (int)(endValue - startValue).TotalMinutes);
    }

    private static IcsDateValue? ParseDate(string descriptor, string value)
    {
        string trimmed = value.Trim();
        bool allDay = descriptor.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) ||
                      (trimmed.Length == 8 && !trimmed.Contains('T'));
        string[] formats = allDay
            ? ["yyyyMMdd"]
            : ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"];
        if (!DateTime.TryParseExact(
                trimmed,
                formats,
                CultureInfo.InvariantCulture,
                trimmed.EndsWith('Z') ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None,
                out DateTime parsed))
        {
            return null;
        }
        DateTime local = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
        return new IcsDateValue(
            DateOnly.FromDateTime(local),
            allDay ? null : TimeOnly.FromDateTime(local),
            allDay);
    }

    private static int? ParseDurationMinutes(string value)
    {
        if (!value.StartsWith('P'))
        {
            return null;
        }
        try
        {
            return Math.Max(1, (int)Math.Round(System.Xml.XmlConvert.ToTimeSpan(value).TotalMinutes));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IcsRecurrence? ParseRecurrence(string value)
    {
        var values = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1], StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("FREQ", out string? frequency))
        {
            return null;
        }
        int interval = values.TryGetValue("INTERVAL", out string? intervalText) && int.TryParse(intervalText, out int parsedInterval)
            ? Math.Max(1, parsedInterval)
            : 1;
        int? count = values.TryGetValue("COUNT", out string? countText) && int.TryParse(countText, out int parsedCount)
            ? Math.Max(1, parsedCount)
            : null;
        DateOnly? until = values.TryGetValue("UNTIL", out string? untilText)
            ? ParseDate("UNTIL", untilText)?.Date
            : null;
        var byDays = new HashSet<DayOfWeek>();
        if (values.TryGetValue("BYDAY", out string? byDayText))
        {
            foreach (string day in byDayText.Split(','))
            {
                string code = day.Length > 2 ? day[^2..] : day;
                if (ParseDay(code) is { } parsedDay)
                {
                    byDays.Add(parsedDay);
                }
            }
        }
        return new IcsRecurrence(frequency.ToUpperInvariant(), interval, count, until, byDays);
    }

    private static DayOfWeek? ParseDay(string code) => code.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null
    };

    private static IReadOnlyList<string> Unfold(string content)
    {
        var result = new List<string>();
        foreach (string raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && result.Count > 0)
            {
                result[^1] += line[1..];
            }
            else
            {
                result.Add(line);
            }
        }
        return result;
    }

    private static string Unescape(string value) => value
        .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",", StringComparison.Ordinal)
        .Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private sealed class IcsEventTemplate
    {
        public string Uid { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Location { get; set; }
        public IcsDateValue? Start { get; set; }
        public IcsDateValue? End { get; set; }
        public int? DurationMinutes { get; set; }
        public IcsRecurrence? Recurrence { get; set; }
        public HashSet<DateOnly> ExcludedDates { get; } = [];
    }

    private readonly record struct IcsDateValue(DateOnly Date, TimeOnly? Time, bool IsAllDay);

    private sealed record IcsRecurrence(
        string Frequency,
        int Interval,
        int? Count,
        DateOnly? Until,
        HashSet<DayOfWeek> ByDays);
}

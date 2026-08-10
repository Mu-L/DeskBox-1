using System.Globalization;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public enum TodoQuickAddTokenKind
{
    Date,
    Time,
    Tag,
    List,
    Priority
}

public sealed record TodoQuickAddToken(
    TodoQuickAddTokenKind Kind,
    string SourceText,
    string DisplayText);

public sealed record TodoQuickAddResult(
    string OriginalText,
    string Title,
    TodoSchedule? Schedule,
    TodoPriority Priority,
    string? ListName,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<TodoQuickAddToken> Tokens);

public sealed partial class TodoQuickAddParser
{
    public TodoQuickAddResult Parse(string? input, DateTimeOffset? now = null)
    {
        string original = input?.Trim() ?? string.Empty;
        if (original.Length == 0)
        {
            return new TodoQuickAddResult(original, string.Empty, null, TodoPriority.None, null, [], []);
        }

        DateTimeOffset localNow = (now ?? DateTimeOffset.Now).ToLocalTime();
        var tokens = new List<TodoQuickAddToken>();
        var consumed = new List<(int Start, int Length)>();
        var tags = new List<string>();
        string? listName = null;
        TodoPriority priority = TodoPriority.None;
        DateOnly? date = null;
        TimeOnly? time = null;

        foreach (Match match in MetadataTokenRegex().Matches(original))
        {
            string marker = match.Groups[1].Value;
            string value = match.Groups[2].Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (marker == "#")
            {
                if (!tags.Contains(value, StringComparer.CurrentCultureIgnoreCase))
                {
                    tags.Add(value);
                }

                tokens.Add(new TodoQuickAddToken(TodoQuickAddTokenKind.Tag, match.Value, $"#{value}"));
            }
            else if (marker == "@")
            {
                listName = value;
                tokens.Add(new TodoQuickAddToken(TodoQuickAddTokenKind.List, match.Value, $"@{value}"));
            }
            else
            {
                priority = ParsePriority(value);
                if (priority == TodoPriority.None)
                {
                    continue;
                }

                tokens.Add(new TodoQuickAddToken(TodoQuickAddTokenKind.Priority, match.Value, GetPriorityDisplay(priority)));
            }

            consumed.Add((match.Index, match.Length));
        }

        foreach ((Regex regex, Func<DateTimeOffset, Match, DateOnly?> resolver) in DateResolvers)
        {
            Match match = regex.Match(original);
            if (!match.Success || IsConsumed(match, consumed))
            {
                continue;
            }

            date = resolver(localNow, match);
            if (date is not null)
            {
                tokens.Add(new TodoQuickAddToken(
                    TodoQuickAddTokenKind.Date,
                    match.Value,
                    date.Value.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)));
                consumed.Add((match.Index, match.Length));
                break;
            }
        }

        foreach (Match match in TimeTokenRegex().Matches(original))
        {
            if (IsConsumed(match, consumed) || !TryParseTime(match, out TimeOnly parsed))
            {
                continue;
            }

            time = parsed;
            date ??= DateOnly.FromDateTime(localNow.LocalDateTime);
            tokens.Add(new TodoQuickAddToken(
                TodoQuickAddTokenKind.Time,
                match.Value,
                parsed.ToString("HH:mm", CultureInfo.CurrentCulture)));
            consumed.Add((match.Index, match.Length));
            break;
        }

        string title = RemoveRanges(original, consumed);
        var schedule = date is null
            ? null
            : new TodoSchedule
            {
                Date = date.Value,
                Time = time,
                TimeZoneId = TimeZoneInfo.Local.Id,
                DurationMinutes = time is null ? null : TodoWorkspaceDefaults.DefaultDurationMinutes
            };

        return new TodoQuickAddResult(original, title, schedule, priority, listName, tags, tokens);
    }

    private static readonly (Regex Regex, Func<DateTimeOffset, Match, DateOnly?> Resolver)[] DateResolvers =
    [
        (new Regex(@"(?<!\S)(今天|today)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            (now, _) => DateOnly.FromDateTime(now.LocalDateTime)),
        (new Regex(@"(?<!\S)(明天|tomorrow)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            (now, _) => DateOnly.FromDateTime(now.AddDays(1).LocalDateTime)),
        (new Regex(@"(?<!\S)(后天|day\s+after\s+tomorrow)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            (now, _) => DateOnly.FromDateTime(now.AddDays(2).LocalDateTime)),
        (new Regex(@"(?<!\S)(下周|next\s+week)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            (now, _) => StartOfWeek(DateOnly.FromDateTime(now.LocalDateTime)).AddDays(7)),
        (new Regex(@"(?<!\S)(下周[一二三四五六日天]|next\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday))(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            ResolveNamedWeekday),
        (new Regex(@"(?<!\d)(?<month>\d{1,2})[/-](?<day>\d{1,2})(?!\d)", RegexOptions.CultureInvariant),
            ResolveNumericDate)
    ];

    private static TodoPriority ParsePriority(string value) => value.Trim().ToLowerInvariant() switch
    {
        "高" or "high" or "p1" => TodoPriority.High,
        "中" or "medium" or "p2" => TodoPriority.Medium,
        "低" or "low" or "p3" => TodoPriority.Low,
        _ => TodoPriority.None
    };

    private static string GetPriorityDisplay(TodoPriority priority) => priority switch
    {
        TodoPriority.High => "!高",
        TodoPriority.Medium => "!中",
        TodoPriority.Low => "!低",
        _ => string.Empty
    };

    private static DateOnly? ResolveNamedWeekday(DateTimeOffset now, Match match)
    {
        string token = match.Value.ToLowerInvariant();
        DayOfWeek target = token.EndsWith('一') || token.Contains("monday", StringComparison.Ordinal)
            ? DayOfWeek.Monday
            : token.EndsWith('二') || token.Contains("tuesday", StringComparison.Ordinal)
                ? DayOfWeek.Tuesday
                : token.EndsWith('三') || token.Contains("wednesday", StringComparison.Ordinal)
                    ? DayOfWeek.Wednesday
                    : token.EndsWith('四') || token.Contains("thursday", StringComparison.Ordinal)
                        ? DayOfWeek.Thursday
                        : token.EndsWith('五') || token.Contains("friday", StringComparison.Ordinal)
                            ? DayOfWeek.Friday
                            : token.EndsWith('六') || token.Contains("saturday", StringComparison.Ordinal)
                                ? DayOfWeek.Saturday
                                : DayOfWeek.Sunday;
        DateOnly nextWeek = StartOfWeek(DateOnly.FromDateTime(now.LocalDateTime)).AddDays(7);
        return nextWeek.AddDays(((int)target + 6) % 7);
    }

    private static DateOnly? ResolveNumericDate(DateTimeOffset now, Match match)
    {
        if (!int.TryParse(match.Groups["month"].Value, out int month) ||
            !int.TryParse(match.Groups["day"].Value, out int day))
        {
            return null;
        }

        int year = now.Year;
        try
        {
            var value = new DateOnly(year, month, day);
            DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
            return value < today ? value.AddYears(1) : value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly StartOfWeek(DateOnly date)
    {
        int delta = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-delta);
    }

    private static bool TryParseTime(Match match, out TimeOnly time)
    {
        time = default;
        string hourText = match.Groups["hour"].Value;
        if (!int.TryParse(hourText, out int hour))
        {
            return false;
        }

        int minute = int.TryParse(match.Groups["minute"].Value, out int parsedMinute)
            ? parsedMinute
            : 0;
        string prefix = match.Groups["prefix"].Value;
        string suffix = match.Groups["suffix"].Value;
        bool isAfternoon = prefix is "下午" or "晚上" || suffix.Equals("pm", StringComparison.OrdinalIgnoreCase);
        bool isMorning = prefix is "上午" or "早上" || suffix.Equals("am", StringComparison.OrdinalIgnoreCase);
        if (isAfternoon && hour < 12)
        {
            hour += 12;
        }
        else if (isMorning && hour == 12)
        {
            hour = 0;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    private static bool IsConsumed(Match match, IEnumerable<(int Start, int Length)> consumed) =>
        consumed.Any(range => match.Index < range.Start + range.Length && range.Start < match.Index + match.Length);

    private static string RemoveRanges(string source, IEnumerable<(int Start, int Length)> ranges)
    {
        char[] chars = source.ToCharArray();
        foreach ((int start, int length) in ranges)
        {
            for (int index = start; index < Math.Min(chars.Length, start + length); index++)
            {
                chars[index] = ' ';
            }
        }

        return WhitespaceRegex().Replace(new string(chars), " ").Trim();
    }

    [GeneratedRegex(@"(?<!\S)([#@!])([^\s#@!]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTokenRegex();

    [GeneratedRegex(@"(?<!\w)(?<prefix>上午|下午|晚上|早上)?\s*(?<hour>\d{1,2})(?:(?:[:：点时])(?<minute>\d{1,2})?分?)?\s*(?<suffix>am|pm)?(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

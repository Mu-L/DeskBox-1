using System.Text;
using System.Text.RegularExpressions;

namespace DeskBox.Services;

public enum MarkdownEditCommand
{
    Bold,
    Italic,
    Strikethrough,
    Code,
    Link,
    Heading,
    List,
    Task,
    Quote,
    Table,
    Indent,
    Outdent
}

public readonly record struct MarkdownTextEdit(
    int Start,
    int Length,
    string Replacement,
    int SelectionStart,
    int SelectionLength)
{
    public string Apply(string? source)
    {
        source ??= string.Empty;
        int start = Math.Clamp(Start, 0, source.Length);
        int length = Math.Clamp(Length, 0, source.Length - start);
        return source[..start] + Replacement + source[(start + length)..];
    }
}

/// <summary>
/// Pure Markdown source transformations. The editor applies the returned
/// minimal range through TextBox.SelectedText so native undo remains intact.
/// </summary>
public static partial class MarkdownEditCommandEngine
{
    public static bool TryCreateEdit(
        string? source,
        int selectionStart,
        int selectionLength,
        MarkdownEditCommand command,
        out MarkdownTextEdit edit)
    {
        source ??= string.Empty;
        selectionStart = Math.Clamp(selectionStart, 0, source.Length);
        selectionLength = Math.Clamp(selectionLength, 0, source.Length - selectionStart);

        return command switch
        {
            MarkdownEditCommand.Bold => TryWrap(
                source, selectionStart, selectionLength, "**", "**", out edit),
            MarkdownEditCommand.Italic => TryWrap(
                source, selectionStart, selectionLength, "*", "*", out edit),
            MarkdownEditCommand.Strikethrough => TryWrap(
                source, selectionStart, selectionLength, "~~", "~~", out edit),
            MarkdownEditCommand.Code => TryCreateCodeEdit(
                source, selectionStart, selectionLength, out edit),
            MarkdownEditCommand.Link => TryCreateLinkEdit(
                source, selectionStart, selectionLength, out edit),
            MarkdownEditCommand.Heading or
            MarkdownEditCommand.List or
            MarkdownEditCommand.Task or
            MarkdownEditCommand.Quote => TryTransformLines(
                source, selectionStart, selectionLength, command, out edit),
            MarkdownEditCommand.Table => TryInsertTable(
                source, selectionStart, selectionLength, out edit),
            MarkdownEditCommand.Indent => TryIndent(
                source, selectionStart, selectionLength, out edit),
            MarkdownEditCommand.Outdent => TryOutdent(
                source, selectionStart, selectionLength, out edit),
            _ => Fail(out edit)
        };
    }

    private static bool TryWrap(
        string source,
        int start,
        int length,
        string prefix,
        string suffix,
        out MarkdownTextEdit edit)
    {
        if (length == 0)
        {
            edit = new MarkdownTextEdit(
                start,
                0,
                prefix + suffix,
                start + prefix.Length,
                0);
            return true;
        }

        string selected = source.Substring(start, length);
        if (selected.Length >= prefix.Length + suffix.Length &&
            selected.StartsWith(prefix, StringComparison.Ordinal) &&
            selected.EndsWith(suffix, StringComparison.Ordinal))
        {
            string unwrapped = selected[prefix.Length..^suffix.Length];
            edit = new MarkdownTextEdit(start, length, unwrapped, start, unwrapped.Length);
            return true;
        }

        if (start >= prefix.Length &&
            start + length + suffix.Length <= source.Length &&
            source.AsSpan(start - prefix.Length, prefix.Length).SequenceEqual(prefix) &&
            source.AsSpan(start + length, suffix.Length).SequenceEqual(suffix))
        {
            edit = new MarkdownTextEdit(
                start - prefix.Length,
                prefix.Length + length + suffix.Length,
                selected,
                start - prefix.Length,
                selected.Length);
            return true;
        }

        edit = new MarkdownTextEdit(
            start,
            length,
            prefix + selected + suffix,
            start + prefix.Length,
            selected.Length);
        return true;
    }

    private static bool TryCreateCodeEdit(
        string source,
        int start,
        int length,
        out MarkdownTextEdit edit)
    {
        if (length == 0 || !source.AsSpan(start, length).Contains('\n'))
        {
            return TryWrap(source, start, length, "`", "`", out edit);
        }

        string selected = source.Substring(start, length);
        string newline = DetectNewline(source);
        string prefix = "```" + newline;
        string suffix = newline + "```";
        edit = new MarkdownTextEdit(
            start,
            length,
            prefix + selected + suffix,
            start + prefix.Length,
            selected.Length);
        return true;
    }

    private static bool TryCreateLinkEdit(
        string source,
        int start,
        int length,
        out MarkdownTextEdit edit)
    {
        string selected = length == 0 ? string.Empty : source.Substring(start, length);
        string replacement = $"[{selected}](https://)";
        edit = new MarkdownTextEdit(
            start,
            length,
            replacement,
            length == 0 ? start + 1 : start + selected.Length + 3,
            0);
        return true;
    }

    private static bool TryInsertTable(
        string source,
        int start,
        int length,
        out MarkdownTextEdit edit)
    {
        string newline = DetectNewline(source);
        string replacement = string.Join(
            newline,
            "| Column 1 | Column 2 |",
            "| --- | --- |",
            "| Content | Content |");
        edit = new MarkdownTextEdit(start, length, replacement, start + 2, "Column 1".Length);
        return true;
    }

    private static bool TryTransformLines(
        string source,
        int selectionStart,
        int selectionLength,
        MarkdownEditCommand command,
        out MarkdownTextEdit edit)
    {
        GetSelectedLineRange(source, selectionStart, selectionLength, out int blockStart, out int blockEnd);
        string block = source[blockStart..blockEnd];
        List<LinePart> lines = SplitLines(block);
        bool includeSingleBlank = selectionLength == 0 && lines.Count == 1;
        LineKind targetKind = command switch
        {
            MarkdownEditCommand.Heading => LineKind.Heading,
            MarkdownEditCommand.List => LineKind.List,
            MarkdownEditCommand.Task => LineKind.Task,
            _ => LineKind.Quote
        };
        LineInfo[] infos = lines.Select(line => ParseLine(line.Content)).ToArray();
        LineInfo[] applicable = infos
            .Where((info, index) => includeSingleBlank || !string.IsNullOrWhiteSpace(lines[index].Content))
            .ToArray();
        bool removeTarget = applicable.Length > 0 && applicable.All(info => info.Kind == targetKind);

        var replacement = new StringBuilder(block.Length + lines.Count * 6);
        var mappings = new List<LineMapping>(lines.Count);
        int oldOffset = 0;
        int newOffset = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            LinePart line = lines[index];
            LineInfo info = infos[index];
            bool skipBlank = !includeSingleBlank && string.IsNullOrWhiteSpace(line.Content);
            LineTransform transform = skipBlank
                ? new LineTransform(line.Content, 0, 0)
                : TransformLine(info, targetKind, removeTarget);
            replacement.Append(transform.Text);
            replacement.Append(line.Newline);
            mappings.Add(new LineMapping(
                oldOffset,
                line.Content.Length,
                newOffset,
                transform.Text.Length,
                line.Newline.Length,
                transform.OldPrefixLength,
                transform.NewPrefixLength));
            oldOffset += line.Content.Length + line.Newline.Length;
            newOffset += transform.Text.Length + line.Newline.Length;
        }

        int relativeSelectionStart = selectionStart - blockStart;
        int relativeSelectionEnd = relativeSelectionStart + selectionLength;
        int mappedStart = MapBoundary(relativeSelectionStart, mappings);
        int mappedEnd = MapBoundary(relativeSelectionEnd, mappings);
        edit = new MarkdownTextEdit(
            blockStart,
            block.Length,
            replacement.ToString(),
            blockStart + mappedStart,
            Math.Max(0, mappedEnd - mappedStart));
        return true;
    }

    private static LineTransform TransformLine(LineInfo info, LineKind targetKind, bool removeTarget)
    {
        if (removeTarget)
        {
            string replacementPrefix = targetKind == LineKind.Task ? "- " : string.Empty;
            return new LineTransform(
                info.Indent + replacementPrefix + info.Content,
                info.PrefixLength,
                info.Indent.Length + replacementPrefix.Length);
        }

        string marker = targetKind switch
        {
            LineKind.Heading => "## ",
            LineKind.List => "- ",
            LineKind.Task => "- [ ] ",
            _ => "> "
        };

        if (info.Kind == targetKind)
        {
            return new LineTransform(info.Original, info.PrefixLength, info.PrefixLength);
        }

        string content = targetKind switch
        {
            LineKind.Task when info.Kind is LineKind.List or LineKind.OrderedList or LineKind.Task => info.Content,
            LineKind.List when info.Kind is LineKind.List or LineKind.OrderedList or LineKind.Task => info.Content,
            LineKind.Heading when info.Kind == LineKind.Heading => info.Content,
            _ => info.Original[info.Indent.Length..]
        };
        int oldPrefixLength = targetKind switch
        {
            LineKind.Task when info.Kind is LineKind.List or LineKind.OrderedList or LineKind.Task => info.PrefixLength,
            LineKind.List when info.Kind is LineKind.List or LineKind.OrderedList or LineKind.Task => info.PrefixLength,
            LineKind.Heading when info.Kind == LineKind.Heading => info.PrefixLength,
            _ => info.Indent.Length
        };
        return new LineTransform(
            info.Indent + marker + content,
            oldPrefixLength,
            info.Indent.Length + marker.Length);
    }

    private static bool TryIndent(
        string source,
        int selectionStart,
        int selectionLength,
        out MarkdownTextEdit edit) =>
        TryAdjustIndent(source, selectionStart, selectionLength, outdent: false, out edit);

    private static bool TryOutdent(
        string source,
        int selectionStart,
        int selectionLength,
        out MarkdownTextEdit edit) =>
        TryAdjustIndent(source, selectionStart, selectionLength, outdent: true, out edit);

    private static bool TryAdjustIndent(
        string source,
        int selectionStart,
        int selectionLength,
        bool outdent,
        out MarkdownTextEdit edit)
    {
        GetSelectedLineRange(source, selectionStart, selectionLength, out int blockStart, out int blockEnd);
        string block = source[blockStart..blockEnd];
        List<LinePart> lines = SplitLines(block);
        var replacement = new StringBuilder(block.Length + lines.Count * 4);
        var mappings = new List<LineMapping>(lines.Count);
        int oldOffset = 0;
        int newOffset = 0;
        foreach (LinePart line in lines)
        {
            int remove = outdent
                ? line.Content.StartsWith("    ", StringComparison.Ordinal) ? 4
                    : line.Content.StartsWith('\t') ? 1 : 0
                : 0;
            string transformed = outdent
                ? line.Content[remove..]
                : "    " + line.Content;
            int added = outdent ? 0 : 4;
            replacement.Append(transformed);
            replacement.Append(line.Newline);
            mappings.Add(new LineMapping(
                oldOffset,
                line.Content.Length,
                newOffset,
                transformed.Length,
                line.Newline.Length,
                remove,
                added));
            oldOffset += line.Content.Length + line.Newline.Length;
            newOffset += transformed.Length + line.Newline.Length;
        }

        int relativeStart = selectionStart - blockStart;
        int relativeEnd = relativeStart + selectionLength;
        int mappedStart = MapBoundary(relativeStart, mappings);
        int mappedEnd = MapBoundary(relativeEnd, mappings);
        edit = new MarkdownTextEdit(
            blockStart,
            block.Length,
            replacement.ToString(),
            blockStart + mappedStart,
            Math.Max(0, mappedEnd - mappedStart));
        return true;
    }

    private static int MapBoundary(int position, IReadOnlyList<LineMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            return 0;
        }

        foreach (LineMapping mapping in mappings)
        {
            int oldLineEnd = mapping.OldStart + mapping.OldContentLength;
            int oldFullEnd = oldLineEnd + mapping.NewlineLength;
            if (position > oldFullEnd)
            {
                continue;
            }

            if (position >= oldLineEnd)
            {
                return mapping.NewStart + mapping.NewContentLength + Math.Min(
                    position - oldLineEnd,
                    mapping.NewlineLength);
            }

            int column = Math.Max(0, position - mapping.OldStart);
            if (column <= mapping.OldPrefixLength)
            {
                return mapping.NewStart + mapping.NewPrefixLength;
            }

            return mapping.NewStart + Math.Min(
                mapping.NewContentLength,
                mapping.NewPrefixLength + column - mapping.OldPrefixLength);
        }

        LineMapping last = mappings[^1];
        return last.NewStart + last.NewContentLength + last.NewlineLength;
    }

    private static void GetSelectedLineRange(
        string source,
        int selectionStart,
        int selectionLength,
        out int blockStart,
        out int blockEnd)
    {
        blockStart = source.LastIndexOf('\n', Math.Max(0, selectionStart - 1));
        blockStart = blockStart < 0 ? 0 : blockStart + 1;

        int selectionEnd = selectionStart + selectionLength;
        int lookupEnd = selectionLength > 0 && selectionEnd <= source.Length &&
                        source[selectionEnd - 1] == '\n'
            ? selectionEnd - 1
            : selectionEnd;
        blockEnd = source.IndexOf('\n', lookupEnd);
        blockEnd = blockEnd < 0 ? source.Length : blockEnd;
        if (blockEnd > blockStart && source[blockEnd - 1] == '\r')
        {
            blockEnd--;
        }
    }

    private static List<LinePart> SplitLines(string block)
    {
        var lines = new List<LinePart>();
        int start = 0;
        for (int index = 0; index < block.Length; index++)
        {
            if (block[index] != '\n')
            {
                continue;
            }

            int contentEnd = index > start && block[index - 1] == '\r' ? index - 1 : index;
            lines.Add(new LinePart(
                block[start..contentEnd],
                block[contentEnd..(index + 1)]));
            start = index + 1;
        }

        lines.Add(new LinePart(block[start..], string.Empty));
        return lines;
    }

    private static LineInfo ParseLine(string line)
    {
        Match task = TaskLineRegex().Match(line);
        if (task.Success)
        {
            return FromMatch(line, task, LineKind.Task);
        }

        Match unordered = UnorderedListLineRegex().Match(line);
        if (unordered.Success)
        {
            return FromMatch(line, unordered, LineKind.List);
        }

        Match ordered = OrderedListLineRegex().Match(line);
        if (ordered.Success)
        {
            return FromMatch(line, ordered, LineKind.OrderedList);
        }

        Match heading = HeadingLineRegex().Match(line);
        if (heading.Success)
        {
            return FromMatch(line, heading, LineKind.Heading);
        }

        Match quote = QuoteLineRegex().Match(line);
        if (quote.Success)
        {
            return FromMatch(line, quote, LineKind.Quote);
        }

        string indent = LeadingWhitespaceRegex().Match(line).Value;
        return new LineInfo(line, indent, LineKind.Plain, indent.Length, line[indent.Length..]);
    }

    private static LineInfo FromMatch(string line, Match match, LineKind kind)
    {
        string indent = match.Groups["indent"].Value;
        string content = match.Groups["content"].Value;
        return new LineInfo(
            line,
            indent,
            kind,
            match.Groups["content"].Index,
            content);
    }

    private static string DetectNewline(string source) =>
        source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool Fail(out MarkdownTextEdit edit)
    {
        edit = default;
        return false;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-+*][ \t]+\[[ xX]\][ \t]+(?<content>.*)$")]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-+*][ \t]+(?<content>.*)$")]
    private static partial Regex UnorderedListLineRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)\d+[.)][ \t]+(?<content>.*)$")]
    private static partial Regex OrderedListLineRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)#{1,6}[ \t]+(?<content>.*)$")]
    private static partial Regex HeadingLineRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)>[ \t]?(?<content>.*)$")]
    private static partial Regex QuoteLineRegex();

    [GeneratedRegex(@"^[ \t]*")]
    private static partial Regex LeadingWhitespaceRegex();

    private enum LineKind
    {
        Plain,
        Heading,
        List,
        OrderedList,
        Task,
        Quote
    }

    private readonly record struct LinePart(string Content, string Newline);

    private readonly record struct LineInfo(
        string Original,
        string Indent,
        LineKind Kind,
        int PrefixLength,
        string Content);

    private readonly record struct LineTransform(
        string Text,
        int OldPrefixLength,
        int NewPrefixLength);

    private readonly record struct LineMapping(
        int OldStart,
        int OldContentLength,
        int NewStart,
        int NewContentLength,
        int NewlineLength,
        int OldPrefixLength,
        int NewPrefixLength);
}

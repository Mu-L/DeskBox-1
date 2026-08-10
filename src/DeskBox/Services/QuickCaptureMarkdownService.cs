using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DeskBox.Services;

/// <summary>
/// Host-independent Markdown operations. Raw Markdown remains the source of
/// truth; this service supplies safe text projection, task mutation, search
/// excerpts, and export helpers without taking a dependency on the UI control.
/// </summary>
public sealed partial class QuickCaptureMarkdownService
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public string ToPlainText(string? source, QuickCaptureContentFormat format)
    {
        string text = source ?? string.Empty;
        if (format == QuickCaptureContentFormat.PlainText || text.Length == 0)
        {
            return NormalizeWhitespace(text);
        }

        MarkdownDocument document = Markdown.Parse(text, s_pipeline);
        var builder = new StringBuilder(text.Length);
        AppendContainer(document, builder);
        return NormalizeWhitespace(WebUtility.HtmlDecode(builder.ToString()));
    }

    public string CreateExcerpt(
        string? source,
        QuickCaptureContentFormat format,
        int maxLength = 240)
    {
        string plainText = ToPlainText(source, format);
        if (plainText.Length <= maxLength)
        {
            return plainText;
        }

        int length = Math.Max(1, maxLength - 1);
        return plainText[..length].TrimEnd() + "…";
    }

    public string ToHtml(
        string? source,
        QuickCaptureContentFormat format,
        IEnumerable<TodoAttachment>? attachments = null,
        bool allowRemoteImages = false)
    {
        string text = source ?? string.Empty;
        if (format == QuickCaptureContentFormat.PlainText)
        {
            return $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
        }

        return Markdown.ToHtml(
            SanitizeForPreview(text, attachments, allowRemoteImages),
            s_pipeline);
    }

    /// <summary>
    /// Produces a safe Markdown projection for export and compatibility renderers. The
    /// persisted source is never changed: unsafe protocols are reduced to
    /// their visible label, remote images are hidden unless explicitly
    /// allowed, and DeskBox attachment URIs are resolved only from the note's
    /// own attachment collection.
    /// </summary>
    public string SanitizeForPreview(
        string? source,
        IEnumerable<TodoAttachment>? attachments = null,
        bool allowRemoteImages = false)
    {
        string text = source ?? string.Empty;
        if (text.Length == 0)
        {
            return text;
        }

        Dictionary<string, TodoAttachment> attachmentMap = (attachments ?? [])
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.Id))
            .GroupBy(attachment => attachment.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        MarkdownDocument document = Markdown.Parse(text, s_pipeline);
        var edits = new List<SourceEdit>();
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            string destination = link.Url ?? string.Empty;
            string label = GetInlineText(link);

            if (TryResolveAttachment(destination, attachmentMap, out string? fileUri))
            {
                if (!link.UrlSpan.IsEmpty)
                {
                    edits.Add(new SourceEdit(
                        link.UrlSpan.Start,
                        link.UrlSpan.Length,
                        fileUri!));
                }
                continue;
            }

            if (!Uri.TryCreate(destination, UriKind.RelativeOrAbsolute, out Uri? uri))
            {
                AddBlockedLinkEdit(edits, link, label, "链接已阻止");
                continue;
            }

            if (!uri.IsAbsoluteUri)
            {
                if (!destination.StartsWith('#'))
                {
                    AddBlockedLinkEdit(
                        edits,
                        link,
                        label,
                        link.IsImage ? "本地图片未导入" : "链接已阻止");
                }
                continue;
            }

            string scheme = uri.Scheme.ToLowerInvariant();
            if (link.IsImage)
            {
                if (scheme is "http" or "https")
                {
                    if (!allowRemoteImages)
                    {
                        AddBlockedLinkEdit(edits, link, label, "远程图片已阻止");
                    }
                    continue;
                }

                AddBlockedLinkEdit(edits, link, label, "图片协议已阻止");
                continue;
            }

            if (scheme is not ("http" or "https" or "mailto"))
            {
                AddBlockedLinkEdit(edits, link, label, "链接协议已阻止");
            }
        }

        return ApplySourceEdits(text, edits);
    }

    public string CreateAttachmentMarkdown(TodoAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        string label = EscapeLinkLabel(string.IsNullOrWhiteSpace(attachment.DisplayName)
            ? Path.GetFileName(attachment.FilePath)
            : attachment.DisplayName);
        string marker = string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase)
            ? "!"
            : string.Empty;
        return $"{marker}[{label}](deskbox-attachment://{attachment.Id})";
    }

    public bool TryToggleTask(string source, int taskIndex, out string updatedSource)
    {
        updatedSource = source;
        if (taskIndex < 0 || string.IsNullOrEmpty(source))
        {
            return false;
        }

        MarkdownDocument document = Markdown.Parse(source, s_pipeline);
        TaskList[] tasks = document.Descendants<TaskList>()
            .OrderBy(task => task.Span.Start)
            .ToArray();
        if (taskIndex >= tasks.Length)
        {
            return false;
        }

        SourceSpan span = tasks[taskIndex].Span;
        if (span.Length != 3 || span.Start < 0 || span.End >= source.Length ||
            source[span.Start] != '[' || source[span.End] != ']')
        {
            return false;
        }

        int stateIndex = span.Start + 1;
        char replacement = char.ToLowerInvariant(source[stateIndex]) == 'x'
            ? ' '
            : 'x';
        updatedSource = source[..stateIndex] + replacement + source[(stateIndex + 1)..];
        return true;
    }

    public string CreateDerivedTitle(
        string? explicitTitle,
        string? source,
        QuickCaptureContentFormat format)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        string plainText = ToPlainText(source, format);
        string firstLine = plainText.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 80 ? firstLine : firstLine[..79].TrimEnd() + "…";
    }

    private static void AppendContainer(ContainerBlock container, StringBuilder builder)
    {
        foreach (Block block in container)
        {
            if (block is LeafBlock leaf)
            {
                AppendInline(leaf.Inline, builder);
                if (leaf is CodeBlock codeBlock)
                {
                    AppendSeparated(builder, codeBlock.Lines.ToString());
                }
            }
            else if (block is ContainerBlock nested)
            {
                AppendContainer(nested, builder);
            }

            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.AppendLine();
            }
        }
    }

    private static void AppendInline(ContainerInline? inline, StringBuilder builder)
    {
        if (inline is null)
        {
            return;
        }

        for (Inline? current = inline.FirstChild; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.AppendLine();
                    break;
                case ContainerInline nested:
                    AppendInline(nested, builder);
                    break;
            }
        }
    }

    private static void AppendSeparated(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }

        builder.Append(value);
    }

    private static string NormalizeWhitespace(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = HorizontalWhitespaceRegex().Replace(normalized, " ");
        normalized = BlankLinesRegex().Replace(normalized, "\n");
        return normalized.Trim();
    }

    private static bool TryResolveAttachment(
        string destination,
        IReadOnlyDictionary<string, TodoAttachment> attachments,
        out string? fileUri)
    {
        fileUri = null;
        const string prefix = "deskbox-attachment://";
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string id = destination[prefix.Length..].Trim('/');
        if (!attachments.TryGetValue(id, out TodoAttachment? attachment) ||
            string.IsNullOrWhiteSpace(attachment.FilePath) ||
            !File.Exists(attachment.FilePath))
        {
            return false;
        }

        fileUri = new Uri(Path.GetFullPath(attachment.FilePath)).AbsoluteUri;
        return true;
    }

    private static string GetInlineText(ContainerInline inline)
    {
        var builder = new StringBuilder();
        AppendInline(inline, builder);
        return NormalizeWhitespace(WebUtility.HtmlDecode(builder.ToString()));
    }

    private static void AddBlockedLinkEdit(
        ICollection<SourceEdit> edits,
        LinkInline link,
        string label,
        string reason)
    {
        if (link.Span.IsEmpty)
        {
            return;
        }

        string visibleLabel = string.IsNullOrWhiteSpace(label)
            ? link.IsImage ? "图片" : "链接"
            : label;
        edits.Add(new SourceEdit(
            link.Span.Start,
            link.Span.Length,
            link.IsImage
                ? $"🖼 {visibleLabel}（{reason}）"
                : visibleLabel));
    }

    private static string ApplySourceEdits(string source, IEnumerable<SourceEdit> edits)
    {
        var builder = new StringBuilder(source);
        int previousStart = source.Length;
        foreach (SourceEdit edit in edits
                     .Distinct()
                     .OrderByDescending(edit => edit.Start))
        {
            if (edit.Start < 0 || edit.Length < 0 ||
                edit.Start + edit.Length > source.Length ||
                edit.Start + edit.Length > previousStart)
            {
                continue;
            }

            builder.Remove(edit.Start, edit.Length);
            builder.Insert(edit.Start, edit.Replacement);
            previousStart = edit.Start;
        }

        return builder.ToString();
    }

    private static string EscapeLinkLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex BlankLinesRegex();

    private sealed record SourceEdit(int Start, int Length, string Replacement);
}

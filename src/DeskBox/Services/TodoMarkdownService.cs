using Markdig;
using Markdig.Syntax;

namespace DeskBox.Services;

public sealed record TodoMarkdownDocument(
    string Source,
    MarkdownDocument Document,
    bool WasTruncated);

public sealed class TodoMarkdownService
{
    public const int MaxCharacters = 256 * 1024;

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseCitations()
        .DisableHtml()
        .Build();

    public TodoMarkdownDocument Parse(string? markdown)
    {
        string normalized = (markdown ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
        bool truncated = normalized.Length > MaxCharacters;
        if (truncated)
        {
            normalized = normalized[..MaxCharacters];
        }

        return new TodoMarkdownDocument(
            normalized,
            Markdown.Parse(normalized, _pipeline),
            truncated);
    }

    public static bool IsAllowedLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > "attachment:".Length;
        }
        const string deskBoxAttachmentPrefix = "deskbox-attachment://";
        if (value.StartsWith(deskBoxAttachmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > deskBoxAttachmentPrefix.Length;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRemoteImage(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}

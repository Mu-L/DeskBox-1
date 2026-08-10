using DeskBox.Models;
using DeskBox.Services;
using Markdig.Syntax;

namespace DeskBox.Tests;

public sealed class TodoQuickAddAndMarkdownTests
{
    [Fact]
    public void QuickAdd_ParsesChineseMetadataWithoutLosingOriginalText()
    {
        var parser = new TodoQuickAddParser();
        DateTimeOffset now = new(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(8));

        TodoQuickAddResult result = parser.Parse("明天 下午3点 交方案 #工作 @项目 !高", now);

        Assert.Equal("明天 下午3点 交方案 #工作 @项目 !高", result.OriginalText);
        Assert.Equal("交方案", result.Title);
        Assert.Equal(new DateOnly(2026, 8, 10), result.Schedule?.Date);
        Assert.Equal(new TimeOnly(15, 0), result.Schedule?.Time);
        Assert.Equal(TodoPriority.High, result.Priority);
        Assert.Equal("项目", result.ListName);
        Assert.Equal("工作", Assert.Single(result.TagNames));
        Assert.Equal(5, result.Tokens.Count);
    }

    [Fact]
    public void QuickAdd_ParsesEnglishDateAndTime()
    {
        var parser = new TodoQuickAddParser();
        TodoQuickAddResult result = parser.Parse(
            "submit report tomorrow 3pm #work",
            new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal("submit report", result.Title);
        Assert.Equal(new DateOnly(2026, 8, 10), result.Schedule?.Date);
        Assert.Equal(new TimeOnly(15, 0), result.Schedule?.Time);
    }

    [Fact]
    public void Markdown_DisablesHtmlAndRestrictsLinks()
    {
        var service = new TodoMarkdownService();
        TodoMarkdownDocument parsed = service.Parse("# title\n\n<script>alert(1)</script>\n\n- [ ] task");

        Assert.IsType<MarkdownDocument>(parsed.Document);
        Assert.False(parsed.WasTruncated);
        Assert.True(TodoMarkdownService.IsAllowedLink("https://example.com"));
        Assert.True(TodoMarkdownService.IsAllowedLink("mailto:user@example.com"));
        Assert.True(TodoMarkdownService.IsAllowedLink("deskbox-attachment://attachment-id"));
        Assert.True(TodoMarkdownService.IsAllowedLink("attachment:file-id"));
        Assert.False(TodoMarkdownService.IsAllowedLink("javascript:alert(1)"));
        Assert.False(TodoMarkdownService.IsAllowedLink("http://example.com"));
        Assert.True(TodoMarkdownService.IsRemoteImage("https://example.com/image.png"));
    }

    [Fact]
    public void Markdown_TruncatesOversizedNotes()
    {
        var service = new TodoMarkdownService();
        TodoMarkdownDocument parsed = service.Parse(new string('x', TodoMarkdownService.MaxCharacters + 10));

        Assert.True(parsed.WasTruncated);
        Assert.Equal(TodoMarkdownService.MaxCharacters, parsed.Source.Length);
    }
}

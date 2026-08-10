using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MarkdownEditCommandEngineTests
{
    [Fact]
    public void TaskAtCaret_AddsMarkerAndLeavesCollapsedCaretAfterMarker()
    {
        const string source = "今天处理";

        MarkdownTextEdit edit = Create(source, 2, 0, MarkdownEditCommand.Task);

        Assert.Equal("- [ ] 今天处理", edit.Apply(source));
        Assert.Equal(8, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void TaskOnList_ConvertsInsteadOfNestingListMarker()
    {
        const string source = "  - 已有列表";

        MarkdownTextEdit edit = Create(source, source.Length, 0, MarkdownEditCommand.Task);

        Assert.Equal("  - [ ] 已有列表", edit.Apply(source));
        Assert.Equal(source.Length + 4, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void TaskOnTask_ConvertsBackToOrdinaryList()
    {
        const string source = "- [x] 已完成";

        MarkdownTextEdit edit = Create(source, 6, 0, MarkdownEditCommand.Task);

        Assert.Equal("- 已完成", edit.Apply(source));
        Assert.Equal(2, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void TaskAcrossLines_PreservesTextSelectionAndSkipsBlankLines()
    {
        const string source = "甲\r\n\r\n乙";

        MarkdownTextEdit edit = Create(source, 0, source.Length, MarkdownEditCommand.Task);
        string updated = edit.Apply(source);

        Assert.Equal("- [ ] 甲\r\n\r\n- [ ] 乙", updated);
        Assert.Equal(6, edit.SelectionStart);
        Assert.Equal("甲\r\n\r\n- [ ] 乙".Length, edit.SelectionLength);
        Assert.Equal("甲\r\n\r\n- [ ] 乙", updated.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void BoldWithoutSelection_InsertsEmptyPairAtCaret()
    {
        const string source = "内容";

        MarkdownTextEdit edit = Create(source, 1, 0, MarkdownEditCommand.Bold);

        Assert.Equal("内****容", edit.Apply(source));
        Assert.Equal(3, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void BoldSelection_TogglesWithoutChangingSelectedText()
    {
        const string source = "中文内容";

        MarkdownTextEdit add = Create(source, 0, source.Length, MarkdownEditCommand.Bold);
        string wrapped = add.Apply(source);
        Assert.Equal("**中文内容**", wrapped);
        Assert.Equal("中文内容", wrapped.Substring(add.SelectionStart, add.SelectionLength));

        MarkdownTextEdit remove = Create(
            wrapped,
            add.SelectionStart,
            add.SelectionLength,
            MarkdownEditCommand.Bold);
        Assert.Equal(source, remove.Apply(wrapped));
        Assert.Equal(source, remove.Apply(wrapped).Substring(remove.SelectionStart, remove.SelectionLength));
    }

    [Fact]
    public void LinkWithoutSelection_PutsCaretInsideLabel()
    {
        MarkdownTextEdit edit = Create(string.Empty, 0, 0, MarkdownEditCommand.Link);

        Assert.Equal("[](https://)", edit.Apply(string.Empty));
        Assert.Equal(1, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void HeadingPreservesCrlfAndOriginalTextSelection()
    {
        const string source = "标题一\r\n标题二";

        MarkdownTextEdit edit = Create(source, 0, source.Length, MarkdownEditCommand.Heading);
        string updated = edit.Apply(source);

        Assert.Equal("## 标题一\r\n## 标题二", updated);
        Assert.Equal("标题一\r\n## 标题二", updated.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void IndentAndOutdent_RoundTripMultilineText()
    {
        const string source = "甲\n乙";
        MarkdownTextEdit indent = Create(source, 0, source.Length, MarkdownEditCommand.Indent);
        string indented = indent.Apply(source);
        Assert.Equal("    甲\n    乙", indented);

        MarkdownTextEdit outdent = Create(
            indented,
            indent.SelectionStart,
            indent.SelectionLength,
            MarkdownEditCommand.Outdent);
        Assert.Equal(source, outdent.Apply(indented));
    }

    private static MarkdownTextEdit Create(
        string source,
        int selectionStart,
        int selectionLength,
        MarkdownEditCommand command)
    {
        Assert.True(MarkdownEditCommandEngine.TryCreateEdit(
            source,
            selectionStart,
            selectionLength,
            command,
            out MarkdownTextEdit edit));
        return edit;
    }
}

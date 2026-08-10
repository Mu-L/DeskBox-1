namespace DeskBox.Tests;

public sealed class MarkdownAndSplitterContractTests
{
    [Fact]
    public void Foundation_UsesStableToolkitSplitterWithSharedTwentyPixelGutter()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", appXaml, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"Control\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"4\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"24\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("SplitterWidth = 20", File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MasterDetailLayoutPolicy.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_PreservesUndoSelectionAndViewportAcrossFormattingCommands()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));

        Assert.Contains("IsDynamicOverflowEnabled=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrepareEditorCommandViewport", code, StringComparison.Ordinal);
        Assert.Contains("RestoreEditorViewport", code, StringComparison.Ordinal);
        Assert.Contains("EditorTextBox.SelectedText = replacement", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", code, StringComparison.Ordinal);
        Assert.Contains("TryContinueMarkdownList", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_DisablesHtmlAndBlocksRemoteImagesByDefault()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/MarkdownDocumentService.cs"));
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains(".DisableHtml()", service, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(false, OnDocumentPropertyChanged)", reader, StringComparison.Ordinal);
        Assert.Contains("IsAllowedLink", reader, StringComparison.Ordinal);
        Assert.Contains("AttachmentResolver", reader, StringComparison.Ordinal);
    }
}

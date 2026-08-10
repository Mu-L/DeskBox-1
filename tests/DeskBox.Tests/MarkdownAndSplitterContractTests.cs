namespace DeskBox.Tests;

public sealed class MarkdownAndSplitterContractTests
{
    [Fact]
    public void Foundation_UsesStableToolkitSplitterWithCompactGutterAndWideHitTarget()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", appXaml, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"Control\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SplitterHoverTrack\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"2\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"24\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("SplitterWidth = 8", File.ReadAllText(TestPaths.FromRepository(
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
        Assert.Contains("MarkdownEditCommandEngine.TryCreateEdit", code, StringComparison.Ordinal);
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
        Assert.Contains("UseInternalScrollViewer", reader, StringComparison.Ordinal);
        Assert.Contains("private readonly RichTextBlock _documentText", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.Blocks.Add", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly StackPanel _documentPanel", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentedTabs_LeaveWidthCalculationToToolkitDuringResponsiveLayout()
    {
        string helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSegmentedLayoutHelper.cs"));

        Assert.Contains("EqualPanel", helper, StringComparison.Ordinal);
        Assert.Contains("item.Width = double.NaN", helper, StringComparison.Ordinal);
        Assert.Contains("item.MaxWidth = double.PositiveInfinity", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Width = itemWidth", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyEqualItemWidthsCore", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoSegmentedTabs_WaitForASafeLayoutSlotBeforeBecomingVisible()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));

        Assert.Contains("x:Name=\"TodoFilterSegmented\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("QueueTodoSegmentedRestore", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", code, StringComparison.Ordinal);
        Assert.Contains("ListHeaderArea.ActualWidth < 48", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Todo_UsesSharedResponsiveSplitterAndMarkdownDetailControls()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string layout = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.MasterDetail.cs"));
        string detail = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"));

        Assert.Contains("<toolkit:GridSplitter", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("EnsureWideDetailSelection", layout, StringComparison.Ordinal);
        Assert.Contains("ViewModel?.LayoutPreference", layout, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMetadataGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataGrid_SizeChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataColumn3", xaml, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(600)", detail, StringComparison.Ordinal);
        Assert.Contains("TryToggleTask", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_WiresSharedReaderEditorAndPersistedSplitter()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));
        string responsive = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.ResponsiveDetail.cs"));

        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"-3,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource WidgetMasterDetailSplitterStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("MasterPaneWidthMetadataKey", responsive, StringComparison.Ordinal);
        Assert.Contains("DetailAutoSaveDelayMs", responsive, StringComparison.Ordinal);
        Assert.Contains("_detailItem?.IsRecent == true", responsive, StringComparison.Ordinal);
    }
}

namespace DeskBox.Tests;

public sealed class QuickCaptureInteractionContractTests
{
    [Fact]
    public void ClipboardEntries_AreReadOnlyAcrossOpenEditAndTaskPaths()
    {
        string root = FindRepositoryRoot();
        string content = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml.cs"));
        string editing = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.Editing.cs"));

        Assert.Contains("edit = edit && !item.IsRecent", content, StringComparison.Ordinal);
        Assert.Contains("_selectedItem is not { IsRecent: false } item", editing, StringComparison.Ordinal);
        Assert.Contains(
            "{ IsRecent: false, ContentFormat: QuickCaptureContentFormat.Markdown }",
            editing,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ItemMenu_RestoresPaperChoicesAndKeepsClipboardMenuEditFree()
    {
        string interactions = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.Interactions.cs"));
        int recentBranch = interactions.IndexOf("if (item.IsRecent)", StringComparison.Ordinal);
        int normalEdit = interactions.IndexOf(
            "_localizationService.T(\"QuickCapture.Edit\")",
            recentBranch,
            StringComparison.Ordinal);
        int recentReturn = interactions.IndexOf("return flyout;", recentBranch, StringComparison.Ordinal);

        Assert.True(recentBranch >= 0 && recentReturn > recentBranch);
        Assert.True(normalEdit > recentReturn);
        Assert.Contains("CreateAppearanceFlyout([item], flyout)", interactions, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureAppearancePreset.Paper", interactions, StringComparison.Ordinal);
        Assert.Contains("OpenTextInNotepadAsync(item)", interactions, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_UsesOneAlignedSurfaceAndNoPersistentSaveLabel()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml"));

        Assert.Contains("PlaceholderText=\"标题\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("标题（可选）", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorSaveState", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadingSaveState", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FormattingToolbarHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GotFocus=\"EditorField_GotFocus\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingToolbar_UsesExplicitResponsiveOverflowWithoutObscuringTheTextStart()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml"));
        string editing = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.Editing.cs"));

        Assert.Contains("IsDynamicOverflowEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"FormattingMoreButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FlyoutPlacementMode.BottomEdgeAlignedLeft", editing, StringComparison.Ordinal);
        Assert.Contains("UpdateFormattingToolbarLayout(width)", File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void DualPane_UsesToolkitGridSplitterWithNativeInputAndPersistedWidth()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "QuickCaptureContent.xaml.cs"));
        string project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "DeskBox.csproj"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("<toolkit:GridSplitter", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResizeBehavior=\"PreviousAndNext\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResizeDirection=\"Columns\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsThumbVisible=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyboardIncrement=\"8\"", xaml, StringComparison.Ordinal);
        Assert.Contains("QuickCapturePaneSplitterStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DividerColumn\" Width=\"20\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PaneSplitterGutterWidth = 20", code, StringComparison.Ordinal);
        Assert.Contains("PaneSplitter_DoubleTapped", xaml, StringComparison.Ordinal);
        Assert.Contains("CommitPaneSplitterWidth", code, StringComparison.Ordinal);
        Assert.Contains("PersistPresentationOverrides();", code, StringComparison.Ordinal);
        Assert.Contains("MinDetailPaneWidth", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneDivider_PointerMoved", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}

namespace DeskBox.Tests;

public sealed class QuickCaptureAttachmentSurfaceContractTests
{
    [Fact]
    public void UnifiedSurface_OffersAttachmentRemovalOnlyWhileEditing()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("Click=\"DetailRemoveAttachmentButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ElementName=DetailMarkdownEditor, Path=Visibility}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("DetailRemoveAttachmentText", xaml, StringComparison.Ordinal);
        Assert.Contains("_pendingDetailAttachments.RemoveAll", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.DeleteAttachmentAsync", code, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text)", code, StringComparison.Ordinal);
    }
}

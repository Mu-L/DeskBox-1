using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class ShellThumbnailProxyContractTests
{
    [Theory]
    [InlineData("program.exe")]
    [InlineData("library.dll")]
    [InlineData("shortcut.lnk")]
    [InlineData("website.url")]
    public async Task ProviderProbe_RejectsExecutableAndShortcutTypes(
        string path)
    {
        Assert.False(
            await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(path));
    }

    [Fact]
    public void ProxyProcess_IsBoundedAndNeverLoadsShellHandlersInDeskBox()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ShellThumbnailProxy.cs"));
        string iconHelper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));

        Assert.Contains("UseShellExecute = false", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = true", source, StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow = true", source, StringComparison.Ordinal);
        Assert.Contains("ExtractionTimeout", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("MaximumPayloadBytes", source, StringComparison.Ordinal);
        Assert.Contains(
            "await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(path)",
            iconHelper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IShellItemImageFactory",
            iconHelper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProxy_RequiresARealThumbnailAndReturnsAnAlphaBitmap()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-thumbnail-proxy/src/main.rs"));

        Assert.Contains("SIIGBF_THUMBNAILONLY", source, StringComparison.Ordinal);
        Assert.Contains("IShellItemImageFactory", source, StringComparison.Ordinal);
        Assert.Contains("BITMAP_V5_HEADER_SIZE", source, StringComparison.Ordinal);
        Assert.Contains("0xFF00_0000", source, StringComparison.Ordinal);
        Assert.Contains("DeleteObject", source, StringComparison.Ordinal);
        Assert.Contains("CoUninitialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AlwaysCopiesProxyAndAddsItToStorePayload()
    {
        string project = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/DeskBox.csproj"));

        Assert.Contains(
            "<DeskBoxShellThumbnailProxy Condition=\"'$(DeskBoxShellThumbnailProxy)' == ''\">true</DeskBoxShellThumbnailProxy>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("BuildDeskBoxThumbnailProxy", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxThumbnailProxyToOutput", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxThumbnailProxyToPublish", project, StringComparison.Ordinal);
        Assert.Contains("PrepareDeskBoxStoreThumbnailProxyPayload", project, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.exe", project, StringComparison.Ordinal);
    }
}

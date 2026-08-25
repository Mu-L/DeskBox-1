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
        Assert.Contains("<TargetPath>DeskBox.ThumbnailProxy.pdb</TargetPath>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAudits_RequireProxyPayloadSymbolsAndTargetArchitecture()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string retail = Read("scripts/publish-aot-retail.ps1");
        string arm64 = Read("scripts/publish-arm64-aot-static-audit.ps1");
        string distribution = Read("scripts/build-stage-7c1-distribution.ps1");
        string store = Read("scripts/audit-store-native-aot-package.ps1");

        foreach (string script in new[] { audit, retail, arm64, distribution, store })
        {
            Assert.Contains("DeskBox.ThumbnailProxy.exe", script, StringComparison.Ordinal);
        }

        Assert.Contains("DeskBox.ThumbnailProxy.pdb", audit, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", retail, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", arm64, StringComparison.Ordinal);
        Assert.Contains("DeskBox.ThumbnailProxy.pdb", store, StringComparison.Ordinal);
        Assert.Contains("$thumbnailProxyMachine = Get-PeMachine", distribution, StringComparison.Ordinal);
        Assert.Contains("$thumbnailProxyPe = Get-PeFacts", store, StringComparison.Ordinal);
        Assert.Contains("thumbnailProxy = $thumbnailProxyPe", store, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

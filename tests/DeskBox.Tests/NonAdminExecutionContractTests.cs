namespace DeskBox.Tests;

public sealed class NonAdminExecutionContractTests
{
    [Fact]
    public void Manifest_StaysAtInvokerWithoutUiAccess()
    {
        string manifest = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/app.manifest"));

        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
        Assert.Contains("uiAccess=\"false\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_UsesLowestPrivilegesAndOriginalUserLaunch()
    {
        string installer = File.ReadAllText(TestPaths.FromRepository("installer/DeskBox.iss"));

        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("runasoriginaluser", installer, StringComparison.OrdinalIgnoreCase);
    }
}

namespace DeskBox.Tests;

public sealed class Windows10InstallerCompatibilityContractTests
{
    [Theory]
    [InlineData("installer/DeskBox.iss")]
    [InlineData("installer/DeskBox.arm64.iss")]
    public void DirectInstaller_MatchesPackagedWindows10Minimum(string scriptPath)
    {
        string installer = File.ReadAllText(TestPaths.FromRepository(scriptPath));
        string manifest = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Package.appxmanifest"));

        Assert.Contains("MinVersion=10.0.19044", installer, StringComparison.Ordinal);
        Assert.Contains("MinVersion=\"10.0.19044.0\"", manifest, StringComparison.Ordinal);
    }
}

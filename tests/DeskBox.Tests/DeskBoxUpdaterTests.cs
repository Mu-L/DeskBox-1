namespace DeskBox.Tests;

public sealed class DeskBoxUpdaterTests
{
    [Fact]
    public void InstallerArguments_ShowProgressAndLockTheExistingDirectory()
    {
        const string InstallDirectory = @"D:\Apps\DeskBox";

        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(InstallDirectory, silent: true);

        Assert.Contains($"/DIR={InstallDirectory}", arguments);
        Assert.Contains("/SILENT", arguments);
        Assert.Contains("/SP-", arguments);
        Assert.Contains("/NORESTART", arguments);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", arguments);
        Assert.DoesNotContain("/VERYSILENT", arguments);
        Assert.DoesNotContain("/SUPPRESSMSGBOXES", arguments);
    }

    [Theory]
    [InlineData(2, "cancelled")]
    [InlineData(5, "cancelled")]
    [InlineData(20, "path-mismatch")]
    [InlineData(1, "failed")]
    [InlineData(99, "failed")]
    public void IncompleteInstallerExitCode_MapsToRecoveryOutcome(int exitCode, string expected)
    {
        Assert.Equal(expected, DeskBox.Updater.Program.GetIncompleteUpdateOutcome(exitCode));
    }
}

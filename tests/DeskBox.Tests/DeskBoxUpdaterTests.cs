namespace DeskBox.Tests;

public sealed class DeskBoxUpdaterTests
{
    [Fact]
    public void InstallerArguments_ShowProgressAndLockTheExistingDirectory()
    {
        const string InstallDirectory = @"D:\Apps\DeskBox";

        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            InstallDirectory,
            silent: true,
            DeskBox.Updater.DirectInstallScope.CurrentUser);

        Assert.Contains($"/DIR={InstallDirectory}", arguments);
        Assert.Contains("/CURRENTUSER", arguments);
        Assert.DoesNotContain("/ALLUSERS", arguments);
        Assert.Contains("/SILENT", arguments);
        Assert.Contains("/SP-", arguments);
        Assert.Contains("/NORESTART", arguments);
        Assert.Contains("/FORCECLOSEAPPLICATIONS", arguments);
        Assert.DoesNotContain("/VERYSILENT", arguments);
        Assert.DoesNotContain("/SUPPRESSMSGBOXES", arguments);
    }

    [Fact]
    public void MachineWideInstallerArguments_PreserveAllUsersScope()
    {
        IReadOnlyList<string> arguments = DeskBox.Updater.Program.BuildInstallerArguments(
            @"C:\Program Files\DeskBox",
            silent: true,
            DeskBox.Updater.DirectInstallScope.AllUsers);

        Assert.Contains("/ALLUSERS", arguments);
        Assert.DoesNotContain("/CURRENTUSER", arguments);
    }

    [Fact]
    public void ProgramFilesInstallWithoutRegistration_FallsBackToAllUsersScope()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(string.IsNullOrWhiteSpace(programFiles));

        string unregisteredPath = Path.Combine(
            programFiles,
            $"DeskBox-Scope-Probe-{Guid.NewGuid():N}");

        Assert.Equal(
            DeskBox.Updater.DirectInstallScope.AllUsers,
            DeskBox.Updater.Program.ResolveInstallScope(unregisteredPath));
    }

    [Fact]
    public void PerUserInstallWithoutRegistration_DefaultsToCurrentUserScope()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(localAppData));

        string unregisteredPath = Path.Combine(
            localAppData,
            $"DeskBox-Scope-Probe-{Guid.NewGuid():N}");

        Assert.Equal(
            DeskBox.Updater.DirectInstallScope.CurrentUser,
            DeskBox.Updater.Program.ResolveInstallScope(unregisteredPath));
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

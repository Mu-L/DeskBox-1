using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DeskBoxDataPathIsolationTests
{
    [Fact]
    public void ProductionRoot_PreservesLegacyInstanceNamesAndRecoveryLocation()
    {
        var service = new DeskBoxDataPathService();

        Assert.False(service.IsDevelopmentRoot);
        Assert.Equal("DeskBox_Activate_Event_7F3A9B2E", service.ActivationEventName);
        Assert.Equal("DeskBox_SingleInstance_Mutex_7F3A9B2E", service.SingleInstanceMutexName);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(service.RootPath)!, "DeskBox-Recovery"),
            service.RecoveryDirectory);
    }

    [Fact]
    public void DevelopmentRoot_IsolatesDataRecoveryAndInstanceNames()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "DeskBox-Dev-QuickCapture139");
        string secondRoot = Path.Combine(Path.GetTempPath(), "DeskBox-Dev-Todo139");
        var first = new DeskBoxDataPathService(firstRoot);
        var same = new DeskBoxDataPathService(firstRoot.ToLowerInvariant());
        var second = new DeskBoxDataPathService(secondRoot);

        Assert.True(first.IsDevelopmentRoot);
        Assert.Equal($"{first.RootPath}-Recovery", first.RecoveryDirectory);
        Assert.StartsWith(first.RootPath, first.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.SingleInstanceMutexName, same.SingleInstanceMutexName);
        Assert.NotEqual(first.SingleInstanceMutexName, second.SingleInstanceMutexName);
        Assert.NotEqual("DeskBox_SingleInstance_Mutex_7F3A9B2E", first.SingleInstanceMutexName);
    }

    [Fact]
    public void DebugEnvironmentVariable_IsPartOfTheStartupContract()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DeskBoxDataPathService.cs"));

        Assert.Contains("DESKBOX_DEV_DATA_ROOT", source, StringComparison.Ordinal);
        Assert.Contains("#if DEBUG", source, StringComparison.Ordinal);
        Assert.Contains("ResolveDevelopmentRoot", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Services/SettingsService.cs")]
    [InlineData("src/DeskBox/Services/QuickCaptureStore.cs")]
    [InlineData("src/DeskBox/Services/TodoWidgetStore.cs")]
    [InlineData("src/DeskBox/Services/SearchHistoryService.cs")]
    [InlineData("src/DeskBox/Services/SearchIndexService.cs")]
    [InlineData("src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs")]
    [InlineData("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs")]
    public void AppOwnedStorage_UsesSharedDataRoot(string relativePath)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));

        Assert.Contains("DeskBoxDataPathService.Current", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SpecialFolder.LocalApplicationData", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugLauncher_DefaultsToAWorktreeScopedDataRoot()
    {
        string source = File.ReadAllText(TestPaths.FromRepository("scripts/start-debug.ps1"));

        Assert.Contains("DeskBox-Dev", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_DEV_DATA_ROOT", source, StringComparison.Ordinal);
        Assert.Contains("UseProductionData", source, StringComparison.Ordinal);
        Assert.Contains("ExecutablePath.StartsWith($repoRootPath", source, StringComparison.Ordinal);
    }
}

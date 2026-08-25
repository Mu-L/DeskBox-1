namespace DeskBox.Tests;

public sealed class PerformanceSettingsContractTests
{
    [Fact]
    public void GeneralPage_PlacesPerformanceBeforeStartupWithPresetAndDrillDown()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml");

        int attachment = xaml.IndexOf(
            "Settings.AttachmentStorageMode.Title",
            StringComparison.Ordinal);
        int performance = xaml.IndexOf(
            "Tag=\"PerformanceSettings\"",
            StringComparison.Ordinal);
        int autoStart = xaml.IndexOf(
            "Settings.AutoStart.Title",
            StringComparison.Ordinal);

        Assert.True(attachment >= 0);
        Assert.True(performance > attachment);
        Assert.True(autoStart > performance);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailablePerformanceModeOptions}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedPerformanceMode, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceDrillDown_ContainsAllDetailedControlsAndRouteMetadata()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml.cs");
        string navigation = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains(
            "x:Name=\"PerformanceSettingsSection\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedHiddenCacheCleanupDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnableContinuousDecorativeAnimations",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"PerformanceSettings\"] = new(\"PerformanceSettings\", \"Settings.Performance.Title\", \"General\", \"General\")",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"PerformanceSettings\"] = PerformanceSettingsSection",
            navigation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePolicy_ControlsHiddenCleanupAndOnlyContinuousDecoration()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        string shell = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetShell.xaml.cs");
        string collapse = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        string musicAdapter = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContentAdapter.cs");

        Assert.Contains(
            "PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "BackgroundMemoryCleanupDisabled",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BackgroundMemoryCleanupDelaySeconds",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinuousDecorativeAnimationsEnabled()",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "IWidgetPerformanceAwareContent",
            musicAdapter,
            StringComparison.Ordinal);

        Assert.Contains(
            "WidgetCompactTransitionVisualProfile.Resolve(",
            collapse,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PerformanceSettingsPolicy",
            collapse,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

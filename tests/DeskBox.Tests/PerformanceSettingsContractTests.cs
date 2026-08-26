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
            "SelectedVisibleIdleCacheCleanupDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedTransientWindowReleaseDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedPerformanceCacheBudget",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinuousDecorativeAnimationsSummaryText",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinuousDecorativeAnimationsDropDown_Click",
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
    public void RuntimePolicy_ControlsRetentionAndOnlyContinuousDecoration()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        string shell = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetShell.xaml.cs");
        string collapse = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        string musicAdapter = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContentAdapter.cs");
        string glance = ReadRepositoryFile(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");

        Assert.Contains(
            "PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "BackgroundMemoryCleanupDisabled",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryRunHiddenWorkingSetTrimAsync(",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScheduleTransientWindowRelease()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "clearVisibleCaches: performance.ClearVisibleIdleCaches",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConfigurePerformanceCacheBudget(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BackgroundMemoryCleanupDelaySeconds",
            app,
            StringComparison.Ordinal);
        Assert.Contains("TextMarqueeAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("VinylRotationAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("CompactAmbientAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("AllowGlanceImageAutoRotation", glance, StringComparison.Ordinal);
        Assert.Contains("UpdateRotationTimer();", glance, StringComparison.Ordinal);
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

    [Fact]
    public void PerformanceOptions_ExposeOnlyFiniteCleanupAndActivePresets()
    {
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.Performance.cs");

        Assert.DoesNotContain(
            "PerformanceSettingsPolicy.ModeBestVisual,",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PerformanceSettingsPolicy.CleanupNever,",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PerformanceSettingsPolicy.ModeBalanced,",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PerformanceSettingsPolicy.ModeResourceSaver,",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AvailableContinuousDecorativeAnimationOptions",
            viewModel,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

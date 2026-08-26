using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsMigrationPipelineTests
{
    [Fact]
    public void VersionTwo_ClearsLegacyWheelOverrideForFollowDefaultGroup()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.FollowDefault,
                    WheelSwitchEnabled = false
                },
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.Tabs,
                    WheelSwitchEnabled = false
                }
            ]
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.WidgetGroups[0].WheelSwitchEnabled);
        Assert.False(settings.WidgetGroups[1].WheelSwitchEnabled);
    }

    [Fact]
    public void VersionThree_RepairsFollowDefaultWheelOverrideCreatedAfterVersionTwo()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.FollowDefault,
                    WheelSwitchEnabled = false
                },
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.Tabs,
                    WheelSwitchEnabled = false
                }
            ]
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.WidgetGroups[0].WheelSwitchEnabled);
        Assert.False(settings.WidgetGroups[1].WheelSwitchEnabled);
        Assert.True(settings.HasResolvedInitialFileWidgetSetup);
    }

    [Fact]
    public void VersionFour_MarksExistingProfileFileWidgetSetupAsResolved()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 3,
            HasResolvedInitialFileWidgetSetup = false,
            Widgets = []
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.HasResolvedInitialFileWidgetSetup);
    }

    [Theory]
    [InlineData(50, 200)]
    [InlineData(100, 100)]
    [InlineData(200, 200)]
    public void VersionFive_MigratesOnlyLegacySearchResultDefault(
        int storedLimit,
        int expectedLimit)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 4,
            SearchMaxResults = storedLimit
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(expectedLimit, settings.SearchMaxResults);
    }

    [Fact]
    public void VersionSix_PreservesLegacyGeometryForFirstTopologyCapture()
    {
        var widget = new WidgetConfig
        {
            X = 420,
            Y = 260,
            Width = 640,
            Height = 520
        };
        var settings = new AppSettings
        {
            SchemaVersion = 5,
            Widgets = [widget]
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.NotNull(settings.WidgetTopologyLayouts);
        Assert.Empty(settings.WidgetTopologyLayouts);
        Assert.Null(settings.ActiveWidgetTopologyKey);
        Assert.Equal(420, widget.X);
        Assert.Equal(260, widget.Y);
        Assert.Equal(640, widget.Width);
        Assert.Equal(520, widget.Height);
    }

    [Fact]
    public void VersionSeven_RequiresFreshEverythingConsent()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 6,
            SearchEverythingEnabled = true,
            SearchEverythingExecutablePath = @"C:\Portable\Everything.exe",
            SearchEverythingAdvancedSyntaxEnabled = true
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.SearchEverythingEnabled);
        Assert.Equal(string.Empty, settings.SearchEverythingExecutablePath);
        Assert.False(settings.SearchEverythingAdvancedSyntaxEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VersionEight_SplitsLegacyDecorativeEffectsWithoutChangingGlance(
        bool legacyEnabled)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            EnableContinuousDecorativeAnimations = legacyEnabled
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(legacyEnabled, settings.EnableTextMarqueeAnimations);
        Assert.Equal(legacyEnabled, settings.EnableVinylRotationAnimations);
        Assert.Equal(legacyEnabled, settings.EnableCompactAmbientAnimations);
        Assert.True(settings.EnableGlanceImageAutoRotation);
    }

    [Fact]
    public void VersionEight_RetiresBestVisualAndUnboundedCleanupValues()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeBestVisual,
            HiddenCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            VisibleIdleCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds = PerformanceSettingsPolicy.CleanupNever
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));

        Assert.Equal(PerformanceSettingsPolicy.ModeBalanced, settings.PerformanceMode);
        Assert.Equal(30, settings.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.TransientWindowReleaseDelaySeconds);
    }

    [Fact]
    public void VersionEight_CustomNeverValuesBecomeLongestFiniteChoices()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            VisibleIdleCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds = PerformanceSettingsPolicy.CleanupNever
        };

        Assert.True(new SettingsMigrationPipeline().RunMigrations(settings));

        Assert.Equal(PerformanceSettingsPolicy.ModeCustom, settings.PerformanceMode);
        Assert.Equal(5 * 60, settings.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(15 * 60, settings.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.TransientWindowReleaseDelaySeconds);
    }
}

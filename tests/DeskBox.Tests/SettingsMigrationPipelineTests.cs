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
}

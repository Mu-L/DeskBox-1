using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetStartupRestorePolicyTests
{
    [Fact]
    public void SelectEnabledWidgets_IncludesPreviouslyHiddenWidgets()
    {
        var hidden = new WidgetConfig
        {
            Id = "hidden",
            IsVisible = false
        };
        var disabled = new WidgetConfig
        {
            Id = "disabled",
            IsVisible = false,
            IsDisabled = true
        };
        var deleted = new WidgetConfig
        {
            Id = "deleted",
            IsVisible = false
        };
        var settings = new AppSettings
        {
            Widgets = [hidden, disabled, deleted]
        };

        IReadOnlyList<WidgetConfig> selected =
            WidgetStartupRestorePolicy.SelectEnabledWidgets(
                settings,
                id => string.Equals(id, deleted.Id, StringComparison.Ordinal));

        Assert.Equal(hidden.Id, Assert.Single(selected).Id);
    }

    [Fact]
    public void SelectEnabledWidgets_RestoresOnlyTheActiveGroupSurface()
    {
        var first = new WidgetConfig { Id = "first", IsVisible = false };
        var second = new WidgetConfig { Id = "second", IsVisible = false };
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "group",
                    SurfaceId = "surface",
                    MemberIds = [first.Id, second.Id],
                    ActiveMemberId = second.Id,
                    IsVisible = false
                }
            ]
        };

        IReadOnlyList<WidgetConfig> selected =
            WidgetStartupRestorePolicy.SelectEnabledWidgets(settings, _ => false);

        Assert.Equal(second.Id, Assert.Single(selected).Id);
    }

    [Fact]
    public void MarkVisible_SynchronizesTheWholeGroupAndStandaloneWidget()
    {
        var first = new WidgetConfig { Id = "first", IsVisible = false };
        var second = new WidgetConfig { Id = "second", IsVisible = false };
        var standalone = new WidgetConfig { Id = "standalone", IsVisible = false };
        var group = new WidgetGroupConfig
        {
            Id = "group",
            SurfaceId = "surface",
            MemberIds = [first.Id, second.Id],
            ActiveMemberId = first.Id,
            IsVisible = false
        };
        var settings = new AppSettings
        {
            Widgets = [first, second, standalone],
            WidgetGroups = [group]
        };

        bool changed = WidgetStartupRestorePolicy.MarkVisible(
            settings,
            [first, standalone]);

        Assert.True(changed);
        Assert.True(group.IsVisible);
        Assert.True(first.IsVisible);
        Assert.True(second.IsVisible);
        Assert.True(standalone.IsVisible);
        Assert.False(WidgetStartupRestorePolicy.MarkVisible(
            settings,
            [first, standalone]));
    }
}

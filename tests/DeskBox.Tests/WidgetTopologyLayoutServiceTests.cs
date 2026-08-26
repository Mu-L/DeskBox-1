using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTopologyLayoutServiceTests
{
    [Fact]
    public void TopologyKey_IgnoresTransientDeviceAliasForTheSameStableMonitor()
    {
        WidgetDisplayTopologySnapshot first = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot reEnumerated = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY5", true, 0, 0, 1920, 1040, 1));

        Assert.Equal(first.Key, reEnumerated.Key);
    }

    [Fact]
    public void FirstActivation_CapturesExistingGeometryWithoutChangingIt()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        WidgetDisplayTopologySnapshot topology = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));

        Assert.True(new WidgetTopologyLayoutService().Activate(settings, topology));

        Assert.Equal(topology.Key, settings.ActiveWidgetTopologyKey);
        Assert.Single(settings.WidgetTopologyLayouts);
        Assert.Equal(200, widget.X);
        Assert.Equal(160, widget.Y);
        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(100, widget.PositionMarginX);
        Assert.Equal(80, widget.PositionMarginY);
    }

    [Fact]
    public void SamePhysicalMonitorAtDifferentDpi_PreservesLogicalSizeAndMargins()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);

        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(100, widget.PositionMarginX);
        Assert.Equal(80, widget.PositionMarginY);
        Assert.Equal(100, widget.X);
        Assert.Equal(80, widget.Y);
    }

    [Fact]
    public void ReturningToKnownTopology_RestoresTheLayoutEditedForThatTopology()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);
        widget.Width = 720;
        widget.Height = 620;
        widget.PositionMarginX = 44;
        widget.PositionMarginY = 36;
        widget.X = 44;
        widget.Y = 36;

        service.Activate(settings, highDpi);
        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(200, widget.X);
        Assert.Equal(160, widget.Y);

        service.Activate(settings, standardDpi);
        Assert.Equal(720, widget.Width);
        Assert.Equal(620, widget.Height);
        Assert.Equal(44, widget.X);
        Assert.Equal(36, widget.Y);
    }

    [Fact]
    public void ReplacementMonitor_SeedsProportionallyInsideItsEffectiveWorkArea()
    {
        var source = new WidgetSurfaceLayoutProfile
        {
            PositionMonitorStableId = "panel",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500
        };
        WidgetTopologyMonitorProfile oldMonitor =
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2);
        WidgetTopologyMonitorProfile replacement =
            Monitor("external", @"\\.\DISPLAY2", true, 0, 0, 1366, 728, 1);

        WidgetSurfaceLayoutProfile mapped = WidgetTopologyLayoutService.MapToTopology(
            source,
            [oldMonitor],
            [replacement]);

        Assert.Equal("external", mapped.PositionMonitorStableId);
        Assert.Equal(@"\\.\DISPLAY2", mapped.PositionMonitorDeviceName);
        Assert.InRange(mapped.Width, 426, 428);
        Assert.InRange(mapped.Height, 349, 351);
        Assert.InRange(mapped.PositionMarginX, 70, 72);
        Assert.InRange(mapped.PositionMarginY, 55, 57);
        Assert.True(mapped.X >= 0 && mapped.Y >= 0);
        Assert.True(mapped.X + mapped.Width <= replacement.WorkAreaWidth + 1);
        Assert.True(mapped.Y + mapped.Height <= replacement.WorkAreaHeight + 1);
    }

    [Fact]
    public void GroupedWidgets_PersistOneSurfaceAndProjectItToEveryMember()
    {
        WidgetConfig first = CreateWidget();
        WidgetConfig second = CreateWidget();
        second.Id = "widget-2";
        var group = new WidgetGroupConfig
        {
            Id = "group-1",
            SurfaceId = "surface-1",
            MemberIds = [first.Id, second.Id],
            ActiveMemberId = first.Id,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups = [group]
        };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        Assert.True(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey("surface-1"));
        Assert.False(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey(first.Id));
        Assert.False(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey(second.Id));

        service.Activate(settings, standardDpi);

        Assert.Equal(group.X, first.X);
        Assert.Equal(group.Y, first.Y);
        Assert.Equal(group.Width, first.Width);
        Assert.Equal(group.Height, first.Height);
        Assert.Equal(group.X, second.X);
        Assert.Equal(group.Y, second.Y);
        Assert.Equal(group.Width, second.Width);
        Assert.Equal(group.Height, second.Height);
    }

    private static WidgetConfig CreateWidget() => new()
    {
        Id = "widget-1",
        X = 200,
        Y = 160,
        Width = 600,
        Height = 500,
        BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
        PositionAnchor = WidgetPositionAnchors.LeftTop,
        PositionMarginX = 100,
        PositionMarginY = 80,
        PositionMonitorDeviceName = @"\\.\DISPLAY1",
        PositionMonitorWasPrimary = true,
        PositionMonitorKey = "0:0:3840:2080"
    };

    private static WidgetTopologyMonitorProfile Monitor(
        string stableId,
        string deviceName,
        bool primary,
        int x,
        int y,
        int width,
        int height,
        double scale) => new()
    {
        StableId = stableId,
        DeviceName = deviceName,
        IsPrimary = primary,
        MonitorX = x,
        MonitorY = y,
        MonitorWidth = width,
        MonitorHeight = height,
        WorkAreaX = x,
        WorkAreaY = y,
        WorkAreaWidth = width,
        WorkAreaHeight = height,
        DpiScale = scale
    };
}

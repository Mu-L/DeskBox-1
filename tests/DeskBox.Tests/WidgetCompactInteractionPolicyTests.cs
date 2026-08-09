using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactInteractionPolicyTests
{
    [Fact]
    public void SynchronizeForSmartEntry_ReplacesStaleRoutedPointerState()
    {
        WidgetCompactInteractionSnapshot stale = CollapsedSnapshot() with
        {
            IsCollapsed = false,
            IsPointerInside = true,
            IsExpansionZoneActive = true,
            IsPointerOverMoveHandle = true,
            IsPointerOverActions = true,
            SuppressHoverExpansion = true
        };

        WidgetCompactInteractionSnapshot synchronized =
            WidgetCompactInteractionPolicy.SynchronizeForSmartEntry(
                stale,
                isPointerPhysicallyInside: false);

        Assert.False(synchronized.IsPointerInside);
        Assert.False(synchronized.IsExpansionZoneActive);
        Assert.False(synchronized.IsPointerOverMoveHandle);
        Assert.False(synchronized.IsPointerOverActions);
        Assert.False(synchronized.SuppressHoverExpansion);
        Assert.True(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
    }

    [Fact]
    public void SynchronizeForSmartEntry_PreservesRealPointerPresence()
    {
        WidgetCompactInteractionSnapshot synchronized =
            WidgetCompactInteractionPolicy.SynchronizeForSmartEntry(
                CollapsedSnapshot() with { IsCollapsed = false },
                isPointerPhysicallyInside: true);

        Assert.True(synchronized.IsPointerInside);
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
        Assert.True(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
    }

    [Fact]
    public void CanHoverExpand_OnlyAllowsUnblockedSmartContentHover()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsPointerInside = true,
            IsExpansionZoneActive = true
        };

        Assert.True(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Click,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsExpansionZoneActive = false }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverMoveHandle = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverActions = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { SuppressHoverExpansion = true }));
    }

    [Fact]
    public void CanAutoCollapse_RequiresPointerAndInteractionToBeClear()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.True(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { InteractionDepth = 1 }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsDropInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPinned = true }));
    }

    [Fact]
    public void ShouldRetryAutoCollapse_OnlyStopsForSettledPinnedOrNonSmartWidgets()
    {
        WidgetCompactInteractionSnapshot expanded = CollapsedSnapshot() with
        {
            IsCollapsed = false,
            IsPointerInside = true,
            InteractionDepth = 1
        };

        Assert.True(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded with { IsCollapsed = true }));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded with { IsPinned = true }));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Click,
            expanded));
    }

    [Theory]
    [InlineData(true, false, false, "Glance")]
    [InlineData(false, false, false, "Peek")]
    [InlineData(false, true, false, "Pinned")]
    [InlineData(false, false, true, "Open")]
    public void ResolveViewState_MapsSmartInteractionToFourUserStates(
        bool collapsed,
        bool pinned,
        bool interacting,
        string expected)
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = collapsed,
            IsPinned = pinned,
            InteractionDepth = interacting ? 1 : 0
        };

        Assert.Equal(
            Enum.Parse<WidgetCompactViewState>(expected),
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Smart,
                snapshot));
    }

    [Fact]
    public void ResolveViewState_ClickModeUsesOpenState()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.Equal(
            WidgetCompactViewState.Open,
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Click,
                snapshot));
    }

    private static WidgetCompactInteractionSnapshot CollapsedSnapshot() => new(
        IsCollapsed: true,
        IsPinned: false,
        IsPointerInside: false,
        IsExpansionZoneActive: false,
        IsPointerOverMoveHandle: false,
        IsPointerOverActions: false,
        IsDropInside: false,
        IsBoundsInteractionActive: false,
        InteractionDepth: 0,
        IsDragging: false,
        IsResizing: false,
        HasBlockingSurface: false,
        SuppressHoverExpansion: false);
}

using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RuntimeActivityPolicyTests
{
    [Fact]
    public void MemoryCleanupPolicy_AllowsTrimOnlyWhenEveryUiSurfaceIsInactive()
    {
        Assert.True(MemoryCleanupPolicy.CanTrimWorkingSet(
            new MemoryCleanupActivitySnapshot(
                HasVisibleWidgets: false,
                IsWidgetInteractionActive: false,
                IsSettingsOpen: false,
                IsOnboardingOpen: false,
                IsSearchPopupVisible: false,
                IsDeskBoxForeground: false,
                IsPointerOverDeskBox: false)));
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, false)]
    [InlineData(false, false, false, false, true, false, false)]
    [InlineData(false, false, false, false, false, true, false)]
    [InlineData(false, false, false, false, false, false, true)]
    public void MemoryCleanupPolicy_BlocksTrimWhileAnyUiSurfaceIsActive(
        bool hasVisibleWidgets,
        bool isWidgetInteractionActive,
        bool isSettingsOpen,
        bool isOnboardingOpen,
        bool isSearchPopupVisible,
        bool isDeskBoxForeground,
        bool isPointerOverDeskBox)
    {
        Assert.False(MemoryCleanupPolicy.CanTrimWorkingSet(
            new MemoryCleanupActivitySnapshot(
                hasVisibleWidgets,
                isWidgetInteractionActive,
                isSettingsOpen,
                isOnboardingOpen,
                isSearchPopupVisible,
                isDeskBoxForeground,
                isPointerOverDeskBox)));
    }

    [Fact]
    public void MemoryCleanupPolicy_AllowsOneManagedCollectionWithVisibleIdleWidgets()
    {
        Assert.True(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            new MemoryCleanupActivitySnapshot(
                HasVisibleWidgets: true,
                IsWidgetInteractionActive: false,
                IsSettingsOpen: false,
                IsOnboardingOpen: false,
                IsSearchPopupVisible: false,
                IsDeskBoxForeground: false,
                IsPointerOverDeskBox: false),
            isSearchIndexing: false,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: 0,
            privateBytes: 0,
            allocatedSinceLastCollection: 0,
            hasCompletedVisibleIdleCollection: false));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void MemoryCleanupPolicy_BlocksVisibleIdleCollectionDuringActivity(
        bool isWidgetInteractionActive,
        bool isSettingsOpen,
        bool isOnboardingOpen,
        bool isSearchPopupVisible,
        bool isSearchIndexing)
    {
        Assert.False(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            new MemoryCleanupActivitySnapshot(
                HasVisibleWidgets: true,
                IsWidgetInteractionActive: isWidgetInteractionActive,
                IsSettingsOpen: isSettingsOpen,
                IsOnboardingOpen: isOnboardingOpen,
                IsSearchPopupVisible: isSearchPopupVisible,
                IsDeskBoxForeground: false,
                IsPointerOverDeskBox: false),
            isSearchIndexing,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            privateBytes: MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            allocatedSinceLastCollection: MemoryCleanupPolicy.VisibleIdleMinimumAllocationBytes,
            hasCompletedVisibleIdleCollection: false));
    }

    [Fact]
    public void MemoryCleanupPolicy_RequiresFreshAllocationsAfterFirstVisibleIdleCollection()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: true,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot,
            isSearchIndexing: false,
            isSearchIndexResident: false,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetTrimThresholdBytes));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot with { IsDeskBoxForeground = true },
            isSearchIndexing: false,
            isSearchIndexResident: false,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetTrimThresholdBytes));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot,
            isSearchIndexing: false,
            isSearchIndexResident: true,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetTrimThresholdBytes));

        Assert.False(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            snapshot,
            isSearchIndexing: false,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            privateBytes: MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            allocatedSinceLastCollection: MemoryCleanupPolicy.VisibleIdleMinimumAllocationBytes - 1,
            hasCompletedVisibleIdleCollection: true));
        Assert.True(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            snapshot,
            isSearchIndexing: false,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            privateBytes: MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            allocatedSinceLastCollection: MemoryCleanupPolicy.VisibleIdleMinimumAllocationBytes,
            hasCompletedVisibleIdleCollection: true));
    }

    [Fact]
    public void WidgetCompactWarmupPolicy_AllowsReadyIdleCollapsedWindow()
    {
        Assert.True(WidgetCompactWarmupPolicy.CanRun(CreateWarmupSnapshot()));
    }

    [Theory]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsCollapseInitialized))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsCollapsed))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsExpansionWarmed))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsClosing))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsAnimationActive))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsPointerOverWidget))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.HasActiveInteraction))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsWindowVisible))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsContentReady))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsApplicationIdle))]
    public void WidgetCompactWarmupPolicy_BlocksEveryUnsafeState(string propertyName)
    {
        WidgetCompactWarmupSnapshot snapshot = CreateWarmupSnapshot();
        snapshot = propertyName switch
        {
            nameof(WidgetCompactWarmupSnapshot.IsCollapseInitialized) =>
                snapshot with { IsCollapseInitialized = false },
            nameof(WidgetCompactWarmupSnapshot.IsCollapsed) =>
                snapshot with { IsCollapsed = false },
            nameof(WidgetCompactWarmupSnapshot.IsExpansionWarmed) =>
                snapshot with { IsExpansionWarmed = true },
            nameof(WidgetCompactWarmupSnapshot.IsClosing) =>
                snapshot with { IsClosing = true },
            nameof(WidgetCompactWarmupSnapshot.IsAnimationActive) =>
                snapshot with { IsAnimationActive = true },
            nameof(WidgetCompactWarmupSnapshot.IsPointerOverWidget) =>
                snapshot with { IsPointerOverWidget = true },
            nameof(WidgetCompactWarmupSnapshot.HasActiveInteraction) =>
                snapshot with { HasActiveInteraction = true },
            nameof(WidgetCompactWarmupSnapshot.IsWindowVisible) =>
                snapshot with { IsWindowVisible = false },
            nameof(WidgetCompactWarmupSnapshot.IsContentReady) =>
                snapshot with { IsContentReady = false },
            nameof(WidgetCompactWarmupSnapshot.IsApplicationIdle) =>
                snapshot with { IsApplicationIdle = false },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName))
        };

        Assert.False(WidgetCompactWarmupPolicy.CanRun(snapshot));
    }

    private static WidgetCompactWarmupSnapshot CreateWarmupSnapshot()
    {
        return new WidgetCompactWarmupSnapshot(
            IsCollapseInitialized: true,
            IsCollapsed: true,
            IsExpansionWarmed: false,
            IsClosing: false,
            IsAnimationActive: false,
            IsPointerOverWidget: false,
            HasActiveInteraction: false,
            IsWindowVisible: true,
            IsContentReady: true,
            IsApplicationIdle: true);
    }
}

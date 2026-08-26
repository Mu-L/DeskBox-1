using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RuntimeActivityPolicyTests
{
    [Fact]
    public void VisibleIdleMemoryTracker_TriggersAfterThirtySecondsAndRespectsCooldown()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(29), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(30), isEligible: true));
        tracker.CommitMaintenance(start.AddSeconds(30));
        Assert.False(tracker.Observe(start.AddSeconds(89), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(90), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_DueObservationDoesNotConsumeCooldown()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(30), isEligible: true));

        // The caller found no useful maintenance work. The next five-second
        // timer tick must be allowed to retry instead of waiting a full cooldown.
        Assert.True(tracker.Observe(start.AddSeconds(35), isEligible: true));

        tracker.CommitMaintenance(start.AddSeconds(35));
        Assert.False(tracker.Observe(start.AddSeconds(94), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(95), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_RestartsIdleWindowAfterActivity()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(20), isEligible: false));
        Assert.False(tracker.Observe(start.AddSeconds(21), isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(50), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(51), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_ReconfigureRestartsTheIdleWindow()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        tracker.Configure(
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10));
        Assert.False(tracker.Observe(start.AddMinutes(10), isEligible: true));
        Assert.False(tracker.Observe(
            start.AddMinutes(19).AddSeconds(59),
            isEligible: true));
        Assert.True(tracker.Observe(start.AddMinutes(20), isEligible: true));
    }

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
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: 0,
            privateBytes: 0,
            allocatedSinceLastCollection: 0,
            hasCompletedVisibleIdleCollection: false));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void MemoryCleanupPolicy_BlocksVisibleIdleCollectionDuringActivity(
        bool isWidgetInteractionActive,
        bool isSettingsOpen,
        bool isOnboardingOpen,
        bool isSearchPopupVisible)
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

        Assert.False(MemoryCleanupPolicy.CanTrimWorkingSet(snapshot));
        Assert.False(MemoryCleanupPolicy.CanTrimWorkingSet(
            snapshot with { IsDeskBoxForeground = true }));

        Assert.False(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            snapshot,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            privateBytes: MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            allocatedSinceLastCollection: MemoryCleanupPolicy.VisibleIdleMinimumAllocationBytes - 1,
            hasCompletedVisibleIdleCollection: true));
        Assert.True(MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
            snapshot,
            managedHeapBytes: MemoryCleanupPolicy.VisibleIdleManagedHeapThresholdBytes,
            workingSetBytes: MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            privateBytes: MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            allocatedSinceLastCollection: MemoryCleanupPolicy.VisibleIdleMinimumAllocationBytes,
            hasCompletedVisibleIdleCollection: true));
    }

    [Fact]
    public void VisibleIdleWorkingSetTrim_RequiresBothThresholdsAndNoVisualWork()
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
            MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            hasActiveVisualWork: false));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot,
            MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes - 1,
            MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            hasActiveVisualWork: false));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot,
            MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold - 1,
            hasActiveVisualWork: false));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot,
            MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            hasActiveVisualWork: true));
        Assert.False(MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
            snapshot with { IsPointerOverDeskBox = true },
            MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes,
            MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold,
            hasActiveVisualWork: false));
    }

    [Fact]
    public void WidgetCompactWarmupPolicy_AllowsReadyIdleCollapsedWindow()
    {
        Assert.True(WidgetCompactWarmupPolicy.CanRun(CreateWarmupSnapshot()));
    }

    [Fact]
    public void MemoryCleanupPolicy_RequiresVisibleInactiveUiForThirtySecondTracker()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: true,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.IsVisibleIdleCandidate(snapshot));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { IsPointerOverDeskBox = true }));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { IsDeskBoxForeground = true }));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { HasVisibleWidgets = false }));
    }

    [Fact]
    public void MemoryCleanupPolicy_TrimsHiddenWorkingSetOnlyAboveThreshold()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: false,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.ShouldTrimHiddenIdleWorkingSet(
            snapshot,
            MemoryCleanupPolicy.HiddenIdleWorkingSetTrimThresholdBytes));
        Assert.False(MemoryCleanupPolicy.ShouldTrimHiddenIdleWorkingSet(
            snapshot,
            MemoryCleanupPolicy.HiddenIdleWorkingSetTrimThresholdBytes - 1));
    }

    [Fact]
    public void ResourceSaverWorkingSetTrim_RequiresInactiveUiAndHighUsageOrPressure()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: false,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.ShouldTrimResourceSaverHiddenWorkingSet(
            snapshot,
            MemoryCleanupPolicy.ResourceSaverWorkingSetTrimHighBytes,
            memoryLoadBytes: 0,
            highMemoryLoadThresholdBytes: 1_000));
        Assert.True(MemoryCleanupPolicy.ShouldTrimResourceSaverHiddenWorkingSet(
            snapshot,
            MemoryCleanupPolicy.ResourceSaverWorkingSetTrimMinimumBytes,
            memoryLoadBytes: 850,
            highMemoryLoadThresholdBytes: 1_000));
        Assert.False(MemoryCleanupPolicy.ShouldTrimResourceSaverHiddenWorkingSet(
            snapshot,
            MemoryCleanupPolicy.ResourceSaverWorkingSetTrimMinimumBytes,
            memoryLoadBytes: 849,
            highMemoryLoadThresholdBytes: 1_000));
        Assert.False(MemoryCleanupPolicy.ShouldTrimResourceSaverHiddenWorkingSet(
            snapshot with { HasVisibleWidgets = true },
            MemoryCleanupPolicy.ResourceSaverWorkingSetTrimHighBytes,
            memoryLoadBytes: 1_000,
            highMemoryLoadThresholdBytes: 1_000));
    }

    [Theory]
    [InlineData(true, 4, 4, true)]
    [InlineData(false, 4, 4, false)]
    [InlineData(true, 3, 4, false)]
    [InlineData(true, -1, 0, false)]
    public void WidgetCompactWarmupPolicy_RejectsReadinessFromAnOlderMemoryEpoch(
        bool isWarmed,
        long warmedEpoch,
        long memoryCleanupEpoch,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactWarmupPolicy.IsExpansionReady(
                isWarmed,
                warmedEpoch,
                memoryCleanupEpoch));
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

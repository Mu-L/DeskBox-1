namespace DeskBox.Services;

internal readonly record struct MemoryCleanupActivitySnapshot(
    bool HasVisibleWidgets,
    bool IsWidgetInteractionActive,
    bool IsSettingsOpen,
    bool IsOnboardingOpen,
    bool IsSearchPopupVisible,
    bool IsDeskBoxForeground,
    bool IsPointerOverDeskBox);

internal static class MemoryCleanupPolicy
{
    internal const long VisibleIdleManagedHeapThresholdBytes = 96L * 1024 * 1024;
    internal const long VisibleIdleWorkingSetThresholdBytes = 300L * 1024 * 1024;
    internal const long VisibleIdlePrivateBytesThreshold = 320L * 1024 * 1024;
    internal const long VisibleIdleMinimumAllocationBytes = 32L * 1024 * 1024;
    internal const long HiddenIdleWorkingSetTrimThresholdBytes = 220L * 1024 * 1024;

    public static bool IsVisibleIdleCandidate(MemoryCleanupActivitySnapshot snapshot)
    {
        return snapshot.HasVisibleWidgets &&
            !snapshot.IsWidgetInteractionActive &&
            !snapshot.IsSettingsOpen &&
            !snapshot.IsOnboardingOpen &&
            !snapshot.IsSearchPopupVisible &&
            !snapshot.IsDeskBoxForeground &&
            !snapshot.IsPointerOverDeskBox;
    }

    public static bool CanTrimWorkingSet(MemoryCleanupActivitySnapshot snapshot)
    {
        return !snapshot.HasVisibleWidgets &&
            !snapshot.IsWidgetInteractionActive &&
            !snapshot.IsSettingsOpen &&
            !snapshot.IsOnboardingOpen &&
            !snapshot.IsSearchPopupVisible &&
            !snapshot.IsDeskBoxForeground &&
            !snapshot.IsPointerOverDeskBox;
    }

    public static bool ShouldTrimHiddenIdleWorkingSet(
        MemoryCleanupActivitySnapshot snapshot,
        long workingSetBytes)
    {
        return CanTrimWorkingSet(snapshot) &&
            workingSetBytes >= HiddenIdleWorkingSetTrimThresholdBytes;
    }

    public static bool ShouldCollectVisibleIdleManagedMemory(
        MemoryCleanupActivitySnapshot snapshot,
        long managedHeapBytes,
        long workingSetBytes,
        long privateBytes,
        long allocatedSinceLastCollection,
        bool hasCompletedVisibleIdleCollection)
    {
        if (snapshot.IsWidgetInteractionActive ||
            snapshot.IsSettingsOpen ||
            snapshot.IsOnboardingOpen ||
            snapshot.IsSearchPopupVisible ||
            snapshot.IsDeskBoxForeground ||
            snapshot.IsPointerOverDeskBox)
        {
            return false;
        }

        bool aboveMemoryThreshold =
            managedHeapBytes >= VisibleIdleManagedHeapThresholdBytes ||
            workingSetBytes >= VisibleIdleWorkingSetThresholdBytes ||
            privateBytes >= VisibleIdlePrivateBytesThreshold;
        if (!aboveMemoryThreshold)
        {
            return false;
        }

        // Always allow one delayed post-startup collection. Later checks require
        // meaningful allocation growth so a native WinUI high-water mark cannot
        // cause a forced Gen2 collection every few minutes.
        return !hasCompletedVisibleIdleCollection ||
            allocatedSinceLastCollection >= VisibleIdleMinimumAllocationBytes;
    }
}

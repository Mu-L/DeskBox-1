namespace DeskBox.Services;

internal readonly record struct WidgetCompactWarmupSnapshot(
    bool IsCollapseInitialized,
    bool IsCollapsed,
    bool IsExpansionWarmed,
    bool IsClosing,
    bool IsAnimationActive,
    bool IsPointerOverWidget,
    bool HasActiveInteraction,
    bool IsWindowVisible,
    bool IsContentReady,
    bool IsApplicationIdle);

internal static class WidgetCompactWarmupPolicy
{
    public static bool CanRun(WidgetCompactWarmupSnapshot snapshot)
    {
        return snapshot.IsCollapseInitialized &&
            snapshot.IsCollapsed &&
            !snapshot.IsExpansionWarmed &&
            !snapshot.IsClosing &&
            !snapshot.IsAnimationActive &&
            !snapshot.IsPointerOverWidget &&
            !snapshot.HasActiveInteraction &&
            snapshot.IsWindowVisible &&
            snapshot.IsContentReady &&
            snapshot.IsApplicationIdle;
    }
}

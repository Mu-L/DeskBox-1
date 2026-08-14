namespace DeskBox.Models;

public readonly record struct WidgetGroupPositionRailSlot(
    int MemberIndex,
    bool IsActive);

/// <summary>
/// Pure decision rules shared by mouse, touch, pen, precision touchpad and
/// keyboard navigation. Keeping these rules free of XAML makes the gesture
/// boundary and keyboard wrap behavior directly testable.
/// </summary>
public static class WidgetGroupNavigationInteractionPolicy
{
    public const double DirectionLockDistance = 7;
    public const double GestureCommitDistance = 56;
    public const double GestureCommitVelocity = 520;
    public const double WheelStep = 120;
    public static readonly TimeSpan WheelRepeatCoalescingInterval =
        TimeSpan.FromMilliseconds(120);

    public static string ResolveEffectiveStyle(
        string? requestedStyle,
        int memberCount,
        double availableWidth)
    {
        string requested = WidgetGroupNavigationStyles.Normalize(
            requestedStyle,
            allowFollowDefault: false);
        if (requested != WidgetGroupNavigationStyles.Auto)
        {
            return requested;
        }

        return memberCount <= 3 && availableWidth >= 240
            ? WidgetGroupNavigationStyles.Tabs
            : WidgetGroupNavigationStyles.Stack;
    }

    public static bool ShouldLockVertical(double deltaX, double deltaY)
    {
        return Math.Abs(deltaY) >= DirectionLockDistance &&
               Math.Abs(deltaY) > Math.Abs(deltaX) * 1.2;
    }

    public static double ApplyEdgeDamping(
        double deltaY,
        int activeIndex,
        int memberCount)
    {
        bool beyondStart = activeIndex == 0 && deltaY > 0;
        bool beyondEnd = activeIndex == memberCount - 1 && deltaY < 0;
        return beyondStart || beyondEnd ? deltaY * 0.35 : deltaY;
    }

    public static bool ShouldCommitGesture(
        bool cancelled,
        bool directionLocked,
        double deltaY,
        TimeSpan elapsed)
    {
        double seconds = Math.Max(0.001, elapsed.TotalSeconds);
        double velocity = deltaY / seconds;
        return !cancelled &&
               directionLocked &&
               (Math.Abs(deltaY) >= GestureCommitDistance ||
                Math.Abs(velocity) >= GestureCommitVelocity);
    }

    public static bool TryResolveRelativeTarget(
        int activeIndex,
        int memberCount,
        int delta,
        out int targetIndex,
        bool wrap = false)
    {
        targetIndex = -1;
        if (delta == 0 ||
            memberCount <= 0 ||
            activeIndex < 0 ||
            activeIndex >= memberCount)
        {
            return false;
        }

        targetIndex = activeIndex + Math.Sign(delta);
        if (targetIndex >= 0 && targetIndex < memberCount)
        {
            return true;
        }

        if (!wrap)
        {
            return false;
        }

        targetIndex = targetIndex < 0 ? memberCount - 1 : 0;
        return true;
    }

    public static bool TryConsumeWheelStep(
        ref double accumulator,
        double wheelDelta,
        out int direction)
    {
        // Precision touchpads can emit a small counter-delta when inertia
        // settles or the user reverses direction. Start a fresh gesture when
        // the sign changes so stale input cannot swallow the new intent.
        if (accumulator != 0 &&
            Math.Sign(accumulator) != Math.Sign(wheelDelta))
        {
            accumulator = 0;
        }

        accumulator += wheelDelta;
        if (Math.Abs(accumulator) < WheelStep)
        {
            direction = 0;
            return false;
        }

        direction = accumulator < 0 ? 1 : -1;
        accumulator = 0;
        return true;
    }

    /// <summary>
    /// Coalesces duplicate wheel impulses reported by some mouse wheels for a
    /// single detent. Rejected impulses deliberately do not refresh the
    /// accepted timestamp, so a sustained scroll remains responsive instead
    /// of extending a sliding cooldown indefinitely.
    /// </summary>
    public static bool TryAcceptCoalescedWheelStep(
        ref DateTimeOffset lastAcceptedAt,
        ref int lastAcceptedDirection,
        DateTimeOffset observedAt,
        int direction)
    {
        if (direction is not (-1 or 1))
        {
            return false;
        }

        TimeSpan sinceAccepted = observedAt - lastAcceptedAt;
        bool accept = lastAcceptedAt == default ||
                      direction != lastAcceptedDirection ||
                      sinceAccepted < TimeSpan.Zero ||
                      sinceAccepted >= WheelRepeatCoalescingInterval;
        if (!accept)
        {
            return false;
        }

        lastAcceptedAt = observedAt;
        lastAcceptedDirection = direction;
        return true;
    }

    /// <summary>
    /// Resolves the compact title-bar position rail. Two- and three-member
    /// groups map one-to-one; larger groups expose a rolling three-slot window
    /// so the active member is at the leading edge, center or trailing edge.
    /// </summary>
    public static IReadOnlyList<WidgetGroupPositionRailSlot>
        ResolvePositionRailSlots(int activeIndex, int memberCount)
    {
        if (memberCount < 2)
        {
            return Array.Empty<WidgetGroupPositionRailSlot>();
        }

        int resolvedActiveIndex = Math.Clamp(
            activeIndex,
            0,
            memberCount - 1);
        int visibleCount = Math.Min(3, memberCount);
        int startIndex = memberCount <= visibleCount
            ? 0
            : Math.Clamp(
                resolvedActiveIndex - 1,
                0,
                memberCount - visibleCount);
        var slots = new WidgetGroupPositionRailSlot[visibleCount];
        for (int slotIndex = 0; slotIndex < visibleCount; slotIndex++)
        {
            int memberIndex = startIndex + slotIndex;
            slots[slotIndex] = new WidgetGroupPositionRailSlot(
                memberIndex,
                memberIndex == resolvedActiveIndex);
        }

        return slots;
    }
}

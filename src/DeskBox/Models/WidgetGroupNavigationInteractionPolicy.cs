namespace DeskBox.Models;

/// <summary>
/// Pure decision rules shared by mouse, touch, pen, precision touchpad and
/// keyboard navigation. Keeping these rules free of XAML makes the gesture
/// boundary and no-wrap behavior directly testable.
/// </summary>
public static class WidgetGroupNavigationInteractionPolicy
{
    public const double DirectionLockDistance = 7;
    public const double GestureCommitDistance = 56;
    public const double GestureCommitVelocity = 520;
    public const double WheelStep = 120;

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
        out int targetIndex)
    {
        targetIndex = activeIndex + Math.Sign(delta);
        return delta != 0 &&
               activeIndex >= 0 &&
               targetIndex >= 0 &&
               targetIndex < memberCount;
    }

    public static bool TryConsumeWheelStep(
        ref double accumulator,
        double wheelDelta,
        out int direction)
    {
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
}

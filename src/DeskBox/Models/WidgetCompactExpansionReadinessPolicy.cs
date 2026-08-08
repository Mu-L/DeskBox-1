namespace DeskBox.Models;

public enum WidgetCompactExpansionReadinessDecision
{
    ExpandNow,
    WaitForWarmup,
    ExpandWithLiveLayoutFallback
}

/// <summary>
/// Readiness may improve first-frame smoothness, but can never become a hard
/// functional gate. Every deferred request therefore has a fixed deadline.
/// </summary>
public static class WidgetCompactExpansionReadinessPolicy
{
    public const int DefaultDeadlineMilliseconds = 96;

    public static WidgetCompactExpansionReadinessDecision Decide(
        bool isReady,
        bool deadlineElapsed)
    {
        if (isReady)
        {
            return WidgetCompactExpansionReadinessDecision.ExpandNow;
        }

        return deadlineElapsed
            ? WidgetCompactExpansionReadinessDecision.ExpandWithLiveLayoutFallback
            : WidgetCompactExpansionReadinessDecision.WaitForWarmup;
    }
}

namespace DeskBox.Services;

internal readonly record struct TrayToggleDecisionContext(
    bool IsRaisedSession,
    bool HasVisibleWidgets,
    bool IsForegroundLocal);

internal static class TrayToggleDecisionPolicy
{
    public static bool ShouldHide(TrayToggleDecisionContext context)
    {
        if (context.IsRaisedSession)
        {
            return true;
        }

        if (!context.HasVisibleWidgets)
        {
            return false;
        }

        return context.IsForegroundLocal;
    }
}

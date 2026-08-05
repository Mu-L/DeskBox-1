namespace DeskBox.Services;

/// <summary>
/// Converts native lifecycle messages into stable recovery reasons. Keeping
/// this mapping independent from the Win32 window subclass makes the sleep,
/// display/DPI, session, and Explorer-restart recovery contract testable.
/// </summary>
internal static class AppLifecycleRecoverySignalClassifier
{
    internal const uint WmPowerBroadcast = 0x0218;
    internal const uint WmWtsSessionChange = 0x02B1;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmDpiChanged = 0x02E0;

    private const uint PbtResumeAutomatic = 0x0012;
    private const uint PbtResumeSuspend = 0x0007;
    private const uint PbtResumeCritical = 0x0006;
    private const uint WtsSessionUnlock = 0x0008;
    private const uint WtsSessionLogon = 0x0005;
    private const uint WtsSessionRemoteConnect = 0x0009;

    internal static string? ResolveRecoveryReason(
        uint message,
        UIntPtr wParam,
        uint taskbarCreatedMessage)
    {
        uint eventValue = unchecked((uint)wParam.ToUInt64());
        if (message == WmPowerBroadcast &&
            eventValue is PbtResumeAutomatic or PbtResumeSuspend or PbtResumeCritical)
        {
            return "resume";
        }

        if (message == WmWtsSessionChange &&
            eventValue is WtsSessionUnlock or WtsSessionLogon or WtsSessionRemoteConnect)
        {
            return eventValue == WtsSessionUnlock
                ? "session-unlock"
                : "session-reconnect";
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            return "display-message";
        }

        return message == taskbarCreatedMessage
            ? "explorer-restart"
            : null;
    }
}

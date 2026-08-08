namespace DeskBox.Helpers;

/// <summary>
/// Chooses OLE drag feedback separately from the completion effect. DeskBox
/// imports native file drops asynchronously and owns the actual move/copy, so
/// the source must never be told to perform move cleanup after Drop returns.
/// </summary>
internal static class NativeDropEffectPolicy
{
    internal const uint None = 0;
    internal const uint Copy = 1;
    internal const uint Move = 2;
    private const uint ControlKeyState = 0x0008;

    public static uint ResolveFeedbackEffect(
        bool hasFileData,
        bool hasVirtualFileData,
        uint keyState,
        uint allowedEffects)
    {
        if (!hasFileData)
        {
            return None;
        }

        if (hasVirtualFileData || (keyState & ControlKeyState) != 0)
        {
            return (allowedEffects & Copy) != 0 ? Copy : None;
        }

        if ((allowedEffects & Move) != 0)
        {
            return Move;
        }

        return (allowedEffects & Copy) != 0 ? Copy : None;
    }

    public static uint ResolveCompletionEffect(
        bool hasExtractedPaths,
        uint allowedEffects)
    {
        if (!hasExtractedPaths)
        {
            return None;
        }

        // Returning MOVE would authorize the drag source (notably Explorer)
        // to delete its source after this callback returns. DeskBox has only
        // queued its own asynchronous transfer at that point, producing a
        // check-then-disappear race. Report COPY so source cleanup stays off.
        return (allowedEffects & Copy) != 0 ? Copy : None;
    }
}

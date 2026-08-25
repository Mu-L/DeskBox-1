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
    internal const uint Link = 4;
    private const uint ControlKeyState = 0x0008;
    private const uint ShiftKeyState = 0x0004;

    public static uint ResolveFeedbackEffect(
        bool hasFileData,
        bool hasVirtualFileData,
        uint keyState,
        uint allowedEffects,
        bool hasShellApplicationData = false,
        bool defaultMove = true)
    {
        if (hasShellApplicationData)
        {
            if ((allowedEffects & Link) != 0)
            {
                return Link;
            }

            // Some third-party Start replacements expose application objects
            // as copy-only payloads. DeskBox still creates a shortcut and never
            // authorizes source cleanup.
            return (allowedEffects & Copy) != 0 ? Copy : None;
        }

        if (!hasFileData)
        {
            return None;
        }

        FileDropIntent intent = FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: true,
            forceCopy: hasVirtualFileData,
            controlDown: (keyState & ControlKeyState) != 0,
            shiftDown: (keyState & ShiftKeyState) != 0,
            defaultMove,
            canCopy: (allowedEffects & Copy) != 0,
            canMove: (allowedEffects & Move) != 0);
        return intent switch
        {
            FileDropIntent.Copy => Copy,
            FileDropIntent.Move => Move,
            _ => None
        };
    }

    public static uint ResolveCompletionEffect(
        bool hasExtractedPaths,
        uint allowedEffects,
        bool createdShellApplicationLinks = false)
    {
        if (!hasExtractedPaths)
        {
            return None;
        }

        if (createdShellApplicationLinks)
        {
            return (allowedEffects & Link) != 0
                ? Link
                : (allowedEffects & Copy) != 0
                    ? Copy
                    : None;
        }

        // Returning MOVE would authorize the drag source (notably Explorer)
        // to delete its source after this callback returns. DeskBox has only
        // queued its own asynchronous transfer at that point, producing a
        // check-then-disappear race. Report COPY so source cleanup stays off.
        return (allowedEffects & Copy) != 0 ? Copy : None;
    }
}

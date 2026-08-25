namespace DeskBox.Helpers;

internal enum FileDropIntent
{
    None,
    Copy,
    Move,
    Reference,
    Reorder,
    Organize
}

/// <summary>
/// Resolves the semantic intent of a file drop before individual XAML and OLE
/// adapters translate it into their platform-specific operation flags.
/// </summary>
internal static class FileDropIntentPolicy
{
    public static FileDropIntent ResolveMappedTransfer(
        bool hasMappedFolder,
        bool forceCopy,
        bool controlDown,
        bool shiftDown,
        bool defaultMove,
        bool canCopy = true,
        bool canMove = true)
    {
        if (!hasMappedFolder)
        {
            return FileDropIntent.Reference;
        }

        // Temporary and virtual payloads must outlive their provider-owned
        // staging directory, so they can never be moved from the source.
        if (forceCopy)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        // Ctrl+Shift normally means Link in Explorer. DeskBox intentionally
        // does not create links for folder-backed grids, so the safer Ctrl
        // copy behavior wins when both modifiers are held.
        if (controlDown)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if (shiftDown)
        {
            return canMove ? FileDropIntent.Move : FileDropIntent.None;
        }

        if (defaultMove && canMove)
        {
            return FileDropIntent.Move;
        }

        return canCopy
            ? FileDropIntent.Copy
            : canMove
                ? FileDropIntent.Move
                : FileDropIntent.None;
    }
}

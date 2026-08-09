using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public enum FileItemPointerSelectionAction
{
    Preserve,
    Add,
    Replace
}

public static class FileItemSelectionBehavior
{
    /// <summary>
    /// Applies the common pointer-selection rule to a realized file item.
    /// Shift is intentionally left to the ListView range-selection behavior.
    /// </summary>
    public static bool ApplyPointerSelection(
        ListViewBase listView,
        WidgetItem item,
        bool controlPressed,
        bool shiftPressed)
    {
        FileItemPointerSelectionAction action = ResolvePointerSelectionAction(
            listView.SelectedItems.Contains(item),
            controlPressed,
            shiftPressed);
        if (action == FileItemPointerSelectionAction.Preserve)
        {
            return false;
        }

        if (action == FileItemPointerSelectionAction.Add)
        {
            listView.SelectedItems.Add(item);
            return true;
        }

        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        return true;
    }

    public static FileItemPointerSelectionAction ResolvePointerSelectionAction(
        bool itemIsSelected,
        bool controlPressed,
        bool shiftPressed)
    {
        if (shiftPressed || itemIsSelected)
        {
            // Keep an existing multi-selection intact on pointer down. WinUI
            // raises DragItemsStarting after this event; replacing the selection
            // here would silently reduce a multi-file drag to its anchor item.
            return FileItemPointerSelectionAction.Preserve;
        }

        return controlPressed
            ? FileItemPointerSelectionAction.Add
            : FileItemPointerSelectionAction.Replace;
    }
}

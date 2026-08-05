using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

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
        if (shiftPressed)
        {
            return false;
        }

        if (controlPressed)
        {
            if (!listView.SelectedItems.Contains(item))
            {
                listView.SelectedItems.Add(item);
                return true;
            }

            return false;
        }

        if (listView.SelectedItems.Count == 1 &&
            listView.SelectedItems.Contains(item))
        {
            return false;
        }

        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        return true;
    }
}

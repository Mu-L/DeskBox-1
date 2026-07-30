using DeskBox.Models;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    public object? CaptureTransientState()
    {
        return new FileWidgetTransientState(
            GetSelectedItems()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _cutClipboardPaths.ToArray());
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not FileWidgetTransientState fileState)
        {
            return;
        }

        _cutClipboardPaths = fileState.CutPaths.ToArray();
        if (GetActiveItemsView() is { } view)
        {
            WidgetItem[] selectedItems = ViewModel.Items
                .Where(item => fileState.SelectedPaths.Contains(
                    item.Path,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            SynchronizeListViewSelection(view, selectedItems);
            ApplySelectionState(view);
        }
    }
}

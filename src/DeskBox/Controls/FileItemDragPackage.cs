using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

public readonly record struct FileItemDragPackageResult(
    IReadOnlyList<string> SourcePaths,
    bool HasStorageItems,
    bool UsesNativeShellDataObject);

/// <summary>
/// Creates the common file-item drag payload. Hosts remain responsible for
/// deciding which items are dragged and how the completed drop is reconciled.
/// </summary>
public static class FileItemDragPackage
{
    public static IReadOnlyList<WidgetItem> ResolveDraggedItems(
        IReadOnlyList<WidgetItem> eventItems,
        IReadOnlyList<WidgetItem> selectedItems)
    {
        WidgetItem[] distinctEventItems = eventItems.Distinct().ToArray();
        WidgetItem[] distinctSelectedItems = selectedItems.Distinct().ToArray();
        if (distinctSelectedItems.Length <= 1 || distinctEventItems.Length == 0)
        {
            return distinctEventItems;
        }

        // Some WinUI ListView input paths report only the pointer anchor in
        // DragItemsStarting even though it belongs to a larger selection. The
        // visible selection is authoritative whenever the event anchor is one
        // of its members.
        return distinctEventItems.Any(distinctSelectedItems.Contains)
            ? distinctSelectedItems
            : distinctEventItems;
    }

    public static bool TryPrepare(
        DataPackage dataPackage,
        IReadOnlyList<WidgetItem> draggedItems,
        string sourceWidgetId,
        Func<IEnumerable<string>, IReadOnlyList<IStorageItem>> getStorageItems,
        Func<IReadOnlyList<string>, string> getTitle,
        out FileItemDragPackageResult result)
    {
        result = default;
        if (draggedItems.Count == 0)
        {
            return false;
        }

        string[] sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            return false;
        }

        // WinRT's StorageFile broker can reject .lnk files (including ones
        // whose filesystem attributes look normal). More importantly, this
        // event is raised on the UI STA, so synchronously waiting for that
        // broker can deadlock the drag/drop message loop. Wrap a native Shell
        // IDataObject before attempting that broker so Explorer receives the
        // original filesystem item and owns its desktop drop position.
        bool requiresStorageBrokerBypass =
            NativeShellFileDragProvider.RequiresStorageBrokerBypass(
                sourcePaths);
        bool usesNativeShellDataObject =
            requiresStorageBrokerBypass &&
            NativeShellFileDragProvider.TryAttach(dataPackage, sourcePaths);
        IReadOnlyList<IStorageItem> storageItems = [];
        if (requiresStorageBrokerBypass && !usesNativeShellDataObject)
        {
            App.Log(
                $"[DragStart] Canceled broker-blocked file drag because a " +
                $"native Shell payload could not be created paths=" +
                $"{sourcePaths.Length}");
            return false;
        }

        if (!usesNativeShellDataObject)
        {
            storageItems = getStorageItems(sourcePaths);
            if (storageItems.Count == sourcePaths.Length)
            {
                dataPackage.SetStorageItems(storageItems, readOnly: false);
            }
            else
            {
                // Never advertise a partial selection or fall back to a
                // coordinate-free filesystem move after Drop. A native Shell
                // data object can represent the same existing paths without
                // involving the StorageItem broker.
                usesNativeShellDataObject =
                    NativeShellFileDragProvider.TryAttach(
                        dataPackage,
                        sourcePaths);
                storageItems = [];
                if (!usesNativeShellDataObject)
                {
                    App.Log(
                        $"[DragStart] Canceled file drag because only a " +
                        $"partial StorageItems payload was available " +
                        $"paths={sourcePaths.Length}");
                    return false;
                }
            }
        }

        dataPackage.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move |
            DataPackageOperation.Link;

        dataPackage.Properties[DeskBoxDragData.SourceWidgetIdProperty] =
            sourceWidgetId;
        dataPackage.Properties[DeskBoxDragData.SourcePathsProperty] =
            sourcePaths;
        dataPackage.Properties[
            DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        dataPackage.Properties.Title = getTitle(sourcePaths);
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));

        result = new FileItemDragPackageResult(
            sourcePaths,
            storageItems.Count > 0 || usesNativeShellDataObject,
            usesNativeShellDataObject);
        return true;
    }
}

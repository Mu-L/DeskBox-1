using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

public readonly record struct FileItemDragPackageResult(
    IReadOnlyList<string> SourcePaths,
    bool HasStorageItems,
    bool UsesVirtualStorageItems);

/// <summary>
/// Creates the common file-item drag payload. Hosts remain responsible for
/// deciding which items are dragged and how the completed drop is reconciled.
/// </summary>
public static class FileItemDragPackage
{
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

        dataPackage.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move |
            DataPackageOperation.Link;

        IReadOnlyList<IStorageItem> storageItems =
            getStorageItems(sourcePaths);
        bool usesVirtualStorageItems = false;
        if (storageItems.Count > 0)
        {
            dataPackage.SetStorageItems(storageItems, readOnly: false);
        }
        else if (VirtualShortcutDragProvider.TryAttach(
                     dataPackage,
                     sourcePaths))
        {
            usesVirtualStorageItems = true;
            App.LogVerbose(
                $"[DragStart] Attached virtual shortcut StorageItems " +
                $"paths={sourcePaths.Length}");
        }

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
            storageItems.Count > 0,
            usesVirtualStorageItems);
        return true;
    }
}

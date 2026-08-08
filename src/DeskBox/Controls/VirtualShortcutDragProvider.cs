using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

/// <summary>
/// Supplies .lnk files as on-demand virtual StorageFiles when the WinRT file
/// broker refuses to open their real paths. Advertising a genuine
/// StorageItems format lets Explorer negotiate the drag operation and display
/// the correct copy/move feedback instead of a prohibited badge.
/// </summary>
internal static class VirtualShortcutDragProvider
{
    internal static bool TryAttach(
        DataPackage dataPackage,
        IReadOnlyList<string> sourcePaths)
    {
        if (!CanProvide(sourcePaths))
        {
            return false;
        }

        string[] paths = sourcePaths
            .Select(Path.GetFullPath)
            .ToArray();
        dataPackage.SetDataProvider(
            StandardDataFormats.StorageItems,
            async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var storageItems = new List<IStorageItem>(paths.Length);
                    foreach (string path in paths)
                    {
                        StorageFile virtualFile =
                            await StorageFile.CreateStreamedFileAsync(
                                Path.GetFileName(path),
                                streamRequest =>
                                    CopySourceToStreamAsync(
                                        path,
                                        streamRequest),
                                thumbnail: null);
                        storageItems.Add(virtualFile);
                    }

                    request.SetData(storageItems);
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[DragStart] Failed to provide virtual shortcut " +
                        $"StorageItems: {ex}");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        return true;
    }

    internal static bool CanProvide(
        IReadOnlyList<string> sourcePaths,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return sourcePaths.Count > 0 &&
               sourcePaths.All(path =>
                   !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(
                       Path.GetExtension(path),
                       ".lnk",
                       StringComparison.OrdinalIgnoreCase) &&
                   fileExists(path));
    }

    internal static bool RequiresStorageBrokerBypass(
        IReadOnlyList<string> sourcePaths,
        Func<string, bool>? fileExists = null,
        Func<string, System.IO.FileAttributes>? getAttributes = null)
    {
        if (!CanProvide(sourcePaths, fileExists))
        {
            return false;
        }

        getAttributes ??= File.GetAttributes;
        foreach (string path in sourcePaths)
        {
            try
            {
                System.IO.FileAttributes attributes = getAttributes(path);
                if ((attributes &
                     (System.IO.FileAttributes.Hidden |
                      System.IO.FileAttributes.System)) != 0)
                {
                    return true;
                }
            }
            catch
            {
                // If even the attributes cannot be read, asking the WinRT
                // broker for this path is both unlikely to succeed and unsafe
                // to wait on from the UI STA.
                return true;
            }
        }

        return false;
    }

    private static async void CopySourceToStreamAsync(
        string sourcePath,
        StreamedFileDataRequest request)
    {
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            using Stream destination = request.AsStreamForWrite();
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DragStart] Failed to stream virtual shortcut " +
                $"path='{sourcePath}': {ex.Message}");
            try
            {
                request.FailAndClose(
                    StreamedFileFailureMode.Incomplete);
            }
            catch
            {
            }
        }
    }
}

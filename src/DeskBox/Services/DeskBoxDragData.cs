using System.Net.Http;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public static class DeskBoxDragData
{
    public const string TextFormat = "DeskBox.Internal.Text.v1";
    public const string SourceFormat = "DeskBox.Internal.Source.v1";
    public const string TodoColorMarkerFormat = "DeskBox.Todo.ColorMarker.v1";
    public const string SourceWidgetIdProperty = "DeskBoxSourceWidgetId";
    public const string SourcePathsProperty = "DeskBoxSourcePaths";
    public const string InternalFileDragTokenProperty =
        "DeskBoxInternalDragToken";
    public const string InternalFileDragToken =
        "DeskBox.WidgetItemDrag.v2";
    public const string StackReorderKeyProperty =
        "DeskBoxStackReorderKey";
    public const string SourceTodo = "todo";
    public const string SourceQuickCapture = "quick-capture";
    private static readonly HttpClient s_virtualDropHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static void SetText(DataPackage dataPackage, string? text, string source)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return;
        }

        dataPackage.SetText(normalizedText);
        dataPackage.SetData(TextFormat, normalizedText);
        dataPackage.SetData(SourceFormat, source);
    }

    public static void SetTodoColorMarker(DataPackage dataPackage, string colorMarker)
    {
        if (!string.IsNullOrWhiteSpace(colorMarker))
        {
            dataPackage.SetData(TodoColorMarkerFormat, colorMarker.Trim());
        }
    }

    public static async Task<string?> TryGetTodoColorMarkerAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(TodoColorMarkerFormat))
        {
            return null;
        }

        try
        {
            return await dataView.GetDataAsync(TodoColorMarkerFormat) as string;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read todo color marker: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> TryGetTextAsync(DataPackageView dataView)
    {
        string? internalText = await TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText;
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeText(text);
            }
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        return null;
    }

    public static bool HasDroppedFiles(DataPackageView dataView)
    {
        if (GetInternalDroppedFiles(dataView).Count > 0 ||
            dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.Contains(StandardDataFormats.Bitmap))
        {
            return true;
        }

        return dataView.AvailableFormats.Any(IsLikelyFileTransferFormat);
    }

    /// <summary>
    /// Returns whether a package can yield importable file data. Browser drags
    /// commonly expose a virtual file, a bitmap, or only an HTTP(S) URL in text.
    /// </summary>
    public static bool HasImportableFileData(DataPackageView dataView)
    {
        return HasDroppedFiles(dataView) ||
               dataView.Contains(StandardDataFormats.Text) ||
               dataView.Contains(StandardDataFormats.WebLink);
    }

    /// <summary>
    /// Identifies a file drag that originated from a DeskBox file surface. Content
    /// widgets treat this as a link/association, never as a file-system transfer.
    /// </summary>
    public static bool IsInternalFileDrag(DataPackageView dataView)
    {
        return dataView.Properties.TryGetValue(
                   InternalFileDragTokenProperty,
                   out object? token) &&
               string.Equals(
                   token as string,
                   InternalFileDragToken,
                   StringComparison.Ordinal) &&
               dataView.Properties.ContainsKey(SourcePathsProperty);
    }

    public static DataPackageOperation GetFileAssociationOperation(
        DataPackageView dataView)
    {
        return IsInternalFileDrag(dataView)
            ? DataPackageOperation.Link
            : DataPackageOperation.Copy;
    }

    public static bool ShouldShowImportOverlay(
        IReadOnlyList<string> paths)
    {
        const long ThresholdBytes = 10 * 1024 * 1024;

        long totalSize = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    totalSize += new FileInfo(path).Length;
                }
                else if (Directory.Exists(path))
                {
                    // Avoid recursively enumerating a folder on the UI path.
                    return true;
                }
            }
            catch
            {
                return true;
            }

            if (totalSize >= ThresholdBytes)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<DroppedFileBatch> TryGetDroppedFilesAsync(DataPackageView dataView)
    {
        IReadOnlyList<DroppedFilePath> internalFiles =
            GetInternalDroppedFiles(dataView);
        if (internalFiles.Count > 0)
        {
            return new DroppedFileBatch(
                internalFiles,
                temporaryDirectory: null,
                skippedCount: 0);
        }

        var files = new List<DroppedFilePath>();
        string? temporaryDirectory = null;
        int skippedCount = 0;

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.AvailableFormats.Any(IsLikelyFileTransferFormat))
        {
            try
            {
                IReadOnlyList<IStorageItem> storageItems = await dataView.GetStorageItemsAsync();
                foreach (IStorageItem storageItem in storageItems)
                {
                    if (storageItem is StorageFolder storageFolder &&
                        !string.IsNullOrWhiteSpace(storageFolder.Path) &&
                        Directory.Exists(storageFolder.Path))
                    {
                        files.Add(new DroppedFilePath(
                            Path.GetFullPath(storageFolder.Path),
                            storageFolder.Name,
                            ForceManagedCopy: false));
                        continue;
                    }

                    if (storageItem is not StorageFile storageFile)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(storageFile.Path) && File.Exists(storageFile.Path))
                    {
                        files.Add(new DroppedFilePath(
                            Path.GetFullPath(storageFile.Path),
                            storageFile.Name,
                            ForceManagedCopy: false));
                        continue;
                    }

                    temporaryDirectory ??= CreateTemporaryDropDirectory();
                    string? materializedPath = await MaterializeStorageFileAsync(storageFile, temporaryDirectory);
                    if (materializedPath is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    files.Add(new DroppedFilePath(
                        materializedPath,
                        Path.GetFileName(materializedPath),
                        ForceManagedCopy: true));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DragDrop] Failed to read dropped storage items: {ex.Message}");
            }
        }

        if (files.Count == 0 && dataView.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                RandomAccessStreamReference bitmapReference = await dataView.GetBitmapAsync();
                using IRandomAccessStreamWithContentType bitmapStream = await bitmapReference.OpenReadAsync();
                temporaryDirectory ??= CreateTemporaryDropDirectory();
                string extension = GetBitmapExtension(bitmapStream.ContentType);
                string fileName = $"Dropped image {DateTime.Now:yyyyMMdd-HHmmss}{extension}";
                string path = await SaveRandomAccessStreamAsync(bitmapStream, fileName, temporaryDirectory);
                files.Add(new DroppedFilePath(path, fileName, ForceManagedCopy: true));
            }
            catch (Exception ex)
            {
                skippedCount++;
                App.Log($"[DragDrop] Failed to read dropped bitmap: {ex.Message}");
            }
        }

        // A browser may expose an image as FileContents/FileGroupDescriptorW,
        // but some sites only provide the resource URL. Materialize that URL as
        // a managed temporary file so the normal import pipeline can copy it.
        if (files.Count == 0)
        {
            string? webUrl = await TryGetDroppedWebUrlAsync(dataView);
            if (!string.IsNullOrWhiteSpace(webUrl))
            {
                temporaryDirectory ??= CreateTemporaryDropDirectory();
                string? materializedPath = await MaterializeWebUrlAsync(
                    webUrl,
                    temporaryDirectory);
                if (materializedPath is null)
                {
                    skippedCount++;
                }
                else
                {
                    files.Add(new DroppedFilePath(
                        materializedPath,
                        Path.GetFileName(materializedPath),
                        ForceManagedCopy: true));
                }
            }
        }

        return new DroppedFileBatch(files, temporaryDirectory, skippedCount);
    }

    public static IReadOnlyList<DroppedFilePath> GetInternalDroppedFiles(
        DataPackageView dataView)
    {
        if (!dataView.Properties.TryGetValue(
                SourcePathsProperty,
                out object? value))
        {
            return [];
        }

        IEnumerable<string> paths = value switch
        {
            string[] array => array,
            IReadOnlyList<string> list => list,
            IEnumerable<string> sequence => sequence,
            _ => []
        };

        var files = new List<DroppedFilePath>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                (!File.Exists(path) && !Directory.Exists(path)))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    files.Add(new DroppedFilePath(
                        fullPath,
                        Path.GetFileName(fullPath),
                        ForceManagedCopy: false));
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DragDrop] Ignored invalid internal path '{path}': " +
                    ex.Message);
            }
        }

        return files;
    }

    public static async Task<string?> TryGetInternalTextAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(TextFormat))
        {
            return null;
        }

        try
        {
            if (await dataView.GetDataAsync(TextFormat) is string internalText &&
                !string.IsNullOrWhiteSpace(internalText))
            {
                return NormalizeText(internalText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read DeskBox internal text: {ex.Message}");
        }

        return null;
    }

    private static string NormalizeText(string? text)
    {
        return (text ?? string.Empty).Trim();
    }

    private static bool IsLikelyFileTransferFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format) ||
            format.StartsWith("Preferred DropEffect", StringComparison.Ordinal))
        {
            return false;
        }

        return format.Contains("StorageItems", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("StorageItem", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("FileName", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TryGetDroppedWebUrlAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            try
            {
                Uri? link = await dataView.GetWebLinkAsync();
                if (IsHttpUrl(link?.AbsoluteUri))
                {
                    return link!.AbsoluteUri;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DragDrop] Failed to read dropped web link: {ex.Message}");
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return null;
        }

        try
        {
            string text = await dataView.GetTextAsync();
            return text.Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsHttpUrl);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read dropped text: {ex.Message}");
            return null;
        }
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value?.Trim().Trim('"'), UriKind.Absolute, out Uri? uri) &&
               uri.Scheme is "http" or "https";
    }

    private static async Task<string?> MaterializeWebUrlAsync(
        string url,
        string temporaryDirectory)
    {
        if (!Uri.TryCreate(url.Trim().Trim('"'), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            string fileName = FileService.SanitizeFileSystemName(
                Path.GetFileName(uri.LocalPath));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "Dropped web resource";
            }

            string destinationPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, fileName));
            byte[] bytes = await s_virtualDropHttpClient.GetByteArrayAsync(uri);
            await File.WriteAllBytesAsync(destinationPath, bytes);
            destinationPath =
                VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                    destinationPath);
            App.Log(
                $"[DragDrop] Materialized web drop url='{uri}' " +
                $"path='{destinationPath}' bytes={bytes.Length}");
            return destinationPath;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to materialize web drop '{uri}': {ex.Message}");
            return null;
        }
    }

    internal static async Task<string?> MaterializeStorageFileAsync(
        StorageFile storageFile,
        string temporaryDirectory)
    {
        string fileName = FileService.SanitizeFileSystemName(Path.GetFileName(storageFile.Name));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"Dropped file {DateTime.Now:yyyyMMdd-HHmmss}";
        }

        try
        {
            using IRandomAccessStreamWithContentType source = await storageFile.OpenReadAsync();
            return await SaveRandomAccessStreamAsync(source, fileName, temporaryDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to materialize virtual file '{storageFile.Name}': {ex.Message}");
            return null;
        }
    }

    private static async Task<string> SaveRandomAccessStreamAsync(
        IRandomAccessStream source,
        string fileName,
        string temporaryDirectory)
    {
        Directory.CreateDirectory(temporaryDirectory);
        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(temporaryDirectory, fileName));
        source.Seek(0);
        using Stream sourceStream = source.AsStreamForRead();
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await sourceStream.CopyToAsync(destination);
        }

        return VirtualDropFileNameResolver.AddMissingExtensionFromContent(
            destinationPath);
    }

    private static string CreateTemporaryDropDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "DroppedFiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetBitmapExtension(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => ".png"
        };
    }
}

public sealed record DroppedFilePath(string Path, string DisplayName, bool ForceManagedCopy);

public sealed class DroppedFileBatch : IDisposable
{
    private readonly string? _temporaryDirectory;

    internal DroppedFileBatch(
        IReadOnlyList<DroppedFilePath> files,
        string? temporaryDirectory,
        int skippedCount)
    {
        Files = files;
        _temporaryDirectory = temporaryDirectory;
        SkippedCount = skippedCount;
    }

    public IReadOnlyList<DroppedFilePath> Files { get; }

    public int SkippedCount { get; }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_temporaryDirectory) ||
            !Directory.Exists(_temporaryDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to clean temporary drop files: {ex.Message}");
        }
    }
}

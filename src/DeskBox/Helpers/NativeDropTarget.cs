using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskBox.Services;

namespace DeskBox.Helpers;

/// <summary>
/// COM IDropTarget implementation that bridges native OLE drag-drop to .NET events.
/// Replaces the legacy WM_DROPFILES approach, providing real-time drag-over feedback.
/// </summary>
public sealed class NativeDropTarget : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SIZEL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public SIZEL sizel;
        public POINTL pointl;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    // ── Constants ──

    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const uint TYMED_ISTREAM = 4;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    // ── P/Invoke ──

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref NativeStorageMedium medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, System.Text.StringBuilder? fileName, uint bufferSize);

    private const uint CF_HDROP = 15;
    private static readonly ushort s_fileGroupDescriptorFormat;
    private static readonly ushort s_fileContentsFormat;

    // ── State ──

    private readonly IntPtr _hwnd;
    private readonly NativeDropTargetComObject _comObject;
    private bool _registered;

    /// <summary>Fired when a drag enters the window. Provides screen coordinates and whether file data is available.</summary>
    public event Action<int, int, bool>? DragEnterEvent;

    /// <summary>Fired as the drag moves over the window. Provides screen coordinates.</summary>
    public event Action<int, int>? DragOverEvent;

    /// <summary>Fired when the drag leaves the window without dropping.</summary>
    public event Action? DragLeaveEvent;

    /// <summary>Fired when files are dropped. Provides the list of file paths and screen coordinates.</summary>
    public event Action<IReadOnlyList<string>, int, int, bool, bool>? DropEvent;

    /// <summary>
    /// Whether the current drag payload contains file drop data (CF_HDROP).
    /// Valid between DragEnter and DragLeave/Drop.
    /// </summary>
    public bool HasFileData { get; private set; }

    public bool HasVirtualFileData { get; private set; }

    internal bool IsRegistered => _registered;

    static NativeDropTarget()
    {
        s_fileGroupDescriptorFormat = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW");
        s_fileContentsFormat = (ushort)RegisterClipboardFormatW("FileContents");

        // Ensure OLE is initialized (WinUI 3 usually does this, but call
        // again is harmless if already initialized).
        try
        {
            OleInitialize(IntPtr.Zero);
        }
        catch
        {
        }
    }

    public NativeDropTarget(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _comObject = new NativeDropTargetComObject(this);
    }

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            NativeDropTargetComInterop.Register(_hwnd, _comObject);
            _registered = true;
            App.Log($"[DropTarget] Registered IDropTarget for hwnd=0x{_hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] RegisterDragDrop failed: {ex.Message}");
        }
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            NativeDropTargetComInterop.Revoke(_hwnd);
        }
        catch
        {
        }
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
    }

#if DESKBOX_NATIVE_AOT
    internal nint AcquireAotSmokeInterfacePointer()
    {
        return NativeDropTargetComInterop.AcquireInterfacePointer(_comObject);
    }
#endif

    internal int OnDragEnter(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        uint allowedEffects = effect;
        HasVirtualFileData = TryHasVirtualFileData(dataObject);
        HasFileData = HasVirtualFileData || TryHasHDropData(dataObject);
        DragEnterEvent?.Invoke(point.X, point.Y, HasFileData);

        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects);
        return S_OK;
    }

    internal int OnDragOver(
        uint keyState,
        POINT point,
        ref uint effect)
    {
        uint allowedEffects = effect;
        DragOverEvent?.Invoke(point.X, point.Y);

        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects);
        return S_OK;
    }

    internal int OnDragLeave()
    {
        HasFileData = false;
        HasVirtualFileData = false;
        DragLeaveEvent?.Invoke();
        return S_OK;
    }

    internal int OnDrop(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        uint allowedEffects = effect;
        bool copyRequested =
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                HasVirtualFileData,
                keyState,
                allowedEffects) != NativeDropEffectPolicy.Move;
        var (paths, containsTemporaryFiles) = TryExtractFilePaths(dataObject);
        HasFileData = false;
        HasVirtualFileData = false;

        // Always log so we can tell whether the native OLE drop target receives
        // CF_HDROP drops (WeChat / Explorer) at all, and what it extracted.
        App.Log($"[DropTarget] NativeDrop received count={paths.Count} temp={containsTemporaryFiles}");

        if (paths.Count > 0)
        {
            DropEvent?.Invoke(
                paths,
                point.X,
                point.Y,
                containsTemporaryFiles,
                copyRequested);
            effect = NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects);
        }
        else
        {
            effect = NativeDropEffectPolicy.None;
        }

        return S_OK;
    }

    // ── Data extraction helpers ──

    private bool TryHasHDropData(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            return dataObject.QueryGetData(ref format) == S_OK;
        }
        catch
        {
            return false;
        }
    }

    private bool TryHasVirtualFileData(IntPtr pDataObj)
    {
        return TryQueryFormat(pDataObj, s_fileGroupDescriptorFormat, TYMED_HGLOBAL, -1);
    }

    private static bool TryQueryFormat(IntPtr pDataObj, ushort clipboardFormat, uint tymed, int index)
    {
        if (pDataObj == IntPtr.Zero || clipboardFormat == 0)
        {
            return false;
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = clipboardFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = index,
                MediumType = tymed,
            };
            return dataObject.QueryGetData(ref format) == S_OK;
        }
        catch
        {
            return false;
        }
    }

    private (IReadOnlyList<string> Paths, bool ContainsTemporaryFiles) TryExtractFilePaths(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return ([], false);
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            int hr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (hr == S_OK && medium.Content != IntPtr.Zero)
            {
                try
                {
                    IReadOnlyList<string> paths = GetDroppedFiles(medium.Content);
                    if (paths.Count > 0)
                    {
                        return (paths, false);
                    }
                }
                finally
                {
                    ReleaseStgMedium(ref medium);
                }
            }

            IReadOnlyList<string> virtualPaths = ExtractVirtualFiles(dataObject);
            return (virtualPaths, virtualPaths.Count > 0);
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to extract file paths: {ex.Message}");
            return ([], false);
        }
    }

    private static IReadOnlyList<string> GetDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            uint length = DragQueryFile(hDrop, i, null, 0);
            if (length == 0)
            {
                continue;
            }

            var builder = new System.Text.StringBuilder((int)length + 1);
            uint copied = DragQueryFile(hDrop, i, builder, (uint)builder.Capacity);
            if (copied > 0)
            {
                paths.Add(builder.ToString());
            }
        }

        return paths;
    }

    private static IReadOnlyList<string> ExtractVirtualFiles(NativeOleDataObject dataObject)
    {
        var descriptorFormat = new NativeFormatEtc
        {
            ClipboardFormat = s_fileGroupDescriptorFormat,
            TargetDevice = IntPtr.Zero,
            Aspect = DVASPECT_CONTENT,
            Index = -1,
            MediumType = TYMED_HGLOBAL,
        };
        if (dataObject.GetData(
                ref descriptorFormat,
                out NativeStorageMedium descriptorMedium) != S_OK ||
            descriptorMedium.Content == IntPtr.Zero)
        {
            return [];
        }

        List<FILEDESCRIPTORW> descriptors;
        try
        {
            descriptors = ReadVirtualFileDescriptors(descriptorMedium.Content);
        }
        finally
        {
            ReleaseStgMedium(ref descriptorMedium);
        }

        if (descriptors.Count == 0)
        {
            return [];
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>();
        for (int index = 0; index < descriptors.Count; index++)
        {
            FILEDESCRIPTORW descriptor = descriptors[index];
            if ((descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }

            string fileName = FileService.SanitizeFileSystemName(
                Path.GetFileName(descriptor.cFileName));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"Dropped file {index + 1}";
            }

            string destinationPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, fileName));
            if (TrySaveVirtualFileContents(dataObject, index, destinationPath))
            {
                string resolvedPath =
                    VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                        destinationPath);
                if (!string.Equals(
                        resolvedPath,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    App.Log(
                        $"[DropTarget] Added missing virtual-file extension " +
                        $"source='{destinationPath}' resolved='{resolvedPath}'");
                }

                paths.Add(resolvedPath);
            }
        }

        if (paths.Count == 0)
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }

        return paths;
    }

    private static List<FILEDESCRIPTORW> ReadVirtualFileDescriptors(IntPtr descriptorHandle)
    {
        IntPtr pointer = GlobalLock(descriptorHandle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = Marshal.ReadInt32(pointer);
            if (count <= 0 || count > 4096)
            {
                return [];
            }

            int descriptorSize = Marshal.SizeOf<FILEDESCRIPTORW>();
            var descriptors = new List<FILEDESCRIPTORW>(count);
            IntPtr descriptorPointer = IntPtr.Add(pointer, sizeof(uint));
            for (int index = 0; index < count; index++)
            {
                descriptors.Add(Marshal.PtrToStructure<FILEDESCRIPTORW>(
                    IntPtr.Add(descriptorPointer, index * descriptorSize)));
            }

            return descriptors;
        }
        finally
        {
            GlobalUnlock(descriptorHandle);
        }
    }

    private static bool TrySaveVirtualFileContents(
        NativeOleDataObject dataObject,
        int index,
        string destinationPath)
    {
        // Try TYMED_ISTREAM first — FileContents from browser drag sources
        // (Chrome / Edge / Firefox) is strictly an IStream. Asking for the
        // combined mask TYMED_ISTREAM | TYMED_HGLOBAL in one FORMATETC is
        // unreliable across OLE sources: some reject the combined mask
        // outright (returning DV_E_FORMATETC), which made the widget silently
        // ignore browser drops even though DragEnter/Drop fired. Fall back to
        // TYMED_HGLOBAL for sources that provide the contents as a memory blob.
        NativeStorageMedium contentsMedium = default;
        uint actualTymed = 0;
        IntPtr actualMedium = IntPtr.Zero;
        foreach (uint tymed in new uint[] { TYMED_ISTREAM, TYMED_HGLOBAL })
        {
            var contentsFormat = new NativeFormatEtc
            {
                ClipboardFormat = s_fileContentsFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = index,
                MediumType = tymed,
            };
            int hr = dataObject.GetData(ref contentsFormat, out contentsMedium);
            if (hr == S_OK && contentsMedium.Content != IntPtr.Zero)
            {
                actualTymed = contentsMedium.MediumType;
                actualMedium = contentsMedium.Content;
                break;
            }
            // Release any partially populated medium before retrying with a
            // different tymed (GetData may have set unionMember on failure).
            if (contentsMedium.Content != IntPtr.Zero)
            {
                ReleaseStgMedium(ref contentsMedium);
                contentsMedium = default;
            }
        }

        if (actualMedium == IntPtr.Zero)
        {
            App.Log(
                $"[DropTarget] No FileContents payload for virtual file index={index} " +
                $"destination='{destinationPath}' (browser may not have provided a stream)");
            return false;
        }

        try
        {
            if ((actualTymed & TYMED_ISTREAM) != 0)
            {
                SaveComStream(actualMedium, destinationPath);
                return true;
            }

            if ((actualTymed & TYMED_HGLOBAL) != 0)
            {
                SaveGlobalMemory(actualMedium, destinationPath);
                return true;
            }

            App.Log(
                $"[DropTarget] Unexpected FileContents tymed=0x{actualTymed:X} " +
                $"for index={index}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to save virtual file '{destinationPath}': {ex.Message}");
            try { File.Delete(destinationPath); } catch { }
            return false;
        }
        finally
        {
            ReleaseStgMedium(ref contentsMedium);
        }
    }

    private static void SaveComStream(IntPtr streamPointer, string destinationPath)
    {
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        NativeComStreamReader.CopyTo(streamPointer, destination);
    }

    private static void SaveGlobalMemory(IntPtr memoryHandle, string destinationPath)
    {
        long size = GlobalSize(memoryHandle).ToInt64();
        if (size < 0 || size > int.MaxValue)
        {
            throw new IOException("Virtual file memory payload is too large.");
        }

        IntPtr pointer = GlobalLock(memoryHandle);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Could not lock virtual file memory payload.");
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            File.WriteAllBytes(destinationPath, bytes);
        }
        finally
        {
            GlobalUnlock(memoryHandle);
        }
    }
}

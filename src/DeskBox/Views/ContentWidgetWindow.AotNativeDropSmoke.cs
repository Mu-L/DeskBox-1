#if DESKBOX_NATIVE_AOT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Helpers;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal unsafe AotNativeDropCallbackResult InvokeAotNativeHDropCallbacks(
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        uint keyState,
        bool leaveWithoutDrop,
        bool stopAfterDragOver = false)
    {
        if (_nativeFileDropTarget is not { IsRegistered: true } target)
        {
            throw new InvalidOperationException(
                "The real ContentWidgetWindow OLE drop target is not registered.");
        }

        string[] normalizedPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0 ||
            normalizedPaths.Any(path =>
                !File.Exists(path) && !Directory.Exists(path)))
        {
            throw new InvalidOperationException(
                "The AOT HDROP probe requires existing filesystem paths.");
        }

        using var dataObject = new AotNativeHDropDataObject(normalizedPaths);
        nint interfacePointer = target.AcquireAotSmokeInterfacePointer();
        try
        {
            nint* vtable = *(nint**)interfacePointer;
            if (vtable is null ||
                vtable[3] == 0 ||
                vtable[4] == 0 ||
                vtable[5] == 0 ||
                vtable[6] == 0)
            {
                throw new InvalidOperationException(
                    "The generated IDropTarget CCW is missing a required callback slot.");
            }

            var dragEnter = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[3];
            var dragOver = (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[4];
            var dragLeave = (delegate* unmanaged[Stdcall]<nint, int>)vtable[5];
            var drop = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[6];

            var point = new NativeDropTarget.POINT
            {
                X = screenX,
                Y = screenY
            };
            uint enterEffect =
                NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
            int enterResult = dragEnter(
                interfacePointer,
                dataObject.Pointer,
                keyState,
                point,
                &enterEffect);
            uint overEffect =
                NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
            int overResult = dragOver(
                interfacePointer,
                keyState,
                point,
                &overEffect);

            int leaveResult = int.MinValue;
            int dropResult = int.MinValue;
            uint completionEffect = NativeDropEffectPolicy.None;
            if (stopAfterDragOver)
            {
                // Keep the OLE session alive long enough for the caller to
                // inspect the native screen-point fallback independently from
                // DragLeave. The caller must subsequently invoke the leave slot.
            }
            else if (leaveWithoutDrop)
            {
                leaveResult = dragLeave(interfacePointer);
            }
            else
            {
                completionEffect =
                    NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
                dropResult = drop(
                    interfacePointer,
                    dataObject.Pointer,
                    keyState,
                    point,
                    &completionEffect);
            }

            return new AotNativeDropCallbackResult(
                target.IsRegistered,
                normalizedPaths,
                screenX,
                screenY,
                keyState,
                leaveWithoutDrop,
                stopAfterDragOver,
                enterResult,
                overResult,
                leaveResult,
                dropResult,
                enterEffect,
                overEffect,
                completionEffect);
        }
        finally
        {
            NativeDropTargetComInterop.ReleaseInterfacePointer(interfacePointer);
        }
    }

    internal unsafe int InvokeAotNativeDragLeaveCallback()
    {
        if (_nativeFileDropTarget is not { IsRegistered: true } target)
        {
            throw new InvalidOperationException(
                "The real ContentWidgetWindow OLE drop target is not registered.");
        }

        nint interfacePointer = target.AcquireAotSmokeInterfacePointer();
        try
        {
            nint* vtable = *(nint**)interfacePointer;
            if (vtable is null || vtable[5] == 0)
            {
                throw new InvalidOperationException(
                    "The generated IDropTarget CCW has no DragLeave slot.");
            }

            var dragLeave = (delegate* unmanaged[Stdcall]<nint, int>)vtable[5];
            return dragLeave(interfacePointer);
        }
        finally
        {
            NativeDropTargetComInterop.ReleaseInterfacePointer(interfacePointer);
        }
    }
}

internal sealed unsafe class AotNativeHDropDataObject : IDisposable
{
    private const int SlotCount = 12;
    private const ushort FileDropClipboardFormat = 15;
    private const uint ContentAspect = 1;
    private const uint HGlobalMedium = 1;
    private const int Success = 0;
    private const int FormatError = unchecked((int)0x80040064);
    private const int GeneralFailure = unchecked((int)0x80004005);

    private readonly string[] _paths;
    private readonly GCHandle _selfHandle;
    private readonly nint* _vtable;
    private readonly nint* _instance;
    private bool _disposed;

    internal AotNativeHDropDataObject(IReadOnlyList<string> paths)
    {
        _paths = paths.ToArray();
        _vtable = (nint*)NativeMemory.AllocZeroed(
            SlotCount,
            (nuint)sizeof(nint));
        _instance = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        _vtable[3] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int>)&GetData;
        _vtable[5] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            int>)&QueryGetData;
        _instance[0] = (nint)_vtable;
        _instance[1] = GCHandle.ToIntPtr(_selfHandle);
    }

    internal nint Pointer => (nint)_instance;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryGetData(nint self, NativeFormatEtc* format)
    {
        if (self == 0 || format is null)
        {
            return FormatError;
        }

        return format->ClipboardFormat == FileDropClipboardFormat &&
               format->Aspect == ContentAspect &&
               format->Index == -1 &&
               (format->MediumType & HGlobalMedium) != 0
            ? Success
            : FormatError;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(
        nint self,
        NativeFormatEtc* format,
        NativeStorageMedium* medium)
    {
        if (medium is not null)
        {
            *medium = default;
        }
        if (QueryFormat(self, format) != Success || medium is null)
        {
            return FormatError;
        }

        try
        {
            AotNativeHDropDataObject owner = GetOwner(self);
            nint hDrop = owner.CreateHDrop();
            *medium = new NativeStorageMedium
            {
                MediumType = HGlobalMedium,
                Content = hDrop,
                ReleaseUnknown = 0
            };
            return Success;
        }
        catch
        {
            return GeneralFailure;
        }
    }

    private static int QueryFormat(nint self, NativeFormatEtc* format)
    {
        if (self == 0 || format is null)
        {
            return FormatError;
        }

        return format->ClipboardFormat == FileDropClipboardFormat &&
               format->Aspect == ContentAspect &&
               format->Index == -1 &&
               (format->MediumType & HGlobalMedium) != 0
            ? Success
            : FormatError;
    }

    private static AotNativeHDropDataObject GetOwner(nint self)
    {
        nint handlePointer = ((nint*)self)[1];
        return GCHandle.FromIntPtr(handlePointer).Target as
            AotNativeHDropDataObject ??
            throw new InvalidOperationException(
                "The AOT HDROP data object lost its managed owner.");
    }

    private nint CreateHDrop()
    {
        byte[] pathBytes = Encoding.Unicode.GetBytes(
            string.Join('\0', _paths) + "\0\0");
        nuint totalBytes = checked((nuint)(20 + pathBytes.Length));
        nint hDrop = AotNativeDropWin32.GlobalAlloc(
            AotNativeDropWin32.Moveable | AotNativeDropWin32.ZeroInitialize,
            totalBytes);
        if (hDrop == 0)
        {
            throw new OutOfMemoryException(
                "GlobalAlloc failed for the AOT HDROP payload.");
        }

        nint locked = AotNativeDropWin32.GlobalLock(hDrop);
        if (locked == 0)
        {
            _ = AotNativeDropWin32.GlobalFree(hDrop);
            throw new InvalidOperationException(
                "GlobalLock failed for the AOT HDROP payload.");
        }

        try
        {
            Marshal.WriteInt32(locked, 0, 20);
            Marshal.WriteInt32(locked, 4, 0);
            Marshal.WriteInt32(locked, 8, 0);
            Marshal.WriteInt32(locked, 12, 0);
            Marshal.WriteInt32(locked, 16, 1);
            Marshal.Copy(pathBytes, 0, locked + 20, pathBytes.Length);
        }
        finally
        {
            _ = AotNativeDropWin32.GlobalUnlock(hDrop);
        }

        return hDrop;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _selfHandle.Free();
        NativeMemory.Free(_instance);
        NativeMemory.Free(_vtable);
    }
}

internal static partial class AotNativeDropWin32
{
    internal const uint Moveable = 0x0002;
    internal const uint ZeroInitialize = 0x0040;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint memory);
}

internal sealed record AotNativeDropCallbackResult(
    bool TargetRegistered,
    IReadOnlyList<string> Paths,
    int ScreenX,
    int ScreenY,
    uint KeyState,
    bool LeaveWithoutDrop,
    bool StoppedAfterDragOver,
    int DragEnterHResult,
    int DragOverHResult,
    int DragLeaveHResult,
    int DropHResult,
    uint DragEnterEffect,
    uint DragOverEffect,
    uint CompletionEffect);
#endif

using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

public static class ShellClipboardHelper
{
    private const uint CfHdrop = 15;
    private const uint DragQueryFileCount = 0xFFFFFFFF;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const int DropFilesHeaderSize = 20;
    private const int ClipboardOpenAttempts = 5;
    private const int ClipboardOpenRetryDelayMs = 5;

    private static readonly uint PreferredDropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

    public static bool TrySetFileDropList(IReadOnlyList<string> paths, bool cut)
    {
        var validPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validPaths.Length == 0)
        {
            return false;
        }

        if (!TryOpenClipboard())
        {
            return false;
        }

        IntPtr dropHandle = IntPtr.Zero;
        IntPtr effectHandle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            dropHandle = CreateDropFilesHandle(validPaths);
            if (PreferredDropEffectFormat != 0)
            {
                effectHandle = CreateDropEffectHandle(
                    cut ? DropEffectMove : DropEffectCopy);
            }

            if (SetClipboardData(CfHdrop, dropHandle) == IntPtr.Zero)
            {
                return false;
            }

            dropHandle = IntPtr.Zero;

            if (PreferredDropEffectFormat != 0 &&
                SetClipboardData(PreferredDropEffectFormat, effectHandle) ==
                    IntPtr.Zero)
            {
                return false;
            }

            effectHandle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (dropHandle != IntPtr.Zero)
            {
                GlobalFree(dropHandle);
            }

            if (effectHandle != IntPtr.Zero)
            {
                GlobalFree(effectHandle);
            }

            CloseClipboard();
        }
    }

    public static bool HasFileDropList()
    {
        return IsClipboardFormatAvailable(CfHdrop);
    }

    public static bool TryGetFileDropList(
        out string[] paths,
        out bool cut)
    {
        paths = [];
        cut = false;
        if (!HasFileDropList() || !TryOpenClipboard())
        {
            return false;
        }

        try
        {
            IntPtr dropHandle = GetClipboardData(CfHdrop);
            if (dropHandle == IntPtr.Zero)
            {
                return false;
            }

            uint count = DragQueryFile(
                dropHandle,
                DragQueryFileCount,
                null,
                0);
            if (count == 0 || count > int.MaxValue)
            {
                return false;
            }

            var result = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                uint length = DragQueryFile(dropHandle, index, null, 0);
                if (length == 0 || length >= int.MaxValue)
                {
                    continue;
                }

                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFile(
                        dropHandle,
                        index,
                        buffer,
                        (uint)buffer.Capacity) == 0)
                {
                    continue;
                }

                string path = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(path);
                }
            }

            paths = result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cut = ReadPreferredDropEffect() is uint effect &&
                (effect & DropEffectMove) != 0;
            return paths.Length > 0;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static IntPtr CreateDropFilesHandle(IReadOnlyList<string> paths)
    {
        byte[] payload = CreateDropFilesPayload(paths);
        IntPtr handle = GlobalAlloc(
            GmemMoveable | GmemZeroinit,
            (nuint)payload.Length);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static byte[] CreateDropFilesPayload(IReadOnlyList<string> paths)
    {
        string pathList = string.Join('\0', paths) + "\0\0";
        byte[] pathBytes = Encoding.Unicode.GetBytes(pathList);
        byte[] payload = new byte[DropFilesHeaderSize + pathBytes.Length];

        // DROPFILES is fixed at 20 bytes: DWORD pFiles, POINT, BOOL fNC,
        // BOOL fWide. Writing the native layout explicitly avoids runtime
        // bool-marshalling differences that can truncate a multi-file list.
        BitConverter.GetBytes((uint)DropFilesHeaderSize).CopyTo(payload, 0);
        BitConverter.GetBytes(1).CopyTo(payload, 16);
        pathBytes.CopyTo(payload, DropFilesHeaderSize);
        return payload;
    }

    private static uint? ReadPreferredDropEffect()
    {
        if (PreferredDropEffectFormat == 0 ||
            !IsClipboardFormatAvailable(PreferredDropEffectFormat))
        {
            return null;
        }

        IntPtr effectHandle = GetClipboardData(PreferredDropEffectFormat);
        if (effectHandle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr pointer = GlobalLock(effectHandle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return unchecked((uint)Marshal.ReadInt32(pointer));
        }
        finally
        {
            GlobalUnlock(effectHandle);
        }
    }

    private static bool TryOpenClipboard()
    {
        for (int attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (attempt + 1 < ClipboardOpenAttempts)
            {
                Thread.Sleep(ClipboardOpenRetryDelayMs);
            }
        }

        return false;
    }

    private static IntPtr CreateDropEffectHandle(uint effect)
    {
        IntPtr handle = GlobalAlloc(GmemMoveable | GmemZeroinit, sizeof(uint));
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            Marshal.WriteInt32(pointer, unchecked((int)effect));
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static string Localize(string key)
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService?.T(key) ?? key;
        }
        catch
        {
            return key;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memoryHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(
        IntPtr dropHandle,
        uint fileIndex,
        StringBuilder? fileName,
        uint fileNameLength);
}

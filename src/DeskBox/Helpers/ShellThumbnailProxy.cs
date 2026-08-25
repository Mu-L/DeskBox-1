using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves third-party Explorer thumbnails in a short-lived native process.
/// No thumbnail handler DLL is ever loaded into the DeskBox process, and a
/// hung or crashing handler is contained by the per-request timeout.
/// </summary>
internal static class ShellThumbnailProxy
{
    internal const string ExecutableName = "DeskBox.ThumbnailProxy.exe";
    private const string ThumbnailHandlerClassId =
        "{e357fccd-a995-4576-b01f-234630154e96}";
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumFailureEntries = 256;
    private const long FailureRetryDelayMilliseconds = 30_000;
    private static readonly TimeSpan ExtractionTimeout =
        TimeSpan.FromMilliseconds(2500);
    private static readonly ConcurrentDictionary<string, bool>
        s_registeredProviderByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long>
        s_recentFailures = new(StringComparer.OrdinalIgnoreCase);
    private static int s_missingExecutableLogged;

    public static Task<bool> HasRegisteredThumbnailProviderAsync(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension) ||
            IsExcludedExtension(extension))
        {
            return Task.FromResult(false);
        }

        if (s_registeredProviderByExtension.TryGetValue(
                extension,
                out bool cached))
        {
            return Task.FromResult(cached);
        }

        return Task.Run(() => s_registeredProviderByExtension.GetOrAdd(
            extension,
            QueryRegisteredThumbnailProvider));
    }

    public static async Task<byte[]?> TryLoadAsync(
        string path,
        int requestedSize)
    {
        string normalizedPath = NormalizePath(path);
        if (IsRecentFailure(normalizedPath))
        {
            return null;
        }

        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            ExecutableName);
        if (!File.Exists(executablePath))
        {
            if (Interlocked.Exchange(ref s_missingExecutableLogged, 1) == 0)
            {
                App.Log(
                    $"[ShellThumbnailProxy] Native proxy is missing: " +
                    $"{executablePath}");
            }

            RecordFailure(normalizedPath);
            return null;
        }

        int normalizedSize = Math.Clamp(requestedSize, 24, 512);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(normalizedPath);
        startInfo.ArgumentList.Add(normalizedSize.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                RecordFailure(normalizedPath);
                return null;
            }
        }
        catch (Exception ex)
        {
            RecordFailure(normalizedPath);
            App.Log(
                $"[ShellThumbnailProxy] Start failed path={normalizedPath}: " +
                ex.Message);
            return null;
        }

        Task<byte[]> outputTask = ReadBoundedOutputAsync(
            process.StandardOutput.BaseStream,
            MaximumPayloadBytes);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(
            ExtractionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            await ObserveOutputAsync(outputTask, errorTask);
            RecordFailure(normalizedPath);
            App.Log(
                $"[ShellThumbnailProxy] Extraction timed out " +
                $"timeoutMs={ExtractionTimeout.TotalMilliseconds:0} " +
                $"path={normalizedPath}");
            return null;
        }

        byte[] output;
        string error;
        try
        {
            output = await outputTask;
            error = await errorTask;
        }
        catch (Exception ex)
        {
            TryKill(process);
            RecordFailure(normalizedPath);
            App.Log(
                $"[ShellThumbnailProxy] Invalid proxy output " +
                $"path={normalizedPath}: {ex.Message}");
            return null;
        }

        if (process.ExitCode != 0 || !IsBitmapPayload(output))
        {
            RecordFailure(normalizedPath);
            App.LogVerbose(
                $"[ShellThumbnailProxy] No thumbnail exit={process.ExitCode} " +
                $"path={normalizedPath} error={error.Trim()}");
            return null;
        }

        s_recentFailures.TryRemove(normalizedPath, out _);
        return output;
    }

    public static void Invalidate(string path)
    {
        s_recentFailures.TryRemove(NormalizePath(path), out _);
    }

    public static void ClearTransientFailures()
    {
        s_recentFailures.Clear();
    }

    private static bool QueryRegisteredThumbnailProvider(string extension)
    {
        try
        {
            using RegistryKey? extensionKey =
                Registry.ClassesRoot.OpenSubKey(extension);
            string? programmaticId = extensionKey?.GetValue(null) as string;
            string? perceivedType = extensionKey?.GetValue(
                "PerceivedType") as string;

            return HasHandler(extension) ||
                HasHandler($"SystemFileAssociations\\{extension}") ||
                (!string.IsNullOrWhiteSpace(programmaticId) &&
                 HasHandler(programmaticId)) ||
                (!string.IsNullOrWhiteSpace(perceivedType) &&
                 HasHandler($"SystemFileAssociations\\{perceivedType}"));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasHandler(string classPath)
    {
        using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(
            $"{classPath}\\ShellEx\\{ThumbnailHandlerClassId}");
        return key?.GetValue(null) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsExcludedExtension(string extension) =>
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".url", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedOutputAsync(
        Stream stream,
        int maximumBytes)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The thumbnail proxy payload exceeded its limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read));
        }

        return output.ToArray();
    }

    private static bool IsBitmapPayload(byte[] bytes)
    {
        if (bytes.Length < 138 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return false;
        }

        uint declaredSize = BitConverter.ToUInt32(bytes, 2);
        uint pixelOffset = BitConverter.ToUInt32(bytes, 10);
        return declaredSize == bytes.Length &&
            pixelOffset >= 54 &&
            pixelOffset < bytes.Length;
    }

    private static bool IsRecentFailure(string path)
    {
        if (!s_recentFailures.TryGetValue(path, out long failedAt))
        {
            return false;
        }

        if (Environment.TickCount64 - failedAt < FailureRetryDelayMilliseconds)
        {
            return true;
        }

        s_recentFailures.TryRemove(path, out _);
        return false;
    }

    private static void RecordFailure(string path)
    {
        if (s_recentFailures.Count >= MaximumFailureEntries &&
            !s_recentFailures.ContainsKey(path))
        {
            s_recentFailures.Clear();
        }

        s_recentFailures[path] = Environment.TickCount64;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task ObserveOutputAsync(
        Task<byte[]> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch
        {
        }
    }
}

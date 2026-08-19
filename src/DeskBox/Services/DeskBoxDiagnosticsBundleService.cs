using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record DeskBoxDiagnosticRect(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record DeskBoxDisplayDiagnostic(
    int Number,
    bool IsPrimary,
    double DpiScale,
    DeskBoxDiagnosticRect MonitorBounds,
    DeskBoxDiagnosticRect WorkAreaBounds);

public sealed record DeskBoxWidgetHostDiagnostic(
    int Number,
    WidgetKind WidgetKind,
    string HostKind,
    bool IsGroupSurface,
    bool Visible,
    bool Raised,
    bool Compact,
    bool ImportBusy,
    long? ImportBusyElapsedMilliseconds,
    DeskBoxDiagnosticRect AnimationBounds,
    DeskBoxDiagnosticRect RestingBounds);

public sealed record DeskBoxTrayQueueDiagnostic(
    int PendingCount,
    bool WorkerRunning,
    long TotalRequests,
    long EffectiveToggles,
    long FoldedNoOpBatches,
    string? LastSource,
    bool LastRequestFailed);

public sealed record DeskBoxFileHostDiagnostic(
    string Strategy,
    bool LegacyFallbackAvailable,
    long TotalStandaloneCreationCount,
    long UnifiedStandaloneCreationCount,
    long LegacyStandaloneCreationCount,
    double? UnifiedStandaloneUsagePercent,
    int LoadedStandaloneCount,
    int LoadedGroupedCount,
    int FallbackRequestCount,
    string? LastFallbackReason);

public sealed record DeskBoxWidgetManagerDiagnostic(
    bool WidgetsRaisedFromTray,
    string SessionState,
    bool InteractionActive,
    int LoadedSurfaceCount,
    int VisibleSurfaceCount,
    DeskBoxFileHostDiagnostic FileHosts,
    DeskBoxTrayQueueDiagnostic ToggleQueue,
    IReadOnlyList<DeskBoxWidgetHostDiagnostic> Hosts)
{
    public static DeskBoxWidgetManagerDiagnostic Empty { get; } = new(
        false,
        "Unavailable",
        false,
        0,
        0,
        new DeskBoxFileHostDiagnostic(
            "Unavailable",
            false,
            0,
            0,
            0,
            null,
            0,
            0,
            0,
            null),
        new DeskBoxTrayQueueDiagnostic(0, false, 0, 0, 0, null, false),
        []);
}

public sealed record DeskBoxSettingsDiagnostic(
    SettingsLoadRecoveryState LoadRecoveryState,
    bool HasPendingSave,
    string? LastSaveFailureOperation,
    DateTimeOffset? LastSaveFailureAtUtc);

public sealed record DeskBoxHotkeyDiagnostic(
    bool ToggleEnabled,
    bool ToggleRegistered,
    int ToggleModifiers,
    int ToggleVirtualKey,
    long ToggleReceivedCount,
    long ToggleInvocationCount,
    long ToggleDispatchFailureCount,
    bool ToggleUsesReservedHook,
    uint ToggleReservedHookThreadId,
    int ToggleReservedHookLastErrorCode,
    long ToggleReservedHookTriggerCount,
    long ToggleReservedHookPostFailureCount,
    long ToggleReservedHookInputFailureCount,
    bool ToggleRegistrationFailed,
    bool SearchEnabled,
    bool SearchRegistered,
    int SearchModifiers,
    int SearchVirtualKey);

public sealed record DeskBoxDiagnosticSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    string DistributionChannel,
    bool IsPackaged,
    string OperatingSystem,
    string ProcessArchitecture,
    string UiCulture,
    DeskBoxHotkeyDiagnostic Hotkeys,
    DeskBoxSettingsDiagnostic Settings,
    AppRuntimeHealthSnapshot? RuntimeHealth,
    DeskBoxWidgetManagerDiagnostic WidgetManager,
    IReadOnlyList<DeskBoxDisplayDiagnostic> Displays);

/// <summary>
/// Exports a deliberately narrow, privacy-filtered support package. It never
/// includes settings.json, widget content stores, file inventories, or user
/// attachment data.
/// </summary>
public sealed partial class DeskBoxDiagnosticsBundleService
{
    private const int MaximumLogTailBytes = 2 * 1024 * 1024;
    private const string ReadmeText = """
        DeskBox diagnostic package

        This archive contains a runtime snapshot and a sanitized tail of the application log.
        It does not contain settings.json, widget contents, file lists, clipboard records, tasks, or attachments.
        Paths, account names, email addresses, and Windows security identifiers are automatically redacted.
        Please review the archive before sharing it with support.

        DeskBox 诊断包

        本压缩包只包含运行状态快照和经过脱敏的应用日志末尾，不包含设置文件、格子内容、文件列表、剪贴板记录、待办或附件。
        路径、账户名、电子邮箱和 Windows 安全标识符会自动隐藏。发送给支持人员前仍建议快速检查一次。
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex(
        "(?imx)\\b([a-z0-9_]*(?:path|folder|root|directory|dir|file|exe|commandline)[a-z0-9_]*)\\s*=\\s*(?:'[^'\\r\\n]*'|\"[^\"\\r\\n]*\"|[^\\r\\n]*?)(?=\\s+[a-z0-9_]+\\s*=|\\r?$)")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex("(?i)(?<quote>['\"])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\r\\n]*?\\k<quote>")]
    private static partial Regex QuotedWindowsPathRegex();

    [GeneratedRegex("(?i)(?<![\\w])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\s'\",;)\\]]+")]
    private static partial Regex UnquotedWindowsPathRegex();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bS-1-(?:\d+-){1,14}\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityIdentifierRegex();

    public async Task<string> ExportAsync(
        string destinationDirectory,
        DeskBoxDiagnosticSnapshot snapshot,
        string? logFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(destinationDirectory);
        string archivePath = GetAvailableArchivePath(destinationDirectory, snapshot.GeneratedAtUtc);
        string temporaryPath = archivePath + ".tmp";

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonEntryAsync(archive, snapshot, cancellationToken);
                await WriteTextEntryAsync(archive, "README.txt", ReadmeText, cancellationToken);

                string sanitizedLog = await ReadSanitizedLogTailAsync(logFilePath, cancellationToken);
                await WriteTextEntryAsync(
                    archive,
                    "DeskBox-sanitized.log",
                    sanitizedLog,
                    cancellationToken);
            }

            File.Move(temporaryPath, archivePath);
            return archivePath;
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static string SanitizeLog(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string sanitized = SensitiveAssignmentRegex().Replace(
            value,
            match => $"{match.Groups[1].Value}=<REDACTED>");
        sanitized = QuotedWindowsPathRegex().Replace(sanitized, "'<PATH>'");
        sanitized = UnquotedWindowsPathRegex().Replace(sanitized, "<PATH>");
        sanitized = EmailRegex().Replace(sanitized, "<EMAIL>");
        sanitized = SecurityIdentifierRegex().Replace(sanitized, "<SID>");

        foreach (string accountValue in GetAccountValues())
        {
            sanitized = sanitized.Replace(
                accountValue,
                "<USER>",
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive,
        DeskBoxDiagnosticSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static async Task<string> ReadSanitizedLogTailAsync(
        string? logFilePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
        {
            return "DeskBox log was not available when the package was created.";
        }

        await using var stream = new FileStream(
            logFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long start = Math.Max(0, stream.Length - MaximumLogTailBytes);
        stream.Position = start;
        byte[] buffer = new byte[checked((int)(stream.Length - start))];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        string log = Encoding.UTF8.GetString(buffer, 0, totalRead);
        if (start > 0)
        {
            int firstLineBreak = log.IndexOf('\n');
            log = firstLineBreak >= 0 ? log[(firstLineBreak + 1)..] : string.Empty;
        }

        return SanitizeLog(log);
    }

    private static IEnumerable<string> GetAccountValues()
    {
        string[] candidates =
        [
            Environment.UserName,
            Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty,
            Environment.GetEnvironmentVariable("USERDOMAIN") ?? string.Empty
        ];

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length);
    }

    private static string GetAvailableArchivePath(
        string destinationDirectory,
        DateTimeOffset generatedAtUtc)
    {
        string stem = $"DeskBox-Diagnostics-{generatedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}";
        string path = Path.Combine(destinationDirectory, stem + ".zip");
        for (int suffix = 2; File.Exists(path) || File.Exists(path + ".tmp"); suffix++)
        {
            path = Path.Combine(destinationDirectory, $"{stem}-{suffix}.zip");
        }

        return path;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

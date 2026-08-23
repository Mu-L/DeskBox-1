using System.IO.Compression;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DeskBoxDiagnosticsBundleServiceTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxDiagnosticsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAsync_CreatesNarrowSanitizedSupportArchive()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string logPath = Path.Combine(_temporaryRoot, "DeskBox.log");
        string userName = Environment.UserName;
        string privatePath = $@"C:\Users\{userName}\Private Folder\secret-file.txt";
        await File.WriteAllTextAsync(
            logPath,
            $"[TrayToggle] path='{privatePath}' user={userName} email=user@example.com SID=S-1-5-21-123-456-789-1001\n" +
            $"[Hotkey] executable={privatePath}\n");
        var service = new DeskBoxDiagnosticsBundleService();

        string archivePath = await service.ExportAsync(
            _temporaryRoot,
            CreateSnapshot(),
            logPath);

        Assert.True(File.Exists(archivePath));
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string[] entryNames = archive.Entries
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "DeskBox-sanitized.log", "README.txt", "diagnostics.json" }
                .Order(StringComparer.Ordinal),
            entryNames);

        string log = await ReadEntryAsync(archive, "DeskBox-sanitized.log");
        Assert.DoesNotContain(userName, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-file.txt", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user@example.com", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-21", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<REDACTED>", log, StringComparison.Ordinal);

        string diagnostics = await ReadEntryAsync(archive, "diagnostics.json");
        Assert.Contains("\"schemaVersion\": 5", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"loadRecoveryState\": \"Primary\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"shortcutNative\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"moduleName\": \"deskbox_native.dll\"", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"toggleReservedHookThreadId\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"toggleReservedHookInputFailureCount\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"widgetManager\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"fileHosts\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"strategy\": \"Unavailable\"", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.json", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeLog_RemovesUnquotedPathsAndAccountIdentifiers()
    {
        string source = $"open C:\\Users\\{Environment.UserName}\\private.txt " +
                        "contact me@example.com S-1-5-18";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.DoesNotContain("private.txt", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("me@example.com", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-18", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    private static DeskBoxDiagnosticSnapshot CreateSnapshot()
    {
        return new DeskBoxDiagnosticSnapshot(
            5,
            new DateTimeOffset(2026, 8, 5, 12, 30, 0, TimeSpan.Zero),
            "1.3.6",
            "Direct",
            false,
            "Windows",
            "X64",
            "zh-CN",
            new DeskBoxHotkeyDiagnostic(
                true,
                true,
                3,
                68,
                4,
                4,
                0,
                false,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                false,
                3,
                70),
            new DeskBoxSettingsDiagnostic(
                SettingsLoadRecoveryState.Primary,
                false,
                null,
                null),
            new DeskBoxShortcutNativeDiagnostic(
                "CSharp",
                "deskbox_native.dll",
                true,
                "X64",
                new string('A', 64),
                false,
                "NotProbed",
                null,
                null),
            null,
            DeskBoxWidgetManagerDiagnostic.Empty,
            [
                new DeskBoxDisplayDiagnostic(
                    1,
                    true,
                    1.5,
                    new DeskBoxDiagnosticRect(0, 0, 1920, 1080),
                    new DeskBoxDiagnosticRect(0, 0, 1920, 1040))
            ]);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            item => string.Equals(item.FullName, name, StringComparison.Ordinal));
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}

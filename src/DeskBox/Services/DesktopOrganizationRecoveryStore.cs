using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _journalPath;

    public DesktopOrganizationRecoveryStore(string? journalPath = null)
    {
        _journalPath = journalPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "desktop-organization-recovery.json");
    }

    public bool HasPendingJournal => File.Exists(_journalPath);

    public async Task<DesktopOrganizationRecoveryJournal?> LoadAsync()
    {
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(_journalPath);
        return JsonSerializer.Deserialize<DesktopOrganizationRecoveryJournal>(json, JsonOptions);
    }

    public async Task SaveAsync(DesktopOrganizationRecoveryJournal journal)
    {
        string? directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_journalPath}.tmp";
        string json = JsonSerializer.Serialize(journal, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, _journalPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }
}

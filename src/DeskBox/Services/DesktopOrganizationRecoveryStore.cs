using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(DesktopOrganizationRecoveryJournal),
    TypeInfoPropertyName = "RecoveryJournal")]
internal sealed partial class DesktopRecoveryJsonContext : JsonSerializerContext
{
}

public sealed class DesktopOrganizationRecoveryStore
{
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
        return JsonSerializer.Deserialize(
            json,
            DesktopRecoveryJsonContext.Default.RecoveryJournal);
    }

    public async Task SaveAsync(DesktopOrganizationRecoveryJournal journal)
    {
        string? directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_journalPath}.tmp";
        string json = JsonSerializer.Serialize(
            journal,
            DesktopRecoveryJsonContext.Default.RecoveryJournal);
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

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record TodoWorkspaceMigrationResult(int ImportedSources, int ImportedTasks, string? BackupDirectory);

public sealed class TodoWorkspaceMigrator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITodoWorkspaceRepository _repository;
    private readonly SettingsService _settingsService;
    private readonly string _legacyWidgetsRoot;
    private readonly string _backupRoot;

    public TodoWorkspaceMigrator(
        ITodoWorkspaceRepository repository,
        SettingsService settingsService)
        : this(
            repository,
            settingsService,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "data",
                "widgets"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "data",
                "todo",
                "legacy-backups"))
    {
    }

    internal TodoWorkspaceMigrator(
        ITodoWorkspaceRepository repository,
        SettingsService settingsService,
        string legacyWidgetsRoot,
        string backupRoot)
    {
        _repository = repository;
        _settingsService = settingsService;
        _legacyWidgetsRoot = Path.GetFullPath(legacyWidgetsRoot);
        _backupRoot = Path.GetFullPath(backupRoot);
    }

    public async Task<TodoWorkspaceMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_legacyWidgetsRoot))
        {
            return new TodoWorkspaceMigrationResult(0, 0, null);
        }

        string[] sourceFiles = Directory.EnumerateFiles(
                _legacyWidgetsRoot,
                "todo.json",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            return new TodoWorkspaceMigrationResult(0, 0, null);
        }

        string? backupDirectory = null;
        int importedSources = 0;
        int importedTasks = 0;
        var batchTasks = new List<TodoTask>();
        var batchSources = new List<TodoMigrationSource>();
        TodoWorkspaceSnapshot existing = await _repository.LoadSnapshotAsync(true, cancellationToken);
        var existingIds = new HashSet<string>(existing.Tasks.Select(task => task.Id), StringComparer.Ordinal);

        foreach (string sourcePath in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] sourceBytes;
            try
            {
                sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            }
            catch (Exception ex)
            {
                App.Log($"[TodoMigration] Failed to read '{sourcePath}': {ex.Message}");
                continue;
            }

            string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            if (await _repository.HasMigrationSourceAsync(hash, cancellationToken))
            {
                continue;
            }

            TodoWidgetData? data;
            try
            {
                data = JsonSerializer.Deserialize<TodoWidgetData>(sourceBytes, s_jsonOptions);
            }
            catch (Exception ex)
            {
                App.Log($"[TodoMigration] Invalid Todo source '{sourcePath}': {ex.Message}");
                continue;
            }

            if (data is null)
            {
                continue;
            }

            backupDirectory ??= Path.Combine(
                _backupRoot,
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            string widgetDirectory = Path.GetDirectoryName(sourcePath)!;
            string widgetId = Path.GetFileName(widgetDirectory);
            CopyDirectory(widgetDirectory, Path.Combine(backupDirectory, widgetId));

            var imported = new List<TodoTask>();
            foreach (TodoItem item in data.Items ?? [])
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Text))
                {
                    continue;
                }

                TodoTask task = TodoTaskMapper.FromLegacy(item, TodoWorkspaceDefaults.InboxListId);
                if (!existingIds.Add(task.Id))
                {
                    task.Id = Guid.NewGuid().ToString("N");
                }

                MigrateManagedAttachments(task, widgetDirectory);
                imported.Add(task);
            }

            if (imported.Count > 0)
            {
                batchTasks.AddRange(imported);
                importedTasks += imported.Count;
            }

            batchSources.Add(new TodoMigrationSource(sourcePath, hash));
            importedSources++;
        }

        await _repository.ImportMigrationBatchAsync(batchTasks, batchSources, cancellationToken);

        if (importedSources > 0)
        {
            App.Log($"[TodoMigration] Imported {importedTasks} tasks from {importedSources} source(s). Backup='{backupDirectory}'.");
        }

        return new TodoWorkspaceMigrationResult(importedSources, importedTasks, backupDirectory);
    }

    private void MigrateManagedAttachments(TodoTask task, string widgetDirectory)
    {
        foreach (TodoAttachment attachment in task.Attachments.Where(attachment => attachment.IsManagedCopy))
        {
            string sourcePath = attachment.FilePath;
            if (!Path.IsPathFullyQualified(sourcePath))
            {
                sourcePath = Path.GetFullPath(Path.Combine(widgetDirectory, sourcePath));
            }

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            try
            {
                string taskDirectory = Path.Combine(_repository.AttachmentDirectory, task.Id);
                Directory.CreateDirectory(taskDirectory);
                string fileName = Path.GetFileName(sourcePath);
                string destination = GetAvailableDestination(taskDirectory, fileName);
                File.Copy(sourcePath, destination, overwrite: false);
                attachment.FilePath = destination;
            }
            catch (Exception ex)
            {
                App.Log($"[TodoMigration] Attachment copy failed '{sourcePath}': {ex.Message}");
            }
        }
    }

    private static string GetAvailableDestination(string directory, string fileName)
    {
        string destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            destination = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(destination))
            {
                return destination;
            }
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string child in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(child, Path.Combine(destinationDirectory, Path.GetFileName(child)));
        }
    }
}

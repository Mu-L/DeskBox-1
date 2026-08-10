using System.Globalization;
using System.Text.Json;
using DeskBox.Models;
using Microsoft.Data.Sqlite;

namespace DeskBox.Services;

public sealed class SqliteTodoWorkspaceRepository : ITodoWorkspaceRepository
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _connectionString;
    private bool _initialized;
    private bool _disposed;

    public SqliteTodoWorkspaceRepository()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "data",
            "todo"))
    {
    }

    internal SqliteTodoWorkspaceRepository(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string fullRoot = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(fullRoot);
        DatabasePath = Path.Combine(fullRoot, "todo.db");
        AttachmentDirectory = Path.Combine(fullRoot, "attachments");
        Directory.CreateDirectory(AttachmentDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public event EventHandler<TodoWorkspaceChangedEventArgs>? Changed;

    public string DatabasePath { get; }

    public string AttachmentDirectory { get; }

    internal static async Task<bool> ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(
                    Convert.ToString(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture),
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            await using SqliteCommand schema = connection.CreateCommand();
            schema.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='todo_tasks' LIMIT 1;";
            return await schema.ExecuteScalarAsync(cancellationToken) is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                await InitializeDatabaseCoreAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                string quarantinedPath = QuarantineCorruptDatabase();
                App.Log($"[TodoWorkspace] Corrupt database quarantined at '{quarantinedPath}': {ex.Message}");
                await InitializeDatabaseCoreAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }

        RaiseChanged(TodoWorkspaceChangeKind.Initialized);
    }

    private async Task InitializeDatabaseCoreAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        int version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Todo database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }

        await SeedSystemListsAsync(connection, null, cancellationToken);
        if (version < 2)
        {
            await MigrateToInboxOnlyAsync(connection, cancellationToken);
        }

        await using SqliteCommand setVersion = connection.CreateCommand();
        setVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        await setVersion.ExecuteNonQueryAsync(cancellationToken);
    }

    private string QuarantineCorruptDatabase()
    {
        SqliteConnection.ClearAllPools();
        string suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string quarantinedPath = Path.Combine(
            Path.GetDirectoryName(DatabasePath)!,
            $"todo-corrupt-{suffix}.db");
        if (File.Exists(DatabasePath))
        {
            File.Move(DatabasePath, quarantinedPath, overwrite: false);
        }
        foreach (string sidecarSuffix in new[] { "-wal", "-shm" })
        {
            string sidecar = DatabasePath + sidecarSuffix;
            if (File.Exists(sidecar))
            {
                File.Move(sidecar, quarantinedPath + sidecarSuffix, overwrite: false);
            }
        }
        return quarantinedPath;
    }

    public async Task<TodoWorkspaceSnapshot> LoadSnapshotAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        var snapshot = new TodoWorkspaceSnapshot();
        var tasksById = new Dictionary<string, TodoTask>(StringComparer.Ordinal);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = includeDeleted
                ? SelectTasksSql
                : $"{SelectTasksSql} WHERE deleted_at IS NULL";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                TodoTask task = ReadTask(reader);
                tasksById.Add(task.Id, task);
                snapshot.Tasks.Add(task);
            }
        }

        if (tasksById.Count > 0)
        {
            await LoadStepsAsync(connection, tasksById, cancellationToken);
            await LoadAttachmentsAsync(connection, tasksById, cancellationToken);
            await LoadTaskTagsAsync(connection, tasksById, cancellationToken);
            await LoadRemindersAsync(connection, tasksById, cancellationToken);
            await LoadRecurrenceRulesAsync(connection, tasksById, cancellationToken);
        }

        await LoadListsAsync(connection, snapshot.Lists, cancellationToken);
        await LoadSectionsAsync(connection, snapshot.Sections, cancellationToken);
        await LoadTagsAsync(connection, snapshot.Tags, cancellationToken);
        await LoadSavedViewsAsync(connection, snapshot.SavedViews, cancellationToken);
        await LoadRecurrenceExceptionsAsync(connection, snapshot.RecurrenceExceptions, cancellationToken);
        return snapshot;
    }

    public async Task<TodoTask?> GetTaskAsync(
        string taskId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        TodoWorkspaceSnapshot snapshot = await LoadSnapshotAsync(includeDeleted, cancellationToken);
        return snapshot.Tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
    }

    public async Task UpsertTaskAsync(TodoTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await UpsertTaskCoreAsync(connection, transaction, task, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, [task.Id]);
    }

    public async Task ReplaceTasksAsync(
        IReadOnlyCollection<TodoTask> tasks,
        bool softDeleteMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        var changedIds = new HashSet<string>(tasks.Select(task => task.Id), StringComparer.Ordinal);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (TodoTask task in tasks)
            {
                await UpsertTaskCoreAsync(connection, transaction, task, cancellationToken);
            }

            if (softDeleteMissing)
            {
                await using SqliteCommand select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM todo_tasks WHERE deleted_at IS NULL;";
                var missingIds = new List<string>();
                await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        string id = reader.GetString(0);
                        if (!changedIds.Contains(id))
                        {
                            missingIds.Add(id);
                        }
                    }
                }

                foreach (string id in missingIds)
                {
                    await using SqliteCommand delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "UPDATE todo_tasks SET deleted_at = $deletedAt, updated_at = $deletedAt WHERE id = $id;";
                    delete.Parameters.AddWithValue("$deletedAt", FormatDateTime(DateTimeOffset.UtcNow));
                    delete.Parameters.AddWithValue("$id", id);
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                    changedIds.Add(id);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, changedIds);
    }

    public Task<bool> SoftDeleteTaskAsync(
        string taskId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default) =>
        UpdateDeletedAtAsync(taskId, deletedAt, cancellationToken);

    public Task<bool> RestoreTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        UpdateDeletedAtAsync(taskId, null, cancellationToken);

    public async Task<int> SetTasksDeletedAtAsync(
        IReadOnlyCollection<string> taskIds,
        DateTimeOffset? deletedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        string[] ids = taskIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        var changedIds = new List<string>(ids.Length);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            string updatedAt = FormatDateTime(DateTimeOffset.UtcNow);
            foreach (string id in ids)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE todo_tasks SET deleted_at = $deletedAt, updated_at = $updatedAt WHERE id = $id;";
                command.Parameters.AddWithValue(
                    "$deletedAt",
                    DbValue(deletedAt is null ? null : FormatDateTime(deletedAt.Value)));
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.Parameters.AddWithValue("$id", id);
                if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    changedIds.Add(id);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        if (changedIds.Count > 0)
        {
            RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, changedIds);
        }

        return changedIds.Count;
    }

    public async Task<int> PurgeDeletedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        int affected;
        var managedAttachmentPaths = new List<string>();
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            var taskIds = new List<string>();
            await using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id FROM todo_tasks WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;";
                select.Parameters.AddWithValue("$cutoff", FormatDateTime(cutoff));
                await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    taskIds.Add(reader.GetString(0));
                }
            }
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (string taskId in taskIds)
            {
                managedAttachmentPaths.AddRange(await GetManagedAttachmentPathsForPurgeAsync(
                    connection,
                    transaction,
                    taskId,
                    cancellationToken));
                await PurgeTaskCoreAsync(connection, transaction, taskId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            affected = taskIds.Count;
        }
        finally
        {
            _writeGate.Release();
        }

        DeleteManagedAttachmentFiles(managedAttachmentPaths);

        if (affected > 0)
        {
            RaiseChanged(TodoWorkspaceChangeKind.TasksChanged);
        }

        return affected;
    }

    public async Task<bool> PurgeTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        int affected;
        var managedAttachmentPaths = new List<string>();
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            managedAttachmentPaths.AddRange(await GetManagedAttachmentPathsForPurgeAsync(
                connection,
                transaction,
                taskId.Trim(),
                cancellationToken));
            affected = await PurgeTaskCoreAsync(connection, transaction, taskId.Trim(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        if (affected > 0)
        {
            DeleteManagedAttachmentFiles(managedAttachmentPaths);
        }

        if (affected > 0)
        {
            RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, [taskId]);
        }

        return affected > 0;
    }

    public async Task<int> PurgeTasksAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        string[] ids = taskIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        int affected = 0;
        var managedAttachmentPaths = new List<string>();
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (string id in ids)
            {
                List<string> candidatePaths = await GetManagedAttachmentPathsForPurgeAsync(
                    connection,
                    transaction,
                    id,
                    cancellationToken);
                int purged = await PurgeTaskCoreAsync(connection, transaction, id, cancellationToken);
                affected += purged;
                if (purged > 0)
                {
                    managedAttachmentPaths.AddRange(candidatePaths);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        DeleteManagedAttachmentFiles(managedAttachmentPaths);

        if (affected > 0)
        {
            RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, ids);
        }
        return affected;
    }

    public Task UpsertListAsync(TodoList list, CancellationToken cancellationToken = default) =>
        UpsertStructureAsync(
            """
            INSERT INTO todo_lists(id, name, color_marker, sort_rank, is_system, is_archived)
            VALUES($id, $name, $color, $rank, $system, $archived)
            ON CONFLICT(id) DO UPDATE SET name=$name, color_marker=$color, sort_rank=$rank,
                is_system=$system, is_archived=$archived;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NormalizeId(list.Id));
                command.Parameters.AddWithValue("$name", list.Name.Trim());
                command.Parameters.AddWithValue("$color", DbValue(TodoItem.NormalizeColorMarker(list.ColorMarker)));
                command.Parameters.AddWithValue("$rank", list.SortRank);
                command.Parameters.AddWithValue("$system", list.IsSystem ? 1 : 0);
                command.Parameters.AddWithValue("$archived", list.IsArchived ? 1 : 0);
            },
            cancellationToken);

    public Task UpsertSectionAsync(TodoSection section, CancellationToken cancellationToken = default) =>
        UpsertStructureAsync(
            """
            INSERT INTO todo_sections(id, list_id, name, sort_rank, is_archived)
            VALUES($id, $list, $name, $rank, $archived)
            ON CONFLICT(id) DO UPDATE SET list_id=$list, name=$name, sort_rank=$rank, is_archived=$archived;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NormalizeId(section.Id));
                command.Parameters.AddWithValue("$list", NormalizeListId(section.ListId));
                command.Parameters.AddWithValue("$name", section.Name.Trim());
                command.Parameters.AddWithValue("$rank", section.SortRank);
                command.Parameters.AddWithValue("$archived", section.IsArchived ? 1 : 0);
            },
            cancellationToken);

    public Task UpsertTagAsync(TodoTag tag, CancellationToken cancellationToken = default) =>
        UpsertStructureAsync(
            """
            INSERT INTO todo_tags(id, name, color_marker, sort_rank)
            VALUES($id, $name, $color, $rank)
            ON CONFLICT(id) DO UPDATE SET name=$name, color_marker=$color, sort_rank=$rank;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NormalizeId(tag.Id));
                command.Parameters.AddWithValue("$name", tag.Name.Trim());
                command.Parameters.AddWithValue("$color", DbValue(TodoItem.NormalizeColorMarker(tag.ColorMarker)));
                command.Parameters.AddWithValue("$rank", tag.SortRank);
            },
            cancellationToken);

    public async Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using (SqliteCommand links = connection.CreateCommand())
            {
                links.Transaction = transaction;
                links.CommandText = "DELETE FROM todo_task_tags WHERE tag_id = $id;";
                links.Parameters.AddWithValue("$id", tagId.Trim());
                await links.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (SqliteCommand tag = connection.CreateCommand())
            {
                tag.Transaction = transaction;
                tag.CommandText = "DELETE FROM todo_tags WHERE id = $id;";
                tag.Parameters.AddWithValue("$id", tagId.Trim());
                await tag.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseChanged(TodoWorkspaceChangeKind.StructureChanged);
    }

    public Task UpsertSavedViewAsync(TodoSavedView savedView, CancellationToken cancellationToken = default) =>
        UpsertStructureAsync(
            """
            INSERT INTO todo_saved_views(id, name, icon_glyph, sort_rank, query_json)
            VALUES($id, $name, $icon, $rank, $query)
            ON CONFLICT(id) DO UPDATE SET name=$name, icon_glyph=$icon, sort_rank=$rank, query_json=$query;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NormalizeId(savedView.Id));
                command.Parameters.AddWithValue("$name", savedView.Name.Trim());
                command.Parameters.AddWithValue("$icon", DbValue(savedView.IconGlyph));
                command.Parameters.AddWithValue("$rank", savedView.SortRank);
                command.Parameters.AddWithValue("$query", JsonSerializer.Serialize(savedView.Query, s_jsonOptions));
            },
            cancellationToken);

    public Task DeleteSavedViewAsync(string savedViewId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savedViewId);
        return UpsertStructureAsync(
            "DELETE FROM todo_saved_views WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", savedViewId.Trim()),
            cancellationToken);
    }

    public async Task UpsertRecurrenceExceptionAsync(
        TodoRecurrenceException exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            string seriesId = NormalizeId(exception.SeriesId);
            string occurrenceKey = exception.OccurrenceKey.Trim();
            string? previousTaskId = await GetExceptionTaskIdAsync(
                connection,
                transaction,
                seriesId,
                occurrenceKey,
                cancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO todo_recurrence_exceptions(series_id, occurrence_key, task_id, is_cancelled)
                    VALUES($series, $occurrence, $task, $cancelled)
                    ON CONFLICT(series_id, occurrence_key) DO UPDATE SET
                        task_id=$task, is_cancelled=$cancelled;
                    """;
                command.Parameters.AddWithValue("$series", seriesId);
                command.Parameters.AddWithValue("$occurrence", occurrenceKey);
                command.Parameters.AddWithValue("$task", DbValue(exception.TaskId));
                command.Parameters.AddWithValue("$cancelled", exception.IsCancelled ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(previousTaskId) &&
                !string.Equals(previousTaskId, exception.TaskId, StringComparison.Ordinal))
            {
                await DeleteUnreferencedExceptionTaskAsync(
                    connection,
                    transaction,
                    previousTaskId,
                    seriesId,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, [exception.SeriesId]);
    }

    public async Task DeleteRecurrenceExceptionsFromAsync(
        string seriesId,
        DateOnly fromDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            string normalizedSeriesId = seriesId.Trim();
            string fromKey = TodoRecurrenceExpansionService.BuildOccurrenceKey(normalizedSeriesId, fromDate);
            List<string> orphanTaskIds = await GetExceptionTaskIdsFromAsync(
                connection,
                transaction,
                normalizedSeriesId,
                fromKey,
                cancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM todo_recurrence_exceptions
                    WHERE series_id = $series AND occurrence_key >= $fromKey;
                    """;
                command.Parameters.AddWithValue("$series", normalizedSeriesId);
                command.Parameters.AddWithValue("$fromKey", fromKey);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (string taskId in orphanTaskIds)
            {
                await DeleteUnreferencedExceptionTaskAsync(
                    connection,
                    transaction,
                    taskId,
                    normalizedSeriesId,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, [seriesId]);
    }

    public async Task ApplyRecurrenceMutationAsync(
        IReadOnlyCollection<TodoTask> tasks,
        TodoRecurrenceException? exception = null,
        string? clearSeriesId = null,
        DateOnly? clearFromDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            var orphanTasks = new List<(string TaskId, string SeriesId)>();
            if (!string.IsNullOrWhiteSpace(clearSeriesId) && clearFromDate is { } clearDate)
            {
                string normalizedSeriesId = clearSeriesId.Trim();
                string fromKey = TodoRecurrenceExpansionService.BuildOccurrenceKey(normalizedSeriesId, clearDate);
                orphanTasks.AddRange((await GetExceptionTaskIdsFromAsync(
                        connection,
                        transaction,
                        normalizedSeriesId,
                        fromKey,
                        cancellationToken))
                    .Select(taskId => (taskId, normalizedSeriesId)));
            }
            if (exception is not null)
            {
                string normalizedSeriesId = NormalizeId(exception.SeriesId);
                string? previousTaskId = await GetExceptionTaskIdAsync(
                    connection,
                    transaction,
                    normalizedSeriesId,
                    exception.OccurrenceKey.Trim(),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(previousTaskId))
                {
                    orphanTasks.Add((previousTaskId, normalizedSeriesId));
                }
            }

            foreach (TodoTask task in tasks)
            {
                await UpsertTaskCoreAsync(connection, transaction, task, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(clearSeriesId) && clearFromDate is { } fromDate)
            {
                await using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = """
                    DELETE FROM todo_recurrence_exceptions
                    WHERE series_id = $series AND occurrence_key >= $fromKey;
                    """;
                clear.Parameters.AddWithValue("$series", clearSeriesId.Trim());
                clear.Parameters.AddWithValue("$fromKey", TodoRecurrenceExpansionService.BuildOccurrenceKey(clearSeriesId.Trim(), fromDate));
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            if (exception is not null)
            {
                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO todo_recurrence_exceptions(series_id, occurrence_key, task_id, is_cancelled)
                    VALUES($series, $occurrence, $task, $cancelled)
                    ON CONFLICT(series_id, occurrence_key) DO UPDATE SET
                        task_id=$task, is_cancelled=$cancelled;
                    """;
                insert.Parameters.AddWithValue("$series", NormalizeId(exception.SeriesId));
                insert.Parameters.AddWithValue("$occurrence", exception.OccurrenceKey.Trim());
                insert.Parameters.AddWithValue("$task", DbValue(exception.TaskId));
                insert.Parameters.AddWithValue("$cancelled", exception.IsCancelled ? 1 : 0);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach ((string taskId, string seriesId) in orphanTasks.Distinct())
            {
                await DeleteUnreferencedExceptionTaskAsync(
                    connection,
                    transaction,
                    taskId,
                    seriesId,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        string[] changedIds = tasks.Select(task => task.Id)
            .Append(exception?.SeriesId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, changedIds);
    }

    public async Task RemoveRecurrenceExceptionAsync(
        string seriesId,
        string occurrenceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceKey);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            string normalizedSeriesId = seriesId.Trim();
            string normalizedOccurrenceKey = occurrenceKey.Trim();
            string? taskId = await GetExceptionTaskIdAsync(
                connection,
                transaction,
                normalizedSeriesId,
                normalizedOccurrenceKey,
                cancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM todo_recurrence_exceptions
                    WHERE series_id = $series AND occurrence_key = $occurrence;
                    """;
                command.Parameters.AddWithValue("$series", normalizedSeriesId);
                command.Parameters.AddWithValue("$occurrence", normalizedOccurrenceKey);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                await DeleteUnreferencedExceptionTaskAsync(
                    connection,
                    transaction,
                    taskId,
                    normalizedSeriesId,
                    cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, [seriesId]);
    }

    public async Task<bool> HasMigrationSourceAsync(
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM todo_migration_sources WHERE source_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", sourceHash);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task RecordMigrationSourceAsync(
        string sourcePath,
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO todo_migration_sources(source_hash, source_path, imported_at)
                VALUES($hash, $path, $at)
                ON CONFLICT(source_hash) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$hash", sourceHash);
            command.Parameters.AddWithValue("$path", sourcePath);
            command.Parameters.AddWithValue("$at", FormatDateTime(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ImportMigrationBatchAsync(
        IReadOnlyCollection<TodoTask> tasks,
        IReadOnlyCollection<TodoMigrationSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(sources);
        if (tasks.Count == 0 && sources.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (TodoTask task in tasks)
            {
                await UpsertTaskCoreAsync(connection, transaction, task, cancellationToken);
            }
            foreach (TodoMigrationSource source in sources)
            {
                await using SqliteCommand marker = connection.CreateCommand();
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO todo_migration_sources(source_hash, source_path, imported_at)
                    VALUES($hash, $path, $at)
                    ON CONFLICT(source_hash) DO NOTHING;
                    """;
                marker.Parameters.AddWithValue("$hash", source.SourceHash);
                marker.Parameters.AddWithValue("$path", source.SourcePath);
                marker.Parameters.AddWithValue("$at", FormatDateTime(DateTimeOffset.UtcNow));
                await marker.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        RaiseChanged(TodoWorkspaceChangeKind.TasksChanged, tasks.Select(task => task.Id).ToArray());
    }

    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await InitializeAsync(cancellationToken);
        string fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteConnection source = await OpenConnectionAsync(cancellationToken);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullDestination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        bool committed = false;
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM todo_recurrence_exceptions;
                DELETE FROM todo_tasks;
                DELETE FROM todo_sections;
                DELETE FROM todo_tags;
                DELETE FROM todo_saved_views;
                DELETE FROM todo_lists WHERE is_system = 0;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SeedSystemListsAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        finally
        {
            _writeGate.Release();
        }

        if (committed)
        {
            ClearManagedAttachmentDirectory();
        }

        RaiseChanged(TodoWorkspaceChangeKind.Cleared);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializeGate.Dispose();
        _writeGate.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    private async Task<bool> UpdateDeletedAtAsync(
        string taskId,
        DateTimeOffset? deletedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return await SetTasksDeletedAtAsync([taskId], deletedAt, cancellationToken) > 0;
    }

    private static async Task<string?> GetExceptionTaskIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string seriesId,
        string occurrenceKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_id FROM todo_recurrence_exceptions
            WHERE series_id = $series AND occurrence_key = $occurrence;
            """;
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$occurrence", occurrenceKey);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> GetExceptionTaskIdsFromAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string seriesId,
        string fromKey,
        CancellationToken cancellationToken)
    {
        var taskIds = new List<string>();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_id FROM todo_recurrence_exceptions
            WHERE series_id = $series AND occurrence_key >= $fromKey AND task_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$fromKey", fromKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            taskIds.Add(reader.GetString(0));
        }
        return taskIds;
    }

    private static async Task DeleteUnreferencedExceptionTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM todo_tasks
            WHERE id = $task
              AND id <> $series
              AND NOT EXISTS(
                  SELECT 1 FROM todo_recurrence_exceptions WHERE task_id = $task
              );
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$series", seriesId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertStructureAsync(
        string sql,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseChanged(TodoWorkspaceChangeKind.StructureChanged);
    }

    private async Task UpsertTaskCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TodoTask task,
        CancellationToken cancellationToken)
    {
        NormalizeTask(task);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = UpsertTaskSql;
            BindTask(command, task);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceTaskChildrenAsync(connection, transaction, task, cancellationToken);
    }

    private static async Task<int> PurgeTaskCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand eligible = connection.CreateCommand())
        {
            eligible.Transaction = transaction;
            eligible.CommandText = "SELECT 1 FROM todo_tasks WHERE id = $id AND deleted_at IS NOT NULL;";
            eligible.Parameters.AddWithValue("$id", taskId);
            if (await eligible.ExecuteScalarAsync(cancellationToken) is null)
            {
                return 0;
            }
        }

        var exceptionTaskIds = new List<string>();
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT task_id FROM todo_recurrence_exceptions
                WHERE series_id = $id AND task_id IS NOT NULL;
                """;
            select.Parameters.AddWithValue("$id", taskId);
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                exceptionTaskIds.Add(reader.GetString(0));
            }
        }

        await using (SqliteCommand clearExceptions = connection.CreateCommand())
        {
            clearExceptions.Transaction = transaction;
            clearExceptions.CommandText = """
                DELETE FROM todo_recurrence_exceptions
                WHERE series_id = $id OR task_id = $id;
                """;
            clearExceptions.Parameters.AddWithValue("$id", taskId);
            await clearExceptions.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (string exceptionTaskId in exceptionTaskIds)
        {
            await using SqliteCommand deleteExceptionTask = connection.CreateCommand();
            deleteExceptionTask.Transaction = transaction;
            deleteExceptionTask.CommandText = "DELETE FROM todo_tasks WHERE id = $id;";
            deleteExceptionTask.Parameters.AddWithValue("$id", exceptionTaskId);
            await deleteExceptionTask.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand deleteTask = connection.CreateCommand();
        deleteTask.Transaction = transaction;
        deleteTask.CommandText = "DELETE FROM todo_tasks WHERE id = $id AND deleted_at IS NOT NULL;";
        deleteTask.Parameters.AddWithValue("$id", taskId);
        return await deleteTask.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> GetManagedAttachmentPathsForPurgeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT file_path FROM todo_attachments
            WHERE storage_mode = $managed
              AND (task_id = $task OR task_id IN (
                  SELECT task_id FROM todo_recurrence_exceptions
                  WHERE series_id = $task AND task_id IS NOT NULL
              ));
            """;
        command.Parameters.AddWithValue("$managed", TodoAttachment.ManagedStorageMode);
        command.Parameters.AddWithValue("$task", taskId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }
        return paths;
    }

    private void DeleteManagedAttachmentFiles(IEnumerable<string> paths)
    {
        string root = Path.GetFullPath(AttachmentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (string candidate in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!IsPathInsideManagedRoot(fullPath, root) || HasReparsePointParent(fullPath, root))
                {
                    App.Log($"[TodoWorkspace] Refused to delete managed attachment outside the safe root: '{candidate}'.");
                    continue;
                }
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                string? parent = Path.GetDirectoryName(fullPath);
                while (!string.IsNullOrWhiteSpace(parent) &&
                       !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) &&
                       IsPathInsideManagedRoot(parent, root) &&
                       Directory.Exists(parent) &&
                       !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent, recursive: false);
                    parent = Path.GetDirectoryName(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                App.Log($"[TodoWorkspace] Managed attachment cleanup failed for '{candidate}': {ex.Message}");
            }
        }
    }

    private void ClearManagedAttachmentDirectory()
    {
        string root = Path.GetFullPath(AttachmentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return;
        }

        ClearManagedDirectoryChildren(root, root);
    }

    private static void ClearManagedDirectoryChildren(string directory, string root)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                string fullPath = Path.GetFullPath(file);
                if (IsPathInsideManagedRoot(fullPath, root))
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log($"[TodoWorkspace] Could not remove managed attachment '{file}': {ex.Message}");
            }
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    ClearManagedDirectoryChildren(child, root);
                }
                Directory.Delete(child, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log($"[TodoWorkspace] Could not remove managed attachment directory '{child}': {ex.Message}");
            }
        }
    }

    private static bool IsPathInsideManagedRoot(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePointParent(string path, string root)
    {
        string? parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(parent) &&
               !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPathInsideManagedRoot(parent, root))
            {
                return true;
            }
            if (Directory.Exists(parent) &&
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            parent = Path.GetDirectoryName(parent);
        }
        return !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ReplaceTaskChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TodoTask task,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM todo_steps WHERE task_id = $task;
                DELETE FROM todo_attachments WHERE task_id = $task;
                DELETE FROM todo_task_tags WHERE task_id = $task;
                DELETE FROM todo_reminders WHERE task_id = $task;
                DELETE FROM todo_recurrence_rules WHERE task_id = $task;
                """;
            clear.Parameters.AddWithValue("$task", task.Id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (TodoStep step in task.Steps.OrderBy(step => step.SortOrder))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO todo_steps(id, task_id, text, is_completed, sort_order)
                VALUES($id, $task, $text, $completed, $sort);
                """;
            command.Parameters.AddWithValue("$id", NormalizeId(step.Id));
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$text", step.Text.Trim());
            command.Parameters.AddWithValue("$completed", step.IsCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$sort", step.SortOrder);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (TodoAttachment attachment in task.Attachments)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO todo_attachments(id, task_id, file_path, display_name, type, storage_mode, added_at)
                VALUES($id, $task, $path, $name, $type, $mode, $added);
                """;
            command.Parameters.AddWithValue("$id", NormalizeId(attachment.Id));
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$path", attachment.FilePath.Trim());
            command.Parameters.AddWithValue("$name", attachment.DisplayName.Trim());
            command.Parameters.AddWithValue("$type", attachment.Type.Trim());
            command.Parameters.AddWithValue("$mode", TodoAttachment.NormalizeStorageMode(attachment.StorageMode));
            command.Parameters.AddWithValue("$added", FormatDateTime(attachment.AddedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (string tagId in task.TagIds.Distinct(StringComparer.Ordinal))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO todo_task_tags(task_id, tag_id) VALUES($task, $tag);";
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$tag", tagId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (TodoReminderRule reminder in task.Reminders)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO todo_reminders(
                    id, task_id, target, offset_minutes, absolute_at, occurrence_key,
                    last_notified_at, snoozed_until, snooze_last_notified_at, is_enabled)
                VALUES($id, $task, $target, $offset, $absolute, $occurrence,
                    $notified, $snoozed, $snoozeNotified, $enabled);
                """;
            command.Parameters.AddWithValue("$id", NormalizeId(reminder.Id));
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$target", (int)reminder.Target);
            command.Parameters.AddWithValue("$offset", DbValue(reminder.OffsetMinutes));
            command.Parameters.AddWithValue("$absolute", DbDateTime(reminder.AbsoluteAt));
            command.Parameters.AddWithValue("$occurrence", DbValue(reminder.OccurrenceKey));
            command.Parameters.AddWithValue("$notified", DbDateTime(reminder.LastNotifiedAt));
            command.Parameters.AddWithValue("$snoozed", DbDateTime(reminder.SnoozedUntil));
            command.Parameters.AddWithValue("$snoozeNotified", DbDateTime(reminder.SnoozeLastNotifiedAt));
            command.Parameters.AddWithValue("$enabled", reminder.IsEnabled ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (task.RecurrenceRule is { } recurrence)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO todo_recurrence_rules(
                    task_id, id, frequency, interval_value, week_days_json, month_day,
                    month_week_ordinal, month_week_day, end_date, occurrence_count, anchor, generation_mode)
                VALUES($task, $id, $frequency, $interval, $weekDays, $monthDay,
                    $ordinal, $monthWeekDay, $endDate, $count, $anchor, $generation);
                """;
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$id", NormalizeId(recurrence.Id));
            command.Parameters.AddWithValue("$frequency", (int)recurrence.Frequency);
            command.Parameters.AddWithValue("$interval", Math.Max(1, recurrence.Interval));
            command.Parameters.AddWithValue("$weekDays", JsonSerializer.Serialize(recurrence.WeekDays, s_jsonOptions));
            command.Parameters.AddWithValue("$monthDay", DbValue(recurrence.MonthDay));
            command.Parameters.AddWithValue("$ordinal", DbValue(recurrence.MonthWeekOrdinal));
            command.Parameters.AddWithValue("$monthWeekDay", DbValue(recurrence.MonthWeekDay is null ? null : (int)recurrence.MonthWeekDay));
            command.Parameters.AddWithValue("$endDate", DbDate(recurrence.EndDate));
            command.Parameters.AddWithValue("$count", DbValue(recurrence.OccurrenceCount));
            command.Parameters.AddWithValue("$anchor", (int)recurrence.Anchor);
            command.Parameters.AddWithValue("$generation", (int)recurrence.GenerationMode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void NormalizeTask(TodoTask task)
    {
        task.Id = NormalizeId(task.Id);
        task.Text = task.Text?.Trim() ?? string.Empty;
        task.ColorMarker = TodoItem.NormalizeColorMarker(task.ColorMarker);
        task.ListId = NormalizeListId(task.ListId);
        task.SectionId = string.IsNullOrWhiteSpace(task.SectionId) ? null : task.SectionId.Trim();
        task.TagIds = task.TagIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        task.Steps ??= [];
        task.Attachments ??= [];
        task.Reminders ??= [];
        task.Notes = string.IsNullOrWhiteSpace(task.Notes)
            ? null
            : task.Notes.Trim()[..Math.Min(task.Notes.Trim().Length, TodoWorkspaceDefaults.MaxNotesCharacters)];

        task.DeadlineAt ??= task.DueDate;
        task.DueDate = task.DeadlineAt;
        task.Status = task.IsCompleted
            ? TodoTaskStatus.Completed
            : task.Status == TodoTaskStatus.Cancelled ? TodoTaskStatus.Cancelled : TodoTaskStatus.Open;
        task.IsCompleted = task.Status == TodoTaskStatus.Completed;
        task.Priority = task.IsImportant ? TodoPriority.High : task.Priority;
        task.IsImportant = task.Priority == TodoPriority.High;
        if (task.IsCompleted)
        {
            task.CompletedAt ??= task.UpdatedAt == default ? DateTimeOffset.UtcNow : task.UpdatedAt;
        }
        else
        {
            task.CompletedAt = null;
        }

        task.CreatedAt = task.CreatedAt == default ? DateTimeOffset.UtcNow : task.CreatedAt;
        task.UpdatedAt = task.UpdatedAt == default ? task.CreatedAt : task.UpdatedAt;
        if (task.Schedule is { } schedule)
        {
            schedule.DurationMinutes = schedule.Time is null
                ? null
                : Math.Clamp(
                    schedule.DurationMinutes ?? TodoWorkspaceDefaults.DefaultDurationMinutes,
                    5,
                    24 * 60);
            schedule.TimeZoneId = string.IsNullOrWhiteSpace(schedule.TimeZoneId)
                ? TimeZoneInfo.Local.Id
                : schedule.TimeZoneId.Trim();
        }

        NormalizeLegacyReminder(task);
        NormalizeLegacyRecurrence(task);
    }

    private static void NormalizeLegacyReminder(TodoTask task)
    {
        TodoReminderRule? legacyRule = task.Reminders.FirstOrDefault(rule =>
            string.Equals(rule.Id, $"legacy-{task.Id}", StringComparison.Ordinal));
        if (task.ReminderOffsetMinutes is { } legacyOffset && legacyOffset != TodoReminderOptions.ReminderOff)
        {
            legacyRule ??= new TodoReminderRule
            {
                Id = $"legacy-{task.Id}",
                Target = TodoReminderTarget.Deadline
            };
            legacyRule.OffsetMinutes = legacyOffset;
            legacyRule.LastNotifiedAt = task.ReminderLastNotifiedAt;
            legacyRule.SnoozedUntil = task.SnoozedUntil;
            legacyRule.SnoozeLastNotifiedAt = task.SnoozeLastNotifiedAt;
            if (!task.Reminders.Contains(legacyRule))
            {
                task.Reminders.Insert(0, legacyRule);
            }
        }
        else if (legacyRule is not null)
        {
            task.Reminders.Remove(legacyRule);
        }
    }

    private static void NormalizeLegacyRecurrence(TodoTask task)
    {
        if (task.RecurrenceRule is not null || task.Recurrence is null)
        {
            return;
        }

        string mode = TodoRecurrenceMode.Normalize(task.Recurrence.Mode);
        if (mode == TodoRecurrenceMode.None)
        {
            return;
        }

        task.RecurrenceRule = new TodoRecurrenceRule
        {
            Id = $"legacy-{task.Id}",
            Frequency = mode == TodoRecurrenceMode.Monthly
                ? TodoRecurrenceFrequency.Monthly
                : mode == TodoRecurrenceMode.Weekly || mode == TodoRecurrenceMode.Weekdays
                    ? TodoRecurrenceFrequency.Weekly
                    : TodoRecurrenceFrequency.Daily,
            WeekDays = mode == TodoRecurrenceMode.Weekdays
                ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
                : [],
            Anchor = TodoRecurrenceAnchor.Deadline,
            GenerationMode = TodoRecurrenceGenerationMode.AfterCompletion
        };
    }

    private static void BindTask(SqliteCommand command, TodoTask task)
    {
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$important", task.IsImportant ? 1 : 0);
        command.Parameters.AddWithValue("$color", DbValue(task.ColorMarker));
        command.Parameters.AddWithValue("$list", task.ListId);
        command.Parameters.AddWithValue("$section", DbValue(task.SectionId));
        command.Parameters.AddWithValue("$planDate", DbDate(task.Schedule?.Date));
        command.Parameters.AddWithValue("$planTime", DbTime(task.Schedule?.Time));
        command.Parameters.AddWithValue("$zone", DbValue(task.Schedule?.TimeZoneId));
        command.Parameters.AddWithValue("$duration", DbValue(task.Schedule?.DurationMinutes));
        command.Parameters.AddWithValue("$deadline", DbDateTime(task.DeadlineAt));
        command.Parameters.AddWithValue("$notes", DbValue(task.Notes));
        command.Parameters.AddWithValue("$sort", task.SortOrder);
        command.Parameters.AddWithValue("$todaySort", DbValue(task.TodaySortRank));
        command.Parameters.AddWithValue("$created", FormatDateTime(task.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDateTime(task.UpdatedAt));
        command.Parameters.AddWithValue("$completed", DbDateTime(task.CompletedAt));
        command.Parameters.AddWithValue("$deleted", DbDateTime(task.DeletedAt));
        command.Parameters.AddWithValue("$series", DbValue(task.RecurrenceSeriesId));
        command.Parameters.AddWithValue("$generated", DbValue(task.GeneratedNextItemId));
        command.Parameters.AddWithValue("$reminderNotified", DbDateTime(task.ReminderLastNotifiedAt));
        command.Parameters.AddWithValue("$reminderDismissed", DbDateTime(task.ReminderDismissedForDueDate));
        command.Parameters.AddWithValue("$reminderOffset", DbValue(task.ReminderOffsetMinutes));
        command.Parameters.AddWithValue("$snoozed", DbDateTime(task.SnoozedUntil));
        command.Parameters.AddWithValue("$snoozeNotified", DbDateTime(task.SnoozeLastNotifiedAt));
        command.Parameters.AddWithValue("$legacyRecurrence", DbValue(task.Recurrence is null
            ? null
            : JsonSerializer.Serialize(task.Recurrence, s_jsonOptions)));
    }

    private static TodoTask ReadTask(SqliteDataReader reader)
    {
        var task = new TodoTask
        {
            Id = reader.GetString(0),
            Text = reader.GetString(1),
            Status = (TodoTaskStatus)reader.GetInt32(2),
            Priority = (TodoPriority)reader.GetInt32(3),
            IsImportant = reader.GetInt32(4) != 0,
            ColorMarker = GetNullableString(reader, 5),
            ListId = reader.GetString(6),
            SectionId = GetNullableString(reader, 7),
            DeadlineAt = ParseNullableDateTime(GetNullableString(reader, 12)),
            Notes = GetNullableString(reader, 13),
            SortOrder = reader.GetInt32(14),
            TodaySortRank = reader.IsDBNull(15) ? null : reader.GetDouble(15),
            CreatedAt = ParseDateTime(reader.GetString(16)),
            UpdatedAt = ParseDateTime(reader.GetString(17)),
            CompletedAt = ParseNullableDateTime(GetNullableString(reader, 18)),
            DeletedAt = ParseNullableDateTime(GetNullableString(reader, 19)),
            RecurrenceSeriesId = GetNullableString(reader, 20),
            GeneratedNextItemId = GetNullableString(reader, 21),
            ReminderLastNotifiedAt = ParseNullableDateTime(GetNullableString(reader, 22)),
            ReminderDismissedForDueDate = ParseNullableDateTime(GetNullableString(reader, 23)),
            ReminderOffsetMinutes = reader.IsDBNull(24) ? null : reader.GetInt32(24),
            SnoozedUntil = ParseNullableDateTime(GetNullableString(reader, 25)),
            SnoozeLastNotifiedAt = ParseNullableDateTime(GetNullableString(reader, 26))
        };

        DateOnly? planDate = ParseNullableDate(GetNullableString(reader, 8));
        if (planDate is { } date)
        {
            task.Schedule = new TodoSchedule
            {
                Date = date,
                Time = ParseNullableTime(GetNullableString(reader, 9)),
                TimeZoneId = GetNullableString(reader, 10),
                DurationMinutes = reader.IsDBNull(11) ? null : reader.GetInt32(11)
            };
        }

        string? legacyRecurrence = GetNullableString(reader, 27);
        if (!string.IsNullOrWhiteSpace(legacyRecurrence))
        {
            task.Recurrence = JsonSerializer.Deserialize<TodoRecurrence>(legacyRecurrence, s_jsonOptions);
        }

        task.IsCompleted = task.Status == TodoTaskStatus.Completed;
        task.DueDate = task.DeadlineAt;
        return task;
    }

    private static async Task LoadStepsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TodoTask> tasks,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, task_id, text, is_completed, sort_order FROM todo_steps ORDER BY task_id, sort_order;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (tasks.TryGetValue(reader.GetString(1), out TodoTask? task))
            {
                task.Steps.Add(new TodoStep
                {
                    Id = reader.GetString(0),
                    Text = reader.GetString(2),
                    IsCompleted = reader.GetInt32(3) != 0,
                    SortOrder = reader.GetInt32(4)
                });
            }
        }
    }

    private static async Task LoadAttachmentsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TodoTask> tasks,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, task_id, file_path, display_name, type, storage_mode, added_at FROM todo_attachments;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (tasks.TryGetValue(reader.GetString(1), out TodoTask? task))
            {
                task.Attachments.Add(new TodoAttachment
                {
                    Id = reader.GetString(0),
                    FilePath = reader.GetString(2),
                    DisplayName = reader.GetString(3),
                    Type = reader.GetString(4),
                    StorageMode = reader.GetString(5),
                    AddedAt = ParseDateTime(reader.GetString(6))
                });
            }
        }
    }

    private static async Task LoadTaskTagsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TodoTask> tasks,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, tag_id FROM todo_task_tags;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (tasks.TryGetValue(reader.GetString(0), out TodoTask? task))
            {
                task.TagIds.Add(reader.GetString(1));
            }
        }
    }

    private static async Task LoadRemindersAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TodoTask> tasks,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_id, target, offset_minutes, absolute_at, occurrence_key,
                last_notified_at, snoozed_until, snooze_last_notified_at, is_enabled
            FROM todo_reminders;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (tasks.TryGetValue(reader.GetString(1), out TodoTask? task))
            {
                task.Reminders.Add(new TodoReminderRule
                {
                    Id = reader.GetString(0),
                    Target = (TodoReminderTarget)reader.GetInt32(2),
                    OffsetMinutes = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    AbsoluteAt = ParseNullableDateTime(GetNullableString(reader, 4)),
                    OccurrenceKey = GetNullableString(reader, 5),
                    LastNotifiedAt = ParseNullableDateTime(GetNullableString(reader, 6)),
                    SnoozedUntil = ParseNullableDateTime(GetNullableString(reader, 7)),
                    SnoozeLastNotifiedAt = ParseNullableDateTime(GetNullableString(reader, 8)),
                    IsEnabled = reader.GetInt32(9) != 0
                });
            }
        }
    }

    private static async Task LoadRecurrenceRulesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TodoTask> tasks,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, id, frequency, interval_value, week_days_json, month_day,
                month_week_ordinal, month_week_day, end_date, occurrence_count, anchor, generation_mode
            FROM todo_recurrence_rules;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!tasks.TryGetValue(reader.GetString(0), out TodoTask? task))
            {
                continue;
            }

            task.RecurrenceRule = new TodoRecurrenceRule
            {
                Id = reader.GetString(1),
                Frequency = (TodoRecurrenceFrequency)reader.GetInt32(2),
                Interval = reader.GetInt32(3),
                WeekDays = JsonSerializer.Deserialize<List<DayOfWeek>>(reader.GetString(4), s_jsonOptions) ?? [],
                MonthDay = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                MonthWeekOrdinal = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                MonthWeekDay = reader.IsDBNull(7) ? null : (DayOfWeek)reader.GetInt32(7),
                EndDate = ParseNullableDate(GetNullableString(reader, 8)),
                OccurrenceCount = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Anchor = (TodoRecurrenceAnchor)reader.GetInt32(10),
                GenerationMode = (TodoRecurrenceGenerationMode)reader.GetInt32(11)
            };
        }
    }

    private static async Task LoadListsAsync(SqliteConnection connection, ICollection<TodoList> target, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, color_marker, sort_rank, is_system, is_archived FROM todo_lists ORDER BY sort_rank, name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(new TodoList
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ColorMarker = GetNullableString(reader, 2),
                SortRank = reader.GetDouble(3),
                IsSystem = reader.GetInt32(4) != 0,
                IsArchived = reader.GetInt32(5) != 0
            });
        }
    }

    private static async Task LoadSectionsAsync(SqliteConnection connection, ICollection<TodoSection> target, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, list_id, name, sort_rank, is_archived FROM todo_sections ORDER BY list_id, sort_rank, name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(new TodoSection
            {
                Id = reader.GetString(0),
                ListId = reader.GetString(1),
                Name = reader.GetString(2),
                SortRank = reader.GetDouble(3),
                IsArchived = reader.GetInt32(4) != 0
            });
        }
    }

    private static async Task LoadTagsAsync(SqliteConnection connection, ICollection<TodoTag> target, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, color_marker, sort_rank FROM todo_tags ORDER BY sort_rank, name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(new TodoTag
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ColorMarker = GetNullableString(reader, 2),
                SortRank = reader.GetDouble(3)
            });
        }
    }

    private static async Task LoadSavedViewsAsync(SqliteConnection connection, ICollection<TodoSavedView> target, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, icon_glyph, sort_rank, query_json FROM todo_saved_views ORDER BY sort_rank, name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(new TodoSavedView
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                IconGlyph = GetNullableString(reader, 2),
                SortRank = reader.GetDouble(3),
                Query = JsonSerializer.Deserialize<TodoQuery>(reader.GetString(4), s_jsonOptions) ?? new TodoQuery()
            });
        }
    }

    private static async Task LoadRecurrenceExceptionsAsync(
        SqliteConnection connection,
        ICollection<TodoRecurrenceException> target,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT series_id, occurrence_key, task_id, is_cancelled
            FROM todo_recurrence_exceptions;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(new TodoRecurrenceException
            {
                SeriesId = reader.GetString(0),
                OccurrenceKey = reader.GetString(1),
                TaskId = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsCancelled = reader.GetInt32(3) != 0
            });
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task SeedSystemListsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO todo_lists(id, name, sort_rank, is_system, is_archived)
            VALUES('inbox', 'Inbox', 0, 1, 0)
            ON CONFLICT(id) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateToInboxOnlyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todo_tasks
            SET list_id = 'inbox'
            WHERE list_id = 'tasks';

            UPDATE todo_sections
            SET list_id = 'inbox'
            WHERE list_id = 'tasks';

            UPDATE todo_saved_views
            SET query_json = replace(query_json, '"listId":"tasks"', '"listId":"inbox"')
            WHERE query_json LIKE '%"listId":"tasks"%';

            UPDATE todo_lists
            SET is_archived = 1
            WHERE id = 'tasks';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void RaiseChanged(TodoWorkspaceChangeKind kind, IReadOnlyCollection<string>? taskIds = null) =>
        Changed?.Invoke(this, new TodoWorkspaceChangedEventArgs(kind, taskIds));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();

    private static string NormalizeListId(string? listId) =>
        string.IsNullOrWhiteSpace(listId) ||
        string.Equals(listId.Trim(), TodoWorkspaceDefaults.LegacyDefaultListId, StringComparison.Ordinal)
            ? TodoWorkspaceDefaults.InboxListId
            : listId.Trim();

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static object DbDateTime(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatDateTime(value.Value);

    private static object DbDate(DateOnly? value) =>
        value is null ? DBNull.Value : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object DbTime(TimeOnly? value) =>
        value is null ? DBNull.Value : value.Value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableDateTime(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseDateTime(value);

    private static DateOnly? ParseNullableDate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly? ParseNullableTime(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : TimeOnly.Parse(value, CultureInfo.InvariantCulture);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private const string SelectTasksSql = """
        SELECT id, title, status, priority, is_important, color_marker, list_id, section_id,
            plan_date, plan_time, time_zone_id, duration_minutes, deadline_at, notes,
            sort_order, today_sort_rank, created_at, updated_at, completed_at, deleted_at,
            recurrence_series_id, generated_next_item_id, reminder_last_notified_at,
            reminder_dismissed_for_deadline, reminder_offset_minutes, snoozed_until,
            snooze_last_notified_at, legacy_recurrence_json
        FROM todo_tasks
        """;

    private const string UpsertTaskSql = """
        INSERT INTO todo_tasks(
            id, title, status, priority, is_important, color_marker, list_id, section_id,
            plan_date, plan_time, time_zone_id, duration_minutes, deadline_at, notes,
            sort_order, today_sort_rank, created_at, updated_at, completed_at, deleted_at,
            recurrence_series_id, generated_next_item_id, reminder_last_notified_at,
            reminder_dismissed_for_deadline, reminder_offset_minutes, snoozed_until,
            snooze_last_notified_at, legacy_recurrence_json)
        VALUES(
            $id, $title, $status, $priority, $important, $color, $list, $section,
            $planDate, $planTime, $zone, $duration, $deadline, $notes,
            $sort, $todaySort, $created, $updated, $completed, $deleted,
            $series, $generated, $reminderNotified, $reminderDismissed, $reminderOffset,
            $snoozed, $snoozeNotified, $legacyRecurrence)
        ON CONFLICT(id) DO UPDATE SET
            title=$title, status=$status, priority=$priority, is_important=$important,
            color_marker=$color, list_id=$list, section_id=$section, plan_date=$planDate,
            plan_time=$planTime, time_zone_id=$zone, duration_minutes=$duration,
            deadline_at=$deadline, notes=$notes, sort_order=$sort, today_sort_rank=$todaySort,
            created_at=$created, updated_at=$updated, completed_at=$completed, deleted_at=$deleted,
            recurrence_series_id=$series, generated_next_item_id=$generated,
            reminder_last_notified_at=$reminderNotified,
            reminder_dismissed_for_deadline=$reminderDismissed,
            reminder_offset_minutes=$reminderOffset, snoozed_until=$snoozed,
            snooze_last_notified_at=$snoozeNotified, legacy_recurrence_json=$legacyRecurrence;
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS todo_tasks(
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            status INTEGER NOT NULL DEFAULT 0,
            priority INTEGER NOT NULL DEFAULT 0,
            is_important INTEGER NOT NULL DEFAULT 0,
            color_marker TEXT NULL,
            list_id TEXT NOT NULL DEFAULT 'inbox',
            section_id TEXT NULL,
            plan_date TEXT NULL,
            plan_time TEXT NULL,
            time_zone_id TEXT NULL,
            duration_minutes INTEGER NULL,
            deadline_at TEXT NULL,
            notes TEXT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            today_sort_rank REAL NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            deleted_at TEXT NULL,
            recurrence_series_id TEXT NULL,
            generated_next_item_id TEXT NULL,
            reminder_last_notified_at TEXT NULL,
            reminder_dismissed_for_deadline TEXT NULL,
            reminder_offset_minutes INTEGER NULL,
            snoozed_until TEXT NULL,
            snooze_last_notified_at TEXT NULL,
            legacy_recurrence_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_todo_tasks_status ON todo_tasks(status, deleted_at);
        CREATE INDEX IF NOT EXISTS ix_todo_tasks_list ON todo_tasks(list_id, section_id, deleted_at);
        CREATE INDEX IF NOT EXISTS ix_todo_tasks_plan ON todo_tasks(plan_date, deleted_at);
        CREATE INDEX IF NOT EXISTS ix_todo_tasks_deadline ON todo_tasks(deadline_at, deleted_at);

        CREATE TABLE IF NOT EXISTS todo_steps(
            id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL REFERENCES todo_tasks(id) ON DELETE CASCADE,
            text TEXT NOT NULL,
            is_completed INTEGER NOT NULL DEFAULT 0,
            sort_order INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_todo_steps_task ON todo_steps(task_id, sort_order);

        CREATE TABLE IF NOT EXISTS todo_attachments(
            id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL REFERENCES todo_tasks(id) ON DELETE CASCADE,
            file_path TEXT NOT NULL,
            display_name TEXT NOT NULL,
            type TEXT NOT NULL,
            storage_mode TEXT NOT NULL,
            added_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_todo_attachments_task ON todo_attachments(task_id);

        CREATE TABLE IF NOT EXISTS todo_lists(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            color_marker TEXT NULL,
            sort_rank REAL NOT NULL DEFAULT 0,
            is_system INTEGER NOT NULL DEFAULT 0,
            is_archived INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS todo_sections(
            id TEXT PRIMARY KEY,
            list_id TEXT NOT NULL,
            name TEXT NOT NULL,
            sort_rank REAL NOT NULL DEFAULT 0,
            is_archived INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_todo_sections_list ON todo_sections(list_id, sort_rank);

        CREATE TABLE IF NOT EXISTS todo_tags(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            color_marker TEXT NULL,
            sort_rank REAL NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS todo_task_tags(
            task_id TEXT NOT NULL REFERENCES todo_tasks(id) ON DELETE CASCADE,
            tag_id TEXT NOT NULL,
            PRIMARY KEY(task_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS todo_reminders(
            id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL REFERENCES todo_tasks(id) ON DELETE CASCADE,
            target INTEGER NOT NULL,
            offset_minutes INTEGER NULL,
            absolute_at TEXT NULL,
            occurrence_key TEXT NULL,
            last_notified_at TEXT NULL,
            snoozed_until TEXT NULL,
            snooze_last_notified_at TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS ix_todo_reminders_task ON todo_reminders(task_id);

        CREATE TABLE IF NOT EXISTS todo_recurrence_rules(
            task_id TEXT PRIMARY KEY REFERENCES todo_tasks(id) ON DELETE CASCADE,
            id TEXT NOT NULL,
            frequency INTEGER NOT NULL,
            interval_value INTEGER NOT NULL DEFAULT 1,
            week_days_json TEXT NOT NULL DEFAULT '[]',
            month_day INTEGER NULL,
            month_week_ordinal INTEGER NULL,
            month_week_day INTEGER NULL,
            end_date TEXT NULL,
            occurrence_count INTEGER NULL,
            anchor INTEGER NOT NULL,
            generation_mode INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS todo_recurrence_exceptions(
            series_id TEXT NOT NULL,
            occurrence_key TEXT NOT NULL,
            task_id TEXT NULL REFERENCES todo_tasks(id) ON DELETE SET NULL,
            is_cancelled INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(series_id, occurrence_key)
        );

        CREATE TABLE IF NOT EXISTS todo_saved_views(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            icon_glyph TEXT NULL,
            sort_rank REAL NOT NULL DEFAULT 0,
            query_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS todo_migration_sources(
            source_hash TEXT PRIMARY KEY,
            source_path TEXT NOT NULL,
            imported_at TEXT NOT NULL
        );
        """;
}

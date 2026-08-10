using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;
using Microsoft.Data.Sqlite;

namespace DeskBox.Services;

/// <summary>
/// Transactional Quick Capture persistence. The repository keeps legacy JSON
/// readable as a recovery fallback while all normal writes go to SQLite.
/// </summary>
public sealed partial class SqliteQuickCaptureRepository : IQuickCaptureRepository
{
    private const int SchemaVersion = 1;
    private readonly QuickCaptureStore _legacyStore;
    private readonly QuickCaptureMarkdownService _markdownService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _useLegacyFallback;

    public SqliteQuickCaptureRepository(
        QuickCaptureStore? legacyStore = null,
        QuickCaptureMarkdownService? markdownService = null)
    {
        _legacyStore = legacyStore ?? new QuickCaptureStore();
        _markdownService = markdownService ?? new QuickCaptureMarkdownService();
        DatabasePath = Path.Combine(
            Path.GetDirectoryName(_legacyStore.StorePath)!,
            "quick-capture.db");
    }

    public string DatabasePath { get; }

    internal bool IsUsingLegacyFallback => _useLegacyFallback;

    public async Task<QuickCaptureStoreData> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                return await _legacyStore.LoadAsync();
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            var data = new QuickCaptureStoreData
            {
                Version = 4,
                CurrentView = await ReadCurrentViewAsync(connection, cancellationToken)
            };

            var itemsById = new Dictionary<string, QuickCaptureItem>(StringComparer.Ordinal);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, collection, type, content_format, title, body, url,
                           image_path, content_hash, is_pinned, appearance_preset,
                           source_kind, archived_at, sort_order, pinned_sort_order,
                           created_at, updated_at, revision
                    FROM notes
                    WHERE is_deleted = 0
                    ORDER BY collection, sort_order, updated_at DESC;
                    """;
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    bool isRecent = reader.GetInt32(1) == 1;
                    QuickCaptureItem item = ReadItem(reader, isRecent, isDeleted: false);
                    itemsById[item.Id] = item;
                    (isRecent ? data.RecentItems : data.Items).Add(item);
                }
            }

            await LoadAttachmentsAndTagsAsync(connection, itemsById, cancellationToken);
            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        QuickCaptureStoreData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                await _legacyStore.SaveAsync(data);
                return;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            using SqliteTransaction transaction = connection.BeginTransaction();
            await SaveSnapshotCoreAsync(connection, transaction, data, reconcileMissing: true, cancellationToken);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveItemAsync(
        QuickCaptureItem item,
        bool isRecent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                QuickCaptureStoreData data = await _legacyStore.LoadAsync();
                List<QuickCaptureItem> target = isRecent ? data.RecentItems : data.Items;
                int index = target.FindIndex(candidate =>
                    string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    target[index] = CloneItem(item);
                }
                else
                {
                    target.Add(CloneItem(item));
                }

                await _legacyStore.SaveAsync(data);
                return;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            using SqliteTransaction transaction = connection.BeginTransaction();
            await UpsertItemCoreAsync(connection, transaction, item, isRecent, cancellationToken);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QuickCaptureSearchHit>> SearchAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        string normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 500);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                QuickCaptureStoreData fallback = await _legacyStore.LoadAsync();
                return fallback.Items.Concat(fallback.RecentItems)
                    .Where(item => !item.IsDeleted)
                    .Select(item => new QuickCaptureSearchHit(
                        CloneItem(item),
                        _markdownService.ToPlainText(item.Body, item.ContentFormat),
                        0))
                    .Where(hit =>
                        (hit.Item.Title?.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                        hit.PlainText.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ||
                        hit.Item.Tags.Any(tag => tag.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)))
                    .Take(limit)
                    .ToList();
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            var rankedIds = new List<(string Id, double Rank)>();
            string ftsQuery = BuildFtsQuery(normalized);
            if (ftsQuery.Length > 0 && !ContainsCjk(normalized))
            {
                try
                {
                    await using SqliteCommand ftsCommand = connection.CreateCommand();
                    ftsCommand.CommandText = """
                        SELECT note_id, bm25(note_search)
                        FROM note_search
                        WHERE note_search MATCH $query
                        LIMIT $limit;
                        """;
                    ftsCommand.Parameters.AddWithValue("$query", ftsQuery);
                    ftsCommand.Parameters.AddWithValue("$limit", limit);
                    await using SqliteDataReader reader = await ftsCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        rankedIds.Add((reader.GetString(0), reader.GetDouble(1)));
                    }
                }
                catch (SqliteException ex)
                {
                    App.Log($"[QuickCaptureDb] FTS query fallback: {ex.Message}");
                }
            }

            if (rankedIds.Count == 0)
            {
                await using SqliteCommand likeCommand = connection.CreateCommand();
                likeCommand.CommandText = """
                    SELECT id
                    FROM notes
                    WHERE is_deleted = 0 AND (
                        title LIKE $query ESCAPE '\' OR
                        plain_text LIKE $query ESCAPE '\' OR
                        EXISTS (SELECT 1 FROM tags WHERE tags.note_id = notes.id AND tags.tag LIKE $query ESCAPE '\') OR
                        EXISTS (SELECT 1 FROM attachments WHERE attachments.note_id = notes.id AND attachments.display_name LIKE $query ESCAPE '\'))
                    ORDER BY is_pinned DESC, updated_at DESC
                    LIMIT $limit;
                    """;
                likeCommand.Parameters.AddWithValue("$query", $"%{EscapeLike(normalized)}%");
                likeCommand.Parameters.AddWithValue("$limit", limit);
                await using SqliteDataReader reader = await likeCommand.ExecuteReaderAsync(cancellationToken);
                double rank = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    rankedIds.Add((reader.GetString(0), rank++));
                }
            }

            var hits = new List<QuickCaptureSearchHit>(rankedIds.Count);
            foreach ((string id, double rank) in rankedIds)
            {
                (QuickCaptureItem Item, string PlainText)? loaded =
                    await LoadItemByIdAsync(connection, id, cancellationToken);
                if (loaded is { } value)
                {
                    hits.Add(new QuickCaptureSearchHit(value.Item, value.PlainText, rank));
                }
            }

            return hits;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveDraftAsync(QuickCaptureDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await ExecuteWriteAsync(async (connection, transaction) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO drafts(note_id, title, body, content_format, updated_at)
                VALUES($id, $title, $body, $format, $updated)
                ON CONFLICT(note_id) DO UPDATE SET
                    title = excluded.title,
                    body = excluded.body,
                    content_format = excluded.content_format,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", draft.NoteId);
            command.Parameters.AddWithValue("$title", DbValue(draft.Title));
            command.Parameters.AddWithValue("$body", draft.Body);
            command.Parameters.AddWithValue("$format", (int)draft.ContentFormat);
            command.Parameters.AddWithValue("$updated", ToDbDate(draft.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<QuickCaptureDraft?> GetDraftAsync(
        string noteId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                return null;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT title, body, content_format, updated_at FROM drafts WHERE note_id = $id;";
            command.Parameters.AddWithValue("$id", noteId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new QuickCaptureDraft(
                noteId,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                EnumValue(reader.GetInt32(2), QuickCaptureContentFormat.PlainText),
                FromDbDate(reader.GetString(3)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteDraftAsync(string noteId, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(async (connection, transaction) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM drafts WHERE note_id = $id;";
            command.Parameters.AddWithValue("$id", noteId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<long> SaveRevisionAsync(
        QuickCaptureItem item,
        int retentionDays = 30,
        int maxRevisions = 50,
        CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 3650);
        maxRevisions = Math.Clamp(maxRevisions, 1, 500);
        long revisionId = 0;
        await ExecuteWriteAsync(async (connection, transaction) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO revisions(note_id, title, body, content_format, created_at)
                VALUES($id, $title, $body, $format, $created);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$title", DbValue(item.Title));
            command.Parameters.AddWithValue("$body", item.Body);
            command.Parameters.AddWithValue("$format", (int)item.ContentFormat);
            command.Parameters.AddWithValue("$created", ToDbDate(DateTimeOffset.UtcNow));
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            revisionId = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            await using SqliteCommand prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM revisions
                WHERE note_id = $id AND (
                    created_at < $cutoff OR
                    id NOT IN (
                        SELECT id FROM revisions
                        WHERE note_id = $id
                        ORDER BY created_at DESC
                        LIMIT $limit));
                """;
            prune.Parameters.AddWithValue("$id", item.Id);
            prune.Parameters.AddWithValue("$cutoff", ToDbDate(DateTimeOffset.UtcNow.AddDays(-retentionDays)));
            prune.Parameters.AddWithValue("$limit", maxRevisions);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        return revisionId;
    }

    public async Task<IReadOnlyList<QuickCaptureRevision>> GetRevisionsAsync(
        string noteId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 200);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                return [];
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, body, content_format, created_at
                FROM revisions
                WHERE note_id = $id
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$id", noteId);
            command.Parameters.AddWithValue("$limit", limit);
            var revisions = new List<QuickCaptureRevision>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                revisions.Add(new QuickCaptureRevision(
                    reader.GetInt64(0),
                    noteId,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    EnumValue(reader.GetInt32(3), QuickCaptureContentFormat.PlainText),
                    FromDbDate(reader.GetString(4))));
            }

            return revisions;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QuickCaptureItem>> GetTrashAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                return [];
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            var items = new List<QuickCaptureItem>();
            var byId = new Dictionary<string, QuickCaptureItem>(StringComparer.Ordinal);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, collection, type, content_format, title, body, url,
                           image_path, content_hash, is_pinned, appearance_preset,
                           source_kind, archived_at, sort_order, pinned_sort_order,
                           created_at, updated_at, revision, deleted_at
                    FROM notes
                    WHERE is_deleted = 1
                    ORDER BY deleted_at DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    QuickCaptureItem item = ReadItem(reader, reader.GetInt32(1) == 1, isDeleted: true);
                    item.DeletedAt = reader.IsDBNull(18) ? null : FromDbDate(reader.GetString(18));
                    items.Add(item);
                    byId[item.Id] = item;
                }
            }

            await LoadAttachmentsAndTagsAsync(connection, byId, cancellationToken);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task PurgeDeletedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(async (connection, transaction) =>
        {
            await using SqliteCommand search = connection.CreateCommand();
            search.Transaction = transaction;
            search.CommandText = "DELETE FROM note_search WHERE note_id IN (SELECT id FROM notes WHERE is_deleted = 1 AND deleted_at < $cutoff);";
            search.Parameters.AddWithValue("$cutoff", ToDbDate(cutoff));
            await search.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand notes = connection.CreateCommand();
            notes.Transaction = transaction;
            notes.CommandText = "DELETE FROM notes WHERE is_deleted = 1 AND deleted_at < $cutoff;";
            notes.Parameters.AddWithValue("$cutoff", ToDbDate(cutoff));
            await notes.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task DeletePermanentlyAsync(
        string noteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        return ExecuteWriteAsync(async (connection, transaction) =>
        {
            await using SqliteCommand search = connection.CreateCommand();
            search.Transaction = transaction;
            search.CommandText = "DELETE FROM note_search WHERE note_id = $id;";
            search.Parameters.AddWithValue("$id", noteId);
            await search.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand note = connection.CreateCommand();
            note.Transaction = transaction;
            note.CommandText = "DELETE FROM notes WHERE id = $id AND is_deleted = 1;";
            note.Parameters.AddWithValue("$id", noteId);
            await note.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (_useLegacyFallback)
            {
                File.Copy(_legacyStore.StorePath, destinationPath, overwrite: true);
                return;
            }

            await using SqliteConnection source = await OpenConnectionAsync(cancellationToken);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<bool> ValidateDatabaseAsync(
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
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            return string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ExecuteWriteAsync(
        Func<SqliteConnection, SqliteTransaction, Task> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            if (_useLegacyFallback)
            {
                return;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            using SqliteTransaction transaction = connection.BeginTransaction();
            await operation(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        bool databaseExisted = File.Exists(DatabasePath);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            bool migrationComplete = string.Equals(
                await ReadMetadataAsync(connection, "migration_complete", cancellationToken),
                "1",
                StringComparison.Ordinal);
            if (!migrationComplete)
            {
                if (File.Exists(_legacyStore.StorePath))
                {
                    QuickCaptureStoreData legacy = await _legacyStore.LoadAsync();
                    foreach (QuickCaptureItem item in legacy.Items.Concat(legacy.RecentItems))
                    {
                        item.ContentFormat = QuickCaptureContentFormat.PlainText;
                        item.IsDeleted = false;
                        item.DeletedAt = null;
                    }

                    if (await CountNotesAsync(connection, cancellationToken) == 0)
                    {
                        using SqliteTransaction transaction = connection.BeginTransaction();
                        await SaveSnapshotCoreAsync(
                            connection,
                            transaction,
                            legacy,
                            reconcileMissing: false,
                            cancellationToken);
                        transaction.Commit();
                    }

                    await ValidateLegacyMigrationAsync(connection, legacy, cancellationToken);
                }

                await MarkMigrationCompleteAsync(connection, cancellationToken);

                if (File.Exists(_legacyStore.StorePath))
                {
                    string backupPath = _legacyStore.StorePath + ".migrated.bak";
                    if (!File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(_legacyStore.StorePath, backupPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            App.Log($"[QuickCaptureDb] Could not retain migrated JSON backup: {ex.Message}");
                        }
                    }
                }
            }

            await using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            object? result = await check.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Quick Capture database integrity check failed.");
            }
        }
        catch (Exception ex)
        {
            _useLegacyFallback = true;
            App.Log($"[QuickCaptureDb] SQLite unavailable; using resilient JSON fallback: {ex}");
            if (!databaseExisted)
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDatabaseFiles(DatabasePath);
            }
        }

        _initialized = true;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // SQLite connection pooling keeps Windows file handles alive after
            // the logical connection is disposed. Quick Capture opens short,
            // WAL-backed transactions, so disabling pooling gives deterministic
            // backup/restore, test cleanup, and data-root switching semantics.
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task CreateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS notes(
                id TEXT PRIMARY KEY,
                collection INTEGER NOT NULL,
                type INTEGER NOT NULL,
                content_format INTEGER NOT NULL DEFAULT 0,
                title TEXT NULL,
                body TEXT NOT NULL,
                plain_text TEXT NOT NULL,
                url TEXT NULL,
                image_path TEXT NULL,
                content_hash TEXT NULL,
                is_pinned INTEGER NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT NULL,
                appearance_preset INTEGER NOT NULL,
                source_kind INTEGER NOT NULL,
                archived_at TEXT NULL,
                sort_order INTEGER NOT NULL,
                pinned_sort_order INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS attachments(
                id TEXT PRIMARY KEY,
                note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                file_path TEXT NOT NULL,
                display_name TEXT NOT NULL,
                type TEXT NOT NULL,
                storage_mode TEXT NOT NULL,
                added_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tags(
                note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(note_id, tag)
            );

            CREATE TABLE IF NOT EXISTS revisions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
                title TEXT NULL,
                body TEXT NOT NULL,
                content_format INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS drafts(
                note_id TEXT PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
                title TEXT NULL,
                body TEXT NOT NULL,
                content_format INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS note_search USING fts5(
                note_id UNINDEXED,
                title,
                plain_text,
                tags,
                attachment_names,
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE INDEX IF NOT EXISTS ix_notes_collection_sort ON notes(collection, is_deleted, sort_order);
            CREATE INDEX IF NOT EXISTS ix_notes_updated ON notes(is_deleted, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_notes_deleted ON notes(is_deleted, deleted_at DESC);
            CREATE INDEX IF NOT EXISTS ix_attachments_note ON attachments(note_id);
            CREATE INDEX IF NOT EXISTS ix_revisions_note_created ON revisions(note_id, created_at DESC);
            PRAGMA user_version={{SchemaVersion}};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadMetadataAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
    }

    private static async Task<int> CountNotesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notes;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task MarkMigrationCompleteAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await WriteMetadataAsync(
            connection,
            transaction,
            "migration_complete",
            "1",
            cancellationToken);
        transaction.Commit();
    }

    private static async Task ValidateLegacyMigrationAsync(
        SqliteConnection connection,
        QuickCaptureStoreData legacy,
        CancellationToken cancellationToken)
    {
        QuickCaptureItem[] expectedItems = legacy.Items
            .Concat(legacy.RecentItems)
            .ToArray();
        if (expectedItems.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
            expectedItems.Length)
        {
            throw new InvalidDataException("Legacy Quick Capture data contains duplicate note ids.");
        }

        int actualCount = await CountNotesAsync(connection, cancellationToken);
        if (actualCount != expectedItems.Length)
        {
            throw new InvalidDataException(
                $"Quick Capture migration count mismatch. Expected {expectedItems.Length}, got {actualCount}.");
        }

        foreach (QuickCaptureItem expected in expectedItems)
        {
            await using (SqliteCommand note = connection.CreateCommand())
            {
                note.CommandText = "SELECT collection, content_format, body FROM notes WHERE id = $id;";
                note.Parameters.AddWithValue("$id", expected.Id);
                await using SqliteDataReader reader = await note.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) ||
                    reader.GetInt32(0) != (expected.IsRecent ? 1 : 0) ||
                    reader.GetInt32(1) != (int)QuickCaptureContentFormat.PlainText ||
                    !CryptographicOperations.FixedTimeEquals(
                        ComputeTextHash(reader.GetString(2)),
                        ComputeTextHash(expected.Body ?? string.Empty)))
                {
                    throw new InvalidDataException(
                        $"Quick Capture migration content validation failed for note '{expected.Id}'.");
                }
            }

            var actualAttachments = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (SqliteCommand attachments = connection.CreateCommand())
            {
                attachments.CommandText = """
                    SELECT id, file_path, display_name, type, storage_mode
                    FROM attachments
                    WHERE note_id = $id;
                    """;
                attachments.Parameters.AddWithValue("$id", expected.Id);
                await using SqliteDataReader reader = await attachments.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    actualAttachments[reader.GetString(0)] = string.Join(
                        '\u001F',
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4));
                }
            }

            Dictionary<string, string> expectedAttachments = (expected.Attachments ?? [])
                .ToDictionary(
                    attachment => attachment.Id,
                    attachment => string.Join(
                        '\u001F',
                        attachment.FilePath ?? string.Empty,
                        attachment.DisplayName ?? string.Empty,
                        attachment.Type ?? "file",
                        TodoAttachment.NormalizeStorageMode(attachment.StorageMode)),
                    StringComparer.Ordinal);
            if (actualAttachments.Count != expectedAttachments.Count ||
                expectedAttachments.Any(pair =>
                    !actualAttachments.TryGetValue(pair.Key, out string? actual) ||
                    !string.Equals(actual, pair.Value, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Quick Capture migration attachment validation failed for note '{expected.Id}'.");
            }
        }
    }

    private static byte[] ComputeTextHash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static void TryDeleteDatabaseFiles(string databasePath)
    {
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log($"[QuickCaptureDb] Could not remove incomplete database '{path}': {ex.Message}");
            }
        }
    }

    private async Task SaveSnapshotCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickCaptureStoreData data,
        bool reconcileMissing,
        CancellationToken cancellationToken)
    {
        if (reconcileMissing)
        {
            await using SqliteCommand markDeleted = connection.CreateCommand();
            markDeleted.Transaction = transaction;
            markDeleted.CommandText = """
                UPDATE notes
                SET is_deleted = 1,
                    deleted_at = COALESCE(deleted_at, $deleted)
                WHERE is_deleted = 0;
                """;
            markDeleted.Parameters.AddWithValue("$deleted", ToDbDate(DateTimeOffset.UtcNow));
            await markDeleted.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (QuickCaptureItem item in data.Items)
        {
            await UpsertItemCoreAsync(connection, transaction, item, isRecent: false, cancellationToken);
        }

        foreach (QuickCaptureItem item in data.RecentItems)
        {
            await UpsertItemCoreAsync(connection, transaction, item, isRecent: true, cancellationToken);
        }

        await WriteMetadataAsync(connection, transaction, "current_view", data.CurrentView.ToString(), cancellationToken);
        await WriteMetadataAsync(connection, transaction, "store_version", data.Version.ToString(CultureInfo.InvariantCulture), cancellationToken);

        if (reconcileMissing)
        {
            await using SqliteCommand cleanupSearch = connection.CreateCommand();
            cleanupSearch.Transaction = transaction;
            cleanupSearch.CommandText = "DELETE FROM note_search WHERE note_id IN (SELECT id FROM notes WHERE is_deleted = 1);";
            await cleanupSearch.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpsertItemCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickCaptureItem item,
        bool isRecent,
        CancellationToken cancellationToken)
    {
        string plainText = _markdownService.ToPlainText(item.Body, item.ContentFormat);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO notes(
                    id, collection, type, content_format, title, body, plain_text,
                    url, image_path, content_hash, is_pinned, is_deleted, deleted_at,
                    appearance_preset, source_kind, archived_at, sort_order,
                    pinned_sort_order, created_at, updated_at, revision)
                VALUES(
                    $id, $collection, $type, $format, $title, $body, $plain,
                    $url, $image, $hash, $pinned, 0, NULL,
                    $appearance, $source, $archived, $sort,
                    $pinnedSort, $created, $updated, $revision)
                ON CONFLICT(id) DO UPDATE SET
                    collection = excluded.collection,
                    type = excluded.type,
                    content_format = excluded.content_format,
                    title = excluded.title,
                    body = excluded.body,
                    plain_text = excluded.plain_text,
                    url = excluded.url,
                    image_path = excluded.image_path,
                    content_hash = excluded.content_hash,
                    is_pinned = excluded.is_pinned,
                    is_deleted = 0,
                    deleted_at = NULL,
                    appearance_preset = excluded.appearance_preset,
                    source_kind = excluded.source_kind,
                    archived_at = excluded.archived_at,
                    sort_order = excluded.sort_order,
                    pinned_sort_order = excluded.pinned_sort_order,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at,
                    revision = excluded.revision;
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$collection", isRecent ? 1 : 0);
            command.Parameters.AddWithValue("$type", (int)item.Type);
            command.Parameters.AddWithValue("$format", (int)item.ContentFormat);
            command.Parameters.AddWithValue("$title", DbValue(item.Title));
            command.Parameters.AddWithValue("$body", item.Body ?? string.Empty);
            command.Parameters.AddWithValue("$plain", plainText);
            command.Parameters.AddWithValue("$url", DbValue(item.Url));
            command.Parameters.AddWithValue("$image", DbValue(item.ImagePath));
            command.Parameters.AddWithValue("$hash", DbValue(item.ContentHash));
            command.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
            command.Parameters.AddWithValue("$appearance", (int)item.AppearancePreset);
            command.Parameters.AddWithValue("$source", (int)item.SourceKind);
            command.Parameters.AddWithValue("$archived", DbValue(item.ArchivedAt is { } archived ? ToDbDate(archived) : null));
            command.Parameters.AddWithValue("$sort", item.SortOrder);
            command.Parameters.AddWithValue("$pinnedSort", item.PinnedSortOrder);
            command.Parameters.AddWithValue("$created", ToDbDate(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", ToDbDate(item.UpdatedAt));
            command.Parameters.AddWithValue("$revision", item.Revision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteChildrenAsync(connection, transaction, item.Id, cancellationToken);
        foreach (TodoAttachment attachment in item.Attachments ?? [])
        {
            await using SqliteCommand attachmentCommand = connection.CreateCommand();
            attachmentCommand.Transaction = transaction;
            attachmentCommand.CommandText = """
                INSERT INTO attachments(id, note_id, file_path, display_name, type, storage_mode, added_at)
                VALUES($id, $note, $path, $name, $type, $storage, $added);
                """;
            attachmentCommand.Parameters.AddWithValue("$id", attachment.Id);
            attachmentCommand.Parameters.AddWithValue("$note", item.Id);
            attachmentCommand.Parameters.AddWithValue("$path", attachment.FilePath ?? string.Empty);
            attachmentCommand.Parameters.AddWithValue("$name", attachment.DisplayName ?? string.Empty);
            attachmentCommand.Parameters.AddWithValue("$type", attachment.Type ?? "file");
            attachmentCommand.Parameters.AddWithValue("$storage", TodoAttachment.NormalizeStorageMode(attachment.StorageMode));
            attachmentCommand.Parameters.AddWithValue("$added", ToDbDate(attachment.AddedAt));
            await attachmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (string tag in (item.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            await using SqliteCommand tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = "INSERT OR IGNORE INTO tags(note_id, tag) VALUES($note, $tag);";
            tagCommand.Parameters.AddWithValue("$note", item.Id);
            tagCommand.Parameters.AddWithValue("$tag", tag.Trim());
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand searchCommand = connection.CreateCommand();
        searchCommand.Transaction = transaction;
        searchCommand.CommandText = """
            INSERT INTO note_search(note_id, title, plain_text, tags, attachment_names)
            VALUES($id, $title, $plain, $tags, $attachments);
            """;
        searchCommand.Parameters.AddWithValue("$id", item.Id);
        searchCommand.Parameters.AddWithValue("$title", item.Title ?? string.Empty);
        searchCommand.Parameters.AddWithValue("$plain", plainText);
        searchCommand.Parameters.AddWithValue("$tags", string.Join(' ', item.Tags ?? []));
        searchCommand.Parameters.AddWithValue("$attachments", string.Join(' ', (item.Attachments ?? []).Select(attachment => attachment.DisplayName)));
        await searchCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string noteId,
        CancellationToken cancellationToken)
    {
        foreach (string table in new[] { "attachments", "tags", "note_search" })
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE note_id = $id;";
            command.Parameters.AddWithValue("$id", noteId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task WriteMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<QuickCaptureViewMode> ReadCurrentViewAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'current_view';";
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Enum.TryParse(value?.ToString(), ignoreCase: true, out QuickCaptureViewMode mode)
            ? mode
            : QuickCaptureViewMode.Records;
    }

    private static QuickCaptureItem ReadItem(
        SqliteDataReader reader,
        bool isRecent,
        bool isDeleted)
    {
        return new QuickCaptureItem
        {
            Id = reader.GetString(0),
            Type = EnumValue(reader.GetInt32(2), QuickCaptureItemType.Text),
            ContentFormat = EnumValue(reader.GetInt32(3), QuickCaptureContentFormat.PlainText),
            Title = reader.IsDBNull(4) ? null : reader.GetString(4),
            Body = reader.GetString(5),
            Url = reader.IsDBNull(6) ? null : reader.GetString(6),
            ImagePath = reader.IsDBNull(7) ? null : reader.GetString(7),
            ContentHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            IsPinned = reader.GetInt32(9) != 0,
            IsRecent = isRecent,
            IsDeleted = isDeleted,
            AppearancePreset = EnumValue(reader.GetInt32(10), QuickCaptureAppearancePreset.Default),
            SourceKind = EnumValue(reader.GetInt32(11), QuickCaptureSourceKind.Manual),
            ArchivedAt = reader.IsDBNull(12) ? null : FromDbDate(reader.GetString(12)),
            SortOrder = reader.GetInt32(13),
            PinnedSortOrder = reader.GetInt32(14),
            CreatedAt = FromDbDate(reader.GetString(15)),
            UpdatedAt = FromDbDate(reader.GetString(16)),
            Revision = reader.GetInt64(17)
        };
    }

    private static async Task LoadAttachmentsAndTagsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, QuickCaptureItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using (SqliteCommand attachments = connection.CreateCommand())
        {
            string filter = AddNoteIdFilter(attachments, items.Keys);
            attachments.CommandText = $"SELECT id, note_id, file_path, display_name, type, storage_mode, added_at FROM attachments{filter};";
            await using SqliteDataReader reader = await attachments.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!items.TryGetValue(reader.GetString(1), out QuickCaptureItem? item))
                {
                    continue;
                }

                item.Attachments.Add(new TodoAttachment
                {
                    Id = reader.GetString(0),
                    FilePath = reader.GetString(2),
                    DisplayName = reader.GetString(3),
                    Type = reader.GetString(4),
                    StorageMode = reader.GetString(5),
                    AddedAt = FromDbDate(reader.GetString(6))
                });
            }
        }

        await using SqliteCommand tags = connection.CreateCommand();
        string tagFilter = AddNoteIdFilter(tags, items.Keys);
        tags.CommandText = $"SELECT note_id, tag FROM tags{tagFilter} ORDER BY tag COLLATE NOCASE;";
        await using SqliteDataReader tagReader = await tags.ExecuteReaderAsync(cancellationToken);
        while (await tagReader.ReadAsync(cancellationToken))
        {
            if (items.TryGetValue(tagReader.GetString(0), out QuickCaptureItem? item))
            {
                item.Tags.Add(tagReader.GetString(1));
            }
        }
    }

    private static string AddNoteIdFilter(
        SqliteCommand command,
        IEnumerable<string> noteIds)
    {
        string[] ids = noteIds.Take(501).ToArray();
        if (ids.Length == 0 || ids.Length > 500)
        {
            return string.Empty;
        }

        string[] parameters = new string[ids.Length];
        for (int index = 0; index < ids.Length; index++)
        {
            parameters[index] = $"$note{index}";
            command.Parameters.AddWithValue(parameters[index], ids[index]);
        }

        return $" WHERE note_id IN ({string.Join(',', parameters)})";
    }

    private static async Task<(QuickCaptureItem Item, string PlainText)?> LoadItemByIdAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        QuickCaptureItem? item;
        string plainText;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, collection, type, content_format, title, body, url,
                       image_path, content_hash, is_pinned, appearance_preset,
                       source_kind, archived_at, sort_order, pinned_sort_order,
                       created_at, updated_at, revision, plain_text
                FROM notes
                WHERE id = $id AND is_deleted = 0;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            item = ReadItem(reader, reader.GetInt32(1) == 1, isDeleted: false);
            plainText = reader.GetString(18);
        }

        var map = new Dictionary<string, QuickCaptureItem>(StringComparer.Ordinal) { [id] = item };
        await LoadAttachmentsAndTagsAsync(connection, map, cancellationToken);
        return (item, plainText);
    }

    private static string BuildFtsQuery(string query)
    {
        return string.Join(
            " AND ",
            SearchTokenRegex().Matches(query)
                .Select(match => $"\"{match.Value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));
    }

    private static bool ContainsCjk(string value) => value.Any(character =>
        character is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF');

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value)
        ? DBNull.Value
        : value;

    private static string ToDbDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromDbDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TEnum EnumValue<TEnum>(int value, TEnum fallback)
        where TEnum : struct, Enum => Enum.IsDefined(typeof(TEnum), value)
            ? (TEnum)Enum.ToObject(typeof(TEnum), value)
            : fallback;

    private static QuickCaptureItem CloneItem(QuickCaptureItem item) => new()
    {
        Id = item.Id,
        Type = item.Type,
        ContentFormat = item.ContentFormat,
        Body = item.Body,
        Title = item.Title,
        Url = item.Url,
        ImagePath = item.ImagePath,
        ContentHash = item.ContentHash,
        Attachments = item.Attachments.Select(attachment => attachment.Clone()).ToList(),
        IsPinned = item.IsPinned,
        IsRecent = item.IsRecent,
        IsDeleted = item.IsDeleted,
        DeletedAt = item.DeletedAt,
        AppearancePreset = item.AppearancePreset,
        SourceKind = item.SourceKind,
        Tags = [.. item.Tags],
        ArchivedAt = item.ArchivedAt,
        SortOrder = item.SortOrder,
        PinnedSortOrder = item.PinnedSortOrder,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Revision = item.Revision
    };

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex SearchTokenRegex();
}

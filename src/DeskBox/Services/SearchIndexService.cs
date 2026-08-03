using System.Buffers;
using System.Text;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Background file indexer that maintains an in-memory filename index
/// for fast search across user directories. The index is persisted to disk so
/// results are available immediately on launch, and subsequent scans reconcile
/// against the existing index (incremental update) instead of rebuilding from scratch.
/// </summary>
public sealed class SearchIndexService : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Hard cap on in-memory entries to prevent unbounded memory growth.</summary>
    private const int MaxIndexEntries = 300_000;

    /// <summary>Fallback depth for fixed-drive scans when the USN journal is unavailable.</summary>
    private const int DriveRootMaxDepth = 6;

    private const long MaxPersistedFileBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan PersistedIndexFreshness = TimeSpan.FromMinutes(15);
    private const int CompactIndexMagic = 0x58494244; // "DBIX"
    private const int CompactIndexVersion = 1;
    private const int WatcherBufferSizeBytes = 64 * 1024;
    private static readonly TimeSpan WatcherRecoveryDelay = TimeSpan.FromMilliseconds(750);

    private readonly SettingsService _settingsService;
    private readonly ReaderWriterLockSlim _indexLock = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _residencyGate = new(1, 1);
    private readonly object _saveLock = new();
    private readonly object _saveScheduleLock = new();
    private readonly object _watchersLock = new();
    private readonly object _scanWatcherChangesLock = new();
    private readonly object _watcherRecoveryLock = new();
    private readonly object _scanStateLock = new();
    private readonly object _sessionStateLock = new();
    private Dictionary<string, IndexedFileEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _directoryPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingIndexChange> _pendingChanges =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, WatcherFailureState> _watcherCreationFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingWatcherChange> _scanWatcherChanges = [];
    private readonly string _storePath;
    private readonly string _dirtyMarkerPath;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _watcherRecoveryCts;
    private Task? _scanTask;
    private bool _isDisposed;
    private int _isScanning;
    private int _isLoading;
    private int _isPaused;
    private int _indexingEnabled;
    private int _scannedCount;
    private int _scanGeneration;
    private long _sessionEpoch;
    private bool _forceFullScan;
    private bool _scanWatcherChangesOverflowed;
    private bool _isIndexResident;
    private int _persistedEntryCount;
    private DateTime? _lastScanTime;
    private int _watcherRecoveryCount;
    private DateTime? _lastWatcherRecoveryTime;
    private int _watcherRetryScheduled;
    private int _offlineRootCount;
    private int _partialRootCount;
    private int _scanOnlyRootCount;
    private int _lastScanCapacityLimited;
    private readonly string _rootsManifestPath;

    public SearchIndexService(SettingsService settingsService)
        : this(
            settingsService,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "cache",
                "search-index.json"))
    {
    }

    internal SearchIndexService(SettingsService settingsService, string storePath)
    {
        _settingsService = settingsService;
        _storePath = storePath;
        _dirtyMarkerPath = storePath + ".dirty";
        _rootsManifestPath = storePath + ".roots";
    }

    public bool IsScanning => Volatile.Read(ref _isScanning) == 1;
    public bool IsLoading => Volatile.Read(ref _isLoading) == 1;

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    public int EntryCount => GetVisibleEntryCount();
    public int IndexedCount => EntryCount;
    public bool IsIndexResident => Volatile.Read(ref _isIndexResident);

    /// <summary>Number of items scanned during the current/last scan pass.</summary>
    public int ScannedCount => Volatile.Read(ref _scannedCount);

    /// <summary>When the last full scan completed.</summary>
    public DateTime? LastScanTime => _lastScanTime;

    public int WatcherCount
    {
        get
        {
            lock (_watchersLock)
            {
                return _watchers.Count;
            }
        }
    }

    public int WatcherRecoveryCount => Volatile.Read(ref _watcherRecoveryCount);
    public DateTime? LastWatcherRecoveryTime => _lastWatcherRecoveryTime;
    public int OfflineRootCount => Volatile.Read(ref _offlineRootCount);
    public int PartialRootCount => Volatile.Read(ref _partialRootCount);
    public int ScanOnlyRootCount => Volatile.Read(ref _scanOnlyRootCount);
    public bool LastScanCapacityLimited => Volatile.Read(ref _lastScanCapacityLimited) == 1;

    /// <summary>Number of roots whose watcher could not be created.</summary>
    public int WatcherCreationFailureCount
    {
        get
        {
            lock (_watchersLock)
            {
                return _watcherCreationFailures.Count;
            }
        }
    }

    private readonly HashSet<string> _lastScanRoots = new(StringComparer.OrdinalIgnoreCase);

    public event Action? IndexUpdated;

    /// <summary>Raised periodically during scanning with the current scanned count.</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>
    /// Pauses an in-progress scan. The scan thread blocks until <see cref="ResumeIndexing"/> is called.
    /// </summary>
    public void PauseIndexing()
    {
        if (IsScanning && !IsPaused)
        {
            Volatile.Write(ref _isPaused, 1);
            _pauseGate.Reset();
        }
    }

    /// <summary>Resumes a paused scan.</summary>
    public void ResumeIndexing()
    {
        if (IsPaused)
        {
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>Clears the index and starts a fresh full scan.</summary>
    public void RebuildIndex()
    {
        StopIndexing();
        ResetResidentIndex();
        Volatile.Write(ref _scannedCount, 0);
        _lastScanTime = null;
        _forceFullScan = true;
        StartIndexing();
    }

    /// <summary>Returns the on-disk size (bytes) of the persisted index file, or 0 if absent.</summary>
    public long GetIndexStorageBytes()
    {
        try
        {
            return File.Exists(_storePath) ? new FileInfo(_storePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Loads the persisted index from disk (if present) so search returns results
    /// immediately, before the first background scan completes. Safe to call once.
    /// </summary>
    public void TryLoadPersistedIndex()
    {
        if (!_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return;
        }

        try
        {
            _ = EnsureLoadedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to load persisted index: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the compact persisted index on a worker thread. Multiple popup/startup
    /// callers share the same residency gate, so only one copy is materialized.
    /// </summary>
    public async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsIndexResident)
        {
            return true;
        }

        if (_isDisposed ||
            !_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return false;
        }

        await _residencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsIndexResident)
            {
                return true;
            }

            LoadedIndex? loaded;
            Interlocked.Exchange(ref _isLoading, 1);
            try
            {
                loaded = await Task.Run(
                    () => LoadPersistedIndexCore(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isLoading, 0);
            }
            if (loaded is null)
            {
                return false;
            }

            LoadRootManifest();

            bool hadPendingChanges;
            _indexLock.EnterWriteLock();
            try
            {
                if (_isDisposed ||
                    !_settingsService.Settings.SearchCustomIndexerEnabled)
                {
                    return false;
                }

                foreach (var (path, change) in _pendingChanges)
                {
                    if (change.IsDeleted)
                    {
                        loaded.Index.Remove(path);
                        continue;
                    }

                    loaded.Index[path] = CreateIndexedEntry(
                        path,
                        change.IsDirectory,
                        change.LastModified,
                        change.ScanGeneration,
                        loaded.DirectoryPool);
                }

                hadPendingChanges = _pendingChanges.Count > 0;
                _pendingChanges.Clear();
                _index = loaded.Index;
                _directoryPool = loaded.DirectoryPool;
                _persistedEntryCount = _index.Count;
                Volatile.Write(ref _isIndexResident, true);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            App.Log(
                $"[SearchIndex] Loaded {EntryCount} persisted entries " +
                $"from {(loaded.WasLegacyJson ? "legacy JSON" : "compact cache")}.");

            if (loaded.WasLegacyJson || hadPendingChanges)
            {
                _ = Task.Run(SaveIndex);
            }

            return true;
        }
        finally
        {
            _residencyGate.Release();
        }
    }

    /// <summary>
    /// Saves and releases the resident index while keeping file-system watchers alive.
    /// Changes that arrive while unloaded are retained in a small delta map and merged
    /// the next time the search popup requests the index.
    /// </summary>
    public async Task<bool> TryUnloadForIdleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsIndexResident ||
            IsScanning ||
            _isDisposed ||
            !_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return false;
        }

        await _residencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsIndexResident || IsScanning || _isDisposed)
            {
                return false;
            }

            return await Task.Run(
                UnloadResidentIndexCore,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _residencyGate.Release();
        }
    }

    private bool UnloadResidentIndexCore()
    {
        lock (_saveLock)
        {
            _indexLock.EnterWriteLock();
            try
            {
                if (!IsIndexResident ||
                    IsScanning ||
                    _index.Count == 0)
                {
                    return false;
                }

                // The compact cache becomes the stable base for any watcher deltas
                // collected while the large in-memory dictionary is absent.
                SaveCompactIndexCore();
                int releasedEntryCount = _index.Count;
                _persistedEntryCount = releasedEntryCount;
                _index = new Dictionary<string, IndexedFileEntry>(
                    StringComparer.OrdinalIgnoreCase);
                _directoryPool = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                Volatile.Write(ref _isIndexResident, false);
                App.Log(
                    $"[SearchIndex] Unloaded {releasedEntryCount} idle entries; " +
                    "watchers remain active.");
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"[SearchIndex] Idle unload failed: {ex.Message}");
                return false;
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }
    }

    private LoadedIndex? LoadPersistedIndexCore(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return null;
            }

            var fileInfo = new FileInfo(_storePath);
            if (fileInfo.Length > MaxPersistedFileBytes)
            {
                App.Log($"[SearchIndex] Persisted index too large ({fileInfo.Length / 1024 / 1024} MB). Skipping load; will rebuild.");
                return null;
            }

            using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length >= sizeof(int))
            {
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                int magic = reader.ReadInt32();
                stream.Position = 0;
                if (magic == CompactIndexMagic)
                {
                    return LoadCompactIndex(reader, cancellationToken);
                }
            }

            var persisted = JsonSerializer.Deserialize<PersistedIndex>(stream, s_jsonOptions);
            if (persisted?.Entries is not { Count: > 0 })
            {
                return null;
            }

            var index = new Dictionary<string, IndexedFileEntry>(
                Math.Min(persisted.Entries.Count, MaxIndexEntries),
                StringComparer.OrdinalIgnoreCase);
            var directoryPool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in persisted.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index.Count >= MaxIndexEntries)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(entry.FullPath))
                {
                    continue;
                }

                string fullPath = entry.FullPath;
                index[fullPath] = CreateIndexedEntry(
                    fullPath,
                    entry.IsDirectory,
                    entry.LastModified,
                    scanGeneration: 0,
                    directoryPool);
            }

            return index.Count > 0
                ? new LoadedIndex(index, directoryPool, WasLegacyJson: true)
                : null;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to load persisted index: {ex.Message}");
            return null;
        }
    }

    private static LoadedIndex? LoadCompactIndex(
        BinaryReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.ReadInt32() != CompactIndexMagic)
        {
            return null;
        }

        int version = reader.ReadInt32();
        if (version != CompactIndexVersion)
        {
            throw new InvalidDataException($"Unsupported compact search index version {version}.");
        }

        _ = reader.ReadInt64(); // Persisted UTC timestamp, reserved for future migrations.
        int directoryCount = reader.ReadInt32();
        if (directoryCount < 0 || directoryCount > MaxIndexEntries)
        {
            throw new InvalidDataException("Invalid compact search index directory count.");
        }

        var directories = new string[directoryCount];
        var directoryPool = new Dictionary<string, string>(
            directoryCount,
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < directoryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = reader.ReadString();
            directories[index] = directory;
            directoryPool[directory] = directory;
        }

        int entryCount = reader.ReadInt32();
        if (entryCount < 0 || entryCount > MaxIndexEntries)
        {
            throw new InvalidDataException("Invalid compact search index entry count.");
        }

        var entries = new Dictionary<string, IndexedFileEntry>(
            entryCount,
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int directoryId = reader.ReadInt32();
            if ((uint)directoryId >= (uint)directories.Length)
            {
                throw new InvalidDataException("Invalid compact search index directory reference.");
            }

            string fileName = ReadUtf8String(reader);
            bool isDirectory = reader.ReadBoolean();
            DateTime lastModified = DateTime.FromBinary(reader.ReadInt64());
            string directory = directories[directoryId];
            string fullPath = string.IsNullOrEmpty(directory)
                ? fileName
                : Path.Combine(directory, fileName);
            entries[fullPath] = new IndexedFileEntry(
                directory,
                fullPath.Length - fileName.Length,
                isDirectory,
                lastModified,
                ScanGeneration: 0);
        }

        return entries.Count > 0
            ? new LoadedIndex(entries, directoryPool, WasLegacyJson: false)
            : null;
    }

    /// <summary>
    /// Persists the current in-memory index in a compact binary format. Directory
    /// strings are pooled once and file-name segments are streamed through a reusable
    /// UTF-8 buffer, avoiding a second full copy of the index.
    /// </summary>
    public void SaveIndex()
    {
        lock (_saveLock)
        {
            _indexLock.EnterReadLock();
            try
            {
                if (!IsIndexResident || _index.Count == 0)
                {
                    return;
                }

                SaveCompactIndexCore();
            }
            catch (Exception ex)
            {
                App.Log($"[SearchIndex] Failed to save index: {ex.Message}");
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }
    }

    private void SaveCompactIndexCore()
    {
        string? directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var directories = new List<string> { string.Empty };
        var directoryIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = 0
        };
        foreach (IndexedFileEntry entry in _index.Values)
        {
            if (!directoryIds.ContainsKey(entry.DirectoryPath))
            {
                directoryIds[entry.DirectoryPath] = directories.Count;
                directories.Add(entry.DirectoryPath);
            }
        }

        string tempPath = _storePath + ".tmp";
        byte[] utf8Buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(CompactIndexMagic);
                writer.Write(CompactIndexVersion);
                writer.Write(DateTime.UtcNow.Ticks);
                writer.Write(directories.Count);
                foreach (string pooledDirectory in directories)
                {
                    writer.Write(pooledDirectory);
                }

                writer.Write(_index.Count);
                foreach (var (fullPath, entry) in _index)
                {
                    writer.Write(directoryIds[entry.DirectoryPath]);
                    WriteUtf8StringSegment(
                        writer,
                        fullPath.AsSpan(entry.FileNameStart),
                        ref utf8Buffer);
                    writer.Write(entry.IsDirectory);
                    writer.Write(entry.LastModified.ToBinary());
                }
            }

            File.Move(tempPath, _storePath, overwrite: true);
            if (File.Exists(_dirtyMarkerPath))
            {
                File.Delete(_dirtyMarkerPath);
            }

            _persistedEntryCount = _index.Count;
            App.Log(
                $"[SearchIndex] Persisted {_index.Count} entries in compact format.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8Buffer);
        }
    }

    /// <summary>
    /// Schedules a debounced save of the index (used after filesystem watcher changes).
    /// </summary>
    private void ScheduleSave()
    {
        CancellationToken token;
        lock (_saveScheduleLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            token = _saveCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, token);
                if (!token.IsCancellationRequested)
                {
                    SaveIndex();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change.
            }
        }, token);
    }

    private void CancelScheduledSave()
    {
        lock (_saveScheduleLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = null;
        }
    }

    /// <summary>
    /// Starts the background indexing process.
    /// </summary>
    public void StartIndexing()
    {
        if (_isDisposed || !_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return;
        }

        CancellationToken token;
        long epoch;
        lock (_sessionStateLock)
        {
            if (_isDisposed ||
                !_settingsService.Settings.SearchCustomIndexerEnabled ||
                Interlocked.CompareExchange(ref _indexingEnabled, 1, 0) != 0)
            {
                return;
            }

            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            token = _scanCts.Token;
            epoch = Interlocked.Increment(ref _sessionEpoch);
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }

        if (_forceFullScan)
        {
            ResetResidentIndex();
        }
        else
        {
            TryLoadPersistedIndex();
        }

        EnsureEmptyResidentIndex();
        int residentCount = GetResidentEntryCount();
        bool watchersAlreadyArmed = false;
        if (!_forceFullScan &&
            residentCount > 0 &&
            TryGetFreshPersistedIndexTime(out DateTime persistedAt))
        {
            var (userDirs, driveRoots) = GetScanDirectories();
            var currentRoots = userDirs.Concat(driveRoots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            RemoveExplicitlyRemovedRoots(currentRoots, epoch, token);
            ClearScanWatcherChanges();
            SetupWatchers(userDirs, epoch, token);
            watchersAlreadyArmed = true;
            _lastScanTime = persistedAt;
            IndexUpdated?.Invoke();
            App.Log(
                $"[SearchIndex] Reusing fresh persisted index with {residentCount} entries; " +
                "watchers armed and background reconciliation scheduled.");
        }

        _forceFullScan = false;
        _scanTask = Task.Run(
            () => ScanDirectoriesAsync(epoch, token, watchersAlreadyArmed),
            token);
    }

    private bool TryGetFreshPersistedIndexTime(out DateTime persistedAt)
    {
        persistedAt = DateTime.MinValue;
        try
        {
            if (!File.Exists(_storePath) ||
                File.Exists(_dirtyMarkerPath))
            {
                return false;
            }

            persistedAt = File.GetLastWriteTime(_storePath);
            return DateTime.Now - persistedAt <= PersistedIndexFreshness;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stops indexing and clears the index.
    /// </summary>
    public void StopIndexing()
    {
        lock (_sessionStateLock)
        {
            Volatile.Write(ref _indexingEnabled, 0);
            Interlocked.Increment(ref _sessionEpoch);
            _scanCts?.Cancel();
            Volatile.Write(ref _watcherRetryScheduled, 0);
            Interlocked.Exchange(ref _isScanning, 0);
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
        lock (_watcherRecoveryLock)
        {
            _watcherRecoveryCts?.Cancel();
        }
        CancelScheduledSave();
        ClearWatchers();
        ClearIndexForStop();
        Volatile.Write(ref _scannedCount, 0);
        _lastScanTime = null;
        IndexUpdated?.Invoke();
    }

    /// <summary>
    /// Searches the index for files matching the query.
    /// </summary>
    public IReadOnlyList<SearchResultItem> Search(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsIndexResident)
        {
            return [];
        }

        string normalizedQuery = query.Trim();
        if (maxResults <= 0)
        {
            return [];
        }

        _indexLock.EnterReadLock();
        try
        {
            if (_index.Count == 0)
            {
                return [];
            }

            var topResults = new PriorityQueue<SearchCandidate, (double Score, long ModifiedTicks)>();
            ReadOnlySpan<char> querySpan = normalizedQuery.AsSpan();
            foreach (var (fullPath, entry) in _index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<char> fileName = fullPath.AsSpan(entry.FileNameStart);
                double score = ComputeRelevance(fileName, querySpan);
                if (score <= 0)
                {
                    continue;
                }

                long modifiedTicks = entry.LastModified.ToUniversalTime().Ticks;
                topResults.Enqueue(
                    new SearchCandidate(fullPath, entry, score),
                    (score, modifiedTicks));
                if (topResults.Count > maxResults)
                {
                    topResults.Dequeue();
                }
            }

            return topResults.UnorderedItems
                .Select(item => item.Element)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Entry.LastModified)
                .Take(maxResults)
                .Select(candidate => new SearchResultItem
                {
                    Kind = candidate.Entry.IsDirectory ? SearchResultKind.Folder : SearchResultKind.File,
                    Title = candidate.FullPath[candidate.Entry.FileNameStart..],
                    Subtitle = candidate.Entry.DirectoryPath,
                    DetailPath = candidate.FullPath,
                    ModifiedAt = candidate.Entry.LastModified,
                    RelevanceScore = candidate.Score,
                    Glyph = candidate.Entry.IsDirectory ? "\uE8B7" : null
                })
                .ToList();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets recently modified files from the index.
    /// </summary>
    public IReadOnlyList<SearchResultItem> GetRecentFiles(int count)
    {
        _indexLock.EnterReadLock();
        try
        {
            return _index
                .Where(item => !item.Value.IsDirectory)
                .OrderByDescending(item => item.Value.LastModified)
                .Take(count)
                .Select(item => new SearchResultItem
                {
                    Kind = SearchResultKind.File,
                    Title = item.Key[item.Value.FileNameStart..],
                    Subtitle = item.Value.DirectoryPath,
                    DetailPath = item.Key,
                    ModifiedAt = item.Value.LastModified
                })
                .ToList();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Derives the most frequently occurring parent folders from the index.
    /// Folders that host more indexed files are treated as "frequently used" and
    /// surfaced as recommendations.
    /// </summary>
    public IReadOnlyList<SearchResultItem> GetFrequentFolders(int count)
    {
        if (!IsIndexResident)
        {
            return [];
        }

        _indexLock.EnterReadLock();
        try
        {
            return _index
                .Where(item =>
                    !item.Value.IsDirectory &&
                    !string.IsNullOrWhiteSpace(item.Value.DirectoryPath))
                .GroupBy(item => item.Value.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Path = g.Key,
                    FileCount = g.Count(),
                    LastModified = g.Max(item => item.Value.LastModified)
                })
                .OrderByDescending(f => f.FileCount)
                .ThenByDescending(f => f.LastModified)
                .Take(count)
                .Select(f => new SearchResultItem
                {
                    Kind = SearchResultKind.Folder,
                    Title = Path.GetFileName(f.Path),
                    Subtitle = f.Path,
                    DetailPath = f.Path,
                    ModifiedAt = f.LastModified,
                    Glyph = "\uE8B7"
                })
                .ToList();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    private async Task ScanDirectoriesAsync(
        long epoch,
        CancellationToken token,
        bool watchersAlreadyArmed)
    {
        lock (_sessionStateLock)
        {
            if (!IsCurrentSession(epoch, token) ||
                Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
            {
                return;
            }
        }

        Volatile.Write(ref _scannedCount, 0);

        try
        {
            var (userDirs, driveRoots) = GetScanDirectories();
            var allRoots = userDirs.Concat(driveRoots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // A root removed from settings is an explicit deletion scope. Keep
            // this separate from scan outcomes so an offline/partial current root
            // never causes data loss, while removed roots are cleaned promptly.
            RemoveExplicitlyRemovedRoots(allRoots, epoch, token);
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }
            lock (_scanStateLock)
            {
                if (!IsCurrentSession(epoch, token))
                {
                    return;
                }

                _lastScanRoots.Clear();
                foreach (string root in allRoots)
                {
                    _lastScanRoots.Add(root);
                }
            }
            int scanGeneration = Interlocked.Increment(ref _scanGeneration);

            // Arm watchers before the first directory is enumerated. Events
            // that arrive during the scan are queued and applied after the
            // generation reconciliation, closing the startup/rebuild race.
            if (!watchersAlreadyArmed)
            {
                ClearScanWatcherChanges();
                SetupWatchers(userDirs, epoch, token);
            }

            // User directories: full-depth scan (these are the primary search targets).
            var scanOutcomes = new List<RootScanOutcome>(allRoots.Count);
            foreach (string directory in userDirs)
            {
                if (!IsCurrentSession(epoch, token))
                {
                    scanOutcomes.Add(new RootScanOutcome(directory, RootScanStatus.Canceled));
                    break;
                }

                if (GetResidentEntryCount() >= MaxIndexEntries)
                {
                    scanOutcomes.Add(new RootScanOutcome(directory, RootScanStatus.CapacityLimited));
                    continue;
                }

                if (!Directory.Exists(directory))
                {
                    scanOutcomes.Add(new RootScanOutcome(directory, RootScanStatus.Offline));
                    continue;
                }

                RootScanOutcome outcome = await Task.Run(
                    () => ScanDirectoryRecursive(
                        directory,
                        scanGeneration,
                        epoch,
                        token,
                        maxDepth: int.MaxValue),
                    token);
                scanOutcomes.Add(outcome);
            }

            // Drive roots: shallow scan (broad coverage without indexing millions of files).
            foreach (string drive in driveRoots)
            {
                if (!IsCurrentSession(epoch, token))
                {
                    scanOutcomes.Add(new RootScanOutcome(drive, RootScanStatus.Canceled));
                    break;
                }

                if (GetResidentEntryCount() >= MaxIndexEntries)
                {
                    scanOutcomes.Add(new RootScanOutcome(drive, RootScanStatus.CapacityLimited));
                    continue;
                }

                if (!Directory.Exists(drive))
                {
                    scanOutcomes.Add(new RootScanOutcome(drive, RootScanStatus.Offline));
                    continue;
                }

                RootScanOutcome outcome = await Task.Run(
                    () => ScanDirectoryRecursive(
                        drive,
                        scanGeneration,
                        epoch,
                        token,
                        maxDepth: DriveRootMaxDepth),
                    token);
                scanOutcomes.Add(outcome);
            }

            if (IsCurrentSession(epoch, token))
            {
                Volatile.Write(
                    ref _offlineRootCount,
                    scanOutcomes.Count(outcome => outcome.Status == RootScanStatus.Offline));
                Volatile.Write(
                    ref _partialRootCount,
                    scanOutcomes.Count(outcome => outcome.Status == RootScanStatus.Partial));
                Volatile.Write(
                    ref _scanOnlyRootCount,
                    scanOutcomes.Count(outcome => outcome.Status == RootScanStatus.ScanOnly));
                Volatile.Write(
                    ref _lastScanCapacityLimited,
                    scanOutcomes.Any(outcome => outcome.Status == RootScanStatus.CapacityLimited) ? 1 : 0);
                ReconcileIndex(
                    scanOutcomes
                        .Where(outcome => outcome.Status == RootScanStatus.Completed)
                        .Select(outcome => outcome.Root)
                        .ToList(),
                    scanGeneration,
                    epoch,
                    token);
                ApplyScanWatcherChanges(scanGeneration, epoch, token);
                if (!IsCurrentSession(epoch, token))
                {
                    return;
                }
                SaveIndex();
                string[] rootManifestSnapshot;
                lock (_scanStateLock)
                {
                    if (!IsCurrentSession(epoch, token))
                    {
                        return;
                    }

                    rootManifestSnapshot = _lastScanRoots.ToArray();
                }
                SaveRootManifest(rootManifestSnapshot);
                _lastScanTime = DateTime.Now;
                IndexUpdated?.Invoke();
                App.Log($"[SearchIndex] Indexing complete. {GetResidentEntryCount()} entries.");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Indexing error: {ex.Message}");
        }
        finally
        {
            lock (_sessionStateLock)
            {
                if (Interlocked.Read(ref _sessionEpoch) == epoch)
                {
                    Interlocked.Exchange(ref _isScanning, 0);
                    Volatile.Write(ref _isPaused, 0);
                    _pauseGate.Set();
                }
            }
        }
    }

    private void RemoveExplicitlyRemovedRoots(
        IReadOnlyCollection<string> currentRoots,
        long epoch,
        CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return;
        }

        List<string> removedRoots;
        lock (_scanStateLock)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            removedRoots = GetExplicitlyRemovedRoots(_lastScanRoots, currentRoots);
        }
        foreach (string removedRoot in removedRoots)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            RemoveEntriesUnderPath(removedRoot, epoch, token);
        }
    }

    internal static List<string> GetExplicitlyRemovedRoots(
        IEnumerable<string> previousRoots,
        IEnumerable<string> currentRoots)
    {
        var current = new HashSet<string>(currentRoots, StringComparer.OrdinalIgnoreCase);
        return previousRoots
            .Where(previous => !current.Contains(previous))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Removes indexed entries that live under a scanned root but were not observed
    /// in the latest scan (i.e., they were deleted or moved). Entries outside the
    /// current scan roots are left untouched.
    /// </summary>
    private void ReconcileIndex(
        List<string> scannedRoots,
        int scanGeneration,
        long epoch,
        CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return;
        }

        var staleKeys = new List<string>();

        _indexLock.EnterWriteLock();
        try
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            foreach (var (path, entry) in _index)
            {
                if (entry.ScanGeneration == scanGeneration)
                {
                    continue;
                }

                bool underScannedRoot = scannedRoots.Any(root =>
                    IsSameOrDescendant(path, root));

                if (underScannedRoot)
                {
                    staleKeys.Add(path);
                }
            }

            foreach (string key in staleKeys)
            {
                _index.Remove(key);
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }

        if (staleKeys.Count > 0)
        {
            App.Log($"[SearchIndex] Reconciled {staleKeys.Count} stale entries.");
        }
    }

    private RootScanOutcome ScanDirectoryRecursive(
        string rootPath,
        int scanGeneration,
        long epoch,
        CancellationToken token,
        int maxDepth = int.MaxValue)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return new RootScanOutcome(rootPath, RootScanStatus.Canceled);
        }

        if (!Directory.Exists(rootPath))
        {
            return new RootScanOutcome(rootPath, RootScanStatus.Offline);
        }

        try
        {
            if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            {
                // Never traverse links/junctions. They can escape the configured
                // root or form cycles, and are intentionally not indexed.
                return new RootScanOutcome(rootPath, RootScanStatus.Partial);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new RootScanOutcome(rootPath, RootScanStatus.Partial);
        }
        catch (IOException)
        {
            return new RootScanOutcome(rootPath, RootScanStatus.Partial);
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));
        int progressCounter = 0;
        bool hadErrors = false;

        var skipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "node_modules",
            ".git", "obj", "bin", ".vs", ".artifacts",
            "Windows", "ProgramData", "Program Files", "Program Files (x86)",
            "Recovery", "PerfLogs", "Config.Msi", "MSOCache", "WinSxS",
            "servicing", "assembly", "Intel", "AMD"
        };

        while (queue.Count > 0)
        {
            if (!IsCurrentSession(epoch, token) || GetResidentEntryCount() >= MaxIndexEntries)
            {
                return new RootScanOutcome(
                    rootPath,
                    !IsCurrentSession(epoch, token)
                        ? RootScanStatus.Canceled
                        : RootScanStatus.CapacityLimited);
            }

            // Honor pause: block until resumed or cancelled.
            _pauseGate.Wait(token);

            var (current, depth) = queue.Dequeue();

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(current))
                {
                    if (!IsCurrentSession(epoch, token) || GetResidentEntryCount() >= MaxIndexEntries)
                    {
                        return new RootScanOutcome(
                            rootPath,
                            !IsCurrentSession(epoch, token)
                                ? RootScanStatus.Canceled
                                : RootScanStatus.CapacityLimited);
                    }

                    IndexEntryResult addResult = TryAddEntryCore(
                        file,
                        isDirectory: false,
                        scanGeneration,
                        epoch,
                        token);
                    if (addResult.Status == IndexEntryStatus.SessionExpired)
                    {
                        return new RootScanOutcome(rootPath, RootScanStatus.Canceled);
                    }
                    if (addResult.Status == IndexEntryStatus.CapacityLimited)
                    {
                        return new RootScanOutcome(rootPath, RootScanStatus.CapacityLimited);
                    }
                    if (addResult.Status == IndexEntryStatus.Failed)
                    {
                        hadErrors = true;
                    }

                    // Report progress every 200 files to avoid flooding the UI thread.
                    if (++progressCounter % 200 == 0)
                    {
                        int indexedCount = GetResidentEntryCount();
                        Volatile.Write(ref _scannedCount, indexedCount);
                        ProgressChanged?.Invoke(indexedCount);
                    }
                }

                // Only recurse into subdirectories if we haven't hit the depth limit.
                if (depth < maxDepth)
                {
                    foreach (string dir in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            hadErrors = true;
                            continue;
                        }
                        catch (IOException)
                        {
                            hadErrors = true;
                            continue;
                        }

                        string dirName = Path.GetFileName(dir);
                        if (skipDirectories.Contains(dirName) ||
                            (dirName.StartsWith('.') && dirName.Length > 1))
                        {
                            continue;
                        }

                        IndexEntryResult addResult = TryAddEntryCore(
                            dir,
                            isDirectory: true,
                            scanGeneration,
                            epoch,
                            token);
                        if (addResult.Status == IndexEntryStatus.SessionExpired)
                        {
                            return new RootScanOutcome(rootPath, RootScanStatus.Canceled);
                        }
                        if (addResult.Status == IndexEntryStatus.CapacityLimited)
                        {
                            return new RootScanOutcome(rootPath, RootScanStatus.CapacityLimited);
                        }
                        if (addResult.Status == IndexEntryStatus.Failed)
                        {
                            hadErrors = true;
                        }
                        queue.Enqueue((dir, depth + 1));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
                hadErrors = true;
            }
            catch (IOException)
            {
                // Skip directories with I/O errors
                hadErrors = true;
            }
        }

        // Fixed-drive fallback scans are intentionally shallow. They provide
        // broad discovery but do not cover the entire root, so they must never
        // be used as an authoritative stale-entry reconciliation scope.
        if (maxDepth != int.MaxValue)
        {
            return new RootScanOutcome(
                rootPath,
                hadErrors ? RootScanStatus.Partial : RootScanStatus.ScanOnly);
        }

        return new RootScanOutcome(
            rootPath,
            hadErrors ? RootScanStatus.Partial : RootScanStatus.Completed);
    }

    private void LoadRootManifest()
    {
        try
        {
            if (!File.Exists(_rootsManifestPath))
            {
                return;
            }

            string json = File.ReadAllText(_rootsManifestPath);
            RootManifest? manifest = JsonSerializer.Deserialize<RootManifest>(json, s_jsonOptions);
            if (manifest?.Roots is not { Count: > 0 })
            {
                return;
            }

            lock (_scanStateLock)
            {
                _lastScanRoots.Clear();
                foreach (string root in NormalizeRoots(manifest.Roots))
                {
                    _lastScanRoots.Add(root);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to load root manifest: {ex.Message}");
        }
    }

    private void SaveRootManifest(IEnumerable<string> roots)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_rootsManifestPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = _rootsManifestPath + ".tmp";
            string json = JsonSerializer.Serialize(
                new RootManifest { Roots = NormalizeRoots(roots) },
                s_jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _rootsManifestPath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to save root manifest: {ex.Message}");
        }
    }

    private bool TryAddEntry(string path, bool isDirectory, int? scanGeneration = null)
    {
        return TryAddEntryCore(
            path,
            isDirectory,
            scanGeneration,
            sessionEpoch: null,
            CancellationToken.None).ResidentMutation;
    }

    private IndexEntryResult TryAddEntryCore(
        string path,
        bool isDirectory,
        int? scanGeneration,
        long? sessionEpoch,
        CancellationToken token)
    {
        if (Volatile.Read(ref _indexingEnabled) == 0 ||
            (sessionEpoch is long epoch && !IsCurrentSession(epoch, token)))
        {
            return new IndexEntryResult(IndexEntryStatus.SessionExpired, ResidentMutation: false);
        }

        try
        {
            var info = new FileInfo(path);
            DateTime lastModified = info.LastWriteTime;
            int generation = scanGeneration ?? Volatile.Read(ref _scanGeneration);
            bool residentMutation;

            _indexLock.EnterWriteLock();
            try
            {
                if (Volatile.Read(ref _indexingEnabled) == 0 ||
                    (sessionEpoch is long lockedEpoch && !IsCurrentSession(lockedEpoch, token)))
                {
                    return new IndexEntryResult(IndexEntryStatus.SessionExpired, ResidentMutation: false);
                }

                residentMutation = IsIndexResident;
                if (residentMutation)
                {
                    if (_index.Count >= MaxIndexEntries &&
                        !_index.ContainsKey(path))
                    {
                        return new IndexEntryResult(IndexEntryStatus.CapacityLimited, ResidentMutation: false);
                    }

                    _index[path] = CreateIndexedEntry(
                        path,
                        isDirectory,
                        lastModified,
                        generation,
                        _directoryPool);
                }
                else
                {
                    _pendingChanges[path] = new PendingIndexChange(
                        IsDeleted: false,
                        isDirectory,
                        lastModified,
                        generation);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            if (!residentMutation)
            {
                MarkIndexDirty();
            }

            return new IndexEntryResult(IndexEntryStatus.Added, residentMutation);
        }
        catch
        {
            // The caller performing a scan must retain the previous entry by
            // marking its root partial. Event handlers simply ignore the item
            // and wait for the next watcher/lifecycle reconciliation pass.
            return new IndexEntryResult(IndexEntryStatus.Failed, ResidentMutation: false);
        }
    }

    private static IndexedFileEntry CreateIndexedEntry(
        string fullPath,
        bool isDirectory,
        DateTime lastModified,
        int scanGeneration,
        Dictionary<string, string> directoryPool)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(fullPath.AsSpan());
        int fileNameStart = fullPath.Length - fileName.Length;
        string directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        if (directoryPath.Length > 0 &&
            directoryPool.TryGetValue(directoryPath, out string? pooledDirectory))
        {
            directoryPath = pooledDirectory;
        }
        else if (directoryPath.Length > 0)
        {
            directoryPool[directoryPath] = directoryPath;
        }

        return new IndexedFileEntry(
            directoryPath,
            fileNameStart,
            isDirectory,
            lastModified,
            scanGeneration);
    }

    private bool RemoveEntry(string path)
    {
        if (Volatile.Read(ref _indexingEnabled) == 0)
        {
            return false;
        }

        bool residentMutation;
        _indexLock.EnterWriteLock();
        try
        {
            residentMutation = IsIndexResident;
            if (residentMutation)
            {
                _index.Remove(path);
            }
            else
            {
                _pendingChanges[path] = new PendingIndexChange(
                    IsDeleted: true,
                    IsDirectory: false,
                    LastModified: DateTime.MinValue,
                    ScanGeneration: Volatile.Read(ref _scanGeneration));
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }

        if (!residentMutation)
        {
            MarkIndexDirty();
        }

        return residentMutation;
    }

    private void MarkIndexDirty()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_dirtyMarkerPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var marker = new FileStream(
                _dirtyMarkerPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite);
        }
        catch
        {
            // The in-memory delta still preserves this session's correctness.
        }
    }

    private (List<string> UserDirs, List<string> DriveRoots) GetScanDirectories()
    {
        var userDirs = new List<string>();
        var driveRoots = new List<string>();

        // Default: user profile directories (full-depth scan)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string[] defaultDirs =
            [
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "Pictures"),
                Path.Combine(userProfile, "Music"),
                Path.Combine(userProfile, "Videos"),
                Path.Combine(userProfile, "DeskBox")
            ];

            // Keep configured roots even while temporarily offline. Their scan
            // outcome is recorded as Offline, which preserves prior entries.
            userDirs.AddRange(defaultDirs);
        }

        // Applications and files explicitly surfaced by DeskBox should be searchable
        // even when they live outside the standard user libraries.
        foreach (var widget in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                userDirs.Add(widget.MappedFolderPath);
            }

            foreach (string parent in widget.Items
                         .Select(item => Path.GetDirectoryName(item.Path))
                         .OfType<string>()
                         .Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                userDirs.Add(parent);
            }
        }

        string[] applicationRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];
        userDirs.AddRange(applicationRoots.Where(path => !string.IsNullOrWhiteSpace(path)));

        // Custom paths from settings (full-depth scan)
        var customPaths = _settingsService.Settings.SearchCustomIndexPaths;
        if (customPaths is { Count: > 0 })
        {
            userDirs.AddRange(customPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        // Broad fallback coverage: add every fixed drive root (shallow scan).
        // The USN journal index is preferred when available (it needs elevation);
        // this directory scan keeps near-full-disk coverage without admin.
        // System directories are excluded by name in ScanDirectoryRecursive.
        foreach (string drive in Directory.GetLogicalDrives())
        {
            try
            {
                var info = new DriveInfo(drive);
                if (info.IsReady && info.DriveType == DriveType.Fixed)
                {
                    driveRoots.Add(drive);
                }
            }
            catch
            {
                // Skip drives that cannot be queried.
            }
        }

        return (NormalizeRoots(userDirs), NormalizeRoots(driveRoots));
    }

    private static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRoot)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizeRoot(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (!string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            }

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    private void SetupWatchers(
        List<string> directories,
        long epoch,
        CancellationToken token)
    {
        lock (_watchersLock)
        {
            ClearWatchersCore();
            _watcherCreationFailures.Clear();
            if (_isDisposed || !IsCurrentSession(epoch, token))
            {
                return;
            }

            foreach (string dir in directories)
            {
                try
                {
                    AddWatcherCore(dir);
                }
                catch (Exception ex)
                {
                    RecordWatcherCreationFailure(dir, ex);
                }
            }

            if (_watcherCreationFailures.Count > 0)
            {
                ScheduleFailedWatcherRetry(epoch, token);
            }
        }
    }

    private void AddWatcherCore(string dir)
    {
        var watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite,
            InternalBufferSize = WatcherBufferSizeBytes,
            EnableRaisingEvents = false
        };

        try
        {
            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemRenamed;
            watcher.Error += OnWatcherError;
            _watchers.Add(watcher);
            // Subscribe before enabling events. Otherwise a very fast create/rename
            // during startup can be lost permanently.
            watcher.EnableRaisingEvents = true;
            _watcherCreationFailures.Remove(dir);
        }
        catch
        {
            _watchers.Remove(watcher);
            watcher.Dispose();
            throw;
        }
    }

    private void RecordWatcherCreationFailure(string path, Exception exception)
    {
        string normalized = NormalizeRoot(path) ?? path;
        if (_watcherCreationFailures.TryGetValue(normalized, out WatcherFailureState? prior))
        {
            _watcherCreationFailures[normalized] = prior with
            {
                Attempts = prior.Attempts + 1,
                LastError = exception.Message,
                LastAttempt = DateTime.Now
            };
        }
        else
        {
            _watcherCreationFailures[normalized] = new WatcherFailureState(
                1,
                exception.Message,
                DateTime.Now);
        }

        App.Log($"[SearchIndex] Watcher creation failed for '{normalized}': {exception.Message}");
    }

    private void RetryFailedWatchers(long epoch, CancellationToken token)
    {
        lock (_watchersLock)
        {
            if (_watcherCreationFailures.Count == 0 ||
                _isDisposed ||
                !IsCurrentSession(epoch, token))
            {
                return;
            }

            foreach (string path in _watcherCreationFailures.Keys.ToList())
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                try
                {
                    AddWatcherCore(path);
                }
                catch (Exception ex)
                {
                    RecordWatcherCreationFailure(path, ex);
                }
            }
        }
    }

    private void ScheduleFailedWatcherRetry(long epoch, CancellationToken sessionToken)
    {
        if (Interlocked.Exchange(ref _watcherRetryScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            int attempt = 0;
            try
            {
                while (!_isDisposed &&
                       IsCurrentSession(epoch, sessionToken))
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt++))),
                        sessionToken);
                    RetryFailedWatchers(epoch, sessionToken);
                    lock (_watchersLock)
                    {
                        if (_watcherCreationFailures.Count == 0)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Volatile.Write(ref _watcherRetryScheduled, 0);
            }
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (!IsActiveWatcher(sender))
        {
            return;
        }

        App.Log($"[SearchIndex] File-system watcher fault: {e.GetException().Message}");
        string? affectedRoot = sender is FileSystemWatcher watcher ? watcher.Path : null;
        ScheduleWatcherRecovery("watcher-error", affectedRoot);
    }

    /// <summary>
    /// Reconciles the index after resume, unlock, or an Explorer restart. The
    /// same debounced path is used for buffer overflow errors so only one
    /// recovery scan can be in flight.
    /// </summary>
    public void RecoverAfterLifecycleChange(string reason)
    {
        if (_isDisposed || Volatile.Read(ref _indexingEnabled) == 0)
        {
            return;
        }

        App.Log($"[SearchIndex] Lifecycle recovery requested: {reason}");
        ScheduleWatcherRecovery($"lifecycle:{reason}");
    }

    private void ScheduleWatcherRecovery(string reason, string? affectedRoot = null)
    {
        if (_isDisposed || Volatile.Read(ref _indexingEnabled) == 0)
        {
            return;
        }

        lock (_watcherRecoveryLock)
        {
            _watcherRecoveryCts?.Cancel();
            _watcherRecoveryCts?.Dispose();
            _watcherRecoveryCts = new CancellationTokenSource();
            CancellationToken token = _watcherRecoveryCts.Token;
            long epoch = Interlocked.Read(ref _sessionEpoch);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WatcherRecoveryDelay, token);
                    if (_isDisposed || !IsCurrentSession(epoch, token))
                    {
                        return;
                    }

                    Task? scanTask = _scanTask;
                    if (scanTask is not null)
                    {
                        try
                        {
                            await scanTask.WaitAsync(token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            return;
                        }
                        catch
                        {
                            // A failed scan does not prevent the recovery pass.
                        }
                    }

                    if (!_isDisposed && IsCurrentSession(epoch, token))
                    {
                        Interlocked.Increment(ref _watcherRecoveryCount);
                        _lastWatcherRecoveryTime = DateTime.Now;
                        RetryFailedWatchers(epoch, token);
                        if (string.Equals(reason, "watcher-error", StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(affectedRoot) &&
                            Directory.Exists(affectedRoot) &&
                            IsIndexResident)
                        {
                            int recoveryGeneration = Interlocked.Increment(ref _scanGeneration);
                            App.Log($"[SearchIndex] Reconciling watcher root after overflow: {affectedRoot}");
                            RootScanOutcome outcome = await Task.Run(
                                () => ScanDirectoryRecursive(
                                    affectedRoot,
                                    recoveryGeneration,
                                    epoch,
                                    token,
                                    maxDepth: int.MaxValue),
                                token);
                            if (!IsCurrentSession(epoch, token))
                            {
                                return;
                            }

                            if (ShouldReconcileRoot(outcome.Status))
                            {
                                ReconcileIndex(
                                    [affectedRoot],
                                    recoveryGeneration,
                                    epoch,
                                    token);
                            }
                            else
                            {
                                App.Log(
                                    $"[SearchIndex] Watcher recovery for '{affectedRoot}' " +
                                    $"finished as {outcome.Status}; retaining previous entries.");
                            }
                            SaveIndex();
                            IndexUpdated?.Invoke();
                        }
                        else
                        {
                            // Lifecycle recovery and an overflow whose root is
                            // currently offline must never clear the resident
                            // index first.  Run the normal status-aware scan so
                            // Completed roots reconcile while Offline/Partial/
                            // CapacityLimited roots retain their last results.
                            App.Log(
                                $"[SearchIndex] Running non-destructive reconciliation after {reason}.");
                            await ScanDirectoriesAsync(
                                epoch,
                                token,
                                watchersAlreadyArmed: true);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    App.Log($"[SearchIndex] Watcher recovery failed: {ex.Message}");
                }
            }, token);
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsActiveWatcher(sender))
        {
            return;
        }

        if (IsScanning)
        {
            QueueScanWatcherChange(new PendingWatcherChange(
                e.ChangeType,
                e.FullPath,
                OldFullPath: null));
            return;
        }

        ApplyFileSystemChange(e.ChangeType, e.FullPath);
    }

    private void ApplyFileSystemChange(WatcherChangeTypes changeType, string fullPath)
    {
        bool residentMutation;
        if (changeType == WatcherChangeTypes.Deleted)
        {
            // A directory delete produces one event for the directory, not
            // necessarily one event for every descendant. Remove the entire
            // indexed subtree so stale search results cannot survive.
            residentMutation = RemoveEntriesUnderPath(fullPath);
        }
        else
        {
            residentMutation = TryAddEntry(fullPath, Directory.Exists(fullPath));
        }

        if (residentMutation)
        {
            ScheduleSave();
        }

        IndexUpdated?.Invoke();
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsActiveWatcher(sender))
        {
            return;
        }

        if (IsScanning)
        {
            QueueScanWatcherChange(new PendingWatcherChange(
                WatcherChangeTypes.Renamed,
                e.FullPath,
                e.OldFullPath));
            return;
        }

        ApplyFileSystemRenamed(e.FullPath, e.OldFullPath);
    }

    private void ApplyFileSystemRenamed(string fullPath, string oldFullPath)
    {
        bool residentMutation = RemoveEntriesUnderPath(oldFullPath);
        residentMutation |= TryAddEntry(fullPath, Directory.Exists(fullPath));
        if (Directory.Exists(fullPath))
        {
            long epoch = Interlocked.Read(ref _sessionEpoch);
            CancellationToken token = _scanCts?.Token ?? CancellationToken.None;
            int scanGeneration = Volatile.Read(ref _scanGeneration);
            // Recursive watchers do not reliably replay all children when a
            // directory is renamed. Reconcile the new subtree explicitly.
            _ = Task.Run(() =>
            {
                try
                {
                    RootScanOutcome outcome = ScanDirectoryRecursive(
                        fullPath,
                        scanGeneration,
                        epoch,
                        token,
                        maxDepth: int.MaxValue);
                    if (!IsCurrentSession(epoch, token))
                    {
                        return;
                    }

                    if (outcome.Status != RootScanStatus.Completed)
                    {
                        App.Log(
                            $"[SearchIndex] Renamed subtree scan for '{fullPath}' " +
                            $"finished as {outcome.Status}; retaining observed entries only.");
                    }
                    IndexUpdated?.Invoke();
                    ScheduleSave();
                }
                catch (Exception ex)
                {
                    App.Log($"[SearchIndex] Renamed subtree reconciliation failed: {ex.Message}");
                }
            });
        }
        if (residentMutation)
        {
            ScheduleSave();
        }

        IndexUpdated?.Invoke();
    }

    private void ClearScanWatcherChanges()
    {
        lock (_scanWatcherChangesLock)
        {
            _scanWatcherChanges.Clear();
            _scanWatcherChangesOverflowed = false;
        }
    }

    private void QueueScanWatcherChange(PendingWatcherChange change)
    {
        lock (_scanWatcherChangesLock)
        {
            if (_scanWatcherChangesOverflowed)
            {
                return;
            }

            if (_scanWatcherChanges.Count >= 8192)
            {
                _scanWatcherChangesOverflowed = true;
                _scanWatcherChanges.Clear();
                App.Log("[SearchIndex] Startup scan watcher queue overflowed; scheduling reconciliation.");
                return;
            }

            _scanWatcherChanges.Add(change);
        }
    }

    private void ApplyScanWatcherChanges(
        int scanGeneration,
        long epoch,
        CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return;
        }

        List<PendingWatcherChange> changes;
        bool overflowed;
        lock (_scanWatcherChangesLock)
        {
            changes = _scanWatcherChanges.ToList();
            overflowed = _scanWatcherChangesOverflowed;
            _scanWatcherChanges.Clear();
            _scanWatcherChangesOverflowed = false;
        }

        foreach (PendingWatcherChange change in changes)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            if (change.ChangeType == WatcherChangeTypes.Renamed &&
                !string.IsNullOrWhiteSpace(change.OldFullPath))
            {
                ApplyFileSystemRenamed(change.FullPath, change.OldFullPath);
            }
            else
            {
                ApplyFileSystemChange(change.ChangeType, change.FullPath);
            }
        }

        if (overflowed)
        {
            // The queue itself cannot be replayed, so the already-installed
            // watchers will report an Error event and trigger root recovery.
            // Keep a generation marker in the log for postmortem diagnostics.
            App.Log($"[SearchIndex] Startup scan changes exceeded the queue at generation {scanGeneration}.");
            ScheduleWatcherRecovery("startup-scan-overflow");
        }
    }

    private bool RemoveEntriesUnderPath(
        string path,
        long? sessionEpoch = null,
        CancellationToken token = default)
    {
        if (sessionEpoch is long epoch && !IsCurrentSession(epoch, token))
        {
            return false;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }

        bool residentMutation;
        _indexLock.EnterWriteLock();
        try
        {
            if (sessionEpoch is long lockedEpoch && !IsCurrentSession(lockedEpoch, token))
            {
                return false;
            }

            residentMutation = IsIndexResident;
            List<string> matchingPaths = (residentMutation
                    ? _index.Keys.ToList()
                    : _pendingChanges.Keys.ToList())
                .Where(candidate => IsSameOrDescendant(candidate, normalizedPath))
                .ToList();

            if (residentMutation)
            {
                foreach (string matchingPath in matchingPaths)
                {
                    _index.Remove(matchingPath);
                }
            }
            else
            {
                foreach (string matchingPath in matchingPaths)
                {
                    _pendingChanges[matchingPath] = new PendingIndexChange(
                        IsDeleted: true,
                        IsDirectory: false,
                        LastModified: DateTime.MinValue,
                        ScanGeneration: Volatile.Read(ref _scanGeneration));
                }

                if (matchingPaths.Count == 0)
                {
                    _pendingChanges[normalizedPath] = new PendingIndexChange(
                        IsDeleted: true,
                        IsDirectory: true,
                        LastModified: DateTime.MinValue,
                        ScanGeneration: Volatile.Read(ref _scanGeneration));
                }
            }

            if (matchingPaths.Count == 0 && residentMutation)
            {
                // Keep the original mutation semantics: a missing exact key
                // is not a persistence change.
                return false;
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }

        if (!residentMutation)
        {
            MarkIndexDirty();
        }

        return true;
    }

    internal static bool IsSameOrDescendant(string candidate, string parent)
    {
        string? normalizedCandidate = NormalizeRoot(candidate);
        string? normalizedParent = NormalizeRoot(parent);
        if (normalizedCandidate is null || normalizedParent is null)
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedParent.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentSession(long epoch, CancellationToken token)
    {
        return IsSessionCurrent(
            epoch,
            Interlocked.Read(ref _sessionEpoch),
            Volatile.Read(ref _indexingEnabled) == 1,
            token.IsCancellationRequested);
    }

    internal static bool IsSessionCurrent(
        long expectedEpoch,
        long currentEpoch,
        bool indexingEnabled,
        bool cancellationRequested)
    {
        return expectedEpoch == currentEpoch && indexingEnabled && !cancellationRequested;
    }

    internal static bool ShouldReconcileRoot(RootScanStatus status)
    {
        return status == RootScanStatus.Completed;
    }

    internal static double ComputeRelevance(string fileName, string query)
    {
        return ComputeRelevance(fileName.AsSpan(), query.AsSpan());
    }

    private static double ComputeRelevance(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> query)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100.0;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80.0;
        }

        ReadOnlySpan<char> nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExt.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 90.0;
        }

        if (nameWithoutExt.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70.0;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50.0;
        }

        return 0;
    }

    private int GetVisibleEntryCount()
    {
        _indexLock.EnterReadLock();
        try
        {
            return IsIndexResident
                ? _index.Count
                : _persistedEntryCount;
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    private int GetResidentEntryCount()
    {
        _indexLock.EnterReadLock();
        try
        {
            return IsIndexResident ? _index.Count : 0;
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    private void ResetResidentIndex()
    {
        _indexLock.EnterWriteLock();
        try
        {
            _index = new Dictionary<string, IndexedFileEntry>(
                StringComparer.OrdinalIgnoreCase);
            _directoryPool = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _pendingChanges.Clear();
            _persistedEntryCount = 0;
            Volatile.Write(ref _isIndexResident, true);
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private void EnsureEmptyResidentIndex()
    {
        if (IsIndexResident)
        {
            return;
        }

        _indexLock.EnterWriteLock();
        try
        {
            if (!IsIndexResident)
            {
                _index = new Dictionary<string, IndexedFileEntry>(
                    StringComparer.OrdinalIgnoreCase);
                _directoryPool = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                _pendingChanges.Clear();
                _persistedEntryCount = 0;
                Volatile.Write(ref _isIndexResident, true);
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private void ClearIndexForStop()
    {
        _indexLock.EnterWriteLock();
        try
        {
            _index = new Dictionary<string, IndexedFileEntry>(
                StringComparer.OrdinalIgnoreCase);
            _directoryPool = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _pendingChanges.Clear();
            _persistedEntryCount = 0;
            Volatile.Write(ref _isIndexResident, false);
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private static void WriteUtf8StringSegment(
        BinaryWriter writer,
        ReadOnlySpan<char> value,
        ref byte[] buffer)
    {
        int requiredBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (requiredBytes > buffer.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(requiredBytes);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = larger;
        }

        int byteCount = Encoding.UTF8.GetBytes(value, buffer);
        writer.Write(byteCount);
        writer.Write(buffer, 0, byteCount);
    }

    private static string ReadUtf8String(BinaryReader reader)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid compact search index string length.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(byteCount, 1));
        try
        {
            reader.BaseStream.ReadExactly(buffer.AsSpan(0, byteCount));
            return Encoding.UTF8.GetString(buffer, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ClearWatchers()
    {
        lock (_watchersLock)
        {
            ClearWatchersCore();
        }
    }

    private void ClearWatchersCore()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileSystemChanged;
            watcher.Deleted -= OnFileSystemChanged;
            watcher.Renamed -= OnFileSystemRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private bool IsActiveWatcher(object sender)
    {
        lock (_watchersLock)
        {
            return sender is FileSystemWatcher watcher && _watchers.Contains(watcher);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopIndexing();
        lock (_watcherRecoveryLock)
        {
            _watcherRecoveryCts?.Cancel();
            _watcherRecoveryCts?.Dispose();
            _watcherRecoveryCts = null;
        }
        _scanCts?.Dispose();
        if (_scanTask is { IsCompleted: false } scanTask)
        {
            _ = scanTask.ContinueWith(
                _ => _pauseGate.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _pauseGate.Dispose();
        }
    }

    private sealed record LoadedIndex(
        Dictionary<string, IndexedFileEntry> Index,
        Dictionary<string, string> DirectoryPool,
        bool WasLegacyJson);

    private readonly record struct PendingIndexChange(
        bool IsDeleted,
        bool IsDirectory,
        DateTime LastModified,
        int ScanGeneration);

    private readonly record struct PendingWatcherChange(
        WatcherChangeTypes ChangeType,
        string FullPath,
        string? OldFullPath);

    private readonly record struct RootScanOutcome(
        string Root,
        RootScanStatus Status);

    internal enum RootScanStatus
    {
        Completed,
        Offline,
        Partial,
        ScanOnly,
        CapacityLimited,
        Canceled
    }

    private readonly record struct IndexEntryResult(
        IndexEntryStatus Status,
        bool ResidentMutation);

    private enum IndexEntryStatus
    {
        Added,
        Failed,
        CapacityLimited,
        SessionExpired
    }

    private sealed record WatcherFailureState(
        int Attempts,
        string LastError,
        DateTime LastAttempt);

    private readonly record struct IndexedFileEntry(
        string DirectoryPath,
        int FileNameStart,
        bool IsDirectory,
        DateTime LastModified,
        int ScanGeneration);

    private readonly record struct SearchCandidate(
        string FullPath,
        IndexedFileEntry Entry,
        double Score);

    /// <summary>
    /// On-disk representation of the filename index.
    /// </summary>
    private sealed class PersistedIndex
    {
        public List<Entry> Entries { get; set; } = [];

        public sealed class Entry
        {
            public string FileName { get; set; } = string.Empty;
            public string DirectoryPath { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public DateTime LastModified { get; set; }
        }
    }

    private sealed class RootManifest
    {
        public List<string> Roots { get; set; } = [];
    }
}

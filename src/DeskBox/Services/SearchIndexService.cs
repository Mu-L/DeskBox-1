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

    private readonly SettingsService _settingsService;
    private readonly ReaderWriterLockSlim _indexLock = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _residencyGate = new(1, 1);
    private readonly object _saveLock = new();
    private readonly object _saveScheduleLock = new();
    private readonly object _watchersLock = new();
    private Dictionary<string, IndexedFileEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _directoryPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingIndexChange> _pendingChanges =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string _storePath;
    private readonly string _dirtyMarkerPath;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _saveCts;
    private Task? _scanTask;
    private bool _isDisposed;
    private int _isScanning;
    private int _isLoading;
    private int _isPaused;
    private int _indexingEnabled;
    private int _scannedCount;
    private int _scanGeneration;
    private bool _forceFullScan;
    private bool _isIndexResident;
    private int _persistedEntryCount;
    private DateTime? _lastScanTime;

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
        if (_isDisposed ||
            IsScanning ||
            !_settingsService.Settings.SearchCustomIndexerEnabled)
        {
            return;
        }

        Volatile.Write(ref _indexingEnabled, 1);
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
        if (!_forceFullScan &&
            residentCount > 0 &&
            TryGetFreshPersistedIndexTime(out DateTime persistedAt))
        {
            var (userDirs, _) = GetScanDirectories();
            SetupWatchers(userDirs);
            _lastScanTime = persistedAt;
            IndexUpdated?.Invoke();
            App.Log(
                $"[SearchIndex] Reusing fresh persisted index with {residentCount} entries; " +
                "full startup scan skipped.");
            return;
        }

        _forceFullScan = false;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        _scanTask = Task.Run(() => ScanDirectoriesAsync(token), token);
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
        Volatile.Write(ref _indexingEnabled, 0);
        _scanCts?.Cancel();
        CancelScheduledSave();
        ClearWatchers();
        ClearIndexForStop();
        Volatile.Write(ref _scannedCount, 0);
        _lastScanTime = null;
        Volatile.Write(ref _isPaused, 0);
        _pauseGate.Set();
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

    private async Task ScanDirectoriesAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _indexingEnabled) == 0 ||
            Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _scannedCount, 0);

        try
        {
            var (userDirs, driveRoots) = GetScanDirectories();
            int scanGeneration = Interlocked.Increment(ref _scanGeneration);

            // User directories: full-depth scan (these are the primary search targets).
            foreach (string directory in userDirs)
            {
                if (token.IsCancellationRequested || GetResidentEntryCount() >= MaxIndexEntries)
                {
                    break;
                }

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                await Task.Run(
                    () => ScanDirectoryRecursive(directory, scanGeneration, token, maxDepth: int.MaxValue),
                    token);
            }

            // Drive roots: shallow scan (broad coverage without indexing millions of files).
            foreach (string drive in driveRoots)
            {
                if (token.IsCancellationRequested || GetResidentEntryCount() >= MaxIndexEntries)
                {
                    break;
                }

                if (!Directory.Exists(drive))
                {
                    continue;
                }

                await Task.Run(
                    () => ScanDirectoryRecursive(drive, scanGeneration, token, maxDepth: DriveRootMaxDepth),
                    token);
            }

            if (!token.IsCancellationRequested)
            {
                var allRoots = new List<string>(userDirs);
                allRoots.AddRange(driveRoots);
                ReconcileIndex(allRoots, scanGeneration);
                SetupWatchers(userDirs); // Only watch user directories (drive roots generate too many events)
                SaveIndex();
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
            Interlocked.Exchange(ref _isScanning, 0);
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>
    /// Removes indexed entries that live under a scanned root but were not observed
    /// in the latest scan (i.e., they were deleted or moved). Entries outside the
    /// current scan roots are left untouched.
    /// </summary>
    private void ReconcileIndex(List<string> scannedRoots, int scanGeneration)
    {
        var staleKeys = new List<string>();

        _indexLock.EnterWriteLock();
        try
        {
            foreach (var (path, entry) in _index)
            {
                if (entry.ScanGeneration == scanGeneration)
                {
                    continue;
                }

                bool underScannedRoot = scannedRoots.Any(root =>
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

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

    private void ScanDirectoryRecursive(
        string rootPath,
        int scanGeneration,
        CancellationToken token,
        int maxDepth = int.MaxValue)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));
        int progressCounter = 0;

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
            if (token.IsCancellationRequested || GetResidentEntryCount() >= MaxIndexEntries)
            {
                return;
            }

            // Honor pause: block until resumed or cancelled.
            _pauseGate.Wait(token);

            var (current, depth) = queue.Dequeue();

            try
            {
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    if (token.IsCancellationRequested || GetResidentEntryCount() >= MaxIndexEntries)
                    {
                        return;
                    }

                    TryAddEntry(file, isDirectory: false, scanGeneration);

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
                        string dirName = Path.GetFileName(dir);
                        if (skipDirectories.Contains(dirName) ||
                            (dirName.StartsWith('.') && dirName.Length > 1))
                        {
                            continue;
                        }

                        TryAddEntry(dir, isDirectory: true, scanGeneration);
                        queue.Enqueue((dir, depth + 1));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (IOException)
            {
                // Skip directories with I/O errors
            }
        }
    }

    private bool TryAddEntry(string path, bool isDirectory, int? scanGeneration = null)
    {
        if (Volatile.Read(ref _indexingEnabled) == 0)
        {
            return false;
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
                residentMutation = IsIndexResident;
                if (residentMutation)
                {
                    if (_index.Count >= MaxIndexEntries &&
                        !_index.ContainsKey(path))
                    {
                        return true;
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

            return residentMutation;
        }
        catch
        {
            // Skip entries we can't stat
            return false;
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

            userDirs.AddRange(defaultDirs.Where(Directory.Exists));
        }

        // Applications and files explicitly surfaced by DeskBox should be searchable
        // even when they live outside the standard user libraries.
        foreach (var widget in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                Directory.Exists(widget.MappedFolderPath))
            {
                userDirs.Add(widget.MappedFolderPath);
            }

            foreach (string parent in widget.Items
                         .Select(item => Path.GetDirectoryName(item.Path))
                         .OfType<string>()
                         .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
            {
                userDirs.Add(parent);
            }
        }

        string[] applicationRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];
        userDirs.AddRange(applicationRoots.Where(path =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)));

        // Custom paths from settings (full-depth scan)
        var customPaths = _settingsService.Settings.SearchCustomIndexPaths;
        if (customPaths is { Count: > 0 })
        {
            userDirs.AddRange(customPaths.Where(Directory.Exists));
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

        return (userDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                driveRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private void SetupWatchers(List<string> directories)
    {
        lock (_watchersLock)
        {
            ClearWatchersCore();
            if (_isDisposed || Volatile.Read(ref _indexingEnabled) == 0)
            {
                return;
            }

            foreach (string dir in directories)
            {
                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.LastWrite,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemChanged;
                    watcher.Renamed += OnFileSystemRenamed;
                    _watchers.Add(watcher);
                }
                catch
                {
                    // Skip directories where watching fails
                }
            }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        bool residentMutation;
        if (e.ChangeType == WatcherChangeTypes.Deleted)
        {
            residentMutation = RemoveEntry(e.FullPath);
        }
        else
        {
            residentMutation = TryAddEntry(e.FullPath, Directory.Exists(e.FullPath));
        }

        if (residentMutation)
        {
            ScheduleSave();
        }

        IndexUpdated?.Invoke();
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        bool residentMutation = RemoveEntry(e.OldFullPath);
        residentMutation |= TryAddEntry(e.FullPath, Directory.Exists(e.FullPath));
        if (residentMutation)
        {
            ScheduleSave();
        }

        IndexUpdated?.Invoke();
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
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopIndexing();
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
}

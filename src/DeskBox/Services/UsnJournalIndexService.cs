using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Everything-style full-disk indexer built on the NTFS USN change journal.
/// It enumerates every MFT record on each fixed NTFS volume via FSCTL_ENUM_USN_DATA,
/// reconstructs full paths from the file-reference-number (FRN) hierarchy, and answers
/// filename queries in-memory. This indexes the whole disk in seconds, independent of
/// folder selection.
///
/// Reading the USN journal requires an elevated volume handle. When DeskBox is not
/// running as administrator (its normal mode, since elevation breaks drag-and-drop),
/// opening the volume fails and <see cref="IsAvailable"/> stays false so callers fall
/// back to the directory-scan index. The service never throws for that case.
/// </summary>
public sealed partial class UsnJournalIndexService : IDisposable
{
    // ── Win32 constants ──────────────────────────────────────────────
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 44, METHOD_NEITHER, FILE_ANY_ACCESS)</summary>
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;

    private const int ErrorHandleEof = 38;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;

    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint UsnReasonHardLinkChange = 0x00010000;
    private const uint UsnReasonIndexRelevant = 0x0001B307;
    private const int ErrorMoreData = 234;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const int FileAttributeDirectory = 0x10;

    /// <summary>The NTFS root directory always lives at FRN 5.</summary>
    private const ulong RootFileReferenceNumber = 5;

    /// <summary>Hard cap on in-memory entries to prevent unbounded memory growth.</summary>
    private const int MaxIndexEntries = 500_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MftEnumData lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out UsnJournalData lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadJournalDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref ReadUsnJournalData lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileName(
        string lpFileName,
        uint dwFlags,
        ref uint stringLength,
        StringBuilder linkName);

    [DllImport("kernel32.dll", EntryPoint = "FindNextFileNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileName(
        IntPtr hFindStream,
        ref uint stringLength,
        StringBuilder linkName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(IntPtr hFindFile);

    /// <summary>Resolved index entry, shape-compatible with the directory-scan index.</summary>
    private sealed record UsnEntry(
        string FileName,
        string DirectoryPath,
        string FullPath,
        bool IsDirectory,
        DateTime LastModified);

    /// <summary>Top-level directory names skipped on every volume to keep system noise out of the index.</summary>
    private static readonly string[] s_systemDirectoryNames =
    [
        "Windows", "ProgramData", "Program Files", "Program Files (x86)",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "Config.Msi", "MSOCache", "WinSxS", "servicing", "assembly", "Intel", "AMD"
    ];

    private readonly ConcurrentDictionary<string, UsnEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VolumeState> _volumeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _capacityLimitedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexMutationLock = new();
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private int _isScanning;
    private int _isPaused;
    private volatile bool _isAvailable;
    private bool _isDisposed;
    private int _indexingEnabled;
    private int _incrementalVolumeCount;
    private long _sessionEpoch;

    private sealed class VolumeState
    {
        public VolumeState(string root, ulong journalId, long nextUsn, UsnJournalChangeReducer reducer, long epoch)
        {
            Root = root;
            JournalId = journalId;
            NextUsn = nextUsn;
            Reducer = reducer;
            Epoch = epoch;
        }

        public string Root { get; }
        public ulong JournalId { get; }
        public long NextUsn { get; set; }
        public UsnJournalChangeReducer Reducer { get; set; }
        public long Epoch { get; }
        // A snapshot is not considered live until the reader has replayed from the
        // pre-snapshot NextUsn through the journal position observed after the scan.
        public bool IsSynchronized { get; set; }
        public bool CapacityExceeded { get; set; }
        public DateTime NextCapacityRecoveryUtc { get; set; }
    }

    private enum ReplaceVolumeResult
    {
        Success,
        CapacityExceeded,
        SessionExpired
    }

    private enum HardLinkEnrichmentResult
    {
        Success,
        CapacityExceeded,
        Failed
    }

    internal enum HardLinkEnumerationAction
    {
        Complete,
        GrowBuffer,
        Fail
    }

    /// <summary>True once at least one volume was indexed via the USN journal.</summary>
    public bool IsAvailable => _isAvailable;

    public bool IsScanning => Volatile.Read(ref _isScanning) == 1;

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    /// <summary>True when every eligible NTFS volume has a live incremental journal cursor.</summary>
    public bool IsIncrementalSyncing => _isAvailable && Volatile.Read(ref _incrementalVolumeCount) > 0;

    public int EntryCount => _index.Count;

    public int IndexedCount => _index.Count;

    public int CapacityLimitedVolumeCount => _capacityLimitedVolumes.Count;

    public event Action? IndexUpdated;

    /// <summary>Raised periodically during indexing with the current entry count.</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>Pauses an in-progress scan.</summary>
    public void PauseIndexing()
    {
        if (Volatile.Read(ref _indexingEnabled) == 1 && !IsPaused)
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

    /// <summary>Starts background enumeration of every fixed NTFS volume.</summary>
    public void StartIndexing()
    {
        if (_isDisposed || Interlocked.CompareExchange(ref _indexingEnabled, 1, 0) != 0)
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        long epoch = Interlocked.Increment(ref _sessionEpoch);
        _scanTask = Task.Run(() => RunIndexingAsync(epoch, token), token);
    }

    public void StopIndexing()
    {
        Volatile.Write(ref _indexingEnabled, 0);
        Interlocked.Increment(ref _sessionEpoch);
        _scanCts?.Cancel();
        lock (_indexMutationLock)
        {
            _index.Clear();
            _volumeStates.Clear();
            _capacityLimitedVolumes.Clear();
            _isAvailable = false;
        }
        Volatile.Write(ref _incrementalVolumeCount, 0);
        Interlocked.Exchange(ref _isScanning, 0);
        Volatile.Write(ref _isPaused, 0);
        _pauseGate.Set();
        IndexUpdated?.Invoke();
    }

    /// <summary>Searches the full-disk index by file name.</summary>
    public IReadOnlyList<SearchResultItem> Search(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || _index.IsEmpty)
        {
            return [];
        }

        string normalizedQuery = query.Trim();
        if (maxResults <= 0)
        {
            return [];
        }

        var topResults = new PriorityQueue<SearchCandidate, (double Score, long ModifiedTicks)>();

        foreach (var (_, entry) in _index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double score = SearchIndexService.ComputeRelevance(entry.FileName, normalizedQuery);
            if (score <= 0)
            {
                continue;
            }

            long modifiedTicks = entry.LastModified.ToUniversalTime().Ticks;
            topResults.Enqueue(new SearchCandidate(entry, score), (score, modifiedTicks));
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
                Title = candidate.Entry.FileName,
                Subtitle = candidate.Entry.DirectoryPath,
                DetailPath = candidate.Entry.FullPath,
                ModifiedAt = candidate.Entry.LastModified,
                RelevanceScore = candidate.Score,
                Glyph = candidate.Entry.IsDirectory ? "\uE8B7" : null
            })
            .ToList();
    }

    private async Task RunIndexingAsync(long epoch, CancellationToken token)
    {
        lock (_indexMutationLock)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            Interlocked.Exchange(ref _isScanning, 1);
        }
        try
        {
            var eligibleRoots = new List<string>();

            foreach (string drive in Directory.GetLogicalDrives())
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var info = new DriveInfo(drive);
                    if (!info.IsReady || info.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    if (!string.Equals(info.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    eligibleRoots.Add(drive.TrimEnd('\\'));
                }
                catch (Exception ex)
                {
                    App.Log($"[UsnIndex] Drive {drive} skipped: {ex.Message}");
                }
            }

            foreach (string root in eligibleRoots)
            {
                token.ThrowIfCancellationRequested();
                VolumeState? state = TryCreateVolumeSnapshot(root, epoch, token);
                if (state is not null)
                {
                    _ = TryCommitVolumeState(root, state, epoch, token);
                }
            }

            UpdateAvailability(eligibleRoots.Count, epoch, token);
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            if (_isAvailable)
            {
                IndexUpdated?.Invoke();
                App.Log($"[UsnIndex] USN indexing complete. {_index.Count} entries across all volumes.");
            }
            else
            {
                App.Log("[UsnIndex] USN journal unavailable (not elevated or no NTFS volume). Search falls back to the directory index.");
            }

            Interlocked.Exchange(ref _isScanning, 0);

            if (eligibleRoots.Count > 0 && !token.IsCancellationRequested)
            {
                Task[] monitors = eligibleRoots
                    .Select(root => MonitorVolumeAsync(root, eligibleRoots.Count, epoch, token))
                    .ToArray();
                await Task.WhenAll(monitors).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
        catch (Exception ex)
        {
            App.Log($"[UsnIndex] Indexing error: {ex.Message}");
        }
        finally
        {
            if (Interlocked.Read(ref _sessionEpoch) == epoch)
            {
                Interlocked.Exchange(ref _isScanning, 0);
                Volatile.Write(ref _isPaused, 0);
                _pauseGate.Set();
            }
        }
    }

    /// <summary>
    /// Enumerates a single volume's MFT via the USN journal and merges the resolved
    /// paths into the index. Returns false when the volume cannot be opened (the
    /// non-elevated case) so callers know to rely on the fallback index.
    /// </summary>
    private VolumeState? TryCreateVolumeSnapshot(string driveRoot, long epoch, CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return null;
        }

        string root = driveRoot.TrimEnd('\\');
        string volumePath = @"\\.\" + root;

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(volumePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                App.Log($"[UsnIndex] Cannot open {volumePath} (elevation required). Skipping volume.");
                return null;
            }

            if (!TryQueryJournal(handle, out UsnJournalData journal))
            {
                App.Log($"[UsnIndex] Cannot query the USN journal for {root}: {Marshal.GetLastWin32Error()}.");
                return null;
            }

            var records = new List<UsnJournalRecord>();
            bool enumerationComplete = false;
            bool recordsValid = true;
            int bufferSize = 1024 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var med = new MftEnumData
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = journal.NextUsn
                };
                int inputSize = Marshal.SizeOf<MftEnumData>();

                while (!token.IsCancellationRequested)
                {
                    _pauseGate.Wait(token);
                    if (!CanProcessEnumerationBatch(
                            epoch,
                            Interlocked.Read(ref _sessionEpoch),
                            Volatile.Read(ref _indexingEnabled) == 1,
                            token.IsCancellationRequested,
                            _pauseGate.IsSet))
                    {
                        return null;
                    }

                    bool ok = DeviceIoControl(handle, FsctlEnumUsnData, ref med, inputSize, buffer, bufferSize, out int bytesReturned, IntPtr.Zero);
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        enumerationComplete = error == ErrorHandleEof;
                        break;
                    }

                    if (bytesReturned < sizeof(long))
                    {
                        break;
                    }

                    ulong nextFrn = (ulong)Marshal.ReadInt64(buffer, 0);
                    if (!TryParseSnapshotRecords(buffer, 8, bytesReturned, records))
                    {
                        recordsValid = false;
                        break;
                    }

                    if (nextFrn == med.StartFileReferenceNumber)
                    {
                        break; // No forward progress — guard against an infinite loop.
                    }

                    med.StartFileReferenceNumber = nextFrn;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            token.ThrowIfCancellationRequested();
            if (!enumerationComplete || !recordsValid || records.Count == 0)
            {
                App.Log($"[UsnIndex] Volume {root} returned an incomplete MFT snapshot; keeping USN unavailable.");
                return null;
            }

            var reducer = new UsnJournalChangeReducer(root);
            reducer.ReplaceSnapshot(records);
            HardLinkEnrichmentResult hardLinkResult = EnrichSnapshotHardLinks(reducer, root, epoch, token);
            if (hardLinkResult != HardLinkEnrichmentResult.Success)
            {
                if (hardLinkResult == HardLinkEnrichmentResult.CapacityExceeded)
                {
                    RegisterCapacityLimitedVolume(root, epoch, token);
                    App.Log($"[UsnIndex] Hard-link expansion for {root} exceeds the safe capacity; retrying no sooner than 15 minutes.");
                }
                else
                {
                    App.Log($"[UsnIndex] Hard-link enumeration for {root} was incomplete; keeping USN unavailable.");
                }
                return null;
            }

            var state = new VolumeState(root, journal.UsnJournalId, journal.NextUsn, reducer, epoch);
            ReplaceVolumeResult replaceResult = ReplaceVolumeEntries(state, epoch, token);
            if (replaceResult != ReplaceVolumeResult.Success)
            {
                if (replaceResult == ReplaceVolumeResult.CapacityExceeded)
                {
                    RegisterCapacityLimitedVolume(root, epoch, token);
                    App.Log($"[UsnIndex] Volume {root} exceeds the safe index capacity. Falling back to the directory index; the next full retry is throttled for 15 minutes.");
                }
                return null;
            }

            ClearCapacityLimitedVolume(root, epoch, token);

            return state;
        }
        catch (Exception ex)
        {
            App.Log($"[UsnIndex] Volume {root} enumeration failed: {ex.Message}");
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>Parses contiguous USN_RECORD v2 structures from an MFT enumeration.</summary>
    internal static bool TryParseSnapshotRecords(
        IntPtr buffer,
        int start,
        int end,
        ICollection<UsnJournalRecord> records)
    {
        int offset = start;

        // USN_RECORD v2 layout (byte offsets):
        //   0  RecordLength (4)      8  FileReferenceNumber (8)
        //   16 ParentFileReferenceNumber (8)   32 TimeStamp (8)
        //   52 FileAttributes (4)    56 FileNameLength (2)   58 FileNameOffset (2)
        //   60 FileName (variable, UTF-16)
        while (offset + 60 <= end)
        {
            if (!TryReadRecordLayout(buffer, offset, end, out int recordLength, out ushort fileNameLength, out ushort fileNameOffset))
            {
                return false;
            }

            ulong frn = (ulong)Marshal.ReadInt64(buffer, offset + 8);
            ulong parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16);
            long timestamp = Marshal.ReadInt64(buffer, offset + 32);
            int fileAttributes = Marshal.ReadInt32(buffer, offset + 52);
            string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2) ?? string.Empty;
            if (name.Length == 0)
            {
                return false;
            }

            bool isDir = (fileAttributes & FileAttributeDirectory) != 0;
            records.Add(new UsnJournalRecord(frn, parentFrn, name, isDir, timestamp));

            offset += recordLength;
        }

        return offset == end;
    }

    private HardLinkEnrichmentResult EnrichSnapshotHardLinks(
        UsnJournalChangeReducer reducer,
        string root,
        long epoch,
        CancellationToken token)
    {
        Dictionary<string, ulong> directoryFrns = reducer.BuildDirectoryPathMap();
        int estimatedEntryCount = BuildEntries(root, reducer, reducer.Records.Keys).Count;
        if (estimatedEntryCount > MaxIndexEntries)
        {
            return HardLinkEnrichmentResult.CapacityExceeded;
        }
        UsnJournalRecord[] files = reducer.Records.Values.Where(record => !record.IsDirectory).ToArray();
        foreach (UsnJournalRecord file in files)
        {
            _pauseGate.Wait(token);
            if (!CanProcessEnumerationBatch(
                    epoch,
                    Interlocked.Read(ref _sessionEpoch),
                    Volatile.Read(ref _indexingEnabled) == 1,
                    token.IsCancellationRequested,
                    _pauseGate.IsSet))
            {
                return HardLinkEnrichmentResult.Failed;
            }

            string? knownPath = reducer.ResolvePath(file.FileReferenceNumber);
            if (knownPath is null)
            {
                continue;
            }

            if (!TryEnumerateHardLinkPaths(knownPath, root, out IReadOnlyList<string> paths, out int error))
            {
                if (error is 2 or 3)
                {
                    continue; // The journal replay from the pre-snapshot cursor resolves this race.
                }

                return HardLinkEnrichmentResult.Failed;
            }

            if (paths.Count == 0)
            {
                continue;
            }

            var links = new List<UsnJournalRecord>(paths.Count);
            foreach (string path in paths)
            {
                int separator = path.LastIndexOf('\\');
                if (separator <= 0)
                {
                    return HardLinkEnrichmentResult.Failed;
                }

                string directory = path[..separator].TrimEnd('\\');
                if (!directoryFrns.TryGetValue(directory, out ulong parentFrn))
                {
                    return HardLinkEnrichmentResult.Failed;
                }

                links.Add(file with
                {
                    ParentFileReferenceNumber = parentFrn,
                    Name = path[(separator + 1)..]
                });
            }

            int oldIndexableLinkCount = reducer.ResolvePaths(file.FileReferenceNumber)
                .Count(path => !IsSystemPath(path, root));
            int newIndexableLinkCount = paths.Count(path => !IsSystemPath(path, root));
            if (estimatedEntryCount - oldIndexableLinkCount + newIndexableLinkCount > MaxIndexEntries)
            {
                return HardLinkEnrichmentResult.CapacityExceeded;
            }

            estimatedEntryCount += newIndexableLinkCount - oldIndexableLinkCount;
            reducer.Apply([
                new UsnJournalChange(UsnJournalChangeKind.ReplaceHardLinks, file, links)
            ]);
        }

        return HardLinkEnrichmentResult.Success;
    }

    private async Task MonitorVolumeAsync(string root, int eligibleVolumeCount, long epoch, CancellationToken token)
    {
        int recoveryFailures = 0;
        while (IsCurrentSession(epoch, token))
        {
            try
            {
                await MonitorVolumeCoreAsync(root, eligibleVolumeCount, epoch, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsCurrentSession(epoch, token))
                {
                    return;
                }

                if (_volumeStates.TryGetValue(root, out VolumeState? state))
                {
                    MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                }

                App.Log($"[UsnIndex] Unexpected monitor failure for {root}: {ex.Message}. Retrying.");
                await DelayAfterFailureAsync(++recoveryFailures, token).ConfigureAwait(false);
            }
        }
    }

    private async Task MonitorVolumeCoreAsync(string root, int eligibleVolumeCount, long epoch, CancellationToken token)
    {
        int failures = 0;
        while (IsCurrentSession(epoch, token))
        {
            _pauseGate.Wait(token);
            if (!_volumeStates.TryGetValue(root, out VolumeState? state))
            {
                if (_capacityLimitedVolumes.TryGetValue(root, out DateTime retryAt) && retryAt > DateTime.UtcNow)
                {
                    TimeSpan remaining = retryAt - DateTime.UtcNow;
                    await Task.Delay(
                        remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1),
                        token).ConfigureAwait(false);
                    continue;
                }

                state = TryCreateVolumeSnapshot(root, epoch, token);
                if (state is null)
                {
                    await DelayAfterFailureAsync(++failures, token).ConfigureAwait(false);
                    continue;
                }

                if (!TryCommitVolumeState(root, state, epoch, token))
                {
                    return;
                }
                failures = 0;
                UpdateAvailability(eligibleVolumeCount, epoch, token);
                IndexUpdated?.Invoke();
            }

            if (state.Epoch != epoch || !IsCurrentSession(epoch, token))
            {
                return;
            }

            if (_capacityLimitedVolumes.TryGetValue(root, out DateTime volumeRetryAt) &&
                volumeRetryAt > DateTime.UtcNow)
            {
                TimeSpan remaining = volumeRetryAt - DateTime.UtcNow;
                await Task.Delay(
                    remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1),
                    token).ConfigureAwait(false);
                continue;
            }

            if (state.CapacityExceeded && DateTime.UtcNow >= state.NextCapacityRecoveryUtc)
            {
                VolumeState? recovered = TryCreateVolumeSnapshot(root, epoch, token);
                if (recovered is not null && TryCommitVolumeState(root, recovered, epoch, token))
                {
                    state = recovered;
                    UpdateAvailability(eligibleVolumeCount, epoch, token);
                }
                else
                {
                    state.NextCapacityRecoveryUtc = DateTime.UtcNow.AddMinutes(15);
                }
            }

            string volumePath = @"\\.\" + root;
            using SafeFileHandle handle = CreateFile(
                volumePath,
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                await DelayAfterFailureAsync(++failures, token).ConfigureAwait(false);
                continue;
            }

            if (!TryQueryJournal(handle, out UsnJournalData journal) ||
                !IsJournalCursorValid(
                    state.JournalId,
                    state.NextUsn,
                    journal.UsnJournalId,
                    journal.LowestValidUsn,
                    journal.NextUsn))
            {
                App.Log($"[UsnIndex] Journal cursor for {root} is no longer valid; rebuilding the volume snapshot.");
                RebuildVolume(root, state, eligibleVolumeCount, epoch, token);
                await DelayAfterFailureAsync(1, token).ConfigureAwait(false);
                continue;
            }

            if (state.NextUsn >= journal.NextUsn)
            {
                state.IsSynchronized = !state.CapacityExceeded;
                UpdateAvailability(eligibleVolumeCount, epoch, token);
                failures = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                continue;
            }

            int bufferSize = 256 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            bool delayForRecovery = false;
            try
            {
                var request = new ReadUsnJournalData
                {
                    StartUsn = state.NextUsn,
                    ReasonMask = UsnReasonIndexRelevant,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalId = state.JournalId
                };

                bool ok = ReadJournalDeviceIoControl(
                    handle,
                    FsctlReadUsnJournal,
                    ref request,
                    Marshal.SizeOf<ReadUsnJournalData>(),
                    buffer,
                    bufferSize,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is ErrorJournalEntryDeleted or ErrorJournalNotActive or ErrorJournalDeleteInProgress or ErrorHandleEof)
                    {
                        MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                        App.Log($"[UsnIndex] Journal for {root} was reset, truncated, or ended before its queried cursor ({error}); rebuilding.");
                        RebuildVolume(root, state, eligibleVolumeCount, epoch, token);
                    }
                    else
                    {
                        MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                        App.Log($"[UsnIndex] Incremental read failed for {root}: {error}.");
                    }

                    await DelayAfterFailureAsync(++failures, token).ConfigureAwait(false);
                    continue;
                }

                if (bytesReturned >= sizeof(long))
                {
                    if (!TryParseJournalBatch(
                            buffer,
                            bytesReturned,
                            state.NextUsn,
                            out long nextUsn,
                            out IReadOnlyList<UsnJournalChange> changes))
                    {
                        MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                        App.Log($"[UsnIndex] Malformed or unsupported journal record for {root}; rebuilding without advancing the cursor.");
                        RebuildVolume(root, state, eligibleVolumeCount, epoch, token);
                        failures++;
                        delayForRecovery = true;
                    }
                    else if (!TryHydrateHardLinkChanges(
                                 state,
                                 changes,
                                 epoch,
                                 token,
                                 out IReadOnlyList<UsnJournalChange> hydratedChanges))
                    {
                        MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                        App.Log($"[UsnIndex] Hard-link refresh failed for {root}; rebuilding without advancing the cursor.");
                        RebuildVolume(root, state, eligibleVolumeCount, epoch, token);
                        failures++;
                        delayForRecovery = true;
                    }
                    else
                    {
                        bool applied = ApplyIncrementalChanges(state, hydratedChanges, epoch, token);
                        if (!applied)
                        {
                            state.CapacityExceeded = true;
                            state.NextCapacityRecoveryUtc = DateTime.UtcNow.AddMinutes(15);
                            App.Log($"[UsnIndex] Incremental capacity was exceeded for {root}; using the directory index and throttling full-volume recovery to 15 minutes.");
                        }

                        // Even while over capacity, consume the validated journal stream so a
                        // permanently unindexable create record cannot cause a busy reread loop.
                        state.NextUsn = nextUsn;
                        state.IsSynchronized = !state.CapacityExceeded && nextUsn >= journal.NextUsn;
                        failures = 0;
                        UpdateAvailability(eligibleVolumeCount, epoch, token);
                    }
                }
                else
                {
                    MarkVolumeUnsynchronized(state, eligibleVolumeCount, epoch, token);
                    App.Log($"[UsnIndex] Truncated journal response for {root}; rebuilding without advancing the cursor.");
                    RebuildVolume(root, state, eligibleVolumeCount, epoch, token);
                    failures++;
                    delayForRecovery = true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (delayForRecovery)
            {
                await DelayAfterFailureAsync(failures, token).ConfigureAwait(false);
            }
        }
    }

    private void RebuildVolume(
        string root,
        VolumeState oldState,
        int eligibleVolumeCount,
        long epoch,
        CancellationToken token)
    {
        MarkVolumeUnsynchronized(oldState, eligibleVolumeCount, epoch, token);
        VolumeState? replacement = TryCreateVolumeSnapshot(root, epoch, token);
        if (replacement is not null && TryCommitVolumeState(root, replacement, epoch, token))
        {
            UpdateAvailability(eligibleVolumeCount, epoch, token);
            IndexUpdated?.Invoke();
        }
    }

    private static Task DelayAfterFailureAsync(int failures, CancellationToken token)
    {
        int seconds = Math.Min(60, 1 << Math.Min(6, Math.Max(0, failures - 1)));
        return Task.Delay(TimeSpan.FromSeconds(seconds), token);
    }

    private static bool TryQueryJournal(SafeFileHandle handle, out UsnJournalData journal)
    {
        return QueryDeviceIoControl(
            handle,
            FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            out journal,
            Marshal.SizeOf<UsnJournalData>(),
            out int bytesReturned,
            IntPtr.Zero) && bytesReturned >= Marshal.SizeOf<UsnJournalData>();
    }

    internal static bool IsJournalCursorValid(
        ulong expectedJournalId,
        long nextUsn,
        ulong currentJournalId,
        long lowestValidUsn,
        long currentNextUsn)
    {
        return expectedJournalId == currentJournalId &&
               nextUsn >= lowestValidUsn &&
               nextUsn <= currentNextUsn;
    }

    internal static bool TryParseJournalChanges(
        IntPtr buffer,
        int start,
        int end,
        out IReadOnlyList<UsnJournalChange> parsedChanges)
    {
        var changes = new List<UsnJournalChange>();
        int offset = start;
        while (offset + 60 <= end)
        {
            if (!TryReadRecordLayout(buffer, offset, end, out int recordLength, out ushort nameLength, out ushort nameOffset))
            {
                parsedChanges = [];
                return false;
            }

            ulong frn = (ulong)Marshal.ReadInt64(buffer, offset + 8);
            ulong parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16);
            long timestamp = Marshal.ReadInt64(buffer, offset + 32);
            uint reason = unchecked((uint)Marshal.ReadInt32(buffer, offset + 40));
            int attributes = Marshal.ReadInt32(buffer, offset + 52);
            string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + nameOffset), nameLength / 2) ?? string.Empty;
            if (name.Length == 0)
            {
                parsedChanges = [];
                return false;
            }

            UsnJournalChangeKind kind = (reason & UsnReasonHardLinkChange) != 0
                ? UsnJournalChangeKind.ReplaceHardLinks
                : (reason & UsnReasonRenameOldName) != 0
                    ? UsnJournalChangeKind.RenameOld
                    : (reason & UsnReasonRenameNewName) != 0
                        ? UsnJournalChangeKind.RenameNew
                        : (reason & UsnReasonFileDelete) != 0
                            ? UsnJournalChangeKind.Delete
                            : UsnJournalChangeKind.Upsert;
            changes.Add(new UsnJournalChange(
                kind,
                new UsnJournalRecord(
                    frn,
                    parentFrn,
                    name,
                    (attributes & FileAttributeDirectory) != 0,
                    timestamp),
                null,
                reason));

            offset += recordLength;
        }

        parsedChanges = offset == end ? changes : [];
        return offset == end;
    }

    internal static bool TryParseJournalBatch(
        IntPtr buffer,
        int bytesReturned,
        long currentCursor,
        out long nextCursor,
        out IReadOnlyList<UsnJournalChange> changes)
    {
        nextCursor = currentCursor;
        changes = [];
        if (bytesReturned < sizeof(long))
        {
            return false;
        }

        long candidateCursor = Marshal.ReadInt64(buffer, 0);
        if (candidateCursor < currentCursor ||
            !TryParseJournalChanges(buffer, sizeof(long), bytesReturned, out changes))
        {
            changes = [];
            return false;
        }

        nextCursor = candidateCursor;
        return true;
    }

    private static bool TryReadRecordLayout(
        IntPtr buffer,
        int offset,
        int end,
        out int recordLength,
        out ushort fileNameLength,
        out ushort fileNameOffset)
    {
        recordLength = 0;
        fileNameLength = 0;
        fileNameOffset = 0;
        if (offset < 0 || end < offset || end - offset < 60)
        {
            return false;
        }

        recordLength = Marshal.ReadInt32(buffer, offset);
        ushort majorVersion = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 4));
        fileNameLength = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 56));
        fileNameOffset = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 58));
        return majorVersion == 2 &&
               recordLength >= 60 &&
               recordLength <= end - offset &&
               fileNameLength > 0 &&
               (fileNameLength & 1) == 0 &&
               fileNameOffset >= 60 &&
               fileNameOffset <= recordLength &&
               fileNameLength <= recordLength - fileNameOffset;
    }

    private static bool TryEnumerateHardLinkPaths(
        string knownPath,
        string root,
        out IReadOnlyList<string> paths,
        out int failureError)
    {
        paths = [];
        failureError = 0;
        uint capacity = 512;
        IntPtr handle;
        StringBuilder buffer;
        while (true)
        {
            buffer = new StringBuilder(checked((int)capacity));
            uint length = capacity;
            handle = FindFirstFileName(knownPath, 0, ref length, buffer);
            if (handle != InvalidHandleValue)
            {
                capacity = length;
                break;
            }

            int error = Marshal.GetLastWin32Error();
            if (GetHardLinkEnumerationAction(error, length, capacity, allowComplete: false) !=
                HardLinkEnumerationAction.GrowBuffer)
            {
                failureError = error;
                return false;
            }

            capacity = length;
        }

        try
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string linkName = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(linkName))
                {
                    string fullPath = linkName[0] == '\\'
                        ? root + linkName
                        : root + "\\" + linkName;
                    found.Add(fullPath.TrimEnd('\\'));
                }

                uint nextCapacity = Math.Max(512u, capacity);
                while (true)
                {
                    buffer = new StringBuilder(checked((int)nextCapacity));
                    uint length = nextCapacity;
                    if (FindNextFileName(handle, ref length, buffer))
                    {
                        capacity = length;
                        break;
                    }

                    int error = Marshal.GetLastWin32Error();
                    HardLinkEnumerationAction action = GetHardLinkEnumerationAction(
                        error,
                        length,
                        nextCapacity,
                        allowComplete: true);
                    if (action == HardLinkEnumerationAction.Complete)
                    {
                        paths = found.ToArray();
                        return true;
                    }

                    if (action != HardLinkEnumerationAction.GrowBuffer)
                    {
                        failureError = error;
                        return false;
                    }

                    nextCapacity = length;
                }
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    internal static HardLinkEnumerationAction GetHardLinkEnumerationAction(
        int error,
        uint requiredLength,
        uint currentCapacity,
        bool allowComplete)
    {
        if (allowComplete && error == ErrorHandleEof)
        {
            return HardLinkEnumerationAction.Complete;
        }

        return error == ErrorMoreData && requiredLength > currentCapacity
            ? HardLinkEnumerationAction.GrowBuffer
            : HardLinkEnumerationAction.Fail;
    }

    private bool TryHydrateHardLinkChanges(
        VolumeState state,
        IReadOnlyList<UsnJournalChange> changes,
        long epoch,
        CancellationToken token,
        out IReadOnlyList<UsnJournalChange> hydratedChanges)
    {
        if (!changes.Any(change => change.Kind == UsnJournalChangeKind.ReplaceHardLinks))
        {
            hydratedChanges = changes;
            return true;
        }

        var hydrated = new List<UsnJournalChange>(changes.Count);
        Dictionary<string, ulong> directoryFrns = state.Reducer.BuildDirectoryPathMap();
        foreach (UsnJournalChange change in changes)
        {
            _pauseGate.Wait(token);
            if (!CanProcessEnumerationBatch(
                    epoch,
                    Interlocked.Read(ref _sessionEpoch),
                    Volatile.Read(ref _indexingEnabled) == 1,
                    token.IsCancellationRequested,
                    _pauseGate.IsSet) ||
                state.Epoch != epoch)
            {
                hydratedChanges = [];
                return false;
            }

            if (change.Kind != UsnJournalChangeKind.ReplaceHardLinks)
            {
                hydrated.Add(change);
                continue;
            }

            if (change.Record.IsDirectory)
            {
                hydratedChanges = [];
                return false;
            }

            var candidates = state.Reducer.ResolvePaths(change.Record.FileReferenceNumber).ToList();
            string? eventParent = state.Reducer.ResolvePath(change.Record.ParentFileReferenceNumber);
            if (eventParent is not null)
            {
                candidates.Add(eventParent + "\\" + change.Record.Name);
            }

            IReadOnlyList<string>? paths = null;
            int lastError = 0;
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TryEnumerateHardLinkPaths(candidate, state.Root, out IReadOnlyList<string> found, out lastError))
                {
                    paths = found;
                    break;
                }
            }

            if (paths is null)
            {
                if ((change.Reason & UsnReasonFileDelete) != 0 && lastError is 2 or 3)
                {
                    paths = [];
                }
                else
                {
                    hydratedChanges = [];
                    return false;
                }
            }

            var links = new List<UsnJournalRecord>(paths.Count);
            foreach (string path in paths)
            {
                int separator = path.LastIndexOf('\\');
                if (separator <= 0)
                {
                    hydratedChanges = [];
                    return false;
                }

                string directory = path[..separator].TrimEnd('\\');
                if (!directoryFrns.TryGetValue(directory, out ulong parentFrn))
                {
                    hydratedChanges = [];
                    return false;
                }

                links.Add(change.Record with
                {
                    ParentFileReferenceNumber = parentFrn,
                    Name = path[(separator + 1)..]
                });
            }

            hydrated.Add(change with { ReplacementLinks = links });
        }

        hydratedChanges = hydrated;
        return true;
    }

    private ReplaceVolumeResult ReplaceVolumeEntries(VolumeState state, long epoch, CancellationToken token)
    {
        List<UsnEntry> entries = BuildEntries(state.Root, state.Reducer, state.Reducer.Records.Keys);
        lock (_indexMutationLock)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return ReplaceVolumeResult.SessionExpired;
            }

            int otherVolumeCount = _index.Count(pair => !IsSameOrDescendant(pair.Key, state.Root));
            if (otherVolumeCount + entries.Count > MaxIndexEntries)
            {
                return ReplaceVolumeResult.CapacityExceeded;
            }

            RemovePathAndDescendants(state.Root);
            foreach (UsnEntry entry in entries)
            {
                _index[entry.FullPath] = entry;
            }
        }

        ProgressChanged?.Invoke(_index.Count);
        return ReplaceVolumeResult.Success;
    }

    private bool ApplyIncrementalChanges(
        VolumeState state,
        IReadOnlyList<UsnJournalChange> changes,
        long epoch,
        CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return true;
        }

        if (changes.Count == 0)
        {
            return true;
        }

        UsnJournalChangeReducer reducer = state.Reducer;
        UsnJournalChangeReducer.Checkpoint checkpoint = reducer.CreateCheckpoint(changes);
        bool committed = false;
        try
        {
            UsnJournalChangeImpact impact = reducer.Apply(changes);
            if (!impact.Changed)
            {
                committed = true;
                return true;
            }

            var rebuildFrns = new HashSet<ulong>(impact.UpsertFileReferenceNumbers);
            foreach (ulong directoryFrn in impact.RebuildDirectoryReferenceNumbers)
            {
                rebuildFrns.UnionWith(reducer.EnumerateSubtreeFrns(directoryFrn));
            }

            List<UsnEntry> additions = BuildEntries(state.Root, reducer, rebuildFrns);
            lock (_indexMutationLock)
            {
                if (!IsCurrentSession(epoch, token))
                {
                    return true;
                }

                var removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string candidatePath in _index.Keys)
                {
                    if (impact.RemovedPaths.Any(path => IsSameOrDescendant(candidatePath, path)))
                    {
                        removedKeys.Add(candidatePath);
                    }
                }

                int newPathCount = additions
                    .Select(entry => entry.FullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(path => removedKeys.Contains(path) || !_index.ContainsKey(path));
                if (!IsProjectedEntryCountWithinCapacity(
                        _index.Count,
                        removedKeys.Count,
                        newPathCount,
                        MaxIndexEntries))
                {
                    return false;
                }

                foreach (string removedKey in removedKeys)
                {
                    _index.TryRemove(removedKey, out _);
                }

                foreach (UsnEntry entry in additions)
                {
                    _index[entry.FullPath] = entry;
                }
            }

            committed = true;
            IndexUpdated?.Invoke();
            return true;
        }
        finally
        {
            if (!committed)
            {
                reducer.Restore(checkpoint);
            }
        }
    }

    private static List<UsnEntry> BuildEntries(
        string root,
        UsnJournalChangeReducer reducer,
        IEnumerable<ulong> frns)
    {
        var entries = new List<UsnEntry>();
        foreach (ulong frn in frns)
        {
            if (frn == RootFileReferenceNumber ||
                !reducer.Records.TryGetValue(frn, out UsnJournalRecord record))
            {
                continue;
            }

            foreach (string fullPath in reducer.ResolvePaths(frn))
            {
                if (IsSystemPath(fullPath, root))
                {
                    continue;
                }

                int separator = fullPath.LastIndexOf('\\');
                string directoryPath = separator > 0 ? fullPath[..separator] : root;
                string fileName = separator >= 0 ? fullPath[(separator + 1)..] : record.Name;
                entries.Add(new UsnEntry(
                    fileName,
                    directoryPath,
                    fullPath,
                    record.IsDirectory,
                    TimestampToDateTime(record.Timestamp)));
            }
        }

        return entries;
    }

    private void RemovePathAndDescendants(string path)
    {
        foreach (string candidate in _index.Keys)
        {
            if (IsSameOrDescendant(candidate, path))
            {
                _index.TryRemove(candidate, out _);
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               (candidate.Length > parent.Length &&
                candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                candidate[parent.Length] == '\\');
    }

    internal static bool IsProjectedEntryCountWithinCapacity(
        int currentCount,
        int removedCount,
        int newPathCount,
        int maximumCount)
    {
        if (currentCount < 0 || removedCount < 0 || newPathCount < 0 || maximumCount < 0 ||
            removedCount > currentCount)
        {
            return false;
        }

        return (long)currentCount - removedCount + newPathCount <= maximumCount;
    }

    private void MarkVolumeUnsynchronized(
        VolumeState state,
        int eligibleVolumeCount,
        long epoch,
        CancellationToken token)
    {
        if (!IsCurrentSession(epoch, token))
        {
            return;
        }

        state.IsSynchronized = false;
        UpdateAvailability(eligibleVolumeCount, epoch, token);
    }

    private void UpdateAvailability(
        int eligibleVolumeCount,
        long epoch,
        CancellationToken token)
    {
        bool availabilityChanged;
        lock (_indexMutationLock)
        {
            if (!IsCurrentSession(epoch, token))
            {
                return;
            }

            bool wasAvailable = _isAvailable;
            int synchronized = _volumeStates.Values.Count(state => state.Epoch == epoch && state.IsSynchronized);
            Volatile.Write(ref _incrementalVolumeCount, synchronized);
            _isAvailable = eligibleVolumeCount > 0 &&
                           synchronized == eligibleVolumeCount &&
                           !_index.IsEmpty;
            availabilityChanged = wasAvailable != _isAvailable;
        }

        if (availabilityChanged)
        {
            IndexUpdated?.Invoke();
        }
    }

    private bool TryCommitVolumeState(
        string root,
        VolumeState state,
        long epoch,
        CancellationToken token)
    {
        lock (_indexMutationLock)
        {
            if (!IsCurrentSession(epoch, token) || state.Epoch != epoch)
            {
                return false;
            }

            _volumeStates[root] = state;
            _capacityLimitedVolumes.TryRemove(root, out _);
            return true;
        }
    }

    private void RegisterCapacityLimitedVolume(string root, long epoch, CancellationToken token)
    {
        lock (_indexMutationLock)
        {
            if (IsCurrentSession(epoch, token))
            {
                _capacityLimitedVolumes[root] = DateTime.UtcNow.AddMinutes(15);
            }
        }
    }

    private void ClearCapacityLimitedVolume(string root, long epoch, CancellationToken token)
    {
        lock (_indexMutationLock)
        {
            if (IsCurrentSession(epoch, token))
            {
                _capacityLimitedVolumes.TryRemove(root, out _);
            }
        }
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
        return indexingEnabled &&
               !cancellationRequested &&
               expectedEpoch == currentEpoch;
    }

    internal static bool CanProcessEnumerationBatch(
        long expectedEpoch,
        long currentEpoch,
        bool indexingEnabled,
        bool cancellationRequested,
        bool pauseGateSet)
    {
        return pauseGateSet && IsSessionCurrent(
            expectedEpoch,
            currentEpoch,
            indexingEnabled,
            cancellationRequested);
    }

    /// <summary>True when the path lives under a top-level system directory of the volume.</summary>
    private static bool IsSystemPath(string fullPath, string root)
    {
        foreach (string systemName in s_systemDirectoryNames)
        {
            string prefix = root + "\\" + systemName;
            if (fullPath.Length >= prefix.Length &&
                fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (fullPath.Length == prefix.Length || fullPath[prefix.Length] == '\\'))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime TimestampToDateTime(long timestamp)
    {
        try
        {
            return timestamp > 0 ? DateTime.FromFileTime(timestamp) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
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
        _scanCts?.Dispose();
        // A previous Stop/Start session can still be unwinding a synchronous
        // DeviceIoControl call after the latest task has changed. Do not dispose the
        // shared gate here; session epochs prevent stale writes and leaving this small
        // process-lifetime primitive to GC avoids an ObjectDisposedException race.
        _index.Clear();
    }

    private readonly record struct SearchCandidate(
        UsnEntry Entry,
        double Score);
}

using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// One entry used to build the opt-in stage 6 Rust search index. The managed
/// backend remains the default and the validated fallback for the product preview.
/// </summary>
internal readonly record struct SearchCoreSourceEntry(
    string DirectoryPath,
    string FileName,
    bool IsDirectory,
    DateTime LastModified);

internal readonly record struct SearchCoreQueryItem(
    uint EntryId,
    string DirectoryPath,
    string FileName,
    bool IsDirectory,
    DateTime LastModifiedUtc,
    uint RelevanceScore)
{
    internal string FullPath => string.IsNullOrEmpty(DirectoryPath)
        ? FileName
        : Path.Combine(DirectoryPath, FileName);
}

internal sealed record SearchCoreQuerySnapshot(
    IReadOnlyList<SearchCoreQueryItem> Items,
    uint ScannedEntryCount,
    uint MatchedEntryCount);

internal readonly record struct SearchCoreMemoryStats(
    bool IsSealed,
    uint EntryCount,
    uint DirectoryCount,
    ulong EntryCapacityBytes,
    ulong DirectoryDescriptorCapacityBytes,
    ulong DirectoryUtf16CapacityBytes,
    ulong FileNameUtf16CapacityBytes,
    ulong BuildLookupCapacityBytes,
    ulong TotalTrackedCapacityBytes);

internal readonly record struct SearchCoreDbixLoadInfo(
    uint DbixVersion,
    DateTime PersistedAtUtc,
    ulong SourceFileBytes,
    uint EntryCount,
    uint DirectoryCount);

internal enum SearchCoreMutationKind
{
    Upsert = 1,
    RemoveExact = 2,
    RemoveTree = 3,
    RemoveStaleTree = 4
}

internal readonly record struct SearchCoreMutation(
    SearchCoreMutationKind Kind,
    string Path,
    bool IsDirectory = false,
    DateTime LastModified = default,
    int ScanGeneration = 0);

internal readonly record struct SearchCoreMutationResult(
    uint AppliedMutationCount,
    uint LiveEntryCount,
    uint TombstoneCount,
    uint DirectoryCount);

internal readonly record struct SearchCoreProjectionItem(
    string FullPath,
    bool IsDirectory,
    DateTime LastModifiedUtc,
    uint RankValue);

internal readonly record struct SearchCoreDbixSaveInfo(
    uint DbixVersion,
    DateTime PersistedAtUtc,
    ulong FileBytes,
    uint EntryCount,
    uint DirectoryCount);

internal sealed class SearchCoreNativeOperationException : InvalidOperationException
{
    internal SearchCoreNativeOperationException(string operation, uint status, string details)
        : base($"Rust SearchCore {operation} failed with status {status}: {details}")
    {
        Status = status;
    }

    internal uint Status { get; }
}

/// <summary>
/// Safe managed owner for the independent deskbox_search_core ABI v3 module.
/// Callers retain serialization and lifetime ownership; all cross-boundary text
/// stays in caller-owned buffers.
/// </summary>
internal sealed unsafe class SearchCoreNativeBackend : IDisposable
{
    internal const string DllName = "deskbox_search_core.dll";
    internal const uint AbiVersion = 3;
    internal const uint InvalidArgumentStatus = 1;

    private const uint StructVersion = 1;
    private const uint StatusOk = 0;
    private const uint StatusInvalidArgument = 1;
    private const uint StatusBufferTooSmall = 3;
    private const uint StatusCancelled = 5;
    private const uint StatusIoError = 7;
    private const uint StatusUnsupportedFormat = 8;
    private const uint StatusCorruptData = 9;
    private const uint EntryDirectory = 1;
    private const uint ProjectionRecentFiles = 1;
    private const uint ProjectionFrequentFolders = 2;
    private const int MaximumEntryCount = 300_000;
    private const int DefaultBatchSize = 512;

    private readonly delegate* unmanaged[Cdecl]<NativeOpenDbixRequest*, NativeOpenDbixResult*, uint> _openDbix;
    private readonly delegate* unmanaged[Cdecl]<NativeCreateRequest*, NativeCreateResult*, uint> _create;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeAddBatchRequest*, NativeAddBatchResult*, uint> _addBatch;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeSealResult*, uint> _seal;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _resetCancel;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _cancel;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeQueryRequest*, NativeQueryResult*, uint> _query;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeCopyEntriesRequest*, NativeCopyEntriesResult*, uint> _copyEntries;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeMutateBatchRequest*, NativeMutateBatchResult*, uint> _mutateBatch;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeProjectRequest*, NativeProjectResult*, uint> _project;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeSaveDbixRequest*, NativeSaveDbixResult*, uint> _saveDbix;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeStats*, uint> _stats;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _destroy;

    private nint _module;
    private nint _handle;
    private bool _sealed;

    private SearchCoreNativeBackend(
        NativeExports exports,
        int initialEntryCapacity,
        int initialUtf16CapacityChars,
        nint existingHandle = 0)
    {
        _module = exports.Module;
        _openDbix = (delegate* unmanaged[Cdecl]<NativeOpenDbixRequest*, NativeOpenDbixResult*, uint>)(void*)exports.OpenDbix;
        _create = (delegate* unmanaged[Cdecl]<NativeCreateRequest*, NativeCreateResult*, uint>)(void*)exports.Create;
        _addBatch = (delegate* unmanaged[Cdecl]<nint, NativeAddBatchRequest*, NativeAddBatchResult*, uint>)(void*)exports.AddBatch;
        _seal = (delegate* unmanaged[Cdecl]<nint, NativeSealResult*, uint>)(void*)exports.Seal;
        _resetCancel = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.ResetCancel;
        _cancel = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Cancel;
        _query = (delegate* unmanaged[Cdecl]<nint, NativeQueryRequest*, NativeQueryResult*, uint>)(void*)exports.Query;
        _copyEntries = (delegate* unmanaged[Cdecl]<nint, NativeCopyEntriesRequest*, NativeCopyEntriesResult*, uint>)(void*)exports.CopyEntries;
        _mutateBatch = (delegate* unmanaged[Cdecl]<nint, NativeMutateBatchRequest*, NativeMutateBatchResult*, uint>)(void*)exports.MutateBatch;
        _project = (delegate* unmanaged[Cdecl]<nint, NativeProjectRequest*, NativeProjectResult*, uint>)(void*)exports.Project;
        _saveDbix = (delegate* unmanaged[Cdecl]<nint, NativeSaveDbixRequest*, NativeSaveDbixResult*, uint>)(void*)exports.SaveDbix;
        _stats = (delegate* unmanaged[Cdecl]<nint, NativeStats*, uint>)(void*)exports.Stats;
        _destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Destroy;

        if (existingHandle != 0)
        {
            _handle = existingHandle;
            _sealed = true;
            return;
        }

        var request = new NativeCreateRequest
        {
            StructSize = (uint)sizeof(NativeCreateRequest),
            StructVersion = StructVersion,
            InitialEntryCapacity = (uint)initialEntryCapacity,
            InitialUtf16CapacityChars = (uint)initialUtf16CapacityChars
        };
        var result = new NativeCreateResult
        {
            StructSize = (uint)sizeof(NativeCreateResult),
            StructVersion = StructVersion
        };
        uint returnedStatus = _create(&request, &result);
        ValidateStatus("create", returnedStatus, result.Status);
        if (result.Status != StatusOk || result.Handle == 0)
        {
            throw new InvalidOperationException(
                $"Rust SearchCore create failed with status {result.Status}.");
        }
        _handle = result.Handle;
    }

    internal static bool TryCreate(
        string modulePath,
        int initialEntryCapacity,
        int initialUtf16CapacityChars,
        out SearchCoreNativeBackend? backend,
        out string error)
    {
        backend = null;
        error = string.Empty;
        if (initialEntryCapacity < 0 ||
            initialEntryCapacity > MaximumEntryCount ||
            initialUtf16CapacityChars < 0)
        {
            error = "SearchCore initial capacities are outside the supported range.";
            return false;
        }
        if (!TryLoadModule(modulePath, out NativeExports exports, out error))
        {
            return false;
        }
        try
        {
            backend = new SearchCoreNativeBackend(
                exports,
                initialEntryCapacity,
                initialUtf16CapacityChars);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            NativeLibrary.Free(exports.Module);
            backend = null;
            return false;
        }
    }

    internal static bool TryOpenDbix(
        string modulePath,
        string dbixPath,
        int maxEntryCount,
        out SearchCoreNativeBackend? backend,
        out SearchCoreDbixLoadInfo loadInfo,
        out string error,
        CancellationToken cancellationToken = default)
    {
        backend = null;
        loadInfo = default;
        error = string.Empty;
        if (maxEntryCount <= 0 || maxEntryCount > MaximumEntryCount)
        {
            error = "SearchCore DBIX entry limit is outside the supported range.";
            return false;
        }

        string fullDbixPath;
        try
        {
            fullDbixPath = Path.GetFullPath(dbixPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        if (!Path.IsPathFullyQualified(fullDbixPath) || !File.Exists(fullDbixPath))
        {
            error = $"SearchCore DBIX file was not found at '{fullDbixPath}'.";
            return false;
        }
        if (fullDbixPath.Length > 32_767)
        {
            error = "SearchCore DBIX path exceeds the ABI v3 limit.";
            return false;
        }
        if (!TryLoadModule(modulePath, out NativeExports exports, out error))
        {
            return false;
        }

        nint openedHandle = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((EventWaitHandle)state!).Set(),
                cancelEvent);
            fixed (char* pathPointer = fullDbixPath)
            {
                var request = new NativeOpenDbixRequest
                {
                    StructSize = (uint)sizeof(NativeOpenDbixRequest),
                    StructVersion = StructVersion,
                    Path = pathPointer,
                    PathLengthChars = (uint)fullDbixPath.Length,
                    MaxEntryCount = (uint)maxEntryCount,
                    CancelEvent = cancelEvent.SafeWaitHandle.DangerousGetHandle()
                };
                var result = new NativeOpenDbixResult
                {
                    StructSize = (uint)sizeof(NativeOpenDbixResult),
                    StructVersion = StructVersion
                };
                var openDbix = (delegate* unmanaged[Cdecl]<NativeOpenDbixRequest*, NativeOpenDbixResult*, uint>)(void*)exports.OpenDbix;
                uint returnedStatus = openDbix(&request, &result);
                ValidateStatus("open DBIX", returnedStatus, result.Status);
                openedHandle = result.Handle;
                if (result.Status == StatusCancelled)
                {
                    throw new OperationCanceledException(
                        "Rust SearchCore DBIX load was cancelled.",
                        cancellationToken);
                }
                if (result.Status != StatusOk)
                {
                    if (openedHandle != 0)
                    {
                        var destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Destroy;
                        _ = destroy(openedHandle);
                        openedHandle = 0;
                        throw new InvalidDataException(
                            "Rust SearchCore exposed a partial DBIX handle after failure.");
                    }
                    error = DescribeDbixFailure(result.Status);
                    NativeLibrary.Free(exports.Module);
                    return false;
                }
                if (openedHandle == 0 ||
                    result.DbixVersion != 1 ||
                    result.EntryCount == 0 ||
                    result.EntryCount > (uint)maxEntryCount)
                {
                    throw new InvalidDataException("Rust SearchCore returned invalid DBIX load metadata.");
                }
                loadInfo = new SearchCoreDbixLoadInfo(
                    result.DbixVersion,
                    new DateTime(result.PersistedUtcTicks, DateTimeKind.Utc),
                    result.SourceFileBytes,
                    result.EntryCount,
                    result.DirectoryCount);
            }

            backend = new SearchCoreNativeBackend(exports, 0, 0, openedHandle);
            openedHandle = 0;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (openedHandle != 0)
            {
                var destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Destroy;
                _ = destroy(openedHandle);
            }
            NativeLibrary.Free(exports.Module);
            throw;
        }
        catch (Exception ex)
        {
            if (openedHandle != 0)
            {
                var destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Destroy;
                _ = destroy(openedHandle);
            }
            NativeLibrary.Free(exports.Module);
            error = ex.Message;
            backend = null;
            loadInfo = default;
            return false;
        }
    }

    internal void AddEntries(
        IReadOnlyList<SearchCoreSourceEntry> entries,
        int batchSize = DefaultBatchSize)
    {
        ThrowIfDisposed();
        if (_sealed)
        {
            throw new InvalidOperationException("The Rust SearchCore index is already sealed.");
        }
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumEntryCount || batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        for (int start = 0; start < entries.Count; start += batchSize)
        {
            int count = Math.Min(batchSize, entries.Count - start);
            AddBatch(entries, start, count);
        }
    }

    private void AddBatch(IReadOnlyList<SearchCoreSourceEntry> entries, int start, int count)
    {
        int charCount = 0;
        for (int offset = 0; offset < count; offset++)
        {
            SearchCoreSourceEntry entry = entries[start + offset];
            ValidateEntry(entry);
            charCount = checked(charCount + entry.DirectoryPath.Length + entry.FileName.Length);
        }

        var packedUtf16 = new char[charCount];
        var nativeEntries = new NativeEntryInput[count];
        int cursor = 0;
        for (int offset = 0; offset < count; offset++)
        {
            SearchCoreSourceEntry entry = entries[start + offset];
            int directoryOffset = cursor;
            entry.DirectoryPath.AsSpan().CopyTo(packedUtf16.AsSpan(cursor));
            cursor += entry.DirectoryPath.Length;
            int fileNameOffset = cursor;
            entry.FileName.AsSpan().CopyTo(packedUtf16.AsSpan(cursor));
            cursor += entry.FileName.Length;
            nativeEntries[offset] = new NativeEntryInput
            {
                DirectoryOffsetChars = (uint)directoryOffset,
                DirectoryLengthChars = (uint)entry.DirectoryPath.Length,
                FileNameOffsetChars = (uint)fileNameOffset,
                FileNameLengthChars = (uint)entry.FileName.Length,
                ModifiedUtcTicks = entry.LastModified.ToUniversalTime().Ticks,
                Flags = entry.IsDirectory ? EntryDirectory : 0
            };
        }

        fixed (char* utf16Pointer = packedUtf16)
        fixed (NativeEntryInput* entriesPointer = nativeEntries)
        {
            var request = new NativeAddBatchRequest
            {
                StructSize = (uint)sizeof(NativeAddBatchRequest),
                StructVersion = StructVersion,
                Entries = entriesPointer,
                EntryCount = (uint)nativeEntries.Length,
                Utf16Data = utf16Pointer,
                Utf16LengthChars = (uint)packedUtf16.Length
            };
            var result = new NativeAddBatchResult
            {
                StructSize = (uint)sizeof(NativeAddBatchResult),
                StructVersion = StructVersion
            };
            uint returnedStatus = _addBatch(_handle, &request, &result);
            ValidateStatus("add batch", returnedStatus, result.Status);
            if (result.Status != StatusOk || result.AddedEntryCount != (uint)count)
            {
                throw new InvalidOperationException(
                    $"Rust SearchCore add batch failed: status={result.Status}, added={result.AddedEntryCount}/{count}.");
            }
        }
    }

    internal void Seal()
    {
        ThrowIfDisposed();
        if (_sealed)
        {
            throw new InvalidOperationException("The Rust SearchCore index is already sealed.");
        }
        var result = new NativeSealResult
        {
            StructSize = (uint)sizeof(NativeSealResult),
            StructVersion = StructVersion
        };
        uint returnedStatus = _seal(_handle, &result);
        ValidateStatus("seal", returnedStatus, result.Status);
        if (result.Status != StatusOk)
        {
            throw new InvalidOperationException(
                $"Rust SearchCore seal failed with status {result.Status}.");
        }
        _sealed = true;
    }

    internal SearchCoreMutationResult ApplyMutations(
        IReadOnlyList<SearchCoreMutation> mutations)
    {
        ThrowIfDisposed();
        if (!_sealed)
        {
            throw new InvalidOperationException("Rust SearchCore must be sealed before applying mutations.");
        }
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count is <= 0 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(mutations));
        }

        var paths = new string[mutations.Count];
        var directories = new string[mutations.Count];
        var fileNames = new string[mutations.Count];
        var modifiedValues = new DateTime[mutations.Count];
        int charCount = 0;
        for (int index = 0; index < mutations.Count; index++)
        {
            SearchCoreMutation mutation = mutations[index];
            if (!Enum.IsDefined(mutation.Kind) || mutation.ScanGeneration < 0)
            {
                throw new ArgumentException("SearchCore mutation metadata is invalid.", nameof(mutations));
            }
            string fullPath = Path.GetFullPath(mutation.Path);
            if (fullPath.Length is <= 0 or > 32_767 || fullPath.Contains('\0'))
            {
                throw new ArgumentException("SearchCore mutation path is invalid.", nameof(mutations));
            }
            paths[index] = fullPath;
            if (mutation.Kind == SearchCoreMutationKind.Upsert)
            {
                string fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(fileName))
                {
                    throw new ArgumentException("SearchCore cannot upsert a path without a file name.", nameof(mutations));
                }
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                directories[index] = directory;
                fileNames[index] = fileName;
                DateTime modified = mutation.LastModified.Kind == DateTimeKind.Unspecified &&
                                    mutation.LastModified != DateTime.MinValue
                    ? DateTime.SpecifyKind(mutation.LastModified, DateTimeKind.Local)
                    : mutation.LastModified;
                modifiedValues[index] = modified;
                charCount = checked(charCount + directory.Length + fileName.Length);
            }
            else
            {
                if (mutation.Kind != SearchCoreMutationKind.RemoveStaleTree &&
                    mutation.ScanGeneration != 0)
                {
                    throw new ArgumentException("Only stale-tree removal accepts a scan generation.", nameof(mutations));
                }
                charCount = checked(charCount + fullPath.Length);
            }
        }

        var packedUtf16 = new char[charCount];
        var nativeMutations = new NativeMutationInput[mutations.Count];
        int cursor = 0;
        for (int index = 0; index < mutations.Count; index++)
        {
            SearchCoreMutation mutation = mutations[index];
            if (mutation.Kind == SearchCoreMutationKind.Upsert)
            {
                string directory = directories[index];
                string fileName = fileNames[index];
                int directoryOffset = cursor;
                directory.AsSpan().CopyTo(packedUtf16.AsSpan(cursor));
                cursor += directory.Length;
                int fileNameOffset = cursor;
                fileName.AsSpan().CopyTo(packedUtf16.AsSpan(cursor));
                cursor += fileName.Length;
                DateTime modified = modifiedValues[index];
                nativeMutations[index] = new NativeMutationInput
                {
                    Operation = (uint)mutation.Kind,
                    Flags = mutation.IsDirectory ? EntryDirectory : 0,
                    DirectoryOffsetChars = (uint)directoryOffset,
                    DirectoryLengthChars = (uint)directory.Length,
                    FileNameOffsetChars = (uint)fileNameOffset,
                    FileNameLengthChars = (uint)fileName.Length,
                    ModifiedUtcTicks = modified == DateTime.MinValue
                        ? 0
                        : modified.ToUniversalTime().Ticks,
                    ModifiedBinary = modified.ToBinary(),
                    ScanGeneration = (uint)mutation.ScanGeneration
                };
            }
            else
            {
                string path = paths[index];
                int pathOffset = cursor;
                path.AsSpan().CopyTo(packedUtf16.AsSpan(cursor));
                cursor += path.Length;
                nativeMutations[index] = new NativeMutationInput
                {
                    Operation = (uint)mutation.Kind,
                    PathOffsetChars = (uint)pathOffset,
                    PathLengthChars = (uint)path.Length,
                    ScanGeneration = (uint)mutation.ScanGeneration
                };
            }
        }

        fixed (char* utf16Pointer = packedUtf16)
        fixed (NativeMutationInput* mutationsPointer = nativeMutations)
        {
            var request = new NativeMutateBatchRequest
            {
                StructSize = (uint)sizeof(NativeMutateBatchRequest),
                StructVersion = StructVersion,
                Mutations = mutationsPointer,
                MutationCount = (uint)nativeMutations.Length,
                Utf16Data = utf16Pointer,
                Utf16LengthChars = (uint)packedUtf16.Length
            };
            var result = new NativeMutateBatchResult
            {
                StructSize = (uint)sizeof(NativeMutateBatchResult),
                StructVersion = StructVersion
            };
            uint returnedStatus = _mutateBatch(_handle, &request, &result);
            ValidateStatus("mutate batch", returnedStatus, result.Status);
            if (result.Status != StatusOk ||
                result.AppliedMutationCount != (uint)mutations.Count)
            {
                throw new SearchCoreNativeOperationException(
                    "mutation",
                    result.Status,
                    $"applied={result.AppliedMutationCount}/{mutations.Count}");
            }
            return new SearchCoreMutationResult(
                result.AppliedMutationCount,
                result.LiveEntryCount,
                result.TombstoneCount,
                result.DirectoryCount);
        }
    }

    internal IReadOnlyList<SearchCoreProjectionItem> GetRecentFiles(int count) =>
        Project(ProjectionRecentFiles, count);

    internal IReadOnlyList<SearchCoreProjectionItem> GetFrequentFolders(int count) =>
        Project(ProjectionFrequentFolders, count);

    private IReadOnlyList<SearchCoreProjectionItem> Project(uint kind, int count)
    {
        ThrowIfDisposed();
        if (!_sealed)
        {
            throw new InvalidOperationException("Rust SearchCore must be sealed before projection.");
        }
        if (count is <= 0 or > 200)
        {
            return [];
        }

        var nativeItems = new NativeProjectionItem[count];
        var utf16 = new char[Math.Max(1, checked(count * 260))];
        while (true)
        {
            NativeProjectResult result;
            fixed (NativeProjectionItem* itemsPointer = nativeItems)
            fixed (char* utf16Pointer = utf16)
            {
                var request = new NativeProjectRequest
                {
                    StructSize = (uint)sizeof(NativeProjectRequest),
                    StructVersion = StructVersion,
                    ProjectionKind = kind,
                    MaxResults = (uint)count,
                    Items = itemsPointer,
                    ItemCapacity = (uint)nativeItems.Length,
                    Utf16Data = utf16Pointer,
                    Utf16CapacityChars = (uint)utf16.Length
                };
                result = new NativeProjectResult
                {
                    StructSize = (uint)sizeof(NativeProjectResult),
                    StructVersion = StructVersion
                };
                uint returnedStatus = _project(_handle, &request, &result);
                ValidateStatus("project", returnedStatus, result.Status);
            }
            if (result.Status == StatusBufferTooSmall)
            {
                if (result.RequiredUtf16Chars <= (uint)utf16.Length ||
                    result.RequiredUtf16Chars > 64u * 1024u * 1024u)
                {
                    throw new InvalidDataException("Rust SearchCore returned an invalid projection buffer requirement.");
                }
                utf16 = new char[checked((int)result.RequiredUtf16Chars)];
                continue;
            }
            if (result.Status != StatusOk || result.WrittenItemCount > (uint)count)
            {
                throw new InvalidOperationException(
                    $"Rust SearchCore projection failed with status {result.Status}.");
            }

            var items = new SearchCoreProjectionItem[result.WrittenItemCount];
            for (int index = 0; index < items.Length; index++)
            {
                NativeProjectionItem native = nativeItems[index];
                ValidateTextRange(native.PathOffsetChars, native.PathLengthChars, utf16.Length);
                string fullPath = new(
                    utf16,
                    checked((int)native.PathOffsetChars),
                    checked((int)native.PathLengthChars));
                items[index] = new SearchCoreProjectionItem(
                    fullPath,
                    (native.Flags & EntryDirectory) != 0,
                    new DateTime(native.ModifiedUtcTicks, DateTimeKind.Utc),
                    native.RankValue);
            }
            return items;
        }
    }

    internal SearchCoreDbixSaveInfo SaveDbix(
        string dbixPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_sealed)
        {
            throw new InvalidOperationException("Rust SearchCore must be sealed before persistence.");
        }
        string fullPath = Path.GetFullPath(dbixPath);
        string tempPath = fullPath + ".tmp";
        if (fullPath.Length > 32_767 || tempPath.Length > 32_767)
        {
            throw new PathTooLongException("SearchCore DBIX path exceeds the ABI limit.");
        }
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((EventWaitHandle)state!).Set(),
            cancelEvent);
        fixed (char* pathPointer = fullPath)
        fixed (char* tempPathPointer = tempPath)
        {
            var request = new NativeSaveDbixRequest
            {
                StructSize = (uint)sizeof(NativeSaveDbixRequest),
                StructVersion = StructVersion,
                Path = pathPointer,
                PathLengthChars = (uint)fullPath.Length,
                TempPath = tempPathPointer,
                TempPathLengthChars = (uint)tempPath.Length,
                CancelEvent = cancelEvent.SafeWaitHandle.DangerousGetHandle()
            };
            var result = new NativeSaveDbixResult
            {
                StructSize = (uint)sizeof(NativeSaveDbixResult),
                StructVersion = StructVersion
            };
            uint returnedStatus = _saveDbix(_handle, &request, &result);
            ValidateStatus("save DBIX", returnedStatus, result.Status);
            if (result.Status == StatusCancelled)
            {
                throw new OperationCanceledException(
                    "Rust SearchCore DBIX save was cancelled.",
                    cancellationToken);
            }
            if (result.Status != StatusOk || result.DbixVersion != 1)
            {
                throw new IOException(
                    $"Rust SearchCore DBIX save failed with status {result.Status}.");
            }
            return new SearchCoreDbixSaveInfo(
                result.DbixVersion,
                new DateTime(result.PersistedUtcTicks, DateTimeKind.Utc),
                result.FileBytes,
                result.EntryCount,
                result.DirectoryCount);
        }
    }

    internal SearchCoreQuerySnapshot Query(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_sealed)
        {
            throw new InvalidOperationException("The Rust SearchCore index must be sealed before querying.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maxResults <= 0 || maxResults > MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        string normalizedQuery = query.Trim();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStatus("reset cancellation", _resetCancel(_handle), StatusOk);
        var nativeResults = new NativeSearchResult[maxResults];
        NativeQueryResult result;
        using (cancellationToken.UnsafeRegister(
            static state => ((SearchCoreNativeBackend)state!).RequestCancellation(),
            this))
        {
            fixed (char* queryPointer = normalizedQuery)
            fixed (NativeSearchResult* resultsPointer = nativeResults)
            {
                var request = new NativeQueryRequest
                {
                    StructSize = (uint)sizeof(NativeQueryRequest),
                    StructVersion = StructVersion,
                    Query = queryPointer,
                    QueryLengthChars = (uint)normalizedQuery.Length,
                    MaxResults = (uint)maxResults,
                    Results = resultsPointer,
                    ResultCapacity = (uint)nativeResults.Length
                };
                result = new NativeQueryResult
                {
                    StructSize = (uint)sizeof(NativeQueryResult),
                    StructVersion = StructVersion
                };
                uint returnedStatus = _query(_handle, &request, &result);
                ValidateStatus("query", returnedStatus, result.Status);
            }
        }

        if (result.Status == StatusCancelled)
        {
            throw new OperationCanceledException("Rust SearchCore query was cancelled.", cancellationToken);
        }
        if (result.Status != StatusOk)
        {
            throw new InvalidOperationException(
                $"Rust SearchCore query failed with status {result.Status}.");
        }
        if (result.WrittenResultCount > (uint)nativeResults.Length)
        {
            throw new InvalidDataException("Rust SearchCore returned more results than the caller-owned buffer.");
        }
        if (result.WrittenResultCount == 0)
        {
            return new SearchCoreQuerySnapshot([], result.ScannedEntryCount, result.MatchedEntryCount);
        }

        int resultCount = checked((int)result.WrittenResultCount);
        var entryIds = new uint[resultCount];
        for (int index = 0; index < resultCount; index++)
        {
            NativeSearchResult native = nativeResults[index];
            if (native.Reserved0 != 0 || native.Flags > EntryDirectory)
            {
                throw new InvalidDataException("Rust SearchCore returned an invalid result descriptor.");
            }
            entryIds[index] = native.EntryId;
        }

        int requiredChars = checked((int)result.RequiredUtf16Chars);
        var copiedEntries = new NativeEntryText[resultCount];
        var copiedUtf16 = new char[requiredChars];
        fixed (uint* entryIdsPointer = entryIds)
        fixed (NativeEntryText* entriesPointer = copiedEntries)
        fixed (char* utf16Pointer = copiedUtf16)
        {
            var request = new NativeCopyEntriesRequest
            {
                StructSize = (uint)sizeof(NativeCopyEntriesRequest),
                StructVersion = StructVersion,
                EntryIds = entryIdsPointer,
                EntryCount = (uint)resultCount,
                Entries = entriesPointer,
                EntryCapacity = (uint)copiedEntries.Length,
                Utf16Data = utf16Pointer,
                Utf16CapacityChars = (uint)copiedUtf16.Length
            };
            var copyResult = new NativeCopyEntriesResult
            {
                StructSize = (uint)sizeof(NativeCopyEntriesResult),
                StructVersion = StructVersion
            };
            uint returnedStatus = _copyEntries(_handle, &request, &copyResult);
            ValidateStatus("copy entries", returnedStatus, copyResult.Status);
            if (copyResult.Status == StatusBufferTooSmall)
            {
                throw new InvalidDataException(
                    $"Rust SearchCore text-size contract changed during a read-only query: expected={requiredChars}, required={copyResult.RequiredUtf16Chars}.");
            }
            if (copyResult.Status != StatusOk || copyResult.CopiedEntryCount != (uint)resultCount)
            {
                throw new InvalidOperationException(
                    $"Rust SearchCore copy entries failed with status {copyResult.Status}.");
            }
        }

        var items = new SearchCoreQueryItem[resultCount];
        for (int index = 0; index < resultCount; index++)
        {
            NativeEntryText text = copiedEntries[index];
            NativeSearchResult native = nativeResults[index];
            ValidateTextRange(text.DirectoryOffsetChars, text.DirectoryLengthChars, copiedUtf16.Length);
            ValidateTextRange(text.FileNameOffsetChars, text.FileNameLengthChars, copiedUtf16.Length);
            if (text.EntryId != native.EntryId ||
                text.Flags != native.Flags ||
                text.ModifiedUtcTicks != native.ModifiedUtcTicks)
            {
                throw new InvalidDataException("Rust SearchCore result and copied text descriptors disagree.");
            }
            string directory = new(
                copiedUtf16,
                checked((int)text.DirectoryOffsetChars),
                checked((int)text.DirectoryLengthChars));
            string fileName = new(
                copiedUtf16,
                checked((int)text.FileNameOffsetChars),
                checked((int)text.FileNameLengthChars));
            items[index] = new SearchCoreQueryItem(
                native.EntryId,
                directory,
                fileName,
                (native.Flags & EntryDirectory) != 0,
                new DateTime(native.ModifiedUtcTicks, DateTimeKind.Utc),
                native.Score);
        }
        return new SearchCoreQuerySnapshot(items, result.ScannedEntryCount, result.MatchedEntryCount);
    }

    internal SearchCoreMemoryStats GetMemoryStats()
    {
        ThrowIfDisposed();
        var stats = new NativeStats
        {
            StructSize = (uint)sizeof(NativeStats),
            StructVersion = StructVersion
        };
        uint returnedStatus = _stats(_handle, &stats);
        ValidateStatus("stats", returnedStatus, stats.Status);
        if (stats.Status != StatusOk || stats.Sealed > 1)
        {
            throw new InvalidDataException(
                $"Rust SearchCore returned invalid stats with status {stats.Status}.");
        }
        return new SearchCoreMemoryStats(
            stats.Sealed != 0,
            stats.EntryCount,
            stats.DirectoryCount,
            stats.EntryCapacityBytes,
            stats.DirectoryDescriptorCapacityBytes,
            stats.DirectoryUtf16CapacityBytes,
            stats.FileNameUtf16CapacityBytes,
            stats.BuildLookupCapacityBytes,
            stats.TotalTrackedCapacityBytes);
    }

    private void RequestCancellation()
    {
        nint handle = Interlocked.CompareExchange(ref _handle, 0, 0);
        if (handle != 0)
        {
            _ = _cancel(handle);
        }
    }

    private static void ValidateEntry(SearchCoreSourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry.DirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(entry.FileName);
        if (entry.DirectoryPath.Contains('\0') || entry.FileName.Contains('\0'))
        {
            throw new ArgumentException("SearchCore entries cannot contain embedded NUL characters.");
        }
    }

    private static void ValidateTextRange(uint offset, uint length, int totalChars)
    {
        ulong end = (ulong)offset + length;
        if (end > (ulong)totalChars)
        {
            throw new InvalidDataException("Rust SearchCore returned an out-of-range UTF-16 slice.");
        }
    }

    private static bool TryLoadModule(
        string modulePath,
        out NativeExports exports,
        out string error)
    {
        exports = default;
        error = string.Empty;
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            error = $"Rust SearchCore supports x64 and ARM64; process architecture is {RuntimeInformation.ProcessArchitecture}.";
            return false;
        }
        if (!ManagedOrdinalCasingMatchesSearchCoreV3())
        {
            error = "The active .NET globalization mode does not match SearchCore ABI v3 ordinal-ignore-case semantics.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(modulePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
        {
            error = $"Rust SearchCore module was not found at '{fullPath}'.";
            return false;
        }

        nint module = 0;
        try
        {
            module = NativeLibrary.Load(fullPath);
            nint abiExport = RequireExport(module, "deskbox_search_core_abi_version");
            var abiVersion = (delegate* unmanaged[Cdecl]<uint>)(void*)abiExport;
            uint actualAbi = abiVersion();
            if (actualAbi != AbiVersion)
            {
                error = $"Rust SearchCore ABI mismatch: expected {AbiVersion}, found {actualAbi}.";
                NativeLibrary.Free(module);
                return false;
            }

            exports = new NativeExports(
                module,
                RequireExport(module, "deskbox_search_core_open_dbix_v1"),
                RequireExport(module, "deskbox_search_core_create_v1"),
                RequireExport(module, "deskbox_search_core_add_batch_v1"),
                RequireExport(module, "deskbox_search_core_seal_v1"),
                RequireExport(module, "deskbox_search_core_reset_cancel_v1"),
                RequireExport(module, "deskbox_search_core_cancel_v1"),
                RequireExport(module, "deskbox_search_core_query_v1"),
                RequireExport(module, "deskbox_search_core_copy_entries_v1"),
                RequireExport(module, "deskbox_search_core_mutate_batch_v1"),
                RequireExport(module, "deskbox_search_core_project_v1"),
                RequireExport(module, "deskbox_search_core_save_dbix_v1"),
                RequireExport(module, "deskbox_search_core_stats_v1"),
                RequireExport(module, "deskbox_search_core_destroy_v1"));
            return true;
        }
        catch (Exception ex)
        {
            if (module != 0)
            {
                NativeLibrary.Free(module);
            }
            error = ex.Message;
            exports = default;
            return false;
        }
    }

    private static nint RequireExport(nint module, string name)
    {
        if (!NativeLibrary.TryGetExport(module, name, out nint address))
        {
            throw new EntryPointNotFoundException($"Rust SearchCore export '{name}' is missing.");
        }
        return address;
    }

    private static bool ManagedOrdinalCasingMatchesSearchCoreV3()
    {
        return string.Equals("ς", "σ", StringComparison.OrdinalIgnoreCase) &&
               string.Equals("𐐀", "𐐨", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals("K", "k", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals("ı", "I", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals("ß", "ẞ", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeDbixFailure(uint status) => status switch
    {
        StatusIoError => "Rust SearchCore could not open or read the DBIX file.",
        StatusUnsupportedFormat => "Rust SearchCore rejected the DBIX version or timestamp semantics; rebuild/fallback is required.",
        StatusCorruptData => "Rust SearchCore detected a truncated or corrupt DBIX file; rebuild/fallback is required.",
        StatusInvalidArgument => "Rust SearchCore rejected the DBIX request contract.",
        _ => $"Rust SearchCore DBIX load failed with status {status}."
    };

    private static void ValidateStatus(string operation, uint returnedStatus, uint resultStatus)
    {
        if (returnedStatus != resultStatus)
        {
            throw new InvalidDataException(
                $"Rust SearchCore {operation} returned inconsistent status values: return={returnedStatus}, result={resultStatus}.");
        }
    }

    private static void EnsureStatus(string operation, uint returnedStatus, uint expectedStatus)
    {
        if (returnedStatus != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Rust SearchCore {operation} failed with status {returnedStatus}.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = _destroy(handle);
        }
        nint module = Interlocked.Exchange(ref _module, 0);
        if (module != 0)
        {
            NativeLibrary.Free(module);
        }
        GC.SuppressFinalize(this);
    }

    private readonly record struct NativeExports(
        nint Module,
        nint OpenDbix,
        nint Create,
        nint AddBatch,
        nint Seal,
        nint ResetCancel,
        nint Cancel,
        nint Query,
        nint CopyEntries,
        nint MutateBatch,
        nint Project,
        nint SaveDbix,
        nint Stats,
        nint Destroy);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOpenDbixRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal char* Path;
        internal uint PathLengthChars;
        internal uint MaxEntryCount;
        internal uint Flags;
        internal uint Reserved0;
        internal nint CancelEvent;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOpenDbixResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint DbixVersion;
        internal nint Handle;
        internal long PersistedUtcTicks;
        internal ulong SourceFileBytes;
        internal uint EntryCount;
        internal uint DirectoryCount;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCreateRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint InitialEntryCapacity;
        internal uint InitialUtf16CapacityChars;
        internal uint Flags;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCreateResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint Reserved0;
        internal nint Handle;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEntryInput
    {
        internal uint DirectoryOffsetChars;
        internal uint DirectoryLengthChars;
        internal uint FileNameOffsetChars;
        internal uint FileNameLengthChars;
        internal long ModifiedUtcTicks;
        internal uint Flags;
        internal uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAddBatchRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal NativeEntryInput* Entries;
        internal uint EntryCount;
        internal uint Reserved0;
        internal char* Utf16Data;
        internal uint Utf16LengthChars;
        internal uint Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
        internal ulong Reserved5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAddBatchResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint AddedEntryCount;
        internal uint TotalEntryCount;
        internal uint DirectoryCount;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSealResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint EntryCount;
        internal uint DirectoryCount;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSearchResult
    {
        internal uint EntryId;
        internal uint Score;
        internal long ModifiedUtcTicks;
        internal uint Flags;
        internal uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeQueryRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal char* Query;
        internal uint QueryLengthChars;
        internal uint MaxResults;
        internal NativeSearchResult* Results;
        internal uint ResultCapacity;
        internal uint Flags;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeQueryResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint ScannedEntryCount;
        internal uint MatchedEntryCount;
        internal uint WrittenResultCount;
        internal uint RequiredUtf16Chars;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEntryText
    {
        internal uint EntryId;
        internal uint DirectoryOffsetChars;
        internal uint DirectoryLengthChars;
        internal uint FileNameOffsetChars;
        internal uint FileNameLengthChars;
        internal uint Flags;
        internal long ModifiedUtcTicks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCopyEntriesRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint* EntryIds;
        internal uint EntryCount;
        internal uint Reserved0;
        internal NativeEntryText* Entries;
        internal uint EntryCapacity;
        internal uint Reserved1;
        internal char* Utf16Data;
        internal uint Utf16CapacityChars;
        internal uint Flags;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
        internal ulong Reserved5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCopyEntriesResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint CopiedEntryCount;
        internal uint RequiredUtf16Chars;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMutationInput
    {
        internal uint Operation;
        internal uint Flags;
        internal uint PathOffsetChars;
        internal uint PathLengthChars;
        internal uint DirectoryOffsetChars;
        internal uint DirectoryLengthChars;
        internal uint FileNameOffsetChars;
        internal uint FileNameLengthChars;
        internal long ModifiedUtcTicks;
        internal long ModifiedBinary;
        internal uint ScanGeneration;
        internal uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMutateBatchRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal NativeMutationInput* Mutations;
        internal uint MutationCount;
        internal uint Reserved0;
        internal char* Utf16Data;
        internal uint Utf16LengthChars;
        internal uint Flags;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMutateBatchResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint AppliedMutationCount;
        internal uint LiveEntryCount;
        internal uint TombstoneCount;
        internal uint DirectoryCount;
        internal uint Reserved0;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProjectionItem
    {
        internal uint PathOffsetChars;
        internal uint PathLengthChars;
        internal uint RankValue;
        internal uint Flags;
        internal long ModifiedUtcTicks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProjectRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint ProjectionKind;
        internal uint MaxResults;
        internal NativeProjectionItem* Items;
        internal uint ItemCapacity;
        internal uint Reserved0;
        internal char* Utf16Data;
        internal uint Utf16CapacityChars;
        internal uint Flags;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProjectResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint WrittenItemCount;
        internal uint RequiredUtf16Chars;
        internal uint ScannedEntryCount;
        internal uint Reserved0;
        internal uint Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
        internal ulong Reserved5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSaveDbixRequest
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal char* Path;
        internal uint PathLengthChars;
        internal uint Reserved0;
        internal char* TempPath;
        internal uint TempPathLengthChars;
        internal uint Flags;
        internal nint CancelEvent;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSaveDbixResult
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint DbixVersion;
        internal long PersistedUtcTicks;
        internal ulong FileBytes;
        internal uint EntryCount;
        internal uint DirectoryCount;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStats
    {
        internal uint StructSize;
        internal uint StructVersion;
        internal uint Status;
        internal uint Sealed;
        internal uint EntryCount;
        internal uint DirectoryCount;
        internal ulong EntryCapacityBytes;
        internal ulong DirectoryDescriptorCapacityBytes;
        internal ulong DirectoryUtf16CapacityBytes;
        internal ulong FileNameUtf16CapacityBytes;
        internal ulong BuildLookupCapacityBytes;
        internal ulong TotalTrackedCapacityBytes;
        internal ulong Reserved1;
        internal ulong Reserved2;
        internal ulong Reserved3;
        internal ulong Reserved4;
    }
}

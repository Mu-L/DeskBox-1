using System.Runtime.InteropServices;

namespace DeskBox.SearchCore.Benchmarks;

internal sealed unsafe class NativeSearchBackend : ISearchBackend
{
    private const uint AbiVersion = 3;
    private const uint StructVersion = 1;
    private const uint StatusOk = 0;
    private const uint StatusCancelled = 5;
    private const uint EntryDirectory = 1;

    private readonly delegate* unmanaged[Cdecl]<nint, uint> _resetCancel;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _cancel;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeQueryRequest*, NativeQueryResult*, uint> _query;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeCopyRequest*, NativeCopyResult*, uint> _copy;
    private readonly delegate* unmanaged[Cdecl]<nint, NativeStats*, uint> _stats;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _destroy;
    private nint _module;
    private nint _handle;

    private NativeSearchBackend(
        nint module,
        nint handle,
        int entryCount,
        int directoryCount,
        NativeExports exports)
    {
        _module = module;
        _handle = handle;
        EntryCount = entryCount;
        DirectoryCount = directoryCount;
        _resetCancel = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.ResetCancel;
        _cancel = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Cancel;
        _query = (delegate* unmanaged[Cdecl]<nint, NativeQueryRequest*, NativeQueryResult*, uint>)(void*)exports.Query;
        _copy = (delegate* unmanaged[Cdecl]<nint, NativeCopyRequest*, NativeCopyResult*, uint>)(void*)exports.Copy;
        _stats = (delegate* unmanaged[Cdecl]<nint, NativeStats*, uint>)(void*)exports.Stats;
        _destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)exports.Destroy;

        NativeStats stats = ReadStats();
        NativeTrackedCapacityBytes = stats.TotalTrackedCapacityBytes;
        NativeBuildLookupCapacityBytes = stats.BuildLookupCapacityBytes;
    }

    public int EntryCount { get; }

    public int DirectoryCount { get; }

    public ulong NativeTrackedCapacityBytes { get; }

    public ulong NativeBuildLookupCapacityBytes { get; }

    internal static NativeSearchBackend Open(
        string modulePath,
        string dbixPath,
        int maximumEntries)
    {
        if (!ManagedOrdinalCasingMatches())
        {
            throw new PlatformNotSupportedException(
                "Active .NET ordinal casing does not match SearchCore ABI v3.");
        }
        string fullModulePath = Path.GetFullPath(modulePath);
        string fullDbixPath = Path.GetFullPath(dbixPath);
        nint module = NativeLibrary.Load(fullModulePath);
        nint openedHandle = 0;
        try
        {
            nint abiAddress = RequireExport(module, "deskbox_search_core_abi_version");
            var abi = (delegate* unmanaged[Cdecl]<uint>)(void*)abiAddress;
            if (abi() != AbiVersion)
            {
                throw new InvalidDataException("SearchCore ABI mismatch.");
            }
            var exports = new NativeExports(
                RequireExport(module, "deskbox_search_core_open_dbix_v1"),
                RequireExport(module, "deskbox_search_core_reset_cancel_v1"),
                RequireExport(module, "deskbox_search_core_cancel_v1"),
                RequireExport(module, "deskbox_search_core_query_v1"),
                RequireExport(module, "deskbox_search_core_copy_entries_v1"),
                RequireExport(module, "deskbox_search_core_stats_v1"),
                RequireExport(module, "deskbox_search_core_destroy_v1"));
            fixed (char* pathPointer = fullDbixPath)
            {
                var request = new NativeOpenRequest
                {
                    StructSize = (uint)sizeof(NativeOpenRequest),
                    StructVersion = StructVersion,
                    Path = pathPointer,
                    PathLengthChars = (uint)fullDbixPath.Length,
                    MaxEntryCount = (uint)maximumEntries
                };
                var result = new NativeOpenResult
                {
                    StructSize = (uint)sizeof(NativeOpenResult),
                    StructVersion = StructVersion
                };
                var open = (delegate* unmanaged[Cdecl]<NativeOpenRequest*, NativeOpenResult*, uint>)(void*)exports.Open;
                uint returned = open(&request, &result);
                ValidateStatus("open", returned, result.Status);
                openedHandle = result.Handle;
                if (result.Status != StatusOk ||
                    openedHandle == 0 ||
                    result.EntryCount == 0 ||
                    result.EntryCount > maximumEntries)
                {
                    throw new InvalidDataException(
                        $"SearchCore DBIX open failed with status {result.Status}.");
                }
                var backend = new NativeSearchBackend(
                    module,
                    openedHandle,
                    checked((int)result.EntryCount),
                    checked((int)result.DirectoryCount),
                    exports);
                openedHandle = 0;
                module = 0;
                return backend;
            }
        }
        finally
        {
            if (openedHandle != 0)
            {
                nint destroyAddress = NativeLibrary.GetExport(module, "deskbox_search_core_destroy_v1");
                var destroy = (delegate* unmanaged[Cdecl]<nint, uint>)(void*)destroyAddress;
                _ = destroy(openedHandle);
            }
            if (module != 0)
            {
                NativeLibrary.Free(module);
            }
        }
    }

    public IReadOnlyList<SearchHit> Search(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        string normalized = query.Trim();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStatus("reset cancellation", _resetCancel(_handle), StatusOk);
        var nativeResults = new NativeSearchResult[maxResults];
        NativeQueryResult result;
        using (cancellationToken.UnsafeRegister(
            static state => ((NativeSearchBackend)state!).RequestCancellation(),
            this))
        {
            fixed (char* queryPointer = normalized)
            fixed (NativeSearchResult* resultPointer = nativeResults)
            {
                var request = new NativeQueryRequest
                {
                    StructSize = (uint)sizeof(NativeQueryRequest),
                    StructVersion = StructVersion,
                    Query = queryPointer,
                    QueryLengthChars = (uint)normalized.Length,
                    MaxResults = (uint)maxResults,
                    Results = resultPointer,
                    ResultCapacity = (uint)nativeResults.Length
                };
                result = new NativeQueryResult
                {
                    StructSize = (uint)sizeof(NativeQueryResult),
                    StructVersion = StructVersion
                };
                uint returned = _query(_handle, &request, &result);
                ValidateStatus("query", returned, result.Status);
            }
        }
        if (result.Status == StatusCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (result.Status != StatusOk)
        {
            throw new InvalidOperationException($"SearchCore query failed: {result.Status}.");
        }
        int count = checked((int)result.WrittenResultCount);
        if (count == 0)
        {
            return [];
        }
        var entryIds = new uint[count];
        for (int index = 0; index < count; index++)
        {
            entryIds[index] = nativeResults[index].EntryId;
        }
        var descriptors = new NativeEntryText[count];
        var text = new char[checked((int)result.RequiredUtf16Chars)];
        fixed (uint* idsPointer = entryIds)
        fixed (NativeEntryText* descriptorsPointer = descriptors)
        fixed (char* textPointer = text)
        {
            var request = new NativeCopyRequest
            {
                StructSize = (uint)sizeof(NativeCopyRequest),
                StructVersion = StructVersion,
                EntryIds = idsPointer,
                EntryCount = (uint)count,
                Entries = descriptorsPointer,
                EntryCapacity = (uint)count,
                Utf16Data = textPointer,
                Utf16CapacityChars = (uint)text.Length
            };
            var copyResult = new NativeCopyResult
            {
                StructSize = (uint)sizeof(NativeCopyResult),
                StructVersion = StructVersion
            };
            uint returned = _copy(_handle, &request, &copyResult);
            ValidateStatus("copy", returned, copyResult.Status);
            if (copyResult.Status != StatusOk || copyResult.CopiedEntryCount != count)
            {
                throw new InvalidDataException("SearchCore copy failed.");
            }
        }

        var hits = new SearchHit[count];
        for (int index = 0; index < count; index++)
        {
            NativeEntryText descriptor = descriptors[index];
            NativeSearchResult resultItem = nativeResults[index];
            string directory = new(
                text,
                checked((int)descriptor.DirectoryOffsetChars),
                checked((int)descriptor.DirectoryLengthChars));
            string fileName = new(
                text,
                checked((int)descriptor.FileNameOffsetChars),
                checked((int)descriptor.FileNameLengthChars));
            hits[index] = new SearchHit(
                directory,
                fileName,
                (resultItem.Flags & EntryDirectory) != 0,
                resultItem.ModifiedUtcTicks,
                resultItem.Score);
        }
        return hits;
    }

    private NativeStats ReadStats()
    {
        var stats = new NativeStats
        {
            StructSize = (uint)sizeof(NativeStats),
            StructVersion = StructVersion
        };
        uint returned = _stats(_handle, &stats);
        ValidateStatus("stats", returned, stats.Status);
        if (stats.Status != StatusOk || stats.Sealed != 1)
        {
            throw new InvalidDataException("SearchCore stats are invalid.");
        }
        return stats;
    }

    private void RequestCancellation()
    {
        nint handle = Interlocked.CompareExchange(ref _handle, 0, 0);
        if (handle != 0)
        {
            _ = _cancel(handle);
        }
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
    }

    private static nint RequireExport(nint module, string name) =>
        NativeLibrary.TryGetExport(module, name, out nint address)
            ? address
            : throw new EntryPointNotFoundException(name);

    private static void ValidateStatus(string operation, uint returned, uint output)
    {
        if (returned != output)
        {
            throw new InvalidDataException(
                $"SearchCore {operation} status mismatch: {returned}/{output}.");
        }
    }

    private static void EnsureStatus(string operation, uint returned, uint expected)
    {
        if (returned != expected)
        {
            throw new InvalidOperationException(
                $"SearchCore {operation} failed: {returned}.");
        }
    }

    private static bool ManagedOrdinalCasingMatches() =>
        string.Equals("ς", "σ", StringComparison.OrdinalIgnoreCase) &&
        string.Equals("𐐀", "𐐨", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals("K", "k", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals("ı", "I", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals("ß", "ẞ", StringComparison.OrdinalIgnoreCase);

    private readonly record struct NativeExports(
        nint Open,
        nint ResetCancel,
        nint Cancel,
        nint Query,
        nint Copy,
        nint Stats,
        nint Destroy);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOpenRequest
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
    private struct NativeOpenResult
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
    private struct NativeCopyRequest
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
    private struct NativeCopyResult
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

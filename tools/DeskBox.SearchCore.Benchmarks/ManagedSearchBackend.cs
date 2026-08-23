using System.Buffers;
using System.Text;

namespace DeskBox.SearchCore.Benchmarks;

internal sealed class ManagedSearchBackend : ISearchBackend
{
    private readonly Dictionary<string, IndexedEntry> _entries;

    private ManagedSearchBackend(
        Dictionary<string, IndexedEntry> entries,
        int directoryCount)
    {
        _entries = entries;
        DirectoryCount = directoryCount;
    }

    public int EntryCount => _entries.Count;

    public int DirectoryCount { get; }

    public ulong NativeTrackedCapacityBytes => 0;

    public ulong NativeBuildLookupCapacityBytes => 0;

    internal static ManagedSearchBackend Open(string path, int maximumEntries)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != DbixFixture.Magic)
        {
            throw new InvalidDataException("DBIX magic mismatch.");
        }
        if (reader.ReadInt32() != DbixFixture.Version)
        {
            throw new InvalidDataException("DBIX version mismatch.");
        }
        _ = reader.ReadInt64();
        int directoryCount = reader.ReadInt32();
        if (directoryCount < 0 || directoryCount > maximumEntries)
        {
            throw new InvalidDataException("Invalid DBIX directory count.");
        }
        var directories = new string[directoryCount];
        var directoryPool = new Dictionary<string, string>(
            directoryCount,
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < directoryCount; index++)
        {
            string directory = reader.ReadString();
            directories[index] = directory;
            directoryPool[directory] = directory;
        }

        int entryCount = reader.ReadInt32();
        if (entryCount <= 0 || entryCount > maximumEntries)
        {
            throw new InvalidDataException("Invalid DBIX entry count.");
        }
        var entries = new Dictionary<string, IndexedEntry>(
            entryCount,
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entryCount; index++)
        {
            int directoryId = reader.ReadInt32();
            if ((uint)directoryId >= (uint)directories.Length)
            {
                throw new InvalidDataException("Invalid DBIX directory reference.");
            }
            string fileName = ReadUtf8String(reader);
            bool isDirectory = reader.ReadBoolean();
            DateTime modified = DateTime.FromBinary(reader.ReadInt64());
            string directory = directories[directoryId];
            string fullPath = string.IsNullOrEmpty(directory)
                ? fileName
                : Path.Combine(directory, fileName);
            entries[fullPath] = new IndexedEntry(
                directory,
                fullPath.Length - fileName.Length,
                isDirectory,
                modified);
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("DBIX contains trailing data.");
        }
        return new ManagedSearchBackend(entries, directoryPool.Count);
    }

    public IReadOnlyList<SearchHit> Search(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        string normalized = query.Trim();
        var topResults = new PriorityQueue<Candidate, (uint Score, long ModifiedTicks)>();
        ReadOnlySpan<char> querySpan = normalized.AsSpan();
        foreach ((string fullPath, IndexedEntry entry) in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<char> fileName = fullPath.AsSpan(entry.FileNameStart);
            uint score = ComputeRelevance(fileName, querySpan);
            if (score == 0)
            {
                continue;
            }
            long modifiedUtcTicks = entry.LastModified.ToUniversalTime().Ticks;
            topResults.Enqueue(
                new Candidate(fullPath, entry, score, modifiedUtcTicks),
                (score, modifiedUtcTicks));
            if (topResults.Count > maxResults)
            {
                topResults.Dequeue();
            }
        }

        return topResults.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ModifiedUtcTicks)
            .Take(maxResults)
            .Select(candidate => new SearchHit(
                candidate.Entry.DirectoryPath,
                candidate.FullPath[candidate.Entry.FileNameStart..],
                candidate.Entry.IsDirectory,
                candidate.ModifiedUtcTicks,
                candidate.Score))
            .ToArray();
    }

    private static uint ComputeRelevance(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> query)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }
        ReadOnlySpan<char> nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExtension.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }
        if (nameWithoutExtension.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }
        return fileName.Contains(query, StringComparison.OrdinalIgnoreCase) ? 50U : 0U;
    }

    private static string ReadUtf8String(BinaryReader reader)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid DBIX filename length.");
        }
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(byteCount, 1));
        try
        {
            reader.BaseStream.ReadExactly(bytes.AsSpan(0, byteCount));
            return Encoding.UTF8.GetString(bytes, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void Dispose()
    {
    }

    private readonly record struct IndexedEntry(
        string DirectoryPath,
        int FileNameStart,
        bool IsDirectory,
        DateTime LastModified);

    private readonly record struct Candidate(
        string FullPath,
        IndexedEntry Entry,
        uint Score,
        long ModifiedUtcTicks);
}

namespace DeskBox.Services;

internal enum UsnJournalChangeKind
{
    Upsert,
    Delete,
    RenameOld,
    RenameNew,
    ReplaceHardLinks
}

internal readonly record struct UsnJournalRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    bool IsDirectory,
    long Timestamp);

internal readonly record struct UsnJournalChange(
    UsnJournalChangeKind Kind,
    UsnJournalRecord Record,
    IReadOnlyList<UsnJournalRecord>? ReplacementLinks = null,
    uint Reason = 0);

internal sealed record UsnJournalChangeImpact(
    IReadOnlyList<string> RemovedPaths,
    IReadOnlySet<ulong> UpsertFileReferenceNumbers,
    IReadOnlySet<ulong> RebuildDirectoryReferenceNumbers,
    bool Changed)
{
    public static UsnJournalChangeImpact None { get; } = new(
        [],
        new HashSet<ulong>(),
        new HashSet<ulong>(),
        false);
}

/// <summary>Pure, cloneable FRN/name reducer. Files may have multiple parent/name links.</summary>
internal sealed class UsnJournalChangeReducer
{
    internal const ulong RootFileReferenceNumber = 5;
    private const int MaxPendingRenameRecords = 4096;

    private readonly string _root;
    private readonly Dictionary<ulong, UsnJournalRecord> _records = [];
    private readonly Dictionary<ulong, Dictionary<string, UsnJournalRecord>> _fileLinks = [];
    private readonly Dictionary<ulong, UsnJournalRecord> _pendingRenameOld = [];

    public UsnJournalChangeReducer(string root)
    {
        _root = root.TrimEnd('\\');
    }

    public IReadOnlyDictionary<ulong, UsnJournalRecord> Records => _records;
    internal int PendingRenameCount => _pendingRenameOld.Count;

    internal sealed record Checkpoint(
        IReadOnlyDictionary<ulong, UsnJournalRecord?> Records,
        IReadOnlyDictionary<ulong, Dictionary<string, UsnJournalRecord>?> FileLinks,
        IReadOnlyDictionary<ulong, UsnJournalRecord> PendingRenames);

    public Checkpoint CreateCheckpoint(IEnumerable<UsnJournalChange> changes)
    {
        var affected = new HashSet<ulong>();
        foreach (UsnJournalChange change in changes)
        {
            affected.Add(change.Record.FileReferenceNumber);
            if (change.Kind == UsnJournalChangeKind.Delete && change.Record.IsDirectory)
            {
                affected.UnionWith(EnumerateSubtreeFrns(change.Record.FileReferenceNumber));
            }
        }

        var records = new Dictionary<ulong, UsnJournalRecord?>();
        var links = new Dictionary<ulong, Dictionary<string, UsnJournalRecord>?>();
        foreach (ulong frn in affected)
        {
            records[frn] = _records.TryGetValue(frn, out UsnJournalRecord record) ? record : null;
            links[frn] = _fileLinks.TryGetValue(frn, out Dictionary<string, UsnJournalRecord>? fileLinks)
                ? new Dictionary<string, UsnJournalRecord>(fileLinks, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        return new Checkpoint(records, links, new Dictionary<ulong, UsnJournalRecord>(_pendingRenameOld));
    }

    public void Restore(Checkpoint checkpoint)
    {
        foreach ((ulong frn, UsnJournalRecord? record) in checkpoint.Records)
        {
            if (record is null)
            {
                _records.Remove(frn);
            }
            else
            {
                _records[frn] = record.Value;
            }

            if (checkpoint.FileLinks[frn] is { } links)
            {
                _fileLinks[frn] = new Dictionary<string, UsnJournalRecord>(links, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _fileLinks.Remove(frn);
            }
        }

        _pendingRenameOld.Clear();
        foreach ((ulong frn, UsnJournalRecord record) in checkpoint.PendingRenames)
        {
            _pendingRenameOld[frn] = record;
        }
    }

    public UsnJournalChangeReducer Clone()
    {
        var clone = new UsnJournalChangeReducer(_root);
        foreach ((ulong frn, UsnJournalRecord record) in _records)
        {
            clone._records[frn] = record;
        }

        foreach ((ulong frn, Dictionary<string, UsnJournalRecord> links) in _fileLinks)
        {
            clone._fileLinks[frn] = new Dictionary<string, UsnJournalRecord>(links, StringComparer.OrdinalIgnoreCase);
        }

        foreach ((ulong frn, UsnJournalRecord record) in _pendingRenameOld)
        {
            clone._pendingRenameOld[frn] = record;
        }

        return clone;
    }

    public void ReplaceSnapshot(IEnumerable<UsnJournalRecord> records)
    {
        _records.Clear();
        _fileLinks.Clear();
        _pendingRenameOld.Clear();
        foreach (UsnJournalRecord record in records)
        {
            if (record.IsDirectory)
            {
                _records[record.FileReferenceNumber] = record;
            }
            else
            {
                AddOrUpdateFileLink(record);
            }
        }
    }

    public UsnJournalChangeImpact Apply(IEnumerable<UsnJournalChange> changes)
    {
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upsertFrns = new HashSet<ulong>();
        var rebuildDirectoryFrns = new HashSet<ulong>();
        bool changed = false;

        foreach (UsnJournalChange change in changes)
        {
            UsnJournalRecord incoming = change.Record;
            ulong frn = incoming.FileReferenceNumber;

            if (change.Kind == UsnJournalChangeKind.RenameOld)
            {
                if (_pendingRenameOld.Count >= MaxPendingRenameRecords)
                {
                    _pendingRenameOld.Clear();
                }

                _pendingRenameOld[frn] = incoming;
                continue;
            }

            if (change.Kind == UsnJournalChangeKind.ReplaceHardLinks)
            {
                AddAllResolvedPaths(frn, removedPaths);
                _fileLinks.Remove(frn);
                _records.Remove(frn);
                foreach (UsnJournalRecord link in change.ReplacementLinks ?? [])
                {
                    if (!link.IsDirectory && link.FileReferenceNumber == frn)
                    {
                        AddOrUpdateFileLink(link);
                    }
                }

                _pendingRenameOld.Remove(frn);
                upsertFrns.Add(frn);
                changed = true;
                continue;
            }

            if (change.Kind == UsnJournalChangeKind.Delete)
            {
                string? deletedPath = ResolveRecordPath(incoming);
                if (deletedPath is not null)
                {
                    removedPaths.Add(deletedPath);
                }

                if (incoming.IsDirectory)
                {
                    ulong[] subtree = EnumerateSubtreeFrns(frn).ToArray();
                    var removedDirectories = subtree
                        .Where(descendant => _records.TryGetValue(descendant, out UsnJournalRecord item) && item.IsDirectory)
                        .ToHashSet();
                    foreach (ulong descendant in subtree)
                    {
                        if (_records.TryGetValue(descendant, out UsnJournalRecord item) && item.IsDirectory)
                        {
                            _records.Remove(descendant);
                        }
                        else if (_fileLinks.TryGetValue(descendant, out Dictionary<string, UsnJournalRecord>? links))
                        {
                            foreach (string key in links
                                         .Where(pair => removedDirectories.Contains(pair.Value.ParentFileReferenceNumber))
                                         .Select(pair => pair.Key)
                                         .ToArray())
                            {
                                links.Remove(key);
                            }

                            if (links.Count == 0)
                            {
                                _fileLinks.Remove(descendant);
                                _records.Remove(descendant);
                            }
                            else
                            {
                                _records[descendant] = links.Values.First();
                            }
                        }

                        _pendingRenameOld.Remove(descendant);
                    }
                }
                else
                {
                    RemoveFileLink(incoming);
                    _pendingRenameOld.Remove(frn);
                }

                changed = true;
                continue;
            }

            UsnJournalRecord? explicitOld = null;
            if (change.Kind == UsnJournalChangeKind.RenameNew &&
                _pendingRenameOld.Remove(frn, out UsnJournalRecord renameOld))
            {
                explicitOld = renameOld;
            }
            else if (change.Kind != UsnJournalChangeKind.RenameNew)
            {
                _pendingRenameOld.Remove(frn);
            }

            if (explicitOld is not null)
            {
                string? oldPath = ResolveRecordPath(explicitOld.Value);
                if (oldPath is not null)
                {
                    removedPaths.Add(oldPath);
                }

                if (!explicitOld.Value.IsDirectory)
                {
                    RemoveFileLink(explicitOld.Value);
                }
            }

            if (incoming.IsDirectory)
            {
                if (_records.TryGetValue(frn, out UsnJournalRecord oldDirectory) && explicitOld is null)
                {
                    string? oldPath = ResolveRecordPath(oldDirectory);
                    if (oldPath is not null)
                    {
                        removedPaths.Add(oldPath);
                    }
                }

                _records[frn] = incoming;
                rebuildDirectoryFrns.Add(frn);
            }
            else
            {
                UpdateFileMetadataAndLink(incoming);
                upsertFrns.Add(frn);
            }

            changed = true;
        }

        return changed
            ? new UsnJournalChangeImpact(removedPaths.ToArray(), upsertFrns, rebuildDirectoryFrns, true)
            : UsnJournalChangeImpact.None;
    }

    public string? ResolvePath(ulong frn)
    {
        if (frn == RootFileReferenceNumber)
        {
            return _root;
        }

        return _records.TryGetValue(frn, out UsnJournalRecord record)
            ? ResolveRecordPath(record)
            : null;
    }

    public IEnumerable<string> ResolvePaths(ulong frn)
    {
        if (_fileLinks.TryGetValue(frn, out Dictionary<string, UsnJournalRecord>? links))
        {
            foreach (UsnJournalRecord link in links.Values)
            {
                string? path = ResolveRecordPath(link);
                if (path is not null)
                {
                    yield return path;
                }
            }

            yield break;
        }

        string? single = ResolvePath(frn);
        if (single is not null)
        {
            yield return single;
        }
    }

    public IEnumerable<UsnJournalRecord> EnumerateRecords(ulong frn)
    {
        if (_fileLinks.TryGetValue(frn, out Dictionary<string, UsnJournalRecord>? links))
        {
            return links.Values;
        }

        return _records.TryGetValue(frn, out UsnJournalRecord record) ? [record] : [];
    }

    public IEnumerable<UsnJournalRecord> EnumerateAllRecords()
    {
        foreach ((ulong frn, UsnJournalRecord record) in _records)
        {
            if (_fileLinks.TryGetValue(frn, out Dictionary<string, UsnJournalRecord>? links))
            {
                foreach (UsnJournalRecord link in links.Values)
                {
                    yield return link;
                }
            }
            else
            {
                yield return record;
            }
        }
    }

    public Dictionary<string, ulong> BuildDirectoryPathMap()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [_root] = RootFileReferenceNumber
        };
        foreach ((ulong frn, UsnJournalRecord record) in _records)
        {
            if (!record.IsDirectory)
            {
                continue;
            }

            string? path = ResolvePath(frn);
            if (path is not null)
            {
                result[path] = frn;
            }
        }

        return result;
    }

    public IEnumerable<ulong> EnumerateSubtreeFrns(ulong directoryFrn)
    {
        var children = new Dictionary<ulong, List<ulong>>();
        foreach ((ulong frn, UsnJournalRecord record) in _records)
        {
            IEnumerable<ulong> parents = _fileLinks.TryGetValue(frn, out Dictionary<string, UsnJournalRecord>? links)
                ? links.Values.Select(link => link.ParentFileReferenceNumber).Distinct()
                : [record.ParentFileReferenceNumber];
            foreach (ulong parentFrn in parents)
            {
                if (!children.TryGetValue(parentFrn, out List<ulong>? list))
                {
                    list = [];
                    children[parentFrn] = list;
                }

                list.Add(frn);
            }
        }

        var queue = new Queue<ulong>();
        var seen = new HashSet<ulong>();
        queue.Enqueue(directoryFrn);
        while (queue.Count > 0)
        {
            ulong current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            if (children.TryGetValue(current, out List<ulong>? childFrns))
            {
                foreach (ulong child in childFrns)
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    private void AddAllResolvedPaths(ulong frn, HashSet<string> paths)
    {
        foreach (string path in ResolvePaths(frn))
        {
            paths.Add(path);
        }
    }

    private void AddOrUpdateFileLink(UsnJournalRecord record)
    {
        if (!_fileLinks.TryGetValue(record.FileReferenceNumber, out Dictionary<string, UsnJournalRecord>? links))
        {
            links = new Dictionary<string, UsnJournalRecord>(StringComparer.OrdinalIgnoreCase);
            _fileLinks[record.FileReferenceNumber] = links;
        }

        links[GetLinkKey(record)] = record;
        _records[record.FileReferenceNumber] = links.Values.First();
    }

    private void UpdateFileMetadataAndLink(UsnJournalRecord incoming)
    {
        if (_fileLinks.TryGetValue(incoming.FileReferenceNumber, out Dictionary<string, UsnJournalRecord>? links))
        {
            foreach (string key in links.Keys.ToArray())
            {
                UsnJournalRecord link = links[key];
                links[key] = link with { Timestamp = incoming.Timestamp };
            }
        }

        AddOrUpdateFileLink(incoming);
    }

    private void RemoveFileLink(UsnJournalRecord record)
    {
        if (!_fileLinks.TryGetValue(record.FileReferenceNumber, out Dictionary<string, UsnJournalRecord>? links))
        {
            return;
        }

        links.Remove(GetLinkKey(record));
        if (links.Count == 0)
        {
            _fileLinks.Remove(record.FileReferenceNumber);
            _records.Remove(record.FileReferenceNumber);
        }
        else
        {
            _records[record.FileReferenceNumber] = links.Values.First();
        }
    }

    private static string GetLinkKey(UsnJournalRecord record) => $"{record.ParentFileReferenceNumber:X16}|{record.Name}";

    private string? ResolveRecordPath(UsnJournalRecord record)
    {
        string? parentPath = ResolveDirectoryPath(record.ParentFileReferenceNumber);
        return parentPath is null ? null : parentPath + "\\" + record.Name;
    }

    private string? ResolveDirectoryPath(ulong frn)
    {
        if (frn == RootFileReferenceNumber)
        {
            return _root;
        }

        var chain = new List<string>();
        var seen = new HashSet<ulong>();
        ulong current = frn;
        while (current != RootFileReferenceNumber)
        {
            if (!seen.Add(current) ||
                !_records.TryGetValue(current, out UsnJournalRecord record) ||
                !record.IsDirectory)
            {
                return null;
            }

            chain.Add(record.Name);
            if (record.ParentFileReferenceNumber == current)
            {
                break;
            }

            current = record.ParentFileReferenceNumber;
        }

        string path = _root;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path += "\\" + chain[i];
        }

        return path;
    }
}

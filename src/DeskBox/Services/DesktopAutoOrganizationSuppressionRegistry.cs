namespace DeskBox.Services;

/// <summary>
/// Coordinates explicit restore operations with the desktop watcher. Entries
/// are scoped to exact destination paths and expire quickly; completed moves
/// additionally require the observed file fingerprint to match.
/// </summary>
internal sealed class DesktopAutoOrganizationSuppressionRegistry
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, SuppressionEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _lifetime;

    public DesktopAutoOrganizationSuppressionRegistry(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? lifetime = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetime = lifetime ?? DefaultLifetime;
    }

    public void BeginOperation(
        string operationId,
        IEnumerable<FileService.FileTransferPlan> plans)
    {
        DateTimeOffset expiresAt = _utcNow() + _lifetime;
        lock (_gate)
        {
            RemoveExpiredEntriesLocked();
            foreach (FileService.FileTransferPlan plan in plans)
            {
                string? destination = NormalizePath(plan.DestinationPath);
                if (destination is null)
                {
                    continue;
                }

                _entries[destination] = new SuppressionEntry(
                    operationId,
                    expiresAt,
                    Fingerprint: null,
                    IsPending: true);
            }
        }
    }

    public void CompleteOperation(
        string operationId,
        IEnumerable<string> destinationPaths)
    {
        var destinations = destinationPaths
            .Select(NormalizePath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset expiresAt = _utcNow() + _lifetime;
        lock (_gate)
        {
            RemoveExpiredEntriesLocked();
            foreach ((string path, SuppressionEntry entry) in _entries.ToArray())
            {
                if (!string.Equals(entry.OperationId, operationId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!destinations.Contains(path) || !TryCaptureFingerprint(path, out FileFingerprint fingerprint))
                {
                    _entries.Remove(path);
                    continue;
                }

                _entries[path] = entry with
                {
                    ExpiresAt = expiresAt,
                    Fingerprint = fingerprint,
                    IsPending = false
                };
            }
        }
    }

    public bool TryConsume(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpiredEntriesLocked();
            if (!_entries.TryGetValue(normalized, out SuppressionEntry? entry))
            {
                return false;
            }

            if (!entry.IsPending &&
                (!TryCaptureFingerprint(normalized, out FileFingerprint fingerprint) ||
                 fingerprint != entry.Fingerprint))
            {
                _entries.Remove(normalized);
                return false;
            }

            _entries.Remove(normalized);
            return true;
        }
    }

    private void RemoveExpiredEntriesLocked()
    {
        DateTimeOffset now = _utcNow();
        foreach (string path in _entries
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(path);
        }
    }

    private static string? NormalizePath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCaptureFingerprint(
        string path,
        out FileFingerprint fingerprint)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                fingerprint = default;
                return false;
            }

            fingerprint = new FileFingerprint(
                file.Length,
                file.LastWriteTimeUtc,
                file.CreationTimeUtc);
            return true;
        }
        catch (IOException)
        {
            fingerprint = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            fingerprint = default;
            return false;
        }
    }

    private sealed record SuppressionEntry(
        string OperationId,
        DateTimeOffset ExpiresAt,
        FileFingerprint? Fingerprint,
        bool IsPending);

    private readonly record struct FileFingerprint(
        long Length,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc);
}

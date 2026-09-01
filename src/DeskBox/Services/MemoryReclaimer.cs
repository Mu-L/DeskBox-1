using System.Diagnostics;

namespace DeskBox.Services;

internal enum MemoryReclaimStatus
{
    Completed,
    SkippedCooldown,
    SkippedInProgress,
    Failed
}

/// <summary>
/// Runs an intentionally infrequent full managed collection for disconnected
/// WinUI/WinRT object graphs. The application normally uses cache eviction only;
/// this boundary exists because native allocations are not visible to the GC's
/// managed-allocation trigger.
/// </summary>
internal readonly record struct MemoryReclaimResult(
    MemoryReclaimStatus Status,
    int CollectionsBefore,
    int CollectionsAfter,
    long HeapBeforeBytes,
    long HeapAfterBytes,
    long DurationMilliseconds,
    string Detail)
{
    internal bool Executed => Status == MemoryReclaimStatus.Completed;

    internal long ReleasedHeapBytes => Math.Max(
        0,
        HeapBeforeBytes - HeapAfterBytes);
}

internal static class MemoryReclaimer
{
    internal const int MinimumCooldownSeconds = 120;

    private static long s_lastCompletedTimestamp;
    private static int s_collectionRunning;

    internal static MemoryReclaimResult TryCollect(
        string reason,
        bool bypassCooldown = false)
    {
        reason = string.IsNullOrWhiteSpace(reason)
            ? "unspecified"
            : reason.Trim();
        long startedTimestamp = Stopwatch.GetTimestamp();

        if (Interlocked.Exchange(ref s_collectionRunning, 1) != 0)
        {
            return CreateSkippedResult(
                MemoryReclaimStatus.SkippedInProgress,
                startedTimestamp,
                "collection-in-progress");
        }

        try
        {
            long lastCompletedTimestamp = Volatile.Read(
                ref s_lastCompletedTimestamp);
            if (!bypassCooldown &&
                lastCompletedTimestamp != 0 &&
                Stopwatch.GetElapsedTime(lastCompletedTimestamp) <
                    TimeSpan.FromSeconds(MinimumCooldownSeconds))
            {
                return CreateSkippedResult(
                    MemoryReclaimStatus.SkippedCooldown,
                    startedTimestamp,
                    $"cooldown:{MinimumCooldownSeconds}s");
            }

            int collectionsBefore = GC.CollectionCount(GC.MaxGeneration);
            long heapBeforeBytes = GetHeapSizeBytes();

            // The first pass makes unreachable wrapper graphs eligible for
            // finalization. Waiting here is deliberate: releasing the native
            // WinRT references is the reason this operation exists.
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);
            GC.WaitForPendingFinalizers();

            // A second pass reclaims objects that became unreachable when their
            // finalizers released the native references.
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            Volatile.Write(
                ref s_lastCompletedTimestamp,
                Stopwatch.GetTimestamp());
            int collectionsAfter = GC.CollectionCount(GC.MaxGeneration);
            long heapAfterBytes = GetHeapSizeBytes();
            return new MemoryReclaimResult(
                MemoryReclaimStatus.Completed,
                collectionsBefore,
                collectionsAfter,
                heapBeforeBytes,
                heapAfterBytes,
                GetElapsedMilliseconds(startedTimestamp),
                reason);
        }
        catch (Exception ex)
        {
            return new MemoryReclaimResult(
                MemoryReclaimStatus.Failed,
                0,
                0,
                0,
                0,
                GetElapsedMilliseconds(startedTimestamp),
                $"{reason}:{ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref s_collectionRunning, 0);
        }
    }

    private static MemoryReclaimResult CreateSkippedResult(
        MemoryReclaimStatus status,
        long startedTimestamp,
        string detail)
    {
        return new MemoryReclaimResult(
            status,
            GC.CollectionCount(GC.MaxGeneration),
            GC.CollectionCount(GC.MaxGeneration),
            GetHeapSizeBytes(),
            GetHeapSizeBytes(),
            GetElapsedMilliseconds(startedTimestamp),
            detail);
    }

    private static long GetHeapSizeBytes()
    {
        try
        {
            return GC.GetGCMemoryInfo().HeapSizeBytes;
        }
        catch
        {
            return 0;
        }
    }

    private static long GetElapsedMilliseconds(long startedTimestamp) =>
        (long)Math.Max(
            0,
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
}

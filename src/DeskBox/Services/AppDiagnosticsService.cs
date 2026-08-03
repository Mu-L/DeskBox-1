using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Owns opt-in memory diagnostics and the UI-thread responsiveness watchdog.
/// Extracted from App.xaml.cs to reduce God Class complexity.
/// </summary>
public sealed class AppDiagnosticsService : IDisposable
{
    private DispatcherQueueTimer? _memoryDiagnosticTimer;
    private System.Threading.Timer? _uiWatchdogTimer;
    private volatile bool _uiHeartbeatReceived;
    private int _watchdogMissCount;
    private int _lifecycleEventCount;
    private DateTimeOffset? _lastLifecycleEventAt;
    private string _lastLifecycleReason = string.Empty;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isDisposed;

    public AppDiagnosticsService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Starts all diagnostic loops. Called once during app startup.
    /// </summary>
    public void StartAll()
    {
        ScheduleMemoryDiagnostics();
        StartUiThreadWatchdog();
    }

    public int LifecycleEventCount => Volatile.Read(ref _lifecycleEventCount);
    public DateTimeOffset? LastLifecycleEventAt => _lastLifecycleEventAt;
    public string LastLifecycleReason => _lastLifecycleReason;

    public AppRuntimeHealthSnapshot GetRuntimeHealthSnapshot(
        SearchIndexService? searchIndex,
        SearchEngineService? searchEngine = null,
        IEnumerable<FolderWatcherHealthSnapshot>? folderWatchers = null)
    {
        var folderHealth = folderWatchers?.ToList() ?? [];
        return new AppRuntimeHealthSnapshot(
            LifecycleEventCount,
            LastLifecycleEventAt,
            LastLifecycleReason,
            searchIndex?.WatcherCount ?? 0,
            searchIndex?.WatcherRecoveryCount ?? 0,
            searchIndex?.LastWatcherRecoveryTime,
            searchIndex?.IndexedCount ?? 0,
            searchIndex?.IsScanning == true,
            searchIndex?.LastScanTime,
            searchIndex?.WatcherCreationFailureCount ?? 0,
            searchIndex?.OfflineRootCount ?? 0,
            searchIndex?.PartialRootCount ?? 0,
            searchIndex?.LastScanCapacityLimited == true,
            searchEngine?.IsUsnIndexAvailable == true,
            searchEngine?.IsUsnIndexScanning == true,
            searchEngine?.IsUsnIndexIncrementalSyncing == true,
            folderHealth.Count(item => item.Status == FolderWatcherHealth.Unavailable),
            folderHealth.Count(item => item.Status == FolderWatcherHealth.Degraded),
            folderHealth.Count(item => item.Status == FolderWatcherHealth.AccessDenied));
    }

    /// <summary>
    /// Records a lifecycle recovery signal alongside the regular watchdog
    /// diagnostics so field logs show whether external-state recovery ran.
    /// </summary>
    public void RecordLifecycleEvent(string reason, SearchIndexService? searchIndex)
    {
        int count = Interlocked.Increment(ref _lifecycleEventCount);
        _lastLifecycleEventAt = DateTimeOffset.Now;
        _lastLifecycleReason = reason;
        App.Log(
            $"[Diagnostics] Lifecycle recovery #{count} reason={reason} " +
            $"searchWatchers={searchIndex?.WatcherCount ?? 0} " +
            $"searchRecoveries={searchIndex?.WatcherRecoveryCount ?? 0} " +
            $"lastSearchRecovery={searchIndex?.LastWatcherRecoveryTime?.ToString("O") ?? "none"}");
    }

    /// <summary>
    /// When perf logging is enabled, samples memory and cache statistics
    /// every 30 seconds so long-running memory trends can be observed.
    /// </summary>
    private void ScheduleMemoryDiagnostics()
    {
        if (!PerformanceLogger.IsEnabled)
        {
            return;
        }

        _memoryDiagnosticTimer = _dispatcherQueue.CreateTimer();
        _memoryDiagnosticTimer.Interval = TimeSpan.FromSeconds(30);
        _memoryDiagnosticTimer.IsRepeating = true;
        _memoryDiagnosticTimer.Tick += MemoryDiagnosticTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        _memoryDiagnosticTimer.Start();
        App.Log("[Perf] Memory diagnostics timer started (30s interval)");
    }

    private static void MemoryDiagnosticTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        PerformanceLogger.SampleMemory();
    }

    /// <summary>
    /// Starts a watchdog that detects when the UI thread is unresponsive.
    /// A background timer sets a heartbeat flag every 4 seconds; the UI thread
    /// clears it via DispatcherQueue.TryEnqueue. If the flag is still set on
    /// the next tick, the UI thread was blocked for more than 4 seconds.
    /// </summary>
    private void StartUiThreadWatchdog()
    {
        _uiHeartbeatReceived = true;

        _uiWatchdogTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!_uiHeartbeatReceived)
                {
                    int missCount = Interlocked.Increment(ref _watchdogMissCount);
                    int handleCount = 0;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetCurrentProcess();
                        handleCount = proc.HandleCount;
                    }
                    catch { }

                    App.Log($"[Watchdog] UI thread unresponsive (miss #{missCount}), " +
                        $"handles={handleCount}, " +
                        $"gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}");

                    if (missCount == 5)
                    {
                        // A blocked UI thread cannot be repaired by forcing a collection.
                        // In particular, waiting for WinUI/COM finalizers while the UI STA is
                        // unresponsive can make the original stall harder to recover from.
                        // Keep the watchdog diagnostic-only and let the runtime manage GC.
                        App.Log("[Watchdog] 5 consecutive misses — diagnostics only; forced GC suppressed");
                    }
                }
                else
                {
                    if (Interlocked.Exchange(ref _watchdogMissCount, 0) > 0)
                    {
                        App.Log("[Watchdog] UI thread recovered");
                    }
                }

                _uiHeartbeatReceived = false;
                _dispatcherQueue?.TryEnqueue(() => _uiHeartbeatReceived = true);
            }
            catch (Exception ex)
            {
                App.Log($"[Watchdog] Error: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));

        App.Log("[Watchdog] UI thread watchdog started (4s interval)");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_memoryDiagnosticTimer is not null)
        {
            _memoryDiagnosticTimer.Stop();
            _memoryDiagnosticTimer.Tick -= MemoryDiagnosticTimer_Tick;
            _memoryDiagnosticTimer = null;
            PerformanceLogger.RecordTransientUiTimerReleased();
        }
        _uiWatchdogTimer?.Dispose();
        _uiWatchdogTimer = null;
    }
}

public sealed record AppRuntimeHealthSnapshot(
    int LifecycleEventCount,
    DateTimeOffset? LastLifecycleEventAt,
    string LastLifecycleReason,
    int SearchWatcherCount,
    int SearchWatcherRecoveryCount,
    DateTime? LastSearchWatcherRecoveryTime,
    int IndexedEntryCount,
    bool IsSearchScanning,
    DateTime? LastSearchScanTime,
    int FailedSearchWatcherCount = 0,
    int OfflineSearchRootCount = 0,
    int PartialSearchRootCount = 0,
    bool SearchScanCapacityLimited = false,
    bool IsUsnIndexAvailable = false,
    bool IsUsnIndexScanning = false,
    bool IsUsnIndexIncrementalSyncing = false,
    int OfflineFolderCount = 0,
    int DegradedFolderCount = 0,
    int AccessDeniedFolderCount = 0);

using DeskBox.Helpers;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>
/// Monitors display configuration changes (hot-plug add/remove, resolution
/// changes, DPI changes) by periodically polling the display topology via
/// Win32 <c>EnumDisplayMonitors</c>.
/// <para>
/// This service provides a consolidated <see cref="DisplaysChanged"/> event
/// with debouncing, allowing the application to reposition widgets and
/// invalidate caches when the display topology changes.
/// </para>
/// </summary>
public sealed class DisplayAreaWatcherService : IDisposable
{
    private const int PollIntervalMs = 2000;
    private const int DebounceDelayMs = 500;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DisplayTopologySnapshotProvider _snapshotProvider;
    private volatile bool _isDisposed;
    private bool _isStarted;
    private bool _hasSnapshot;
    private int _pollInProgress;
    private int _captureFailureCount;
    private int _displayCount;
    private string _displaySignature = string.Empty;

    /// <summary>
    /// Fired when the set of displays has changed (add, remove, resolution,
    /// or DPI change).  Fires after a short debounce to avoid spamming
    /// during rapid display configuration changes.
    /// </summary>
    public event Action? DisplaysChanged;

    /// <summary>
    /// The current number of displays.
    /// </summary>
    public int DisplayCount => _displayCount;

    public DisplayAreaWatcherService(DispatcherQueue dispatcherQueue)
        : this(dispatcherQueue, snapshotProvider: null)
    {
    }

    internal DisplayAreaWatcherService(
        DispatcherQueue dispatcherQueue,
        DisplayTopologySnapshotProvider? snapshotProvider = null)
    {
        _dispatcherQueue = dispatcherQueue;
        _snapshotProvider = snapshotProvider ?? new DisplayTopologySnapshotProvider();
        _pollTimer = dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(PollIntervalMs);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += PollTimer_Tick;

        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    public void Start()
    {
        if (_isDisposed || _isStarted)
        {
            return;
        }

        _isStarted = true;
        _pollTimer.Start();
        _ = PollForChangesAsync();
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _ = PollForChangesAsync();
    }

    /// <summary>
    /// Forces an immediate topology check after a resume, unlock, or shell
    /// restart instead of waiting for the next two-second poll tick.
    /// </summary>
    public void RefreshNow()
    {
        if (_isDisposed)
        {
            return;
        }

        _ = PollForChangesAsync();
    }

    private async Task PollForChangesAsync()
    {
        if (_isDisposed || Interlocked.Exchange(ref _pollInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            DisplayTopologySnapshot snapshot;
            try
            {
                snapshot = await _snapshotProvider
                    .CaptureAsync()
                    .WaitAsync(CaptureTimeout);
            }
            catch (TimeoutException)
            {
                RecordCaptureFailure("timeout");
                return;
            }
            catch (Exception ex)
            {
                RecordCaptureFailure(ex.GetType().Name);
                return;
            }

            if (_isDisposed)
            {
                return;
            }

            if (_dispatcherQueue.HasThreadAccess)
            {
                ApplySnapshot(snapshot);
            }
            else
            {
                _dispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
        }
    }

    private void ApplySnapshot(DisplayTopologySnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!snapshot.IsValid)
        {
            RecordCaptureFailure(snapshot.FailureReason);
            return;
        }

        Interlocked.Exchange(ref _captureFailureCount, 0);
        if (!_hasSnapshot)
        {
            _hasSnapshot = true;
            _displayCount = snapshot.DisplayCount;
            _displaySignature = snapshot.SemanticSignature;
            App.Log(
                $"[DisplayAreaWatcher] Started, initial display count: {_displayCount}, " +
                $"signature: {_displaySignature}");
            return;
        }

        bool countChanged = snapshot.DisplayCount != _displayCount;
        bool signatureChanged = !string.Equals(
            snapshot.SemanticSignature,
            _displaySignature,
            StringComparison.Ordinal);
        if (!countChanged && !signatureChanged)
        {
            return;
        }

        _displayCount = snapshot.DisplayCount;
        _displaySignature = snapshot.SemanticSignature;

        App.Log(
            $"[DisplayAreaWatcher] Display topology changed: " +
            $"count={snapshot.DisplayCount} countChanged={countChanged} " +
            $"signature={snapshot.SemanticSignature}");

        // Restart the one-shot timer so a burst of native changes produces one event.
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void RecordCaptureFailure(string? reason)
    {
        int failureCount = Interlocked.Increment(ref _captureFailureCount);
        if (failureCount == 1 || failureCount % 10 == 0)
        {
            App.Log(
                $"[DisplayAreaWatcher] Snapshot unavailable count={failureCount} " +
                $"reason={reason ?? "unknown"}; retaining the last valid topology");
        }
    }

    private void DebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _debounceTimer.Stop();
        DisplaysChanged?.Invoke();
    }

    /// <summary>
    /// Creates a string signature of the current display topology
    /// (monitor bounds + work areas) to detect any geometry changes.
    /// </summary>
    internal static DisplayTopologySnapshot CaptureCurrentSnapshot()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<Win32Helper.MonitorWorkAreaInfo> areas =
                Win32Helper.GetMonitorWorkAreaInfos();
            if (areas.Count == 0)
            {
                return DisplayTopologySnapshot.Invalid("no-displays");
            }

            string semanticSignature = CreateSemanticSignature(areas);
            WidgetDisplayTopologySnapshot layoutTopology =
                WidgetTopologyLayoutService.CreateSnapshotFromMonitorAreas(areas);
            return new DisplayTopologySnapshot(
                areas.Count,
                semanticSignature,
                layoutTopology,
                IsValid: true,
                FailureReason: string.Empty);
        }
        catch (Exception ex)
        {
            return DisplayTopologySnapshot.Invalid(ex.GetType().Name);
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= 250)
            {
                App.Log(
                    $"[DisplayTopology] Slow background snapshot " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
        }
    }

    internal static string CaptureCurrentSignature()
    {
        DisplayTopologySnapshot snapshot = CaptureCurrentSnapshot();
        return snapshot.IsValid ? snapshot.SemanticSignature : string.Empty;
    }

    internal static string CreateSemanticSignature(
        IReadOnlyList<Win32Helper.MonitorWorkAreaInfo> areas)
    {
        return string.Join("|", areas
                .OrderBy(a => a.Monitor.Left)
                .ThenBy(a => a.Monitor.Top)
                .ThenBy(a => a.Monitor.Right)
                .ThenBy(a => a.Monitor.Bottom)
                .Select(a =>
                    FormattableString.Invariant(
                        $"{a.IsPrimary};{a.DpiScale:F3};{a.Monitor.Left},{a.Monitor.Top},{a.Monitor.Right},{a.Monitor.Bottom};{a.WorkArea.Left},{a.WorkArea.Top},{a.WorkArea.Right},{a.WorkArea.Bottom}")));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isStarted = false;
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
    }
}

internal sealed record DisplayTopologySnapshot(
    int DisplayCount,
    string SemanticSignature,
    WidgetDisplayTopologySnapshot? LayoutTopology,
    bool IsValid,
    string FailureReason)
{
    internal static DisplayTopologySnapshot Invalid(string? reason) => new(
        DisplayCount: 0,
        SemanticSignature: string.Empty,
        LayoutTopology: null,
        IsValid: false,
        FailureReason: string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);
}

/// <summary>
/// Shares one background native display capture between the polling fallback and
/// the transition coordinator. If a driver call stalls, callers time out while
/// this provider keeps the single in-flight capture instead of accumulating work.
/// </summary>
internal sealed class DisplayTopologySnapshotProvider
{
    private readonly object _gate = new();
    private readonly Func<DisplayTopologySnapshot> _captureAction;
    private Task<DisplayTopologySnapshot>? _inFlightCapture;

    public DisplayTopologySnapshotProvider(
        Func<DisplayTopologySnapshot>? captureAction = null)
    {
        _captureAction = captureAction ?? DisplayAreaWatcherService.CaptureCurrentSnapshot;
    }

    public Task<DisplayTopologySnapshot> CaptureAsync()
    {
        lock (_gate)
        {
            if (_inFlightCapture is { IsCompleted: false })
            {
                return _inFlightCapture;
            }

            _inFlightCapture = Task.Run(_captureAction);
            return _inFlightCapture;
        }
    }
}

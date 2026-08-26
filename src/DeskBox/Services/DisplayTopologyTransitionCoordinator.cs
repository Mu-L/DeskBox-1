using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Coalesces native per-window display messages and global topology polling into
/// one stable, generation-based restore operation.
/// </summary>
internal sealed class DisplayTopologyTransitionCoordinator : IDisposable
{
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(180);
    internal static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(140);
    internal static readonly TimeSpan SnapshotTimeout = TimeSpan.FromMilliseconds(1500);
    private const int MaxRestoreRetryCount = 8;
    private const int MaxSnapshotRetryCount = 4;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<Task<DisplayTopologySnapshot>> _snapshotProvider;
    private readonly Func<long, string, DisplayTopologySnapshot, Task<bool>> _restoreAction;
    private readonly DisplayTopologyStabilityTracker _stabilityTracker = new(requiredObservations: 2);

    private long _generation;
    private string _pendingReasons = string.Empty;
    private int _restoreRetryCount;
    private int _snapshotRetryCount;
    private string? _lastAppliedSignature;
    private DateTimeOffset _lastAppliedAtUtc;
    private string? _verificationSignature;
    private bool _verificationPending;
    private bool _isExecuting;
    private bool _isDisposed;

    public DisplayTopologyTransitionCoordinator(
        DispatcherQueue dispatcherQueue,
        Func<Task<DisplayTopologySnapshot>> snapshotProvider,
        Func<long, string, DisplayTopologySnapshot, Task<bool>> restoreAction)
    {
        _dispatcherQueue = dispatcherQueue;
        _snapshotProvider = snapshotProvider;
        _restoreAction = restoreAction;
        _timer = dispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += Timer_Tick;
    }

    public void RequestRestore(string reason)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => RequestRestore(reason));
            return;
        }

        _generation++;
        _pendingReasons = CombineReasons(_pendingReasons, reason);
        _restoreRetryCount = 0;
        _snapshotRetryCount = 0;
        _verificationPending = false;
        _verificationSignature = null;
        _stabilityTracker.Reset();
        Schedule(ObservationInterval);
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        _timer.Stop();
        if (_isDisposed)
        {
            return;
        }

        if (_isExecuting)
        {
            Schedule(ObservationInterval);
            return;
        }

        _isExecuting = true;
        long generation = _generation;
        try
        {
            DisplayTopologySnapshot snapshot = await CaptureSnapshotAsync();
            if (_isDisposed || generation != _generation)
            {
                return;
            }

            if (!snapshot.IsValid)
            {
                RetryOrAbandonUnavailableSnapshot(generation, snapshot.FailureReason);
                return;
            }

            _snapshotRetryCount = 0;
            string signature = snapshot.SemanticSignature;
            if (_verificationPending)
            {
                if (string.Equals(
                        signature,
                        _verificationSignature,
                        StringComparison.Ordinal))
                {
                    CompleteSuccessfulRestore(generation, signature);
                    return;
                }

                App.Log(
                    $"[DisplayTopology] Topology changed during verification " +
                    $"generation={generation}; observing the new snapshot");
                _verificationPending = false;
                _verificationSignature = null;
                _restoreRetryCount = 0;
                _stabilityTracker.Reset();
            }

            if (!_stabilityTracker.Observe(signature))
            {
                Schedule(ObservationInterval);
                return;
            }

            bool forceUnchangedRestore =
                RequiresRestoreWhenSignatureUnchanged(_pendingReasons);
            bool signatureAlreadyApplied = string.Equals(
                signature,
                _lastAppliedSignature,
                StringComparison.Ordinal);
            bool recentlyApplied =
                signatureAlreadyApplied &&
                DateTimeOffset.UtcNow - _lastAppliedAtUtc <= TimeSpan.FromSeconds(2);
            if (signatureAlreadyApplied &&
                (!forceUnchangedRestore || recentlyApplied))
            {
                App.LogVerbose(
                    $"[DisplayTopology] Restore skipped generation={generation} " +
                    $"reasons={_pendingReasons} signature={signature} " +
                    $"reason={(recentlyApplied ? "recently-applied" : "unchanged")}");
                ClearPendingState();
                return;
            }

            bool completed;
            try
            {
                completed = await _restoreAction(generation, _pendingReasons, snapshot);
            }
            catch (Exception ex)
            {
                completed = false;
                App.Log($"[DisplayTopology] Restore generation={generation} failed: {ex}");
            }

            if (completed)
            {
                // Record the successful apply before checking the generation.
                // Window/DPI messages raised by this apply may already own a
                // newer generation; that generation can now skip the same work.
                _lastAppliedSignature = signature;
                _lastAppliedAtUtc = DateTimeOffset.UtcNow;
            }

            // A newer native or polling signal owns the next pass. RequestRestore
            // has already reset the tracker and scheduled its observation timer.
            if (_isDisposed || generation != _generation)
            {
                return;
            }

            if (!completed)
            {
                if (_restoreRetryCount < MaxRestoreRetryCount)
                {
                    _restoreRetryCount++;
                    Schedule(ObservationInterval);
                    return;
                }

                App.Log(
                    $"[DisplayTopology] Restore abandoned generation={generation} " +
                    $"reasons={_pendingReasons} retries={_restoreRetryCount}");
                ClearPendingState();
                return;
            }

            // Remember the applied semantic topology immediately. If another
            // per-window message arrives before verification, it will not cause
            // the same full restore to run again.
            _verificationPending = true;
            _verificationSignature = signature;
            _restoreRetryCount = 0;
            _stabilityTracker.Reset();
            Schedule(VerificationDelay);
        }
        catch (Exception ex)
        {
            App.Log($"[DisplayTopology] Coordinator generation={generation} failed: {ex}");
            ClearPendingState();
        }
        finally
        {
            _isExecuting = false;
        }
    }

    private async Task<DisplayTopologySnapshot> CaptureSnapshotAsync()
    {
        try
        {
            return await _snapshotProvider().WaitAsync(SnapshotTimeout) ??
                DisplayTopologySnapshot.Invalid("null-snapshot");
        }
        catch (TimeoutException)
        {
            return DisplayTopologySnapshot.Invalid("timeout");
        }
        catch (Exception ex)
        {
            App.Log($"[DisplayTopology] Snapshot capture failed: {ex.Message}");
            return DisplayTopologySnapshot.Invalid(ex.GetType().Name);
        }
    }

    private void RetryOrAbandonUnavailableSnapshot(long generation, string reason)
    {
        if (_snapshotRetryCount < MaxSnapshotRetryCount)
        {
            _snapshotRetryCount++;
            Schedule(ObservationInterval);
            return;
        }

        App.Log(
            $"[DisplayTopology] Snapshot unavailable; restore skipped " +
            $"generation={generation} reasons={_pendingReasons} " +
            $"retries={_snapshotRetryCount} reason={reason}");
        ClearPendingState();
    }

    private void CompleteSuccessfulRestore(long generation, string signature)
    {
        App.Log(
            $"[DisplayTopology] Restore completed generation={generation} " +
            $"reasons={_pendingReasons} signature={signature}");
        ClearPendingState();
    }

    private void ClearPendingState()
    {
        _pendingReasons = string.Empty;
        _verificationPending = false;
        _verificationSignature = null;
        _restoreRetryCount = 0;
        _snapshotRetryCount = 0;
        _stabilityTracker.Reset();
    }

    private void Schedule(TimeSpan delay)
    {
        if (_isDisposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    internal static string CombineReasons(string current, string? next)
    {
        string normalized = string.IsNullOrWhiteSpace(next) ? "unspecified" : next.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return normalized;
        }

        return current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(normalized, StringComparer.Ordinal)
                ? current
                : current + "," + normalized;
    }

    internal static bool RequiresRestoreWhenSignatureUnchanged(string? reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
        {
            return false;
        }

        return reasons
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(reason =>
                reason.Equals("lifecycle-resume", StringComparison.Ordinal) ||
                reason.StartsWith("lifecycle-session-", StringComparison.Ordinal) ||
                reason.Contains("explorer-restart", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}

internal sealed class DisplayTopologyStabilityTracker
{
    private readonly int _requiredObservations;
    private string? _lastSignature;
    private int _observationCount;

    public DisplayTopologyStabilityTracker(int requiredObservations)
    {
        _requiredObservations = Math.Max(1, requiredObservations);
    }

    public string? LastSignature => _lastSignature;

    public bool Observe(string? signature)
    {
        string normalized = signature ?? string.Empty;
        if (!string.Equals(normalized, _lastSignature, StringComparison.Ordinal))
        {
            _lastSignature = normalized;
            _observationCount = 1;
        }
        else
        {
            _observationCount++;
        }

        return _observationCount >= _requiredObservations;
    }

    public void Reset()
    {
        _lastSignature = null;
        _observationCount = 0;
    }
}

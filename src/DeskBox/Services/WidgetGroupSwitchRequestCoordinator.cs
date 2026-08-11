using DeskBox.Controls;

namespace DeskBox.Services;

/// <summary>
/// Tracks the latest requested member switch for each stable widget surface.
/// A newer request cancels only the older request for the same surface, so
/// independent groups do not interfere with one another.
/// </summary>
internal sealed class WidgetGroupSwitchRequestCoordinator
{
    internal static readonly TimeSpan WheelGestureQuietPeriod =
        TimeSpan.FromMilliseconds(700);

    private readonly object _gate = new();
    private readonly Dictionary<string, WidgetGroupSwitchRequest> _currentRequests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastWheelStepAt =
        new(StringComparer.Ordinal);

    public bool TryAcceptWheelStep(
        string groupId,
        DateTimeOffset observedAt,
        out TimeSpan sincePreviousStep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        lock (_gate)
        {
            if (_lastWheelStepAt.TryGetValue(groupId, out var previous))
            {
                sincePreviousStep = observedAt - previous;
                if (sincePreviousStep >= TimeSpan.Zero &&
                    sincePreviousStep < WheelGestureQuietPeriod)
                {
                    // Refresh the timestamp for every tail event. Precision
                    // touchpad inertia can arrive in several waves, and the
                    // gesture should not re-arm until the whole stream has
                    // actually gone quiet.
                    _lastWheelStepAt[groupId] = observedAt;
                    return false;
                }
            }
            else
            {
                sincePreviousStep = TimeSpan.MaxValue;
            }

            _lastWheelStepAt[groupId] = observedAt;
            return true;
        }
    }

    public WidgetGroupSwitchRequest Begin(
        string groupId,
        string targetWidgetId,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWidgetId);

        WidgetGroupSwitchRequest? previous;
        WidgetGroupSwitchRequest request;
        lock (_gate)
        {
            _currentRequests.Remove(groupId, out previous);
            request = new WidgetGroupSwitchRequest(groupId, targetWidgetId, origin);
            _currentRequests[groupId] = request;
        }

        // Cancellation can synchronously invoke callbacks. Keep it outside the
        // coordinator lock so a callback can safely complete its old request.
        previous?.Cancel();
        previous?.Dispose();
        return request;
    }

    public bool IsCurrent(WidgetGroupSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            return _currentRequests.TryGetValue(request.GroupId, out var current) &&
                   ReferenceEquals(current, request);
        }
    }

    public bool IsCurrentTarget(string groupId, string targetWidgetId)
    {
        if (string.IsNullOrWhiteSpace(groupId) ||
            string.IsNullOrWhiteSpace(targetWidgetId))
        {
            return false;
        }

        lock (_gate)
        {
            return _currentRequests.TryGetValue(groupId, out var current) &&
                   string.Equals(
                       current.TargetWidgetId,
                       targetWidgetId,
                       StringComparison.Ordinal);
        }
    }

    public void Complete(WidgetGroupSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_currentRequests.TryGetValue(request.GroupId, out var current) &&
                ReferenceEquals(current, request))
            {
                _currentRequests.Remove(request.GroupId);
            }
        }

        request.Dispose();
    }

    public void Cancel(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        WidgetGroupSwitchRequest? request = null;
        lock (_gate)
        {
            _currentRequests.Remove(groupId, out request);
        }

        request?.Cancel();
        request?.Dispose();
    }

    public void CancelAll()
    {
        List<WidgetGroupSwitchRequest> requests;
        lock (_gate)
        {
            requests = _currentRequests.Values.ToList();
            _currentRequests.Clear();
            _lastWheelStepAt.Clear();
        }

        // Keep callbacks outside the coordinator lock for the same reason as
        // Begin and Cancel.
        foreach (WidgetGroupSwitchRequest request in requests)
        {
            request.Cancel();
            request.Dispose();
        }
    }
}

internal sealed class WidgetGroupSwitchRequest : IDisposable
{
    private readonly CancellationTokenSource _cancellationSource = new();
    private int _disposed;

    public WidgetGroupSwitchRequest(
        string groupId,
        string targetWidgetId,
        WidgetGroupSwitchOrigin origin)
    {
        GroupId = groupId;
        TargetWidgetId = targetWidgetId;
        Origin = origin;
        CancellationToken = _cancellationSource.Token;
    }

    public string GroupId { get; }

    public string TargetWidgetId { get; }
    public WidgetGroupSwitchOrigin Origin { get; }


    public CancellationToken CancellationToken { get; }

    public void Cancel()
    {
        try
        {
            _cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Complete/Cancel may race on shutdown; cancellation is already
            // terminal once the source has been disposed.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellationSource.Dispose();
        }
    }
}

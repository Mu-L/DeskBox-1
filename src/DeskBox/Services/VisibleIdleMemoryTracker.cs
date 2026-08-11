namespace DeskBox.Services;

/// <summary>
/// Converts periodic UI-activity snapshots into a deterministic idle signal.
/// The tracker deliberately has no timer of its own so callers can test it
/// with a fake clock and keep all WinUI scheduling on the UI thread.
/// </summary>
internal sealed class VisibleIdleMemoryTracker
{
    private readonly TimeSpan _requiredIdleDuration;
    private readonly TimeSpan _maintenanceCooldown;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _lastMaintenanceAt;

    public VisibleIdleMemoryTracker(
        TimeSpan requiredIdleDuration,
        TimeSpan maintenanceCooldown)
    {
        if (requiredIdleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredIdleDuration));
        }

        if (maintenanceCooldown < requiredIdleDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maintenanceCooldown));
        }

        _requiredIdleDuration = requiredIdleDuration;
        _maintenanceCooldown = maintenanceCooldown;
    }

    public bool Observe(DateTimeOffset now, bool isEligible)
    {
        if (!isEligible)
        {
            _idleSince = null;
            return false;
        }

        _idleSince ??= now;
        if (now - _idleSince.Value < _requiredIdleDuration)
        {
            return false;
        }

        if (_lastMaintenanceAt is DateTimeOffset lastMaintenanceAt &&
            now - lastMaintenanceAt < _maintenanceCooldown)
        {
            return false;
        }

        _lastMaintenanceAt = now;
        return true;
    }

    public void Reset()
    {
        _idleSince = null;
    }
}

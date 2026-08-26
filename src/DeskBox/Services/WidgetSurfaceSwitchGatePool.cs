using System.Collections.Concurrent;

namespace DeskBox.Services;

/// <summary>
/// Owns one non-disposed serialization gate per stable Surface id. Gates are
/// independent from HWND lifetime so a hidden group and its later host use
/// the same gate.
/// </summary>
internal sealed class WidgetSurfaceSwitchGatePool
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public int Count => _gates.Count;

    public SemaphoreSlim Get(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        return _gates.GetOrAdd(
            surfaceId,
            static _ => new SemaphoreSlim(1, 1));
    }

    public bool Remove(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        // Do not dispose a removed gate: a completed caller may still execute
        // its final Release. Removal is only used after the persisted surface
        // identity has been retired while the group mutation gate is held.
        return _gates.TryRemove(surfaceId, out _);
    }

    public void Clear()
    {
        _gates.Clear();
    }
}

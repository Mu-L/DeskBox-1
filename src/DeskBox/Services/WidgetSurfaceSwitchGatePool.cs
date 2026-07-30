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

    public SemaphoreSlim Get(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        return _gates.GetOrAdd(
            surfaceId,
            static _ => new SemaphoreSlim(1, 1));
    }
}

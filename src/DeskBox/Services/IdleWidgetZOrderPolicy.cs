namespace DeskBox.Services;

internal readonly record struct IdleWidgetZOrderCandidate(
    IntPtr WindowHandle,
    string DisplayKey,
    double Top,
    double Left,
    string StableKey);

/// <summary>
/// Defines the canonical peer order used while widgets are visible but idle.
/// Lower rows are kept above upper rows so an upper widget's downward shadow
/// cannot darken the top edge of the widget below it.
/// </summary>
internal static class IdleWidgetZOrderPolicy
{
    public static IReadOnlyList<IdleWidgetZOrderCandidate> OrderHighestToLowest(
        IEnumerable<IdleWidgetZOrderCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.WindowHandle != IntPtr.Zero)
            .GroupBy(candidate => candidate.WindowHandle)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayKey, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.Top)
            .ThenByDescending(candidate => candidate.Left)
            .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .ToList();
    }
}

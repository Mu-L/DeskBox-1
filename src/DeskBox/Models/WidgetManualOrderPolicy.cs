namespace DeskBox.Models;

/// <summary>
/// Reconciles a complete folder snapshot with the user's manual item order.
/// The live order wins during a running session; the persisted order is used
/// when a widget is cold-started and has no live items yet.
/// </summary>
internal static class WidgetManualOrderPolicy
{
    public static IReadOnlyList<T> Reconcile<T>(
        IReadOnlyList<T> refreshedItems,
        IReadOnlyList<string> liveOrderPaths,
        IReadOnlyList<WidgetItemConfig> persistedItems,
        Func<T, string> pathSelector)
    {
        ArgumentNullException.ThrowIfNull(refreshedItems);
        ArgumentNullException.ThrowIfNull(liveOrderPaths);
        ArgumentNullException.ThrowIfNull(persistedItems);
        ArgumentNullException.ThrowIfNull(pathSelector);

        IReadOnlyList<string> baseline = liveOrderPaths.Count > 0
            ? liveOrderPaths
            : persistedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .OrderBy(item => item.SortOrder)
                .Select(item => item.Path)
                .ToList();

        var rankByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < baseline.Count; index++)
        {
            string path = baseline[index];
            if (!string.IsNullOrWhiteSpace(path))
            {
                rankByPath.TryAdd(path, index);
            }
        }

        var uniqueItems = new List<(T Item, int SnapshotIndex, int? Rank)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < refreshedItems.Count; index++)
        {
            T item = refreshedItems[index];
            string path = pathSelector(item);
            if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
            {
                continue;
            }

            uniqueItems.Add((
                item,
                index,
                rankByPath.TryGetValue(path, out int rank) ? rank : null));
        }

        return uniqueItems
            .OrderBy(entry => entry.Rank.HasValue ? 0 : 1)
            .ThenBy(entry => entry.Rank ?? int.MaxValue)
            .ThenBy(entry => entry.SnapshotIndex)
            .Select(entry => entry.Item)
            .ToList();
    }
}

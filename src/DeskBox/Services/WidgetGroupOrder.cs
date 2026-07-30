namespace DeskBox.Services;

internal static class WidgetGroupOrder
{
    public static bool MoveToTargetSlot(
        IList<string> memberIds,
        string sourceWidgetId,
        string targetWidgetId)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        int sourceIndex = memberIds.IndexOf(sourceWidgetId);
        int targetIndex = memberIds.IndexOf(targetWidgetId);
        if (sourceIndex < 0 ||
            targetIndex < 0 ||
            sourceIndex == targetIndex)
        {
            return false;
        }

        memberIds.RemoveAt(sourceIndex);
        memberIds.Insert(
            Math.Clamp(targetIndex, 0, memberIds.Count),
            sourceWidgetId);
        return true;
    }
}

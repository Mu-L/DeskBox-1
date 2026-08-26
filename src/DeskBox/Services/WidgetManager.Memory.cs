namespace DeskBox.Services;

internal readonly record struct HiddenWidgetResourceReleaseResult(
    int ContentHostCount,
    int CachedContentCount);

public sealed partial class WidgetManager
{
    internal int ActiveFolderWatcherCount =>
        GetFolderWatcherHealthSnapshots().Count(snapshot =>
            snapshot.NativeWatcherActive || snapshot.QueryWatcherActive);

    internal int CachedGroupContentCount => _contentWidgets.Values
        .DistinctBy(window => window.WindowHandle)
        .Sum(window => window.CachedGroupContentCount);

    internal HiddenWidgetResourceReleaseResult ReleaseLongHiddenWidgetResources()
    {
        int contentHostCount = 0;
        int cachedContentCount = 0;
        foreach (var window in _contentWidgets.Values
                     .DistinctBy(window => window.WindowHandle))
        {
            if (window.Visible)
            {
                continue;
            }

            contentHostCount++;
            cachedContentCount += window.ReleaseLongHiddenContentResources();
        }

        return new HiddenWidgetResourceReleaseResult(
            contentHostCount,
            cachedContentCount);
    }

}

using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlanceWidgetStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_UsesPhaseOneDefaults()
    {
        var store = new GlanceWidgetStore(_tempRoot);

        GlanceWidgetData data = await store.LoadAsync();

        Assert.Equal(GlanceWidgetData.CurrentVersion, data.Version);
        Assert.True(data.ShowTime);
        Assert.True(data.ShowDate);
        Assert.False(data.ShowYear);
        Assert.True(data.ShowWeekday);
        Assert.False(data.ShowCalendar);
        Assert.Equal(GlanceLayoutMode.Centered, data.Layout);
        Assert.Equal(GlanceBackgroundSource.Bing, data.BackgroundSource);
        Assert.Equal(GlanceOnlineImageCategory.Featured, data.OnlineImageCategory);
        Assert.Equal(30, data.RotationIntervalMinutes);
        Assert.Equal(GlanceTransitionMode.CrossFade, data.Transition);
        Assert.Equal(GlanceCalendarMaterialMode.FollowSystem, data.CalendarMaterialMode);
        Assert.Equal(0.32, data.CalendarImageMaterialTransparency, precision: 2);
        Assert.Equal(GlanceTraditionalCalendarMode.None, data.TraditionalCalendarMode);
        Assert.True(data.ShowPhotoControls);
    }

    [Fact]
    public async Task SaveAsync_PreservesYearOnlyWhenDateIsVisible()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            ShowDate = true,
            ShowYear = true
        });

        GlanceWidgetData withDate = await store.LoadAsync();
        Assert.True(withDate.ShowYear);

        withDate.ShowDate = false;
        await store.SaveAsync(withDate);

        GlanceWidgetData withoutDate = await store.LoadAsync();
        Assert.False(withoutDate.ShowDate);
        Assert.False(withoutDate.ShowYear);
    }

    [Fact]
    public async Task SaveAsync_PreservesPhotoOnlyModeAndNormalizesPaths()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            ShowTime = false,
            ShowDate = false,
            ShowWeekday = false,
            ShowCalendar = false,
            BackgroundSource = GlanceBackgroundSource.LocalFiles,
            OnlineImageCategory = GlanceOnlineImageCategory.Astronomy,
            LocalImagePaths = [" C:\\Pictures\\one.jpg ", "c:\\pictures\\ONE.jpg", ""],
            RotationIntervalMinutes = 17,
            TimeScale = 9,
            ShowPhotoControls = false
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.False(reloaded.ShowTime);
        Assert.False(reloaded.ShowDate);
        Assert.False(reloaded.ShowWeekday);
        Assert.False(reloaded.ShowCalendar);
        Assert.Single(reloaded.LocalImagePaths);
        Assert.Equal(GlanceOnlineImageCategory.Astronomy, reloaded.OnlineImageCategory);
        Assert.Equal(@"C:\Pictures\one.jpg", reloaded.LocalImagePaths[0]);
        Assert.Equal(30, reloaded.RotationIntervalMinutes);
        Assert.Equal(1.35, reloaded.TimeScale);
        Assert.False(reloaded.ShowPhotoControls);
    }

    [Fact]
    public async Task LoadAsync_ReturnsCopiesThatCannotMutateCachedState()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        GlanceWidgetData first = await store.LoadAsync();
        first.ShowTime = false;

        GlanceWidgetData second = await store.LoadAsync();

        Assert.True(second.ShowTime);
    }

    [Fact]
    public async Task SaveAsync_PreservesImageMaterialAndClampsTransparency()
    {
        var store = new GlanceWidgetStore(_tempRoot);
        await store.SaveAsync(new GlanceWidgetData
        {
            CalendarMaterialMode = GlanceCalendarMaterialMode.FollowImage,
            CalendarImageMaterialTransparency = 4,
            TraditionalCalendarMode = GlanceTraditionalCalendarMode.Hebrew
        });

        GlanceWidgetData reloaded = await new GlanceWidgetStore(_tempRoot).LoadAsync();

        Assert.Equal(GlanceCalendarMaterialMode.FollowImage, reloaded.CalendarMaterialMode);
        Assert.Equal(1, reloaded.CalendarImageMaterialTransparency);
        Assert.Equal(GlanceTraditionalCalendarMode.Hebrew, reloaded.TraditionalCalendarMode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}

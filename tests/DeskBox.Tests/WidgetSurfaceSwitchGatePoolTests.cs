using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSurfaceSwitchGatePoolTests
{
    [Fact]
    public void SameSurface_ReusesGateAcrossHostLifetimes()
    {
        var pool = new WidgetSurfaceSwitchGatePool();

        Assert.Same(pool.Get("surface"), pool.Get("surface"));
    }

    [Fact]
    public async Task DifferentSurfaces_CanEnterConcurrently()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        SemaphoreSlim first = pool.Get("surface-1");
        SemaphoreSlim second = pool.Get("surface-2");

        await first.WaitAsync();
        bool secondEntered = await second.WaitAsync(TimeSpan.FromMilliseconds(50));

        Assert.NotSame(first, second);
        Assert.True(secondEntered);
        first.Release();
        second.Release();
    }

    [Fact]
    public void RetiredSurface_RemovesItsStableGateWithoutDisposingActiveLease()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        SemaphoreSlim retired = pool.Get("retired-surface");
        retired.Wait();

        Assert.True(pool.Remove("retired-surface"));
        Assert.Equal(0, pool.Count);

        retired.Release();
        SemaphoreSlim replacement = pool.Get("retired-surface");
        Assert.NotSame(retired, replacement);
        Assert.Equal(1, pool.Count);
    }
}

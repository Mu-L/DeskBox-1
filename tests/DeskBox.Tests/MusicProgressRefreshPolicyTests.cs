using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class MusicProgressRefreshPolicyTests
{
    [Theory]
    [InlineData(false, 500)]
    [InlineData(true, 1000)]
    public void ResolveProgressRefreshIntervalMs_UsesLowerCadenceForCapsule(
        bool isCompactCollapsed,
        int expectedIntervalMs)
    {
        Assert.Equal(
            expectedIntervalMs,
            MusicWidgetViewModel.ResolveProgressRefreshIntervalMs(isCompactCollapsed));
    }
}

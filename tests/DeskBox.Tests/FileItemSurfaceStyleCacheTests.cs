using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class FileItemSurfaceStyleCacheTests
{
    [Theory]
    [InlineData(false, (byte)0)]
    [InlineData(true, (byte)255)]
    public void NeutralStateLayers_UseThemeAdaptiveMonochrome(
        bool isDark,
        byte expectedChannel)
    {
        Windows.UI.Color hover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: false);
        Windows.UI.Color selected =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Normal,
                isSelected: true);
        Windows.UI.Color selectedHover =
            FileItemSurfaceStyleCache.GetNeutralStateLayer(
                isDark,
                FileItemSurfaceVisualState.Hover,
                isSelected: true);

        Assert.All(
            new[] { hover, selected, selectedHover },
            color =>
            {
                Assert.Equal(expectedChannel, color.R);
                Assert.Equal(expectedChannel, color.G);
                Assert.Equal(expectedChannel, color.B);
            });
        Assert.True(selected.A > hover.A);
        Assert.True(selectedHover.A > selected.A);
    }
}

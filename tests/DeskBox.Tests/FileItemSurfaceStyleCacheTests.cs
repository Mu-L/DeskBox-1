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

    [Theory]
    [InlineData(false, (byte)0x0C, (byte)0x00)]
    [InlineData(true, (byte)0x16, (byte)0xFF)]
    public void StackChildSurfaceLayer_UsesLowContrastThemeAdaptiveTint(
        bool isDark,
        byte expectedAlpha,
        byte expectedChannel)
    {
        Windows.UI.Color layer =
            FileItemSurfaceStyleCache.GetStackChildSurfaceLayer(isDark);

        Assert.Equal(expectedAlpha, layer.A);
        Assert.Equal(expectedChannel, layer.R);
        Assert.Equal(expectedChannel, layer.G);
        Assert.Equal(expectedChannel, layer.B);
    }
}

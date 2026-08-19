using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SolidColorBackdropTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 128)]
    [InlineData(1.0, 255)]
    public void SolidSurfaceColor_MapsOpacityAcrossTheFullAlphaRange(
        double opacity,
        int expectedAlpha)
    {
        Windows.UI.Color color = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
            isDark: false,
            Windows.UI.Color.FromArgb(255, 0, 120, 215),
            opacity);

        Assert.Equal(expectedAlpha, color.A);
    }

    [Fact]
    public void WidgetWindow_UsesWinUIExTransparentTintBackdropForSolidMaterial()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string baseWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.cs"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/QuickCaptureWidgetWindow.Appearance.cs"));

        Assert.Contains("<PackageReference Include=\"WinUIEx\" Version=\"2.9.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("WinUIEx.TransparentTintBackdrop? _solidColorBackdrop", baseWindow, StringComparison.Ordinal);
        Assert.Contains("new WinUIEx.TransparentTintBackdrop(tintColor)", backdrop, StringComparison.Ordinal);
        Assert.Contains("SystemBackdrop = _solidColorBackdrop", backdrop, StringComparison.Ordinal);
        Assert.Contains("ClearSolidColorBackdrop();", baseWindow, StringComparison.Ordinal);
        Assert.Contains("!IsSolidColorBackdropActive", contentWindow, StringComparison.Ordinal);
        Assert.Contains("!IsSolidColorBackdropActive", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyTransparentAcrylicController", backdrop, StringComparison.Ordinal);
    }
}

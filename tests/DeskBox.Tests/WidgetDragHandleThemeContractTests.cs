namespace DeskBox.Tests;

public sealed class WidgetDragHandleThemeContractTests
{
    [Fact]
    public void FloatingDragHandles_UseOpaqueThemeAdaptiveBrush()
    {
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));
        string widgetShellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string widgetShellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string searchXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string searchCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Equal(2, CountOccurrences(appXaml, "x:Key=\"WidgetDragHandleBrush\""));
        Assert.Contains(
            "x:Key=\"WidgetDragHandleBrush\" Color=\"#6B6B6B\"",
            appXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"WidgetDragHandleBrush\" Color=\"#D6D6D6\"",
            appXaml,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(widgetShellXaml, "{ThemeResource WidgetDragHandleBrush}"));
        Assert.Contains("x:Name=\"OverlayDragGrip\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"1.5,0,0,1.5\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"0,1.5,1.5,0\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("Canvas.ZIndex=\"1\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("const double gripOpacity = 1", widgetShellCode, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource WidgetDragHandleBrush}\"",
            searchXaml,
            StringComparison.Ordinal);
        Assert.Contains("TopDragHandle.Opacity = 1;", searchCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TopDragHandle.Opacity = 0.72", searchCode, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}

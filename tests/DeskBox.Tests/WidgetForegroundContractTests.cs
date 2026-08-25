namespace DeskBox.Tests;

public sealed class WidgetForegroundContractTests
{
    [Fact]
    public void SettingsSurface_ExposesPaletteColorAndEdgeControls()
    {
        string xaml = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string bindable = Read(
            "src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs");

        Assert.Contains("AvailableWidgetForegroundModeOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedWidgetForegroundColor", xaml, StringComparison.Ordinal);
        Assert.Contains("AvailableWidgetTextEdgeModeOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("nameof(SelectedWidgetForegroundColor)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(SelectedWidgetTextEdgeMode)", bindable, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml")]
    [InlineData("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml")]
    public void WidgetRoots_ProvideLocalSemanticBrushesAndShadowHost(string path)
    {
        string xaml = Read(path);

        Assert.Contains("x:Key=\"TextFillColorPrimaryBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextFillColorSecondaryBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WidgetTextShadowHost\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_HighContrastWinsAndOffDisposesAllShadowResources()
    {
        string foreground = Read(
            "src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string shadow = Read(
            "src/DeskBox/Views/WidgetTextShadowManager.cs");

        Assert.Contains("highContrast", foreground, StringComparison.Ordinal);
        Assert.Contains("WidgetForegroundSettings.EdgeOff", foreground, StringComparison.Ordinal);
        Assert.Contains("DisposeWidgetTextShadowManager();", foreground, StringComparison.Ordinal);
        Assert.Contains("LayoutUpdated -= Root_LayoutUpdated", shadow, StringComparison.Ordinal);
        Assert.Contains("SetElementChildVisual(_host, null)", shadow, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleMarquee_IsSuspendedOnlyWhileDetachedTextEdgeVisualsAreActive()
    {
        string foreground = Read(
            "src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");

        Assert.Contains(
            "WidgetShellControl.SetCompactMarqueeTextEdgeModeActive(textEdgeActive)",
            foreground,
            StringComparison.Ordinal);
        Assert.Contains("_compactMarqueeTextEdgeModeActive ||", shell, StringComparison.Ordinal);
        Assert.Contains("StopCompactMarquee();", shell, StringComparison.Ordinal);
        Assert.Contains("QueueCompactMarquee(650);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void BothWidgetMenus_ExposePerWidgetForegroundOverrides()
    {
        Assert.Contains(
            "WidgetForegroundMenuBuilder.Create",
            Read("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetForegroundMenuBuilder.Create",
            Read("src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Controls/WidgetShell.xaml")]
    [InlineData("src/DeskBox/Controls/FileItemSurface.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/SearchWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml")]
    public void WidgetContentRoots_InheritTheWidgetPrimaryForeground(string path)
    {
        Assert.Contains(
            "Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"",
            Read(path),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml")]
    [InlineData("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml")]
    public void WidgetRoots_RedirectDefaultNativeTextStatesToLocalSemanticBrushes(
        string path)
    {
        string xaml = Read(path);

        Assert.Contains("x:Key=\"DefaultTextForegroundThemeBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextControlForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextControlPlaceholderForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GridViewItemForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ListViewItemForeground\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeGeneratedText_ResolvesTheHostingWidgetResourceScope()
    {
        string markdown = Read("src/DeskBox/Controls/MarkdownDocumentView.cs");
        string stackPopover = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        string quickCapture = Read(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Items.cs");
        string todo = Read(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs");

        Assert.Contains("_contentForeground = Foreground ??", markdown, StringComparison.Ordinal);
        Assert.Contains("element.Resources.TryGetValue(key", markdown, StringComparison.Ordinal);
        Assert.Contains("ApplyStackPopoverForegroundResources(content)", stackPopover, StringComparison.Ordinal);
        Assert.Contains("RootGrid.Resources.TryGetValue(resourceKey", quickCapture, StringComparison.Ordinal);
        Assert.Contains("element.Resources.TryGetValue(resourceKey", todo, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

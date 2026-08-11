namespace DeskBox.Tests;

public sealed class ItemHoverActionContractTests
{
    [Fact]
    public void QuickCapturePin_IsAHiddenHoverButtonWithClickHandling()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("PointerEntered=\"QuickCaptureItem_PointerEntered\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"QuickCaptureItem_PointerExited\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuickCapturePinItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PinItemButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SetQuickCaptureItemPinButtonVisible(sender as DependencyObject, true)", code, StringComparison.Ordinal);
        Assert.Contains("button.IsHitTestVisible = isVisible", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PinRecentItemAsync(item)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoImportant_IsHiddenUntilHoverAndUsesTheSmallIconSize()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"));

        Assert.Contains("x:Name=\"TodoImportantItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"28\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Path=DataContext.SmallIconSize", xaml, StringComparison.Ordinal);
        Assert.Contains("FindVisualChild<Button>(itemRoot, \"TodoImportantItemButton\")", code, StringComparison.Ordinal);
        Assert.Contains("importantButton.Opacity = isHovered ? 1 : 0", code, StringComparison.Ordinal);
        Assert.Contains("importantButton.IsHitTestVisible = isHovered", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "DeskBox")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

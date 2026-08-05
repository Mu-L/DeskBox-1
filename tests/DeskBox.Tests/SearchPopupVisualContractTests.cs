namespace DeskBox.Tests;

public sealed class SearchPopupVisualContractTests
{
    [Fact]
    public void FileAndSearchRows_UseCompactNativeAlignedSurfaces()
    {
        string root = FindRepositoryRoot();
        string fileSurface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string resultRow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/SearchResultRowControl.xaml"));
        string searchPopup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string searchInteractions = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("Tag=\"InteractiveSurface\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"4\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains(
            "Padding=\"4,5\" Margin=\"0,1\" CornerRadius=\"4\"",
            resultRow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Margin=\"12,2\" Padding=\"4,3\" CornerRadius=\"4\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"AllowFocusOnInteraction\" Value=\"False\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"UseSystemFocusVisuals\" Value=\"False\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"CornerRadius\" Value=\"2\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"InteractionSurface\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Storyboard.TargetName=\"InteractionSurface\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains("SortTypeDivider", searchPopup, StringComparison.Ordinal);
        Assert.Contains("SortSizeDivider", searchPopup, StringComparison.Ordinal);
        Assert.Contains("SortDateDivider", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("SortHeaderBackground", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"-12,0,0,0\"", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPointerOnRowInteractivePart", searchInteractions, StringComparison.Ordinal);
        Assert.Contains("OnRubberBandAutoScrollTick", searchInteractions, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}

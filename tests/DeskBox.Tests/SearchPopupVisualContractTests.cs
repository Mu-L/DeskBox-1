namespace DeskBox.Tests;

public sealed class SearchPopupVisualContractTests
{
    [Fact]
    public void BackgroundIndexRefresh_DoesNotReplayTheUserSearchEntrance()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("IndexRefreshDebounceDelay = TimeSpan.FromSeconds(1)", viewModel, StringComparison.Ordinal);
        Assert.Contains("SearchRefreshKind.IndexUpdate && !response.IsComplete", viewModel, StringComparison.Ordinal);
        Assert.Contains("HasSameIdentitySequence", viewModel, StringComparison.Ordinal);
        Assert.Contains("ReuseExistingInstances", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.IsApplyingBackgroundResultRefresh)", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultInteraction_SelectsOnClickAndOpensOnlyOnDoubleClickOrEnter()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("var item = ResolveResultItem(source);", popup, StringComparison.Ordinal);
        Assert.Contains("var item = ResolveResultItem(e.OriginalSource as DependencyObject);", popup, StringComparison.Ordinal);
        Assert.Contains("FindItemRow(element)?.Item ?? FindDataContext<SearchResultItem>(element)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceEquals(_pressedItem, releasedItem)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("[DIAG] ResultsPanel_DoubleTapped", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void InstantSearch_UsesShortDebounceWithoutBlockingLoaderAndPagesOnDemand()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));

        Assert.Contains("TimeSpan.FromMilliseconds(35)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(150)", popup, StringComparison.Ordinal);
        Assert.Contains("SearchProgressBar.Visibility = Visibility.Collapsed", popup, StringComparison.Ordinal);
        Assert.Contains("LoadingPanel.Visibility = Visibility.Collapsed", popup, StringComparison.Ordinal);
        Assert.Contains("ViewChanged=\"ResultsPanel_ViewChanged\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("LoadMoreResultsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("LoadMoreAndAdvanceSelectionAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSettings_ExposeOneIndexWithoutAVisibleResultLimit()
    {
        string root = FindRepositoryRoot();
        string settings = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string engine = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/SearchEngineService.cs"));

        Assert.Contains("SearchSystemNoiseToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchSystemIndexToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchCustomIndexerToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchRustPreviewToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchMaxResultsComboBox", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsIndexSearchService", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("UsnJournalIndexService", engine, StringComparison.Ordinal);
        Assert.Contains("SearchIndexQueryPage", engine, StringComparison.Ordinal);
    }

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
        Assert.Contains("x:Name=\"FooterAcrylicSurface\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.5\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", searchPopup, StringComparison.Ordinal);
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

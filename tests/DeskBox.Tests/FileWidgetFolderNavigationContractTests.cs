namespace DeskBox.Tests;

public sealed class FileWidgetFolderNavigationContractTests
{
    [Fact]
    public void FileSurface_UsesUnifiedPinnedNavigationOutsideItemData()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains(
            "x:Name=\"FolderNavigationBar\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding FolderNavigationVisibility}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FolderNavigationRootButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.24\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,4,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"7\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"*,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<PathIcon", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Data=\"M6 15.5c0 .28.22.5.5.5H11",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Symbol=\"GoToStart\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderOpenInExplorerButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness=\"0,0,0,1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IconFolderNavigationBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ListFolderNavigationBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding FolderNavigation",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"FolderNavigationLoadingOverlay\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_PreparesVerticalTransitionBeforeReplacingItems()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        string hydration = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Contains(
            "new Vector3(0, navigatingUp ? -16 : 16, 0)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("contentVisual.Opacity = 0", surface, StringComparison.Ordinal);
        Assert.Contains("beforeItemsReplaced?.Invoke()", hydration, StringComparison.Ordinal);
        Assert.True(
            hydration.IndexOf("beforeItemsReplaced?.Invoke()", StringComparison.Ordinal) <
            hydration.IndexOf("SyncFolderItems(items)", StringComparison.Ordinal));
        Assert.Contains(
            "TimeSpan.FromSeconds(1)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowDelayedFolderNavigationLoadingAsync",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "allowFolderPathTransition: true",
            File.ReadAllText(Path.Combine(
                root,
                "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs")),
            StringComparison.Ordinal);
        Assert.Contains("Task.Run(", hydration, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_DefersShortcutResolutionUntilAfterFirstFrame()
    {
        string root = FindRepositoryRoot();
        string fileService = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/FileService.cs"));
        string shortcutHelper = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Helpers/ShortcutHelper.cs"));
        string hydration = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Contains("loadShortcutTarget: false", fileService, StringComparison.Ordinal);
        Assert.Contains(
            "HydrateShortcutTargetsThenShellKindsAsync",
            hydration,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetStoredShortcutTargetAsync",
            hydration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanApplyHydrationResult(item, expectedPath, generation)",
            hydration,
            StringComparison.Ordinal);

        int fastReadStart = shortcutHelper.IndexOf(
            "private static ShortcutInfo? ReadStoredMetadataUncached",
            StringComparison.Ordinal);
        int fastReadEnd = shortcutHelper.IndexOf(
            "private static ShortcutInfo ReadShellLinkMetadata",
            fastReadStart,
            StringComparison.Ordinal);
        Assert.True(fastReadStart >= 0 && fastReadEnd > fastReadStart);
        Assert.DoesNotContain(
            "link.Resolve(",
            shortcutHelper[fastReadStart..fastReadEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_SeparatesMappedRootFromCurrentFolderOperations()
    {
        string root = FindRepositoryRoot();
        string navigation = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs"));
        string operations = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"));
        string watchers = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.SortingAndWatchers.cs"));

        Assert.Contains("CurrentFolderPath", navigation, StringComparison.Ordinal);
        Assert.Contains("MappedFolderPath", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "destinationFolderPath = CurrentFolderPath",
            operations,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsCurrentWatcherBatch(changeBatch, CurrentFolderPath)",
            watchers,
            StringComparison.Ordinal);
        Assert.Contains("!IsAtMappedRoot", watchers, StringComparison.Ordinal);
        Assert.Contains(
            "IsEmbeddedFolderNavigationEnabled && !IsAtMappedRoot",
            navigation,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DeskBox.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}

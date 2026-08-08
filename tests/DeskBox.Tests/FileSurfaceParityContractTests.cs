using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileSurfaceParityContractTests
{
    [Fact]
    public void UnifiedFileSurface_UsesTheSharedItemSurfaceContract()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace controls = "using:DeskBox.Controls";

        XElement[] surfaces = document
            .Descendants(controls + "FileItemSurface")
            .ToArray();

        Assert.Equal(2, surfaces.Length);
        Assert.Equal(["Icon", "List"], surfaces
            .Select(surface => (string?)surface.Attribute("Mode"))
            .ToArray());
        Assert.All(surfaces, surface =>
        {
            Assert.NotNull(surface.Attribute("LayoutContext"));
            Assert.Equal("True", (string?)surface.Attribute("UseStackChildIndent"));
            Assert.Equal("True", (string?)surface.Attribute("AllowDrop"));
            Assert.Equal("ItemSurface_DragOver", (string?)surface.Attribute("DragOver"));
            Assert.Equal("ItemSurface_DragLeave", (string?)surface.Attribute("DragLeave"));
            Assert.Equal("ItemSurface_Drop", (string?)surface.Attribute("Drop"));
        });
    }

    [Fact]
    public void UnifiedFileSurface_FolderDropOverridesReorderAndUsesFileTransfer()
    {
        string root = FindRepositoryRoot();
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));

        Assert.Contains("PersistSurfaceReorder();", visuals, StringComparison.Ordinal);
        Assert.Contains("ResolveFolderDropOperation", visuals, StringComparison.Ordinal);
        Assert.Contains("TransferItemsWithResultAsync", visuals, StringComparison.Ordinal);
        Assert.Contains("Widget.CannotMoveToFolder", visuals, StringComparison.Ordinal);
        Assert.Contains("FileItemSurfaceVisualState.DropTarget", visuals, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedFileSurface_ReorderIndicatorUsesSoftDirectionalGlow()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XElement indicator = document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
                "ReorderInsertionIndicator");

        XElement glow = indicator
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
                "ReorderInsertionGlow");
        XElement gradient = glow.Descendants().Single(element =>
            element.Name.LocalName == "LinearGradientBrush");
        XElement[] stops = gradient.Descendants()
            .Where(element => element.Name.LocalName == "GradientStop")
            .ToArray();

        Assert.True(stops.Length >= 5);
        Assert.Equal("Transparent", (string?)stops.First().Attribute("Color"));
        Assert.Equal("Transparent", (string?)stops.Last().Attribute("Color"));
        Assert.Contains(stops, stop =>
            (string?)stop.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "ReorderInsertionAccentStop");
    }

    [Fact]
    public void UnifiedFileSurface_UsesNonBlockingBottomTransferProgress()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement card = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ImportProgressCard");
        Assert.Equal("Bottom", (string?)card.Attribute("VerticalAlignment"));
        Assert.Equal("Collapsed", (string?)card.Attribute("Visibility"));
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportProgressBar");
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportCancelButton" &&
            (string?)element.Attribute("Click") == "ImportCancelButton_Click");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportOverlay");
    }

    [Fact]
    public void SharedItemSurface_OwnsDetailAndPathPresentation()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));

        Assert.Contains("ListItemDetailVisibility", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowFileItemPathTooltips", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "DataContextChanged += FileItemSurface_DataContextChanged",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VisualStateChanged?.Invoke",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedFileSurface_RealizesItemBeforeInlineRename()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.Contains(
            "FindOrRealizeItemRenameTargetAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains("FindDisplayedItem(item)", source, StringComparison.Ordinal);
        Assert.Contains(
            "RevealItemForInteraction(item.Path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollIntoView(displayedItem)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DispatcherQueuePriority.Low",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedShortcutDrag_UsesMoveOnlyAndFinalizesVirtualCopy()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        XElement[] itemViews = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName is "GridView" or "ListView" &&
                (string?)element.Attribute("CanDragItems") == "True")
            .ToArray();

        Assert.Equal(2, itemViews.Length);
        Assert.All(itemViews, view => Assert.Equal(
            "Items_DragStarting",
            (string?)view.Attribute("DragStarting")));
        Assert.Contains(
            "e.AllowedOperations = DataPackageOperation.Move",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteVirtualShortcutDesktopMoveAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindMaterializedVirtualShortcutSourcesAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileBackgroundMenu_ReceivesSharedHostActions()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));
        string host = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Contains("HostContextMenuOpening?.Invoke", surface, StringComparison.Ordinal);
        Assert.Contains("WidgetChromeMenuBuilder.Create", host, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(closeWidget)",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowCloseWidgetFlyout(ContentWidgetShell)",
            host,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileBackgroundMenu_UsesTheRequestedActionOrder()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));

        string[] markers =
        [
            "CreateMenuItem(\"Common.Refresh\"",
            "CreateMenuItem(\"Common.Paste\"",
            "CreateMenuItem(\"Common.NewFolder\"",
            "\"Widget.OpenStorageFolder\"",
            "flyout.Items.Add(hostItems.TitleStyleItem)",
            "var viewAndSort = new MenuFlyoutSubItem",
            "flyout.Items.Add(CreateStackSettingsMenu())",
            "flyout.Items.Add(new MenuFlyoutSeparator())",
            "flyout.Items.Add(hostItems.CloseWidgetItem)"
        ];

        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(
                marker,
                StringComparison.Ordinal);
            Assert.True(
                currentIndex > previousIndex,
                $"Menu marker is missing or out of order: {marker}");
            previousIndex = currentIndex;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}

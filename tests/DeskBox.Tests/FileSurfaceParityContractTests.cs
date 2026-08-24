using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileSurfaceParityContractTests
{
    [Fact]
    public void UnifiedFileSurface_AutoHidesScrollBarsAfterInactivity()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        string behavior = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ScrollBars.cs"));

        XElement[] itemViews = document
            .Descendants()
            .Where(element => element.Name.LocalName is "GridView" or "ListView")
            .Where(element =>
                (string?)element.Attribute(XName.Get(
                    "Name",
                    "http://schemas.microsoft.com/winfx/2006/xaml"))
                is "ItemsGrid" or "ItemsList")
            .ToArray();

        Assert.Equal(2, itemViews.Length);
        Assert.All(itemViews, view => Assert.Equal(
            "Hidden",
            (string?)view.Attribute(
                "ScrollViewer.VerticalScrollBarVisibility")));
        Assert.Contains("TimeSpan.FromSeconds(3)", behavior, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerMovedEvent", behavior, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerWheelChangedEvent", behavior, StringComparison.Ordinal);
        Assert.Contains("ScrollBarVisibility.Auto", behavior, StringComparison.Ordinal);
        Assert.Contains("ScrollBarVisibility.Hidden", behavior, StringComparison.Ordinal);
    }

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
        Assert.Equal(
            "Root",
            (string?)card.Parent?.Attribute(x + "Name"));
        Assert.Equal("Bottom", (string?)card.Attribute("VerticalAlignment"));
        Assert.Equal("Collapsed", (string?)card.Attribute("Visibility"));
        Assert.Equal("1000", (string?)card.Attribute("Canvas.ZIndex"));
        Assert.Equal(
            "{ThemeResource SystemControlAcrylicElementBrush}",
            (string?)card.Attribute("Background"));
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportProgressBar");
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportCancelButton" &&
            (string?)element.Attribute("Click") == "ImportCancelButton_Click");
        Assert.Contains(card.Descendants(), element =>
            (string?)element.Attribute(x + "Name") ==
            "ImportCancelProgressRing");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "ImportOverlay");
    }

    [Fact]
    public void ImportCancellation_AcknowledgesImmediatelyAndIgnoresStaleProgress()
    {
        string root = FindRepositoryRoot();
        string progressUi = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ImportProgress.cs"));
        string fileService = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/FileService.cs"));

        Assert.Contains("_isImportCancellationPending", progressUi, StringComparison.Ordinal);
        Assert.Contains("ShowImportCancelingState();", progressUi, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(cancellation.Cancel);", progressUi, StringComparison.Ordinal);
        Assert.Contains(
            "() => ExecuteManagedTransferPlanWithProgressAsync(",
            fileService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessCrossVolumeMoves_UseManagedChunkedTransfer()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Services/FileService.cs"));

        int crossVolumeGuard = source.IndexOf(
            "operations.Any(operation => !CanUseAtomicMove(",
            StringComparison.Ordinal);
        Assert.True(crossVolumeGuard >= 0);
        int managedTransfer = source.IndexOf(
            "() => ExecuteManagedTransferPlanWithProgressAsync(",
            crossVolumeGuard,
            StringComparison.Ordinal);
        int legacyLoop = source.IndexOf(
            "var completedOperations = new List<TransferOperation>",
            crossVolumeGuard,
            StringComparison.Ordinal);

        Assert.True(managedTransfer > crossVolumeGuard);
        Assert.True(legacyLoop > managedTransfer);
    }

    [Fact]
    public void ExternalDrop_ShowsPreparationBeforeResolvingStorageItems()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        string itemVisuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));

        AssertMethodOrdersPreparationBeforePayloadRead(surface, "Root_Drop");
        AssertMethodOrdersPreparationBeforePayloadRead(itemVisuals, "ItemSurface_Drop");
        Assert.Contains(
            "EnsureTrackedImportStarted();",
            surface,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PasteFromClipboardAsync")]
    [InlineData("PickAndImportFilesAsync")]
    public void NonDragImportEntries_UseTrackedCancelableProgress(
        string methodName)
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        string entry = ReadPrivateMethod(
            source,
            "private async Task " + methodName);
        string progressOwner;
        if (methodName == "PasteFromClipboardAsync")
        {
            Assert.Contains(
                "PasteDataPackageAsync(",
                entry,
                StringComparison.Ordinal);
            progressOwner = ReadPrivateMethod(
                source,
                "private async Task PasteDataPackageAsync");
        }
        else
        {
            Assert.Contains(
                "PickAndImportFilesAsync(suggestedFolder: null)",
                entry,
                StringComparison.Ordinal);
            progressOwner = ReadPrivateMethod(
                source,
                "private async Task<IReadOnlyList<string>> PickAndImportFilesAsync");
        }

        Assert.Contains(
            "ImportPathsWithTrackedProgressAsync(",
            progressOwner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.ImportPathsAsync(",
            progressOwner,
            StringComparison.Ordinal);
    }

    private static void AssertMethodOrdersPreparationBeforePayloadRead(
        string source,
        string methodName)
    {
        int method = source.IndexOf(methodName, StringComparison.Ordinal);
        int begin = source.IndexOf(
            "BeginTrackedImport();",
            method,
            StringComparison.Ordinal);
        int read = source.IndexOf(
            "GetSurfaceDropFilesAsync(e.DataView)",
            method,
            StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(begin > method);
        Assert.True(read > begin);
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
    public void FileStacks_UseInlineRenameAndStableProjectionTransitions()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        string itemVisuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        string menus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));
        string stackViewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Stacks.cs"));
        string stackAnimations = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackAnimations.cs"));

        Assert.Contains("FindOrRealizeStackRenameTargetAsync", source, StringComparison.Ordinal);
        Assert.Contains("StartItemRenameAsync(stack)", source, StringComparison.Ordinal);
        Assert.Contains("SetStackNameOverride(stack.StackKey, newName)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ContentDialog", menus, StringComparison.Ordinal);

        Assert.DoesNotContain("AddDeleteThemeTransition", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split(
            "RepositionThemeTransition IsStaggeringEnabled=\"False\"",
            StringSplitOptions.None).Length - 1);
        Assert.Equal(0, xaml.Split(
            "EntranceThemeTransition FromVerticalOffset=\"4\" IsStaggeringEnabled=\"False\"",
            StringSplitOptions.None).Length - 1);

        Assert.Contains("ResetSelectionForStackProjectionChange", menus, StringComparison.Ordinal);
        Assert.Contains("ItemsGrid.SelectedItems.Clear()", menus, StringComparison.Ordinal);
        Assert.Contains("ItemsList.SelectedItems.Clear()", menus, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureExclusiveItemSelection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPointerSelection(", itemVisuals, StringComparison.Ordinal);
        Assert.DoesNotContain("item.IsSelected =", itemVisuals, StringComparison.Ordinal);
        Assert.DoesNotContain("item.IsSelected =", menus, StringComparison.Ordinal);
        Assert.Contains(
            "GetActiveItemsView().SelectedItems.Contains(item)",
            itemVisuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindDescendantByTag(container, \"InteractiveSurface\")",
            itemVisuals,
            StringComparison.Ordinal);
        Assert.Contains("selectedStacks", source, StringComparison.Ordinal);
        Assert.Contains("listView.SelectedItems.Remove(stack)", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", menus, StringComparison.Ordinal);
        Assert.Contains(
            "RequestStackState(\n            stack,\n            !GetDesiredStackState(stack))",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "public void PrepareForReuse()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.StabilizeStackDisplay()",
            source,
            StringComparison.Ordinal);
        Assert.Contains("CanCreateManualStack: true", menus, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (!FileStacksEnabled)\n        {\n            WidgetFileStackSettings.SetEnabledOverride(Config, true);",
            stackViewModel.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool UsesStackProjection",
            stackViewModel,
            StringComparison.Ordinal);
        Assert.Contains("ConvertStackToManual(", stackViewModel, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsCompatibilityService.AreAnimationsEnabled",
            stackAnimations,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackTransitionGeneration",
            stackAnimations,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StartStackMemberEntranceAnimations",
            stackAnimations,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "YieldForStackLayoutAsync",
            stackAnimations,
            StringComparison.Ordinal);
        Assert.Contains(
            "StartStackMemberExitAnimations",
            stackAnimations,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExternalFileDragEnded",
            itemVisuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryMoveStackMemberOverride(",
            stackViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PersistStackCustomizations()",
            stackViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "groupBy,\n            StringComparison.Ordinal),\n            IsEnabled = ViewModel.FileStacksEnabled",
            menus.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "App.Current.ShowSettings(\"FileStackSettings\")",
            menus,
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

    [Fact]
    public void FileSurfaceDragHotPath_CachesPayloadAndSkipsTinyReorderMoves()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.Contains("_dragPayloadSnapshot", source, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(cached.DataView, dataView)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_dragDirectoryCache", source, StringComparison.Ordinal);
        Assert.Contains("ResetDragPayloadCache();", source, StringComparison.Ordinal);
        Assert.Contains(
            "Math.Abs(position.X - _surfaceReorderLastPosition.X) < 0.5",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_surfaceReorderDraggedItem",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DragAcrossFileSurfaces_KeepsTargetVisualsIdempotent()
    {
        string root = FindRepositoryRoot();
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string collapse = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        int dragOverStart = visuals.IndexOf(
            "private void StackSurface_DragOver",
            StringComparison.Ordinal);
        int dropStart = visuals.IndexOf(
            "private async void StackSurface_Drop",
            dragOverStart,
            StringComparison.Ordinal);
        Assert.True(dragOverStart >= 0);
        Assert.True(dropStart > dragOverStart);
        string dragOver = visuals[dragOverStart..dropStart];
        Assert.DoesNotContain(
            "ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.DropTarget)",
            dragOver,
            StringComparison.Ordinal);
        Assert.Contains("_stackMemberDropVisualActive", visuals, StringComparison.Ordinal);
        Assert.Contains("IsPointerInsideDropElement(border, e)", visuals, StringComparison.Ordinal);
        Assert.Contains("_folderDropVisualActive", visuals, StringComparison.Ordinal);
        int folderTargetStart = visuals.IndexOf(
            "private void SetFolderDropTarget(Border border)",
            StringComparison.Ordinal);
        int folderTargetEnd = visuals.IndexOf(
            "private void ClearFolderDropTarget()",
            folderTargetStart,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClearStackMemberDropTarget();",
            visuals[folderTargetStart..folderTargetEnd],
            StringComparison.Ordinal);
        int stackTargetStart = visuals.IndexOf(
            "private void SetStackMemberDropTarget(",
            StringComparison.Ordinal);
        int stackTargetEnd = visuals.IndexOf(
            "private void ClearStackMemberDropTarget()",
            stackTargetStart,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClearFolderDropTarget();",
            visuals[stackTargetStart..stackTargetEnd],
            StringComparison.Ordinal);

        int rootDragOverStart = surface.IndexOf(
            "private void Root_DragOver(",
            StringComparison.Ordinal);
        int rootDragOverEnd = surface.IndexOf(
            "private bool IsUnsafeFolderDrop(",
            rootDragOverStart,
            StringComparison.Ordinal);
        string rootDragOver = surface[rootDragOverStart..rootDragOverEnd];
        Assert.Contains("ClearFolderDropTarget();", rootDragOver, StringComparison.Ordinal);
        Assert.Contains("ClearStackMemberDropTarget();", rootDragOver, StringComparison.Ordinal);
        Assert.Contains("ClearDragSessionVisualState();", shell, StringComparison.Ordinal);

        Assert.Contains("_isShellDragActive", shell, StringComparison.Ordinal);
        Assert.Contains("IsPointerInsideShell(e)", shell, StringComparison.Ordinal);
        int compactDragEnteredStart = collapse.IndexOf(
            "private void WidgetShellControl_CompactDragEntered(",
            StringComparison.Ordinal);
        int compactDragEnteredEnd = collapse.IndexOf(
            "private void ReconcileCompactDragStateAfterPointerRelease()",
            compactDragEnteredStart,
            StringComparison.Ordinal);
        string compactDragEntered = collapse[
            compactDragEnteredStart..compactDragEnteredEnd];
        Assert.Contains(
            "bool animateDragExpansion = Config.WidgetKind == WidgetKind.File;",
            compactDragEntered,
            StringComparison.Ordinal);
        Assert.Contains(
            "animate: animateDragExpansion",
            compactDragEntered,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "durationMs: 0",
            compactDragEntered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LargeSurfaceDrop_ReleasesShellDragBeforeLongTransfer()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        int dropStart = surface.IndexOf(
            "private async void Root_Drop(",
            StringComparison.Ordinal);
        int dropEnd = surface.IndexOf(
            "private void SetImportBusy(",
            dropStart,
            StringComparison.Ordinal);
        Assert.True(dropStart >= 0);
        Assert.True(dropEnd > dropStart);
        string drop = surface[dropStart..dropEnd];
        int materialize = drop.IndexOf(
            "GetSurfaceDropFilesAsync(e.DataView)",
            StringComparison.Ordinal);
        int release = drop.IndexOf(
            "deferral.Complete();",
            materialize,
            StringComparison.Ordinal);
        int transfer = drop.IndexOf(
            "ImportDroppedFilesAsync(",
            materialize,
            StringComparison.Ordinal);

        Assert.True(materialize >= 0);
        Assert.True(release > materialize);
        Assert.True(transfer > release);
        Assert.Contains("deferral = null;", drop, StringComparison.Ordinal);
        Assert.Contains("deferral?.Complete();", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderDropHighlight_ObservesHandledChildDragBoundaries()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        int initialize = surface.IndexOf(
            "InitializeComponent();",
            StringComparison.Ordinal);
        int constructorEnd = surface.IndexOf(
            "Root.DataContext = ViewModel;",
            initialize,
            StringComparison.Ordinal);
        Assert.True(initialize >= 0);
        Assert.True(constructorEnd > initialize);
        string constructorWiring = surface[initialize..constructorEnd];
        Assert.Contains("UIElement.DragOverEvent", constructorWiring, StringComparison.Ordinal);
        Assert.Contains("Root_ObserveHandledDragOver", constructorWiring, StringComparison.Ordinal);
        Assert.Contains("UIElement.DragLeaveEvent", constructorWiring, StringComparison.Ordinal);
        Assert.Contains("Root_ObserveHandledDragLeave", constructorWiring, StringComparison.Ordinal);

        Assert.Contains(
            "private void ClearStaleChildDropTargets(DragEventArgs e)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsPointerInsideDropElement(folderTarget, e)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsPointerInsideDropElement(stackTarget, e)",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileDropSession_ClearsChildCachesAndDisablesWindowHighlight()
    {
        string root = FindRepositoryRoot();
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        string native = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string shellXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml"));

        Assert.Contains("ResetDragPayloadCache();", visuals, StringComparison.Ordinal);
        Assert.Contains("_groupFileDropFormatCached", native, StringComparison.Ordinal);
        Assert.Equal(1, native.Split(
            "RequiresGroupManualDropFallback(dataView)",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "_groupFileDropFormatCached = false;",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDropHighlight", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDropHighlight", shellXaml, StringComparison.Ordinal);
    }

    private static string ReadPrivateMethod(string source, string marker)
    {
        int methodStart = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Missing method marker: {marker}");
        int nextMethod = source.IndexOf(
            "\n    private ",
            methodStart + marker.Length,
            StringComparison.Ordinal);
        return source[methodStart..(nextMethod < 0
            ? source.Length
            : nextMethod)];
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

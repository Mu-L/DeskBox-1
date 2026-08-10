using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoWorkspaceInteractionContractTests
{
    [Fact]
    public void TodoSurface_OwnsContentRightClickAndProvidesRegionMenus()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));
        string contextMenus = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.ContextMenus.cs"));

        Assert.Contains("RightTapped += Surface_RightTapped", surface, StringComparison.Ordinal);
        Assert.Contains("BuildDateContextMenu", contextMenus, StringComparison.Ordinal);
        Assert.Contains("BuildDetailContextMenu", contextMenus, StringComparison.Ordinal);
        Assert.Contains("BuildNavigationBackgroundContextMenu", contextMenus, StringComparison.Ordinal);
        Assert.Contains("BuildQuickAddContextMenu", contextMenus, StringComparison.Ordinal);
        Assert.Contains("ShowExternalEventContextMenu", contextMenus, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", contextMenus, StringComparison.Ordinal);
    }

    [Fact]
    public void MonthSelection_IsImmediateIncrementalAndDebounced()
    {
        string calendar = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Calendar.cs"));
        int start = calendar.IndexOf("private void SelectCalendarDate", StringComparison.Ordinal);
        int end = calendar.IndexOf("private bool TryUpdateMonthSelection", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = calendar[start..end];

        Assert.Contains("SaveDebounced", method, StringComparison.Ordinal);
        Assert.Contains("TryUpdateMonthSelection", method, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RefinedVisuals_UseOneMonthSurfaceAndFlatDetailSections()
    {
        string root = FindRepositoryRoot();
        string calendar = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Calendar.cs"));
        string detail = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Detail.cs"));

        Assert.Contains("var monthSurface = new Border", calendar, StringComparison.Ordinal);
        Assert.Contains("BorderThickness = new Thickness(0)", calendar, StringComparison.Ordinal);
        Assert.Contains("BorderThickness = new Thickness(0, 0, 0, 1)", detail, StringComparison.Ordinal);
        Assert.Contains("Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitPane_UsesToolkitGridSplitterWithDedicatedHitAreaAndPersistence()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "src", "DeskBox", "DeskBox.csproj"));
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("private readonly GridSplitter _splitter", surface, StringComparison.Ordinal);
        Assert.Contains("SplitterColumnWidth = 12", surface, StringComparison.Ordinal);
        Assert.Contains("GridResizeBehavior.PreviousAndNext", surface, StringComparison.Ordinal);
        Assert.Contains("KeyboardIncrement = 8", surface, StringComparison.Ordinal);
        Assert.Contains("ManipulationCompleted += Splitter_ManipulationCompleted", surface, StringComparison.Ordinal);
        Assert.Contains("KeyUp += Splitter_KeyUp", surface, StringComparison.Ordinal);
        Assert.Contains("DoubleTapped += Splitter_DoubleTapped", surface, StringComparison.Ordinal);
        Assert.Contains("CaptureSplitterRatio", surface, StringComparison.Ordinal);
        Assert.Contains("SaveDebounced", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("_splitterThumb", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorMarkers_AreVisibleFilterableAndDirectDropTargets()
    {
        string root = FindRepositoryRoot();
        string colors = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.ColorMarkers.cs"));
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));
        string contextMenus = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.ContextMenus.cs"));

        Assert.Contains("BuildColorMarkerBar", surface, StringComparison.Ordinal);
        Assert.Contains("SetTodoColorMarker", colors, StringComparison.Ordinal);
        Assert.Contains("TryGetTodoColorMarkerAsync", surface, StringComparison.Ordinal);
        Assert.Contains("ApplyTaskColorMarkerAsync", colors, StringComparison.Ordinal);
        Assert.Contains("_activeColorMarkerFilter", colors, StringComparison.Ordinal);
        Assert.Contains("CreateTaskColorMarkerMenu", contextMenus, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactDetail_WrapsMetadataAndUsesInlineMarkdownPreview()
    {
        string root = FindRepositoryRoot();
        string detail = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Detail.cs"));
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceVisualModels.cs"));
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));

        Assert.Contains("TodoWorkspaceWrapPanel", detail, StringComparison.Ordinal);
        Assert.Contains("class TodoWorkspaceWrapPanel", visuals, StringComparison.Ordinal);
        Assert.Contains("previewHost.Visibility", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("previewFlyout", detail, StringComparison.Ordinal);
        Assert.Contains("_quickAddPanel.Visibility = fullPageDetail", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskRows_UseNativeCompletionAndAcceptColorMarkerDropsDirectly()
    {
        string visuals = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceVisualModels.cs"));

        Assert.Contains("private readonly CheckBox _completionCheckBox", visuals, StringComparison.Ordinal);
        Assert.Contains("Drop += TaskCard_Drop", visuals, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDragData.TodoColorMarkerFormat", visuals, StringComparison.Ordinal);
        Assert.Contains("await row.SetColorMarkerAsync(colorMarker)", visuals, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly Button _completionButton", visuals, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailAndCalendar_PreferDirectManipulationOverExtraChrome()
    {
        string root = FindRepositoryRoot();
        string detail = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Detail.cs"));
        string calendar = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Calendar.cs"));

        Assert.Contains("var complete = new CheckBox", detail, StringComparison.Ordinal);
        Assert.Contains("UIElement.DoubleTappedEvent", detail, StringComparison.Ordinal);
        Assert.Contains("BeginEdit();", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("previewButton", detail, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden", calendar, StringComparison.Ordinal);
        Assert.Contains("PointerWheelChanged += HorizontalTaskPool_PointerWheelChanged", calendar, StringComparison.Ordinal);
    }

    [Fact]
    public void MonthSelection_KeepsTodayVisibleWithoutAClippedCellBorder()
    {
        string calendar = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Calendar.cs"));
        int start = calendar.IndexOf("private void ApplyMonthCellSelectionVisual", StringComparison.Ordinal);
        int end = calendar.IndexOf("private void MonthCell_PointerEntered", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = calendar[start..end];

        Assert.Contains("cell.BorderThickness = new Thickness(0)", method, StringComparison.Ordinal);
        Assert.Contains("AccentFillColorDefaultBrush", method, StringComparison.Ordinal);
        Assert.Contains("label.Opacity = today || selected ? 1 : inVisibleMonth ? 0.68 : 0.36", method, StringComparison.Ordinal);
        Assert.Contains("indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed", method, StringComparison.Ordinal);
        Assert.DoesNotContain("new Thickness(1.5)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void MonthView_ReflowsOnMainPaneSizeAndNeverReliesOnClippedTaskBars()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));
        string calendar = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.Calendar.cs"));
        string visuals = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceVisualModels.cs"));

        Assert.Contains("_mainHost.SizeChanged += MainHost_SizeChanged", surface, StringComparison.Ordinal);
        Assert.Contains("ResolveMonthTaskLineCapacity", calendar, StringComparison.Ordinal);
        Assert.Contains("BuildMonthOverflowBar", calendar, StringComparison.Ordinal);
        Assert.Contains("MinHeight = 0", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualWidth < 560", calendar, StringComparison.Ordinal);
        Assert.Contains("internal const double ColorMarkerWidth = 4", visuals, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoToolbarAndContentMenus_DoNotExposeFilterSearchChrome()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.cs"));
        string contextMenus = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DeskBox",
            "Controls",
            "WidgetContents",
            "TodoWorkspaceSurface.ContextMenus.cs"));

        Assert.DoesNotContain("ConfigureIconButton(_filterButton", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowFilterFlyout(_settingsButton)", surface, StringComparison.Ordinal);
        Assert.Contains("AppendViewAndFilterCommands", contextMenus, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationSettings_MigrateOnlyLegacyMonthSplitDefault()
    {
        TodoWidgetPresentationSettings legacy = TodoPresentationSettingsStore.Normalize(new()
        {
            CalendarSplitRatio = 0.64
        });
        TodoWidgetPresentationSettings customized = TodoPresentationSettingsStore.Normalize(new()
        {
            CalendarSplitRatio = 0.70
        });

        Assert.Equal(0.58, legacy.CalendarSplitRatio, precision: 2);
        Assert.Equal(0.70, customized.CalendarSplitRatio, precision: 2);
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

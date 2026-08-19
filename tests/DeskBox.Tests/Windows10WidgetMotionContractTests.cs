namespace DeskBox.Tests;

public sealed class Windows10WidgetMotionContractTests
{
    [Fact]
    public void InteractiveResize_CoalescesWin10BurstsWithoutTimerOrDuplicatePressHandler()
    {
        string baseWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string resizeGuides = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/ResizeGuideOverlayService.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));

        Assert.Contains("if (WindowsCompatibilityService.IsWindows11OrLater)", baseWindow, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.High", baseWindow, StringComparison.Ordinal);
        Assert.Contains("_interactiveResizeCommitQueued", baseWindow, StringComparison.Ordinal);
        Assert.Contains("TotalMilliseconds >= 8", baseWindow, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingInteractiveResizeBounds();", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows10InteractiveResize", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_windows10InteractiveResizeTimer", baseWindow, StringComparison.Ordinal);
        Assert.Contains("!WindowsCompatibilityService.IsWindows11OrLater", bounds, StringComparison.Ordinal);
        Assert.Contains("SWP_NOCOPYBITS | Win32Helper.SWP_DEFERERASE", bounds, StringComparison.Ordinal);
        Assert.Contains("_resizeWorkAreaBounds", resizeGuides, StringComparison.Ordinal);
        Assert.Contains("static glow", resizeGuides, StringComparison.Ordinal);
        Assert.Contains("if (WindowsCompatibilityService.IsWindows11OrLater)", resizeGuides, StringComparison.Ordinal);
        Assert.DoesNotContain("child.PointerPressed += ResizeBorder_PointerPressed", contentWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactTransition_RetainsAnimatedBoundsAndSharedCapacity()
    {
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));

        Assert.DoesNotContain("_collapseAnimationUsesVisualOnlyBounds", collapse, StringComparison.Ordinal);
        Assert.Contains("MoveWindowWithoutPersisting(bounds, suppressRedraw: true);", collapse, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactAnimationCoordinator.TryQueueBoundsMove", collapse, StringComparison.Ordinal);
        Assert.Contains("internal const int MaximumConcurrentBoundsTransitions = 4", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.BeginDeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EndDeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("preposition", collapse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Win10CompactVisuals_UseCompositionWithoutReplacingRealBoundsMotion()
    {
        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains("StartCompactOpacityAnimation", shell, StringComparison.Ordinal);
        Assert.Contains("!WindowsCompatibilityService.IsWindows11OrLater", shell, StringComparison.Ordinal);
        Assert.Contains("ScalarKeyFrameAnimation", shell, StringComparison.Ordinal);
        Assert.Contains("MoveWindowWithoutPersisting(bounds, suppressRedraw: true);", collapse, StringComparison.Ordinal);
    }

    [Fact]
    public void Win10AnimationClocks_UseHighResolutionDispatcherTicksAndRealHwndBatches()
    {
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));
        string trayDriver = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetTrayBatchAnimationDriver.cs"));
        string clockBoost = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/CompositorClockBoostCoordinator.cs"));

        Assert.Contains("DispatcherQueueTimer", coordinator, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(15)", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueueTimer", trayDriver, StringComparison.Ordinal);
        Assert.Contains("MoveEntriesFrameCore", trayDriver, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", trayDriver, StringComparison.Ordinal);
        Assert.Contains("TrySetHighResolutionTimer", clockBoost, StringComparison.Ordinal);
    }
}

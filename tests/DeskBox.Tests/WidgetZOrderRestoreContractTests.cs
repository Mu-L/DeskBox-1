namespace DeskBox.Tests;

public sealed class WidgetZOrderRestoreContractTests
{
    [Fact]
    public void IdleNormalization_OnlyReordersWidgetPeers()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            source,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");

        Assert.Contains("ApplyPeerOrderHighestToLowest", method, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowToBottom", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisedSessionRestore_ReanchorsTheCompleteGroupBehindForeground()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string restore = SliceMethod(
            manager,
            "private void RestoreRaisedWidgetsToDesktopLayer(bool force)",
            "public bool SetWidgetPositionLocked");
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));

        Assert.Contains("RestoreGroupPreservingForeground", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeIdleWidgetZOrder(\"raised-session-restored\")", restore, StringComparison.Ordinal);
        Assert.Contains("case RelativeLayerRestoreDisposition.BehindForeground:", layerService, StringComparison.Ordinal);
        Assert.Contains("ApplyWindowOrderHighestToLowest", layerService, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedPointerActivation_IsBlockedBeforeDefaultWindowActivation()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string setup = SliceMethod(
            bounds,
            "private void InstallDesktopPinnedActivationGuard",
            "private void RemoveDesktopPinnedActivationGuard");
        string callback = SliceMethod(
            bounds,
            "private IntPtr DesktopPinnedActivationSubclassProc",
            "// ── Bounds management");

        Assert.Contains("SetWindowSubclass", setup, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.WM_MOUSEACTIVATE", callback, StringComparison.Ordinal);
        Assert.Contains("WidgetLayerService.ShouldSuppressPointerActivation", callback, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.MA_NOACTIVATE", callback, StringComparison.Ordinal);
        Assert.Contains("WidgetLayerService.MoveToDesktopBottom", callback, StringComparison.Ordinal);
        Assert.DoesNotContain("HoldTemporaryTopMost", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedBlankAreaClick_UsesNoActivateRestingStyleAndRoutedGuard()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string routedHandler = SliceMethod(
            bounds,
            "private void RootElement_PointerPressedForDesktopPinnedLayer",
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer");

        Assert.Contains("WS_EX_NOACTIVATE", layerService, StringComparison.Ordinal);
        Assert.Contains("ApplyDesktopPinnedActivationStyle(windowHandle)", layerService, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerPressedEvent", bounds, StringComparison.Ordinal);
        Assert.Contains("TryAllowDesktopPinnedPointerActivation", routedHandler, StringComparison.Ordinal);
        Assert.Contains("MoveToDesktopBottom", routedHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Handled = true", routedHandler, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}

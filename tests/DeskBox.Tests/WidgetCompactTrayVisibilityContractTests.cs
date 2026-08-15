namespace DeskBox.Tests;

public sealed class WidgetCompactTrayVisibilityContractTests
{
    [Fact]
    public void TrayHide_CollapsesOnlyTransientSmartExpansionToStableCapsuleBounds()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "protected void PrepareCompactHostForTrayHide()",
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)");

        Assert.Contains("UsesSmartCollapseBehavior()", method, StringComparison.Ordinal);
        Assert.Contains("_isSmartPinnedOpen", method, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshCompactPlacementFromExpandedBounds(persist: true);",
            method,
            StringComparison.Ordinal);
        Assert.Contains("collapsed: true", method, StringComparison.Ordinal);
        Assert.Contains("persistManualState: false", method, StringComparison.Ordinal);
        Assert.Contains("animate: false", method, StringComparison.Ordinal);
        Assert.Contains("durationMs: 0", method, StringComparison.Ordinal);
        Assert.Contains("allowDuringInteraction: true", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml.cs")]
    [InlineData("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs")]
    public void TrayHide_PreparesCapsuleBeforeCapturingAnimationPositionAndStopsHoverRecovery(
        string relativePath)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
        string method = ExtractSection(
            source,
            "public bool PrepareTrayHideAnimation(bool persistVisibility = true)",
            "public void PlayPreparedTrayHideAnimation()");

        int prepareIndex = method.IndexOf(
            "PrepareCompactHostForTrayHide();",
            StringComparison.Ordinal);
        int hiddenIndex = method.IndexOf("Visible = false;", StringComparison.Ordinal);
        int notifyIndex = method.IndexOf(
            "NotifyCompactHostVisibilityChanged(false);",
            StringComparison.Ordinal);

        Assert.True(prepareIndex >= 0 && prepareIndex < hiddenIndex);
        Assert.True(hiddenIndex >= 0 && hiddenIndex < notifyIndex);
    }

    [Fact]
    public void SmartEntry_ReconcilesNativePointerAfterFlyoutInteractionActuallyCloses()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string endInteraction = ExtractSection(
            source,
            "protected void EndCompactInteraction()",
            "private void QueueCompactInteractionReconcile()");
        string reconcile = ExtractSection(
            source,
            "private void QueueCompactInteractionReconcile()",
            "private void ReleaseCompactInteraction(string reason)");

        Assert.Contains("QueueCompactInteractionReconcile();", endInteraction, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue", reconcile, StringComparison.Ordinal);
        Assert.Contains("IsPointerPhysicallyInsideWindow()", reconcile, StringComparison.Ordinal);
        Assert.Contains("ApplyEffectiveCollapseBehavior(animate: true);", reconcile, StringComparison.Ordinal);
        Assert.Contains("ScheduleSmartCollapse(SmartCollapseProbeMs);", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void HostVisibilityReset_ClearsShellHoverVisualsBeforeRebuildingNativePointerState()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)",
            "private void StartCompactHoverRecoveryProbe()");

        Assert.Equal(
            2,
            CountOccurrences(
                method,
                "WidgetShellControl.ResetTransientCompactPointerState();"));
        int visibleResetIndex = method.LastIndexOf(
            "WidgetShellControl.ResetTransientCompactPointerState();",
            StringComparison.Ordinal);
        int recoveryIndex = method.IndexOf(
            "StartCompactHoverRecoveryProbe();",
            visibleResetIndex,
            StringComparison.Ordinal);
        int synchronizeIndex = method.IndexOf(
            "SynchronizeCompactHoverFromCurrentCursor();",
            visibleResetIndex,
            StringComparison.Ordinal);
        Assert.True(visibleResetIndex >= 0 && visibleResetIndex < recoveryIndex);
        Assert.True(recoveryIndex >= 0 && recoveryIndex < synchronizeIndex);
    }

    [Fact]
    public void EnteringCompactBehavior_CapturesDirectionAwareTitleEdgeBeforeStateTransition()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void ApplyEffectiveCollapseBehavior(bool animate)",
            "private void SynchronizeCompactPointerStateForSmartEntry()");

        int captureIndex = method.IndexOf(
            "RefreshCompactPlacementFromExpandedBounds(persist: true);",
            StringComparison.Ordinal);
        int transitionIndex = method.IndexOf("SetCollapsedState(", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0 && captureIndex < transitionIndex);
    }

    [Fact]
    public void EveryFixedDirectionCollapse_RepairsLegacyOppositeEdgePlacementFirst()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void SetCollapsedState(",
            "private RectInt32 ResolvePersistedExpandedHostBounds()");

        int repairCheckIndex = method.IndexOf(
            "CompactPlacementNeedsDirectionRepair()",
            StringComparison.Ordinal);
        int captureIndex = method.IndexOf(
            "RefreshCompactPlacementFromExpandedBounds(persist: true);",
            repairCheckIndex,
            StringComparison.Ordinal);
        int targetChangeIndex = method.IndexOf("_targetCollapsed = collapsed;", StringComparison.Ordinal);
        Assert.True(repairCheckIndex >= 0 && repairCheckIndex < captureIndex);
        Assert.True(captureIndex >= 0 && captureIndex < targetChangeIndex);
    }

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

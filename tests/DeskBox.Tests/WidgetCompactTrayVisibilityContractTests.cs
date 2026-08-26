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

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.Commands.cs",
        "private void ContentWidgetShell_RightTapped",
        "private void ContentWidgetShell_TitleDoubleTapped")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs",
        "private void QuickCaptureShell_RightTapped",
        "private MenuFlyout CreateMoreFlyout")]
    public void TrayHide_SuppressesNativeAndRoutedPointerInputUntilHwndIsHidden(
        string relativePath,
        string rightTappedMarker,
        string nextMarker)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
        string handler = ExtractSection(
            source,
            rightTappedMarker,
            nextMarker);

        Assert.Contains("IsTrayHideInputSuppressed", handler, StringComparison.Ordinal);
        Assert.Contains("IsHideAnimationRunning", handler, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", handler, StringComparison.Ordinal);

        string inputSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.InputSuppression.cs"));
        Assert.Contains(
            "RootElement.IsHitTestVisible = false;",
            inputSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.WS_EX_TRANSPARENT",
            inputSource,
            StringComparison.Ordinal);
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
    public void OpenXamlPopups_BlockCapsuleCollapseAcrossHostedContent()
    {
        string baseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string popupGuard = ExtractSection(
            baseSource,
            "protected virtual bool HasBlockingFlyoutOpen()",
            "/// <summary>Allows hosts with custom title bars to update collapse actions.</summary>");
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains("RootElement.XamlRoot", popupGuard, StringComparison.Ordinal);
        Assert.Contains(
            "VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0",
            popupGuard,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasBlockingSurface: HasBlockingFlyoutOpen()",
            collapseSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CloseConfirmation_HoldsSmartCapsuleAcrossEveryFlyoutHandoff()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string moreMenu = ExtractSection(
            source,
            "private MenuFlyout CreateMoreFlyout()",
            "private void SetFileWidgetFolderOpenBehaviorOverride(");
        string contentMenu = ExtractSection(
            source,
            "private void ProvideWidgetActionsForContentMenu(",
            "private string GetFeatureWidgetCloseMenuText()");
        string closeHandoff = ExtractSection(
            source,
            "private void QueueCloseWidgetFlyoutAfterMenuClosed(",
            "private void ShowCloseWidgetFlyout(FrameworkElement target)");
        string fileCloseMenu = ExtractSection(
            source,
            "private MenuFlyout CreateFileWidgetCloseFlyout(",
            "private MenuFlyoutItem CreateFileWidgetCloseAction(");
        string folderRecycleHandoff = ExtractSection(
            source,
            "private void QueueDeleteManagedFolderConfirmationAfterMenuClosed(",
            "private async Task ShowDeleteManagedFolderConfirmationAsync()");

        foreach (string firstMenu in new[] { moreMenu, contentMenu })
        {
            Assert.Contains("AcquireCompactInteraction(", firstMenu, StringComparison.Ordinal);
            Assert.Contains(
                "\"content-close-confirmation-handoff\"",
                firstMenu,
                StringComparison.Ordinal);
            Assert.Contains(
                "QueueCloseWidgetFlyoutAfterMenuClosed(handoffInteraction);",
                firstMenu,
                StringComparison.Ordinal);
        }
        Assert.Contains("AcquireCompactInteraction(", fileCloseMenu, StringComparison.Ordinal);
        Assert.Contains(
            "\"managed-folder-recycle-confirmation-handoff\"",
            fileCloseMenu,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueDeleteManagedFolderConfirmationAfterMenuClosed(",
            fileCloseMenu,
            StringComparison.Ordinal);

        int closeShowIndex = closeHandoff.IndexOf(
            "ShowCloseWidgetFlyout(ContentWidgetShell);",
            StringComparison.Ordinal);
        int closeReleaseIndex = closeHandoff.IndexOf(
            "handoffInteraction?.Dispose();",
            closeShowIndex,
            StringComparison.Ordinal);
        Assert.True(closeShowIndex >= 0 && closeShowIndex < closeReleaseIndex);

        int recycleShowIndex = folderRecycleHandoff.IndexOf(
            "await ShowDeleteManagedFolderConfirmationAsync();",
            StringComparison.Ordinal);
        int recycleReleaseIndex = folderRecycleHandoff.IndexOf(
            "handoffInteraction?.Dispose();",
            recycleShowIndex,
            StringComparison.Ordinal);
        Assert.True(recycleShowIndex >= 0 && recycleShowIndex < recycleReleaseIndex);
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
    public void StaleRoutedDragSession_DoesNotPermanentlyBlockHoverExpansion()
    {
        string shellSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string shellRecovery = ExtractSection(
            shellSource,
            "internal bool TryClearStaleShellDragSessionAfterPointerRelease()",
            "private Brush CreateOpaqueOverlayButtonBackground()");
        string nativeExitRecovery = ExtractSection(
            shellSource,
            "internal bool TryEndShellDragSessionAfterNativePointerExit()",
            "private Brush CreateOpaqueOverlayButtonBackground()");
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string compactRecovery = ExtractSection(
            collapseSource,
            "private void ReconcileCompactDragStateAfterPointerRelease()",
            "private void WidgetShellControl_CompactDragLeft");
        string dragRecoveryProbe = ExtractSection(
            collapseSource,
            "private void ProbeCompactDragSessionState()",
            "protected bool UsesCompactExpansionGeometry()");
        string hoverProbe = ExtractSection(
            collapseSource,
            "private void RunCompactHoverRecoveryProbe(",
            "private void SynchronizeCompactHoverFromNativeCursor(");

        Assert.Contains("Win32Helper.IsAnyMouseButtonDown()", shellRecovery, StringComparison.Ordinal);
        Assert.Contains("_isShellDragActive = false;", shellRecovery, StringComparison.Ordinal);
        Assert.Contains("fileSurface.ClearDragSessionVisualState();", shellRecovery, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactDragLeft?.Invoke", shellRecovery, StringComparison.Ordinal);
        Assert.Contains(
            "EndShellDragSession(notifyCompact: true);",
            nativeExitRecovery,
            StringComparison.Ordinal);
        Assert.Contains("_isCompactDragInside = false;", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("_dragExpandedFromCollapsed = false;", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("CancelTimer(ref _collapseDragRestoreTimer);", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("ReconcileCompactDragStateAfterPointerRelease();", hoverProbe, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.IsAnyMouseButtonDown()", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains("!IsPointerPhysicallyInsideWindow()", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains(
            "TryEndShellDragSessionAfterNativePointerExit()",
            dragRecoveryProbe,
            StringComparison.Ordinal);
        Assert.Contains("ScheduleCompactDragSessionRecoveryProbe();", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains("ScheduleDragRestore(DragRestoreDelayMs);", dragRecoveryProbe, StringComparison.Ordinal);
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

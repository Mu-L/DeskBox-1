using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class WidgetVisualActivityContractTests
{
    [Fact]
    public void CompositionAnimationParameters_PreserveExistingPeriods()
    {
        Assert.Equal(
            1.0 / 0.875,
            WidgetShell.CompactLiveIndeterminateDurationSeconds,
            precision: 8);
        Assert.Equal(
            Math.PI * 2 / 0.8,
            WidgetShell.EdgeGlowPulseDurationSeconds,
            precision: 8);
    }

    [Fact]
    public void MusicTitleMarquee_UsesCompositionWithOriginalDelaySpeedAndGap()
    {
        string source = Read("src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs");

        Assert.Contains("TitleMarqueeGap = 32.0", source, StringComparison.Ordinal);
        Assert.Contains("TitleMarqueeStartDelayMs = 900.0", source, StringComparison.Ordinal);
        Assert.Contains("TitleMarqueeSpeedPixelsPerSecond = 50.0", source, StringComparison.Ordinal);
        Assert.Contains("CreateScalarKeyFrameAnimation()", source, StringComparison.Ordinal);
        Assert.Contains("visual.StartAnimation(\"Translation.X\", animation);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_titleMarqueeTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(33)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleIdleCleanup_IgnoresContinuousDecorativeActivityWithoutStoppingIt()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string manager = Read("src/DeskBox/Services/WidgetManager.cs");

        Assert.Contains("ShouldTrimVisibleIdleWorkingSet", app, StringComparison.Ordinal);
        Assert.Contains("_visibleIdleMemoryTracker.CommitMaintenance", app, StringComparison.Ordinal);
        Assert.Contains(
            "(shouldTrimWorkingSet && !trimmedWorkingSet)",
            app,
            StringComparison.Ordinal);
        Assert.Contains("trimRetryPending={trimRetryPending}", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SuspendVisibleIdleVisualActivity", app, StringComparison.Ordinal);

        string shellGate = ExtractSection(
            shell,
            "internal bool HasActiveVisualWork =>",
            "public bool HasWidgetGroup");
        Assert.DoesNotContain("_isCompactVinylRotating", shellGate, StringComparison.Ordinal);
        Assert.DoesNotContain("_compactMarqueeStoryboard", shellGate, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveMusicTimerCount", manager, StringComparison.Ordinal);
        Assert.Contains("UpdateCompactVinylRotation", shell, StringComparison.Ordinal);
        Assert.Contains("QueueCompactMarquee", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenWorkingSetTrim_HasAnIndependentConfiguredDeadline()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string schedule = ExtractSection(
            app,
            "internal static void ScheduleBackgroundMemoryCleanup()",
            "private bool CanRunBackgroundMemoryCleanup()");

        Assert.Contains("RunBackgroundCacheCleanupScheduleAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("RunHiddenWorkingSetTrimScheduleAsync", schedule, StringComparison.Ordinal);
        Assert.Contains(
            "Task.Delay(TimeSpan.FromSeconds(workingSetTrimDelaySeconds))",
            schedule,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "workingSetTrimDelaySeconds - deepDelaySeconds",
            schedule,
            StringComparison.Ordinal);

        string trim = ExtractSection(
            app,
            "private async Task TryRunHiddenWorkingSetTrimAsync(",
            "internal static void ScheduleLightMemoryCleanup(");
        int trimCallIndex = trim.IndexOf(
            "await Task.Run(Win32Helper.TrimCurrentProcessWorkingSet)",
            StringComparison.Ordinal);
        int cooldownIndex = trim.IndexOf(
            "_lastHiddenWorkingSetTrimAt = now;",
            StringComparison.Ordinal);
        Assert.True(trimCallIndex >= 0 && trimCallIndex < cooldownIndex);
    }

    [Fact]
    public void CompactLiveAndEdgeGlow_UseCompositionAndKeepReducedMotionStaticPath()
    {
        string source = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string compactLive = ExtractSection(
            source,
            "private void StartCompactLiveIndeterminate(bool isFullBleed)",
            "private DispatcherQueueTimer? _compactLiveBreathingTimer");
        string edgeGlow = ExtractSection(
            source,
            "private ScalarKeyFrameAnimation? _edgeGlowPulseAnimation;",
            "// ── Particles (rain / snow)");

        Assert.Contains("isFullBleed ? 0.22 : 0.30", compactLive, StringComparison.Ordinal);
        Assert.Contains("midpoint: 0.6f, amplitude: 0.2f", compactLive, StringComparison.Ordinal);
        Assert.Contains("!SystemAnimationsEnabled()", compactLive, StringComparison.Ordinal);
        Assert.Contains("CompactLiveProgressTransform.ScaleX = 1", compactLive, StringComparison.Ordinal);
        Assert.Contains("CreateScalarKeyFrameAnimation()", compactLive, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", compactLive, StringComparison.Ordinal);

        Assert.Contains("midpoint: 0.48f, amplitude: 0.1f", edgeGlow, StringComparison.Ordinal);
        Assert.Contains("EdgeGlowPulseDurationSeconds", edgeGlow, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", edgeGlow, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherParticles_UseScopedCompositionBatchesInsteadOfUiTicks()
    {
        string source = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string particles = ExtractSection(
            source,
            "// ── Particles (rain / snow)",
            "// ── Bottom glow (music playback)");

        Assert.Contains("for (int i = 0; i < 10; i++)", particles, StringComparison.Ordinal);
        Assert.Contains("CreateVector3KeyFrameAnimation()", particles, StringComparison.Ordinal);
        Assert.Contains("CreateScopedBatch(CompositionBatchTypes.Animation)", particles, StringComparison.Ordinal);
        Assert.Contains("ParticleAnimationBatch_Completed", particles, StringComparison.Ordinal);
        Assert.Contains("!SystemAnimationsEnabled()", particles, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", particles, StringComparison.Ordinal);
        Assert.DoesNotContain("ParticleTimer_Tick", particles, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoSegmentedRestore_DetachesRenderingWhenTheViewCannotRender()
    {
        string source = Read(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs");
        string restore = ExtractSection(
            source,
            "private void QueueTodoSegmentedRestore()",
            "private void ViewModel_PropertyChanged");

        Assert.Contains("if (!IsLoaded ||", restore, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel?.TabBarVisibility != Visibility.Visible",
            restore,
            StringComparison.Ordinal);
        Assert.Contains(
            "ListHeaderArea.Visibility != Visibility.Visible",
            restore,
            StringComparison.Ordinal);
        Assert.Contains("CancelTodoSegmentedRestore();", restore, StringComparison.Ordinal);
        Assert.Contains(
            "CompositionTarget.Rendering -= _todoSegmentedRenderingHandler;",
            restore,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.TrayAnimations.cs",
        "AppWindow.Hide();")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "_appWindow.Hide();")]
    public void VisualActivity_SuspendsOnlyAfterNativeWindowHide(
        string relativePath,
        string hideCall)
    {
        string source = Read(relativePath);
        int hideIndex = source.LastIndexOf(hideCall, StringComparison.Ordinal);
        int suspendIndex = source.IndexOf(
            "WidgetShellControl.SuspendVisualActivity();",
            hideIndex,
            StringComparison.Ordinal);

        Assert.True(hideIndex >= 0 && hideIndex < suspendIndex);
    }

    [Fact]
    public void QuickCaptureShow_ResumesVisualsBeforeRemovingCloak()
    {
        string source = Read("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs");
        string show = ExtractSection(
            source,
            "public void ShowPreparedRaisedFromTray(bool persistVisibility = true)",
            "public void EnsureRaisedFromTrayTopMost()");

        int resumeIndex = show.IndexOf(
            "NotifyCompactHostVisibilityChanged(true);",
            StringComparison.Ordinal);
        int revealIndex = show.IndexOf(
            "_trayAnimation.RevealWindowForTrayShow();",
            StringComparison.Ordinal);
        Assert.True(resumeIndex >= 0 && resumeIndex < revealIndex);
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs",
        "generation != _contentVisibilityGeneration")]
    [InlineData(
        "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs",
        "generation != _visibleContentResumeGeneration")]
    public void RevealCompletedBackgroundWork_IsDelayedAndGenerationCancelled(
        string relativePath,
        string generationGuard)
    {
        string source = Read(relativePath);

        Assert.Contains(
            "Task.Delay(RevealCompletedBackgroundDelayMs)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(generationGuard, source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}

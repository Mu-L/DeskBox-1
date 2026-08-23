using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlobalHotkeySafetyContractTests
{
    [Fact]
    public void SettingsRecorder_IsTheOnlyWinSpaceEntryPointAndShowsPersistentWarning()
    {
        string xaml = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string settingsWindow = Read("src/DeskBox/Views/SettingsWindow.xaml.cs");
        string hotkeyCode = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");

        Assert.Contains("x:Name=\"GlobalHotkeyCaptureButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWinSpaceHotkeyButton", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyReservedWarning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanShowGlobalHotkeyWarning", xaml, StringComparison.Ordinal);
        Assert.Contains("WmReservedHotkeyCapture", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("_hotkeyRecordingHook.TryStart", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("_hotkeyRecordingHook.Stop", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("IsInternalMaskKey", hotkeyCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWinSpaceHotkeyButton_Click", hotkeyCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndOnboardingRecorders_IgnoreReservedHookMaskKey()
    {
        Assert.True(ReservedHotkeyHookService.IsInternalMaskKey(0xE8));
        Assert.False(ReservedHotkeyHookService.IsInternalMaskKey(0x20));

        string settingsSource = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string settingsHandler = Slice(
            settingsSource,
            "private void GlobalHotkeyCaptureButton_KeyDown",
            "private void GlobalHotkeyCaptureButton_LostFocus");
        Assert.True(
            settingsHandler.IndexOf("IsInternalMaskKey", StringComparison.Ordinal) <
            settingsHandler.IndexOf("ApplyRecordedHotkeyAsync", StringComparison.Ordinal));

        string onboardingSource = Read("src/DeskBox/Views/OnboardingWindow.Hotkey.cs");
        string onboardingHandler = Slice(
            onboardingSource,
            "private void OnHotkeyKeyDown",
            "private void Step4HotkeyToggle_Toggled");
        Assert.True(
            onboardingHandler.IndexOf("IsInternalMaskKey", StringComparison.Ordinal) <
            onboardingHandler.IndexOf("ApplyRecordedHotkeyAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void GestureChange_UsesTheRealRegistrationAsCommitPointAndRestoresPreviousGesture()
    {
        string source = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string apply = Slice(
            source,
            "public bool TryApplyGesture",
            "public void SetEnabled");

        Assert.DoesNotContain("CanRegister", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeHotkeyId", source, StringComparison.Ordinal);
        Assert.Contains("int previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("int previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("RefreshRegistration();", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyModifiers = previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyKey = previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("if (IsRegistered)", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchGestureChange_UsesTheRealRegistrationAsCommitPointAndRestoresPreviousGesture()
    {
        string source = Read("src/DeskBox/Services/SearchHotkeyService.cs");
        string apply = Slice(
            source,
            "public bool TryApplyGesture",
            "public void SetEnabled");

        Assert.Contains("int previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("int previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("bool shouldBeActive", apply, StringComparison.Ordinal);
        Assert.Contains("RefreshRegistration();", apply, StringComparison.Ordinal);
        Assert.Contains("settings.SearchHotkeyModifiers = previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("settings.SearchHotkeyKey = previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("if (IsRegistered)", apply, StringComparison.Ordinal);
        Assert.Contains("return false;", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void HotkeyServices_ExposeReceiveInvokeAndDispatchFailureCounters()
    {
        string global = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string search = Read("src/DeskBox/Services/SearchHotkeyService.cs");

        foreach (string source in new[] { global, search })
        {
            Assert.Contains("public long ReceivedCount", source, StringComparison.Ordinal);
            Assert.Contains("public long InvocationCount", source, StringComparison.Ordinal);
            Assert.Contains("public long DispatchFailureCount", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _receivedSequence)", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _invocationSequence)", source, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _dispatchFailureSequence)", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReservedHook_IgnoresInjectedInputAndFailsOpenWhenDeliveryFails()
    {
        string hook = Read("src/DeskBox/Services/ReservedHotkeyHookService.cs");
        string win32 = Read("src/DeskBox/Helpers/Win32Helper.cs");

        Assert.Contains("LLKHF_INJECTED", hook, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualResetEventSlim", hook, StringComparison.Ordinal);
        Assert.Contains("_lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("generation != _lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("TrySendTaggedKeyPress", hook, StringComparison.Ordinal);
        Assert.Contains("CancelSuppression();", hook, StringComparison.Ordinal);
        Assert.Contains("PostMessage", hook, StringComparison.Ordinal);
        Assert.Contains("SendInput", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("keybd_event", win32, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleRecovery_ReRegistersGlobalHotkeyAfterExternalSessionChanges()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string recovery = Slice(
            app,
            "private void OnLifecycleRecoveryRequested",
            "private void FlushSettingsForEndSession");

        Assert.Contains("GlobalHotkeyService?.RefreshRegistration();", recovery, StringComparison.Ordinal);
        Assert.Contains("requiresExternalRecovery", recovery, StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        return File.ReadAllText(TestPaths.FromRepository(path));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}

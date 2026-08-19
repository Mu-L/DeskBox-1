using DeskBox.Models;
using DeskBox.Services;
using Windows.System;

namespace DeskBox.Tests;

public sealed class GlobalHotkeyGestureTests
{
    [Fact]
    public void NormalizeGesture_PreservesWindowsModifier()
    {
        GlobalHotkeyGesture gesture = GlobalHotkeyService.NormalizeGesture(
            (int)(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift),
            (int)VirtualKey.F7);

        Assert.Equal(
            HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift,
            gesture.Modifiers);
    }

    [Fact]
    public void WinSpace_IsTheOnlyReservedHookGesture()
    {
        Assert.True(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, (int)VirtualKey.Space)));
        Assert.False(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows | HotkeyModifierKeys.Control,
                (int)VirtualKey.Space)));
        Assert.False(GlobalHotkeyService.IsReservedSystemGesture(
            new GlobalHotkeyGesture(HotkeyModifierKeys.Windows, (int)VirtualKey.F7)));
    }

    [Theory]
    [InlineData(HotkeyModifierKeys.Windows, VirtualKey.F7)]
    [InlineData(HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift, VirtualKey.F7)]
    [InlineData(HotkeyModifierKeys.Windows, VirtualKey.Space)]
    public void WindowsGestures_AreAcceptedByTheSharedRecorder(
        HotkeyModifierKeys modifiers,
        VirtualKey key)
    {
        Assert.True(GlobalHotkeyService.IsValidGesture(
            new GlobalHotkeyGesture(modifiers, (int)key)));
    }
}

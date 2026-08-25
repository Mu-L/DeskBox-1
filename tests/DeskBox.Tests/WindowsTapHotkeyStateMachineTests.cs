using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsTapHotkeyStateMachineTests
{
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private const uint D = 0x44;

    [Theory]
    [InlineData(LeftWindows)]
    [InlineData(RightWindows)]
    public void StandaloneWindowsTap_TriggersOnReleaseWithoutSuppressingIt(uint windowsKey)
    {
        var state = new WindowsTapHotkeyStateMachine();

        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(windowsKey, true));
        Assert.Equal(
            ReservedHotkeyEventDisposition.TriggerAndPassThrough,
            state.Process(windowsKey, false));
    }

    [Fact]
    public void WindowsChord_NeverTriggersStandaloneActivation()
    {
        var state = new WindowsTapHotkeyStateMachine();

        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(LeftWindows, true));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(D, true));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(D, false));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(LeftWindows, false));
    }

    [Fact]
    public void HoldingBothWindowsKeys_IsTreatedAsAChord()
    {
        var state = new WindowsTapHotkeyStateMachine();
        state.Process(LeftWindows, true);
        state.Process(RightWindows, true);

        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(LeftWindows, false));
        Assert.Equal(ReservedHotkeyEventDisposition.PassThrough, state.Process(RightWindows, false));
    }
}

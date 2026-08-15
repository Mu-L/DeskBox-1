using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetLayerPointerActivationPolicyTests
{
    [Theory]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, false, false, true)]
    public void PointerActivation_IsSuppressedOnlyForPinnedWidgetBehindForeignWindow(
        bool usesDesktopPinnedMode,
        bool hasForegroundWindow,
        bool foregroundIsDesktopShell,
        bool foregroundIsWidget,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetLayerPointerActivationPolicy.ShouldSuppress(
                usesDesktopPinnedMode,
                hasForegroundWindow,
                foregroundIsDesktopShell,
                foregroundIsWidget));
    }
}

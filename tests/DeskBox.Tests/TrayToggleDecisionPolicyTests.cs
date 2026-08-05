using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TrayToggleDecisionPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    public void ShouldHide_coversRaisedHiddenForegroundAndBehindStates(
        bool raised,
        bool visible,
        bool foregroundLocal,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayToggleDecisionPolicy.ShouldHide(
                new TrayToggleDecisionContext(raised, visible, foregroundLocal)));
    }
}

using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void ResolveShouldAnimate_RequiresEffectsAndDisablesHighContrastMotion(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrast,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveShouldAnimate(
                animationsEnabled,
                advancedEffectsEnabled,
                highContrast));
    }
}

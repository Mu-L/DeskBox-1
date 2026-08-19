using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Theory]
    [InlineData(SettingsService.WidgetMaterialTypeMica, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeMicaAlt, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeAcrylic, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeAcrylicBase, 19045, SettingsService.WidgetMaterialTypeAcrylicBase)]
    [InlineData(SettingsService.WidgetMaterialTypeSolid, 19045, SettingsService.WidgetMaterialTypeSolid)]
    [InlineData(SettingsService.WidgetMaterialTypeMica, 22000, SettingsService.WidgetMaterialTypeMica)]
    [InlineData(SettingsService.WidgetMaterialTypeMicaAlt, 26100, SettingsService.WidgetMaterialTypeMicaAlt)]
    [InlineData("Unknown", 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    public void ResolveWidgetMaterialTypeForBuild_UsesAcrylicForWin10Mica(
        string requested,
        int osBuild,
        string expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild(
                requested,
                osBuild));
    }

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

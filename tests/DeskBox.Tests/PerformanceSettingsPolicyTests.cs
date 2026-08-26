using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PerformanceSettingsPolicyTests
{
    [Fact]
    public void Defaults_PreserveTheExistingBalancedRuntimeBehavior()
    {
        var settings = new AppSettings();

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(
            PerformanceSettingsPolicy.ModeBalanced,
            effective.Mode);
        Assert.Equal(30, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(5 * 60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.True(effective.AllowContinuousDecorativeAnimations);
        Assert.False(PerformanceSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void Presets_ChangeOnlyTheNarrowPerformanceControls()
    {
        var settings = new AppSettings
        {
            WidgetAnimationEffect = "Fade",
            WidgetAnimationSpeed = "Slow",
            WidgetCompactAnimationEffect = "Snappy",
            WidgetCompactAnimationDurationMs = 777,
            WidgetCompactExpandDelayMs = 333,
            WidgetCompactCollapseDelayMs = 888
        };

        PerformanceSettingsPolicy.ApplyPreset(
            settings,
            PerformanceSettingsPolicy.ModeResourceSaver);
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(30, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.False(effective.AllowContinuousDecorativeAnimations);
        Assert.Equal("Fade", settings.WidgetAnimationEffect);
        Assert.Equal("Slow", settings.WidgetAnimationSpeed);
        Assert.Equal("Snappy", settings.WidgetCompactAnimationEffect);
        Assert.Equal(777, settings.WidgetCompactAnimationDurationMs);
        Assert.Equal(333, settings.WidgetCompactExpandDelayMs);
        Assert.Equal(888, settings.WidgetCompactCollapseDelayMs);
    }

    [Fact]
    public void BestVisual_KeepsCachesWarmAndAllowsDecorativeEffects()
    {
        var settings = new AppSettings();

        PerformanceSettingsPolicy.ApplyPreset(
            settings,
            PerformanceSettingsPolicy.ModeBestVisual);
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(5 * 60, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.True(effective.AllowContinuousDecorativeAnimations);
    }

    [Fact]
    public void Custom_AllowsCleanupToBeDisabled()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever,
            EnableContinuousDecorativeAnimations = false
        };

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(
            PerformanceSettingsPolicy.CleanupNever,
            effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupNever,
            effective.HiddenDeepCleanupDelaySeconds);
        Assert.False(effective.AllowContinuousDecorativeAnimations);
    }

    [Fact]
    public void Normalize_RepairsUnknownValuesToBalancedDefaults()
    {
        var settings = new AppSettings
        {
            PerformanceMode = "unknown",
            HiddenCacheCleanupDelaySeconds = 17,
            EnableContinuousDecorativeAnimations = false
        };

        Assert.True(PerformanceSettingsPolicy.Normalize(settings));

        Assert.Equal(
            PerformanceSettingsPolicy.ModeBalanced,
            settings.PerformanceMode);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            settings.HiddenCacheCleanupDelaySeconds);
        Assert.True(settings.EnableContinuousDecorativeAnimations);
    }
}

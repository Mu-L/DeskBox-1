using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct EffectivePerformanceSettings(
    string Mode,
    int HiddenCacheCleanupDelaySeconds,
    int HiddenDeepCleanupDelaySeconds,
    bool AllowContinuousDecorativeAnimations);

/// <summary>
/// Resolves the user-facing performance preset into narrowly scoped runtime
/// behavior. Interaction, capsule expansion, and widget show/hide animations
/// are intentionally outside this policy.
/// </summary>
public static class PerformanceSettingsPolicy
{
    public const string ModeBestVisual = "BestVisual";
    public const string ModeBalanced = "Balanced";
    public const string ModeResourceSaver = "ResourceSaver";
    public const string ModeCustom = "Custom";

    public const int CleanupNever = -1;
    public const int CleanupAfter30Seconds = 30;
    public const int CleanupAfter1Minute = 60;
    public const int CleanupAfter5Minutes = 5 * 60;

    public const string DefaultMode = ModeBalanced;
    public const int DefaultHiddenCacheCleanupDelaySeconds = CleanupAfter30Seconds;
    public const bool DefaultContinuousDecorativeAnimationsEnabled = true;

    public static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, ModeBestVisual, StringComparison.OrdinalIgnoreCase))
        {
            return ModeBestVisual;
        }

        if (string.Equals(mode, ModeResourceSaver, StringComparison.OrdinalIgnoreCase))
        {
            return ModeResourceSaver;
        }

        if (string.Equals(mode, ModeCustom, StringComparison.OrdinalIgnoreCase))
        {
            return ModeCustom;
        }

        return ModeBalanced;
    }

    public static int NormalizeHiddenCacheCleanupDelaySeconds(int delaySeconds) =>
        delaySeconds is
            CleanupNever or
            CleanupAfter30Seconds or
            CleanupAfter1Minute or
            CleanupAfter5Minutes
                ? delaySeconds
                : DefaultHiddenCacheCleanupDelaySeconds;

    public static EffectivePerformanceSettings Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string mode = NormalizeMode(settings.PerformanceMode);
        return mode switch
        {
            ModeBestVisual => new(
                mode,
                CleanupAfter5Minutes,
                10 * 60,
                true),
            ModeResourceSaver => new(
                mode,
                CleanupAfter30Seconds,
                CleanupAfter1Minute,
                false),
            ModeCustom => ResolveCustom(settings),
            _ => new(
                ModeBalanced,
                CleanupAfter30Seconds,
                CleanupAfter5Minutes,
                true)
        };
    }

    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string mode = NormalizeMode(settings.PerformanceMode);
        int hiddenDelay = NormalizeHiddenCacheCleanupDelaySeconds(
            settings.HiddenCacheCleanupDelaySeconds);
        bool allowDecorativeAnimations =
            settings.EnableContinuousDecorativeAnimations;

        if (!string.Equals(mode, ModeCustom, StringComparison.Ordinal))
        {
            EffectivePerformanceSettings preset = ResolvePreset(mode);
            hiddenDelay = preset.HiddenCacheCleanupDelaySeconds;
            allowDecorativeAnimations = preset.AllowContinuousDecorativeAnimations;
        }

        bool changed = false;
        if (!string.Equals(
                settings.PerformanceMode,
                mode,
                StringComparison.Ordinal))
        {
            settings.PerformanceMode = mode;
            changed = true;
        }

        if (settings.HiddenCacheCleanupDelaySeconds != hiddenDelay)
        {
            settings.HiddenCacheCleanupDelaySeconds = hiddenDelay;
            changed = true;
        }

        if (settings.EnableContinuousDecorativeAnimations !=
            allowDecorativeAnimations)
        {
            settings.EnableContinuousDecorativeAnimations =
                allowDecorativeAnimations;
            changed = true;
        }

        return changed;
    }

    public static void ApplyPreset(AppSettings settings, string? mode)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.PerformanceMode = NormalizeMode(mode);
        if (string.Equals(
                settings.PerformanceMode,
                ModeCustom,
                StringComparison.Ordinal))
        {
            settings.HiddenCacheCleanupDelaySeconds =
                NormalizeHiddenCacheCleanupDelaySeconds(
                    settings.HiddenCacheCleanupDelaySeconds);
            return;
        }

        EffectivePerformanceSettings preset = ResolvePreset(
            settings.PerformanceMode);
        settings.HiddenCacheCleanupDelaySeconds =
            preset.HiddenCacheCleanupDelaySeconds;
        settings.EnableContinuousDecorativeAnimations =
            preset.AllowContinuousDecorativeAnimations;
    }

    private static EffectivePerformanceSettings ResolvePreset(string mode)
    {
        var settings = new AppSettings
        {
            PerformanceMode = mode
        };
        return Resolve(settings);
    }

    private static EffectivePerformanceSettings ResolveCustom(
        AppSettings settings)
    {
        int hiddenDelay = NormalizeHiddenCacheCleanupDelaySeconds(
            settings.HiddenCacheCleanupDelaySeconds);
        int deepDelay = hiddenDelay switch
        {
            CleanupNever => CleanupNever,
            CleanupAfter5Minutes => 10 * 60,
            _ => CleanupAfter5Minutes
        };
        return new(
            ModeCustom,
            hiddenDelay,
            deepDelay,
            settings.EnableContinuousDecorativeAnimations);
    }

}

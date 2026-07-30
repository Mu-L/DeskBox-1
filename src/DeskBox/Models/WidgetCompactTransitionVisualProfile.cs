namespace DeskBox.Models;

public sealed record WidgetCompactTransitionVisualProfile(
    int DurationMilliseconds,
    double BodyRevealStart,
    double BodyHideEnd,
    double IdentityTranslation,
    bool IsAnimated)
{
    public static WidgetCompactTransitionVisualProfile Resolve(
        string? preset,
        int customDurationMilliseconds,
        bool animationsEnabled)
    {
        if (!animationsEnabled ||
            string.Equals(preset, "None", StringComparison.OrdinalIgnoreCase))
        {
            return new(0, 0, 0, 0, false);
        }

        return preset?.Trim().ToLowerInvariant() switch
        {
            "slow" => new(360, 0.34, 0.58, 4, true),
            "snappy" => new(160, 0.22, 0.48, 3, true),
            "custom" => new(
                Math.Clamp(customDurationMilliseconds, 120, 400),
                0.28,
                0.54,
                4,
                true),
            _ => new(
                Math.Clamp(customDurationMilliseconds, 120, 400),
                0.28,
                0.54,
                4,
                true)
        };
    }

    public (double CompactOpacity, double ExpandedOpacity) GetOpacity(
        bool collapsing,
        double progress)
    {
        if (!IsAnimated)
        {
            return collapsing ? (1, 0) : (0, 1);
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            double handoff = Math.Clamp(BodyHideEnd, 0.36, 0.62);
            double expanded = 1 - SmoothStep(Math.Clamp(value / handoff, 0, 1));
            double compact = SmoothStep(Math.Clamp(
                (value - handoff) / (1 - handoff),
                0,
                1));
            return (compact, expanded);
        }

        double expandHandoff = Math.Clamp(BodyRevealStart, 0.22, 0.42);
        double compactOut = 1 - SmoothStep(Math.Clamp(
            value / expandHandoff,
            0,
            1));
        double expandedIn = SmoothStep(Math.Clamp(
            (value - expandHandoff) / (1 - expandHandoff),
            0,
            1));
        return (compactOut, expandedIn);
    }

    private static double SmoothStep(double value) =>
        value * value * (3 - (2 * value));
}

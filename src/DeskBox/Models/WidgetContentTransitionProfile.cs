namespace DeskBox.Models;

public readonly record struct WidgetContentTransitionProfile(
    int DurationMilliseconds,
    int OutgoingDurationMilliseconds,
    int SwapGapMilliseconds,
    int IncomingDurationMilliseconds,
    double TranslationDistance,
    double MinimumScale,
    double IncomingStartOpacity,
    double OutgoingEndOpacity,
    bool UsesMotion)
{
    public static WidgetContentTransitionProfile Create(
        bool animationsEnabled,
        bool directional)
    {
        if (!animationsEnabled)
        {
            return new WidgetContentTransitionProfile(
                0,
                0,
                0,
                0,
                0,
                1,
                0,
                0,
                UsesMotion: false);
        }

        return new WidgetContentTransitionProfile(
            210,
            78,
            12,
            120,
            directional ? 6 : 0,
            0.975,
            0,
            0,
            UsesMotion: directional);
    }
}

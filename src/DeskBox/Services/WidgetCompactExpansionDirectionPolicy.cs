namespace DeskBox.Services;

internal static class WidgetCompactExpansionDirectionPolicy
{
    public static bool RequiresFullSize(string? configuredDirection) =>
        SettingsService.NormalizeWidgetCompactExpansionDirection(configuredDirection) !=
        SettingsService.WidgetCompactExpansionDirectionAuto;

    public static IReadOnlyList<WidgetCompactExpansionAnchor> Apply(
        string? configuredDirection,
        IReadOnlyList<WidgetCompactExpansionAnchor> anchors)
    {
        string direction = SettingsService.NormalizeWidgetCompactExpansionDirection(
            configuredDirection);
        if (direction == SettingsService.WidgetCompactExpansionDirectionAuto)
        {
            return anchors;
        }

        bool expandsDown = direction == SettingsService.WidgetCompactExpansionDirectionDown;
        var constrained = new List<WidgetCompactExpansionAnchor>(Math.Max(2, anchors.Count));
        foreach (WidgetCompactExpansionAnchor anchor in anchors)
        {
            WidgetCompactExpansionAnchor mapped = (anchor, expandsDown) switch
            {
                (WidgetCompactExpansionAnchor.RightTop or WidgetCompactExpansionAnchor.RightBottom, true) =>
                    WidgetCompactExpansionAnchor.RightTop,
                (WidgetCompactExpansionAnchor.RightTop or WidgetCompactExpansionAnchor.RightBottom, false) =>
                    WidgetCompactExpansionAnchor.RightBottom,
                (_, true) => WidgetCompactExpansionAnchor.LeftTop,
                _ => WidgetCompactExpansionAnchor.LeftBottom
            };
            if (!constrained.Contains(mapped))
            {
                constrained.Add(mapped);
            }
        }

        if (constrained.Count == 0)
        {
            constrained.Add(expandsDown
                ? WidgetCompactExpansionAnchor.LeftTop
                : WidgetCompactExpansionAnchor.LeftBottom);
            constrained.Add(expandsDown
                ? WidgetCompactExpansionAnchor.RightTop
                : WidgetCompactExpansionAnchor.RightBottom);
        }

        return constrained;
    }
}

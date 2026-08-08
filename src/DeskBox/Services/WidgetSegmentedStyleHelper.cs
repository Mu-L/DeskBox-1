using CommunityToolkit.WinUI.Controls;
using DeskBox.Helpers;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public static class WidgetSegmentedStyleHelper
{
    public static void Apply(Segmented segmented, string? style)
    {
        ArgumentNullException.ThrowIfNull(segmented);

        // CommunityToolkit's Segmented template resolves accent resources from
        // the control itself, rather than the hosting window. Keep its active
        // indicator aligned with DeskBox's effective accent.
        AccentResourceScope.Apply(
            segmented,
            App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor);

        string normalizedStyle = SettingsService.NormalizeWidgetTabStyle(style);
        if (normalizedStyle == SettingsService.WidgetTabStyleButton)
        {
            segmented.ClearValue(FrameworkElement.StyleProperty);
            return;
        }

        if (Application.Current.Resources.TryGetValue("WidgetPivotSegmentedStyle", out object resource) &&
            resource is Style segmentedStyle)
        {
            segmented.Style = segmentedStyle;
        }
    }
}

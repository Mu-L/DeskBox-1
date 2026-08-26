using DeskBox.Models;

namespace DeskBox.Services;

internal static class WidgetCompactExpansionDirectionSettings
{
    public const string MetadataKey = "CompactExpansionDirection";

    public static string? GetOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(MetadataKey, out string? value))
        {
            return null;
        }

        return TryNormalize(value, out string normalized) ? normalized : null;
    }

    public static string Resolve(WidgetConfig config, string? globalValue) =>
        GetOverride(config) ??
        SettingsService.NormalizeWidgetCompactExpansionDirection(globalValue);

    public static void SetOverride(WidgetConfig config, string? value)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        if (TryNormalize(value, out string normalized))
        {
            config.Metadata[MetadataKey] = normalized;
            return;
        }

        config.Metadata.Remove(MetadataKey);
    }

    public static bool NormalizeOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        if (!config.Metadata.TryGetValue(MetadataKey, out string? value))
        {
            return false;
        }

        if (!TryNormalize(value, out string normalized))
        {
            config.Metadata.Remove(MetadataKey);
            return true;
        }

        if (string.Equals(value, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        config.Metadata[MetadataKey] = normalized;
        return true;
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        if (string.Equals(
                value,
                SettingsService.WidgetCompactExpansionDirectionAuto,
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = SettingsService.WidgetCompactExpansionDirectionAuto;
            return true;
        }

        if (string.Equals(
                value,
                SettingsService.WidgetCompactExpansionDirectionDown,
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = SettingsService.WidgetCompactExpansionDirectionDown;
            return true;
        }

        if (string.Equals(
                value,
                SettingsService.WidgetCompactExpansionDirectionUp,
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = SettingsService.WidgetCompactExpansionDirectionUp;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}

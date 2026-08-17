using DeskBox.Models;

namespace DeskBox.Services;

public static class FileWidgetFolderOpenBehaviorNames
{
    public const string Explorer = "Explorer";
    public const string Embedded = "Embedded";
    public const string FollowGlobal = "FollowGlobal";
    public const string MetadataKey = "FolderOpenBehavior";

    public static string NormalizeGlobal(string? value) =>
        string.Equals(value, Embedded, StringComparison.Ordinal)
            ? Embedded
            : Explorer;

    public static string? GetOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Metadata.TryGetValue(MetadataKey, out string? value))
        {
            return null;
        }

        return value switch
        {
            Explorer => Explorer,
            Embedded => Embedded,
            _ => null
        };
    }

    public static string Resolve(AppSettings settings, WidgetConfig config) =>
        GetOverride(config) ?? NormalizeGlobal(settings.FileWidgetFolderOpenBehavior);

    public static void SetOverride(WidgetConfig config, string? value)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        if (value is Explorer or Embedded)
        {
            config.Metadata[MetadataKey] = value;
            return;
        }

        config.Metadata.Remove(MetadataKey);
    }

    public static bool NormalizeOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        if (!config.Metadata.TryGetValue(MetadataKey, out string? value) ||
            value is Explorer or Embedded)
        {
            return false;
        }

        config.Metadata.Remove(MetadataKey);
        return true;
    }
}

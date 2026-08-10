using System.Globalization;
using DeskBox.Controls;
using DeskBox.Models;

namespace DeskBox.Services;

public static class TodoMasterDetailSettings
{
    private const string MasterPaneWidthKey = "Todo.MasterPaneWidth";

    public static double? GetMasterPaneWidth(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Metadata.TryGetValue(MasterPaneWidthKey, out string? value) &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) &&
               double.IsFinite(width)
            ? width
            : null;
    }

    public static bool SetMasterPaneWidth(WidgetConfig config, double width)
    {
        ArgumentNullException.ThrowIfNull(config);
        double normalized = new MasterDetailLayoutPolicy().NormalizePersistedMasterWidth(width);
        string value = normalized.ToString("0.###", CultureInfo.InvariantCulture);
        if (config.Metadata.TryGetValue(MasterPaneWidthKey, out string? current) &&
            string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        config.Metadata[MasterPaneWidthKey] = value;
        return true;
    }
}

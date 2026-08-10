using DeskBox.Models;

namespace DeskBox.Controls.WidgetContents;

internal static class TodoResponsiveLayoutResolver
{
    internal static TodoWorkspaceLayoutMode Resolve(
        double correctedWidth,
        double correctedHeight,
        TodoResponsivePreference preference)
    {
        if (correctedWidth < 240 || correctedHeight < 260)
        {
            return TodoWorkspaceLayoutMode.Micro;
        }

        if (correctedWidth < 480)
        {
            return TodoWorkspaceLayoutMode.Compact;
        }

        if (preference == TodoResponsivePreference.SingleColumn || correctedHeight < 360)
        {
            return TodoWorkspaceLayoutMode.Enhanced;
        }

        if (correctedWidth >= 960 && correctedHeight >= 500)
        {
            return TodoWorkspaceLayoutMode.ThreePane;
        }

        double splitThreshold = preference == TodoResponsivePreference.PreferSplit ? 480 : 720;
        return correctedWidth >= splitThreshold
            ? TodoWorkspaceLayoutMode.Split
            : TodoWorkspaceLayoutMode.Enhanced;
    }

    internal static bool HasCrossedHysteresis(
        double correctedWidth,
        double correctedHeight,
        TodoResponsivePreference preference,
        TodoWorkspaceLayoutMode current,
        TodoWorkspaceLayoutMode candidate,
        double hysteresis)
    {
        if (candidate == current)
        {
            return true;
        }

        TodoWorkspaceLayoutMode buffered = candidate > current
            ? Resolve(correctedWidth - hysteresis, correctedHeight - hysteresis, preference)
            : Resolve(correctedWidth + hysteresis, correctedHeight + hysteresis, preference);
        return candidate > current
            ? buffered >= candidate
            : buffered <= candidate;
    }

    /// <summary>
    /// Resolves how many full task rows a month cell can show without clipping.
    /// -1 means date only, 0 means colored dots, and 1-3 means task rows.
    /// The calculation uses the actual main-view viewport instead of the whole
    /// widget because split layouts can leave the calendar much narrower.
    /// </summary>
    internal static int ResolveMonthTaskLineCapacity(
        double hostWidth,
        double hostHeight,
        bool showWeekNumbers,
        bool stacksSelectedDay)
    {
        if (!double.IsFinite(hostWidth) || !double.IsFinite(hostHeight))
        {
            return -1;
        }

        double calendarHeight = Math.Max(0, stacksSelectedDay ? hostHeight * 0.62 : hostHeight);
        double fixedWidth = 4 + (showWeekNumbers ? 30 : 0) + (showWeekNumbers ? 14 : 12);
        double cellWidth = Math.Max(0, hostWidth - fixedWidth) / 7;
        double cellHeight = Math.Max(0, calendarHeight - 32) / 6;

        if (cellHeight < 27)
        {
            return -1;
        }

        if (cellWidth < 74 || cellHeight < 48)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor((cellHeight - 27) / 19), 1, 3);
    }
}

using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure settings helpers for widget groups. Keeping normalization independent
/// from HWND ownership makes corrupt or partially-written settings recoverable
/// before any desktop window is created.
/// </summary>
public static class WidgetGroupSettings
{
    public const int MaximumMemberCount = 8;

    public static WidgetGroupConfig? FindByMember(AppSettings settings, string widgetId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return null;
        }

        return settings.WidgetGroups.FirstOrDefault(group =>
            group.MemberIds.Contains(widgetId, StringComparer.Ordinal));
    }

    public static bool IsActiveMember(AppSettings settings, string widgetId)
    {
        WidgetGroupConfig? group = FindByMember(settings, widgetId);
        return group is null ||
               string.Equals(group.ActiveMemberId, widgetId, StringComparison.Ordinal);
    }

    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Widgets ??= [];
        settings.WidgetGroups ??= [];
        settings.DeletedWidgetIds ??= [];
        string normalizedDefaultStyle = WidgetGroupNavigationStyles.Normalize(
            settings.WidgetGroupDefaultNavigationStyle,
            allowFollowDefault: false);
        string normalizedDefaultTitleDisplayMode =
            WidgetGroupTitleDisplayModes.Normalize(
                settings.WidgetGroupDefaultTitleDisplayMode,
                allowFollowDefault: false);
        bool changed =
            !string.Equals(
                settings.WidgetGroupDefaultNavigationStyle,
                normalizedDefaultStyle,
                StringComparison.Ordinal) ||
            !string.Equals(
                settings.WidgetGroupDefaultTitleDisplayMode,
                normalizedDefaultTitleDisplayMode,
                StringComparison.Ordinal);
        settings.WidgetGroupDefaultNavigationStyle = normalizedDefaultStyle;
        settings.WidgetGroupDefaultTitleDisplayMode =
            normalizedDefaultTitleDisplayMode;


        var deletedWidgetIds = settings.DeletedWidgetIds.ToHashSet(StringComparer.Ordinal);
        var validWidgetIds = settings.Widgets
            .Where(widget => !deletedWidgetIds.Contains(widget.Id))
            .Select(widget => widget.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var claimedMemberIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var surfaceIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedGroups = new List<WidgetGroupConfig>(settings.WidgetGroups.Count);

        foreach (WidgetGroupConfig? candidate in settings.WidgetGroups)
        {
            if (candidate is null)
            {
                changed = true;
                continue;
            }

            candidate.MemberIds ??= [];
            if (string.IsNullOrWhiteSpace(candidate.Id) || !groupIds.Add(candidate.Id))
            {
                candidate.Id = CreateUniqueId(groupIds);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(candidate.SurfaceId) ||
                !surfaceIds.Add(candidate.SurfaceId))
            {
                candidate.SurfaceId = CreateUniqueId(surfaceIds);
                changed = true;
            }

            var normalizedMembers = candidate.MemberIds
                .Where(validWidgetIds.Contains)
                .Where(claimedMemberIds.Add)
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumMemberCount)
                .ToList();
            if (!candidate.MemberIds.SequenceEqual(normalizedMembers, StringComparer.Ordinal))
            {
                candidate.MemberIds = normalizedMembers;
                changed = true;
            }

            if (candidate.MemberIds.Count < 2)
            {
                foreach (string memberId in candidate.MemberIds)
                {
                    claimedMemberIds.Remove(memberId);
                }

                changed = true;
                continue;
            }

            if (!candidate.MemberIds.Contains(candidate.ActiveMemberId, StringComparer.Ordinal))
            {

                candidate.ActiveMemberId = candidate.MemberIds[0];
                changed = true;
            }


            string normalizedNavigationStyle =
                WidgetGroupNavigationStyles.Normalize(
                    candidate.NavigationStyle,
                    allowFollowDefault: true);
            if (!string.Equals(candidate.NavigationStyle, normalizedNavigationStyle, StringComparison.Ordinal))
            {
                candidate.NavigationStyle = normalizedNavigationStyle;
                changed = true;
            }

            string normalizedTitleDisplayMode =
                WidgetGroupTitleDisplayModes.Normalize(
                    candidate.TitleDisplayMode,
                    allowFollowDefault: true);
            if (!string.Equals(
                    candidate.TitleDisplayMode,
                    normalizedTitleDisplayMode,
                    StringComparison.Ordinal))
            {
                candidate.TitleDisplayMode = normalizedTitleDisplayMode;
                changed = true;
            }
            if (candidate.WheelSwitchEnabled is null &&
                normalizedNavigationStyle == WidgetGroupNavigationStyles.Tabs)
            {
                candidate.WheelSwitchEnabled = false;
                changed = true;
            }
            string normalizedChromeValue = WidgetGroupChromePolicy.NormalizePersistedValue(candidate.ChromeMode);
            if (!string.Equals(candidate.ChromeMode, normalizedChromeValue, StringComparison.Ordinal))
            {
                candidate.ChromeMode = normalizedChromeValue;
                changed = true;
            }

            WidgetCollapseBehavior normalizedCollapse = WidgetCollapseBehaviorNames.Normalize(
                candidate.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true);
            string normalizedCollapseValue = WidgetCollapseBehaviorNames.ToSettingValue(normalizedCollapse);
            if (!string.Equals(candidate.CollapseBehavior, normalizedCollapseValue, StringComparison.Ordinal))
            {
                candidate.CollapseBehavior = normalizedCollapseValue;
                changed = true;
            }

            if (!double.IsFinite(candidate.Width) || candidate.Width <= 0)
            {
                candidate.Width = 300;
                changed = true;
            }

            if (!double.IsFinite(candidate.Height) || candidate.Height <= 0)
            {
                candidate.Height = 400;
                changed = true;
            }

            normalizedGroups.Add(candidate);
        }

        if (!settings.WidgetGroups.SequenceEqual(normalizedGroups))
        {
            settings.WidgetGroups = normalizedGroups;
            changed = true;
        }

        return changed;
    }

    private static string CreateUniqueId(HashSet<string> claimedIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString();
        }
        while (!claimedIds.Add(id));

        return id;
    }
}

using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetCollapseMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        WidgetConfig config,
        string defaultBehavior,
        string defaultExpansionDirection,
        LocalizationService localizationService,
        Action<WidgetCollapseBehavior> applyBehavior,
        Action<string?> applyExpansionDirection,
        Action resetCompactWidth)
    {
        WidgetCollapseBehavior selectedBehavior = WidgetCollapseBehaviorNames.GetOverride(config);
        var subItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.CollapseBehavior.Title"),
            Icon = new FontIcon { Glyph = "\uE73F" }
        };

        foreach (WidgetCollapseBehavior behavior in new[]
                 {
                     WidgetCollapseBehavior.System,
                     WidgetCollapseBehavior.Expanded,
                     WidgetCollapseBehavior.Click,
                     WidgetCollapseBehavior.Smart
                 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = GetText(behavior, defaultBehavior, localizationService),
                IsChecked = selectedBehavior == behavior
            };
            item.Click += (_, _) => applyBehavior(behavior);
            subItem.Items.Add(item);
        }

        subItem.Items.Add(new MenuFlyoutSeparator());
        subItem.Items.Add(CreateExpansionDirectionSubItem(
            config,
            defaultExpansionDirection,
            localizationService,
            applyExpansionDirection));
        subItem.Items.Add(new MenuFlyoutSeparator());
        var resetWidthItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Compact.RestoreAutomaticWidth"),
            IsEnabled = config.CompactWidth is not null
        };
        resetWidthItem.Click += (_, _) => resetCompactWidth();
        subItem.Items.Add(resetWidthItem);

        return subItem;
    }

    private static MenuFlyoutSubItem CreateExpansionDirectionSubItem(
        WidgetConfig config,
        string defaultExpansionDirection,
        LocalizationService localizationService,
        Action<string?> applyExpansionDirection)
    {
        string? selectedDirection = WidgetCompactExpansionDirectionSettings.GetOverride(config);
        string normalizedDefault = SettingsService.NormalizeWidgetCompactExpansionDirection(
            defaultExpansionDirection);
        var subItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Settings.Capsule.ExpansionDirection.Title")
        };

        foreach (string? direction in new string?[]
                 {
                     null,
                     SettingsService.WidgetCompactExpansionDirectionAuto,
                     SettingsService.WidgetCompactExpansionDirectionDown,
                     SettingsService.WidgetCompactExpansionDirectionUp
                 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = direction is null
                    ? localizationService.Format(
                        "Widget.CollapseBehavior.SystemWithDefault",
                        localizationService.T(GetExpansionDirectionTextKey(normalizedDefault)))
                    : localizationService.T(GetExpansionDirectionTextKey(direction)),
                IsChecked = string.Equals(selectedDirection, direction, StringComparison.Ordinal)
            };
            item.Click += (_, _) => applyExpansionDirection(direction);
            subItem.Items.Add(item);
        }

        return subItem;
    }

    private static string GetText(
        WidgetCollapseBehavior behavior,
        string defaultBehavior,
        LocalizationService localizationService)
    {
        if (behavior != WidgetCollapseBehavior.System)
        {
            return localizationService.T(GetTextKey(behavior));
        }

        WidgetCollapseBehavior normalizedDefault = WidgetCollapseBehaviorNames.Normalize(
            defaultBehavior,
            WidgetCollapseBehavior.Expanded);
        return localizationService.Format(
            "Widget.CollapseBehavior.SystemWithDefault",
            localizationService.T(GetTextKey(normalizedDefault)));
    }

    private static string GetTextKey(WidgetCollapseBehavior behavior)
    {
        return behavior switch
        {
            WidgetCollapseBehavior.System => "Widget.CollapseBehavior.System",
            WidgetCollapseBehavior.Expanded => "Widget.CollapseBehavior.Expanded",
            WidgetCollapseBehavior.Smart => "Widget.CollapseBehavior.Smart",
            _ => "Widget.CollapseBehavior.Click"
        };
    }

    private static string GetExpansionDirectionTextKey(string direction)
    {
        return SettingsService.NormalizeWidgetCompactExpansionDirection(direction) switch
        {
            SettingsService.WidgetCompactExpansionDirectionAuto =>
                "Settings.Capsule.ExpansionDirection.Auto",
            SettingsService.WidgetCompactExpansionDirectionUp =>
                "Settings.Capsule.ExpansionDirection.Up",
            _ => "Settings.Capsule.ExpansionDirection.Down"
        };
    }
}

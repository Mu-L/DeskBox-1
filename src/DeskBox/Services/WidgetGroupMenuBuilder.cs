using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetGroupMenuBuilder
{
    public static void Append(
        MenuFlyout flyout,
        WidgetConfig config,
        WidgetManager? widgetManager,
        LocalizationService localizationService)
    {
        if (widgetManager is null ||
            !widgetManager.IsWidgetGroupingEnabled)
        {
            return;
        }

        WidgetGroupPresentation? group = widgetManager.GetWidgetGroupPresentation(config.Id);
        IReadOnlyList<WidgetGroupJoinTarget> targets =
            widgetManager.GetWidgetGroupJoinTargets(config.Id);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var joinItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.Join"),
            Icon = new FontIcon { Glyph = "\uE8A1" },
            IsEnabled = targets.Count > 0
        };
        foreach (WidgetGroupJoinTarget target in targets)
        {
            string targetText = target.MemberCount > 1
                ? localizationService.Format(
                    "Widget.Group.TargetWithCount",
                    target.DisplayName,
                    target.MemberCount)
                : target.DisplayName;
            if (!target.CanJoin &&
                !string.IsNullOrWhiteSpace(target.RejectionReasonKey))
            {
                targetText += $" · {localizationService.T(target.RejectionReasonKey)}";
            }

            var targetItem = new MenuFlyoutItem
            {
                Text = targetText,
                IsEnabled = target.CanJoin
            };
            targetItem.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.MergeWidgetsAsync(config.Id, target.TargetWidgetId),
                $"merge source={config.Id} target={target.TargetWidgetId}");
            joinItem.Items.Add(targetItem);
        }
        flyout.Items.Add(joinItem);

        if (group is null)
        {
            return;
        }

        var groupControlMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.Control"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };

        var navigationMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T(
                "Widget.Group.NavigationStyle"),
            Icon = new FontIcon { Glyph = "\uE8A1" }
        };
        AddNavigationItem(
            WidgetGroupNavigationStyles.FollowDefault,
            "Widget.Group.Navigation.FollowDefault");
        AddNavigationItem(
            WidgetGroupNavigationStyles.Stack,
            "Widget.Group.Navigation.Combined");
        AddNavigationItem(
            WidgetGroupNavigationStyles.Tabs,
            "Widget.Group.Navigation.Tabs");
        groupControlMenu.Items.Add(navigationMenu);

        void AddNavigationItem(string style, string textKey)
        {
            string? configured =
                widgetManager.GetWidgetGroupNavigationStyle(config.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(textKey),
                IsChecked =
                    string.Equals(
                        configured,
                        style,
                        StringComparison.Ordinal) ||
                    style == WidgetGroupNavigationStyles.Stack &&
                    WidgetGroupNavigationStyles.Normalize(
                        configured,
                        allowFollowDefault: true) ==
                    WidgetGroupNavigationStyles.Auto
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupNavigationStyleAsync(
                    config.Id,
                    style),
                $"navigation-style member={config.Id} style={style}");
            navigationMenu.Items.Add(item);
        }

        var titleStyleMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.TitleDisplayMode"),
            Icon = new FontIcon { Glyph = "\uE8AB" }
        };
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.FollowDefault,
            "Widget.Group.TitleDisplay.FollowDefault");
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.IconAndText,
            "Widget.Group.TitleDisplay.IconAndText");
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.IconOnly,
            "Widget.Group.TitleDisplay.IconOnly");
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.TextOnly,
            "Widget.Group.TitleDisplay.TextOnly");
        groupControlMenu.Items.Add(titleStyleMenu);

        void AddTitleStyleItem(string style, string textKey)
        {
            string? configuredStyle =
                widgetManager.GetWidgetGroupTitleDisplayMode(config.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(textKey),
                IsChecked = string.Equals(
                    configuredStyle,
                    style,
                    StringComparison.Ordinal)
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupTitleDisplayModeAsync(
                    config.Id,
                    style),
                $"title-style member={config.Id} style={style}");
            titleStyleMenu.Items.Add(item);
        }

        var wheelMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.WheelSwitch"),
            Icon = new FontIcon { Glyph = "\uE7C2" }
        };
        AddWheelItem(
            value: null,
            "Widget.Group.TitleDisplay.FollowDefault");
        AddWheelItem(
            value: true,
            "Common.On");
        AddWheelItem(
            value: false,
            "Common.Off");
        groupControlMenu.Items.Add(wheelMenu);

        void AddWheelItem(bool? value, string textKey)
        {
            bool? configured =
                widgetManager.GetWidgetGroupWheelSwitchEnabled(config.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(textKey),
                IsChecked = configured == value
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupWheelSwitchEnabledAsync(
                    config.Id,
                    value),
                $"wheel-switch member={config.Id} value={value}");
            wheelMenu.Items.Add(item);
        }

        var dissolveItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Group.Dissolve"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        dissolveItem.Click += async (_, _) => await TryExecuteAsync(
            () => widgetManager.DissolveWidgetGroupContainingAsync(config.Id),
            $"dissolve member={config.Id}");
        groupControlMenu.Items.Add(new MenuFlyoutSeparator());
        groupControlMenu.Items.Add(dissolveItem);

        var removeItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Group.RemoveCurrent"),
            Icon = new FontIcon { Glyph = "\uE8D9" }
        };
        removeItem.Click += async (_, _) => await TryExecuteAsync(
            () => widgetManager.RemoveWidgetFromGroupAsync(config.Id, revealStandalone: true),
            $"remove member={config.Id}");
        groupControlMenu.Items.Add(removeItem);
        flyout.Items.Add(groupControlMenu);
    }

    private static async Task TryExecuteAsync(Func<Task<bool>> operation, string description)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Menu operation failed {description}: {ex}");
        }
    }
}

using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetChromeMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        LocalizationService localizationService,
        WidgetManager? widgetManager,
        Action<WidgetChromeMode> applyMode)
    {
        bool grouped = widgetManager?.IsWidgetGrouped(config.Id) == true;
        WidgetChromeMode selectedMode = grouped
            ? widgetManager!.GetWidgetGroupChromeMode(config.Id) ??
              WidgetChromeMode.Standard
            : WidgetChromeModeNames.GetOverrideMode(config);
        var subItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.ChromeMode.Title"),
            Icon = new FontIcon { Glyph = "\uE771" }
        };

        foreach (var mode in new[]
                 {
                     WidgetChromeMode.System,
                     WidgetChromeMode.Standard,
                     WidgetChromeMode.Compact,
                     WidgetChromeMode.Overlay,
                     WidgetChromeMode.Hidden
                 })
        {
            if (!grouped &&
                mode == WidgetChromeMode.Hidden &&
                !descriptor.CanHideChrome)
            {
                continue;
            }

            if (!grouped &&
                mode == WidgetChromeMode.Overlay &&
                !descriptor.CanUseOverlayChrome)
            {
                continue;
            }

            bool isEnabled =
                !grouped ||
                WidgetGroupChromePolicy.IsSupportedGroupMode(mode);
            string text = localizationService.T(GetTextKey(mode));
            if (!isEnabled)
            {
                text += $" · {localizationService.T("Widget.Group.ChromeLocked")}";
            }

            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = selectedMode == mode,
                IsEnabled = isEnabled
            };
            if (!isEnabled)
            {
                ToolTipService.SetToolTip(
                    item,
                    localizationService.T("Widget.Group.ChromeLocked"));
            }
            item.Click += (_, _) => applyMode(mode);
            subItem.Items.Add(item);
        }

        return subItem;
    }

    private static string GetTextKey(WidgetChromeMode mode)
    {
        return mode switch
        {
            WidgetChromeMode.System => "Widget.ChromeMode.System",
            WidgetChromeMode.Compact => "Widget.ChromeMode.Compact",
            WidgetChromeMode.Overlay => "Widget.ChromeMode.Overlay",
            WidgetChromeMode.Hidden => "Widget.ChromeMode.Hidden",
            _ => "Widget.ChromeMode.Standard"
        };
    }
}

using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetLockMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        LocalizationService localizationService,
        bool isPositionLocked,
        bool isSizeLocked,
        Action<bool> setPositionLocked,
        Action<bool> setSizeLocked)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Lock.Title"),
            Icon = new FontIcon { Glyph = "\uE72E" }
        };

        var positionItem = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.LockPosition"),
            Icon = new FontIcon { Glyph = "\uE72E" },
            IsChecked = isPositionLocked
        };
        positionItem.Click += (_, _) => setPositionLocked(positionItem.IsChecked);
        menu.Items.Add(positionItem);

        var sizeItem = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.LockSize"),
            Icon = new FontIcon { Glyph = "\uE9CE" },
            IsChecked = isSizeLocked
        };
        sizeItem.Click += (_, _) => setSizeLocked(sizeItem.IsChecked);
        menu.Items.Add(sizeItem);

        return menu;
    }
}

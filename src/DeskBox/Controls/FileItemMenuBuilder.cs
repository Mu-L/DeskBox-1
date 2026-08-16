using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// Host operations used by the shared file-item context menus.
/// The menu shape lives here; each surface only supplies its host-specific
/// actions and selection state.
/// </summary>
public sealed record FileItemMenuActions(
    Func<string, string, MenuFlyoutItem> CreateMenuItem,
    Action<WidgetItem> OpenItem,
    Func<bool, Task> CopySelectionToClipboardAsync,
    Func<WidgetItem, Task> RenameItemAsync,
    Action CopySelectedPathsToClipboard,
    Action<WidgetItem> ShowInExplorer,
    Action<WidgetItem> ShowProperties,
    Func<bool> CanMoveItemsBackToDesktop,
    Func<IReadOnlyList<WidgetItem>, Task> MoveItemsBackToDesktopAsync,
    Func<IReadOnlyList<WidgetItem>, Task> DeleteItemsAsync,
    Func<IReadOnlyList<WidgetItem>> GetSelectedItems,
    bool CanCreateManualStack,
    Action<IReadOnlyList<WidgetItem>> CreateManualStack,
    Func<WidgetItem, bool> CanRemoveFromStack,
    Action<WidgetItem> RemoveFromStack,
    Action ClearSelection);

public static class FileItemMenuBuilder
{
    public static MenuFlyout CreateItemFlyout(
        WidgetItem item,
        FileItemMenuActions actions)
    {
        var flyout = new MenuFlyout();

        MenuFlyoutItem open = actions.CreateMenuItem(
            "Widget.Open",
            "\uE8E5");
        open.Click += (_, _) =>
        {
            flyout.Hide();
            actions.OpenItem(item);
        };
        flyout.Items.Add(open);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem cut = actions.CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(true);
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = actions.CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(false);
        };
        flyout.Items.Add(copy);

        MenuFlyoutItem rename = actions.CreateMenuItem(
            "Common.Rename",
            "\uE8AC");
        rename.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.RenameItemAsync(item);
        };
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem copyPath = actions.CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            actions.CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        MenuFlyoutItem showInExplorer = actions.CreateMenuItem(
            "Widget.ShowInExplorer",
            "\uE838");
        showInExplorer.Click += (_, _) =>
        {
            flyout.Hide();
            actions.ShowInExplorer(item);
        };
        flyout.Items.Add(showInExplorer);

        MenuFlyoutItem properties = actions.CreateMenuItem(
            "Common.Properties",
            "\uE946");
        properties.Click += (_, _) =>
        {
            flyout.Hide();
            actions.ShowProperties(item);
        };
        flyout.Items.Add(properties);

        if (actions.CanRemoveFromStack(item))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem removeFromStack = actions.CreateMenuItem(
                "Widget.Stack.RemoveItem",
                "\uE8FB");
            removeFromStack.Click += (_, _) =>
            {
                flyout.Hide();
                actions.RemoveFromStack(item);
            };
            flyout.Items.Add(removeFromStack);
        }

        if (actions.CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem moveBack = actions.CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems());
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = actions.CreateMenuItem(
            "Widget.MoveToRecycleBin",
            "\uE74D");
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.DeleteItemsAsync(actions.GetSelectedItems());
        };
        flyout.Items.Add(delete);

        return flyout;
    }

    public static MenuFlyout CreateMultiSelectionFlyout(
        FileItemMenuActions actions)
    {
        var flyout = new MenuFlyout();

        if (actions.CanCreateManualStack)
        {
            MenuFlyoutItem startStack = actions.CreateMenuItem(
                "Widget.Stack.Start",
                "\uE8B7");
            startStack.Click += (_, _) =>
            {
                flyout.Hide();
                actions.CreateManualStack(actions.GetSelectedItems());
                actions.ClearSelection();
            };
            flyout.Items.Add(startStack);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        MenuFlyoutItem cut = actions.CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(true);
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = actions.CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(false);
        };
        flyout.Items.Add(copy);

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem copyPath = actions.CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            actions.CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        if (actions.CanMoveItemsBackToDesktop())
        {
            MenuFlyoutItem moveBack = actions.CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems());
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = actions.CreateMenuItem(
            "Widget.MoveToRecycleBin",
            "\uE74D");
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.DeleteItemsAsync(actions.GetSelectedItems());
        };
        flyout.Items.Add(delete);

        return flyout;
    }
}

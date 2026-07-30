using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private enum ItemVisualState
    {
        Normal,
        Hover,
        Pressed
    }

    private readonly HashSet<Border> _itemSurfaces = [];

    private void ApplySelectionRectangleAppearance()
    {
        bool isDark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        SelectionRectangle.Background = new SolidColorBrush(
            WithAlpha(accent, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(
            WithAlpha(accent, isDark ? (byte)0xD8 : (byte)0xCC));
        SelectionRectangle.BorderThickness = new Thickness(1);
        SelectionRectangle.CornerRadius = new CornerRadius(0);
        SelectionRectangle.Opacity = 1;
    }

    private void ItemSurface_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            _itemSurfaces.Add(border);
            ApplyItemSurfaceVisual(border, ItemVisualState.Normal);
        }
    }

    private void ItemSurface_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            _itemSurfaces.Remove(border);
        }
    }

    private void ItemSurface_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyItemSurfaceVisual(border, ItemVisualState.Hover);
        }
    }

    private void ItemSurface_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyItemSurfaceVisual(border, ItemVisualState.Normal);
        }
    }

    private void ItemSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not Border border ||
            border.DataContext is not WidgetItem item ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ListViewBase listView = GetActiveItemsView();
        ClearOtherWidgetSelections();
        if (!Win32Helper.IsKeyPressed(
                Windows.System.VirtualKey.Shift))
        {
            if (Win32Helper.IsKeyPressed(
                    Windows.System.VirtualKey.Control))
            {
                if (!listView.SelectedItems.Contains(item))
                {
                    listView.SelectedItems.Add(item);
                }
            }
            else if (!listView.SelectedItems.Contains(item))
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
            }
        }

        SynchronizeItemSelectionState();
        ApplyItemSurfaceVisual(border, ItemVisualState.Pressed);
    }

    private void ItemSurface_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            Windows.Foundation.Point point =
                e.GetCurrentPoint(border).Position;
            bool inside =
                point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= border.ActualWidth &&
                point.Y <= border.ActualHeight;
            ApplyItemSurfaceVisual(
                border,
                inside
                    ? ItemVisualState.Hover
                    : ItemVisualState.Normal);
        }
    }

    private void StackSurface_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyStackSurfaceVisual(border, hovered: false);
        }
    }

    private void StackSurface_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyStackSurfaceVisual(border, hovered: true);
        }
    }

    private void StackSurface_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyStackSurfaceVisual(border, hovered: false);
        }
    }
    private void StackSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not Border border ||
            border.DataContext is not WidgetStackItem { IsExpanded: false } stack ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            _pressedStack = null;
            return;
        }

        _pressedStack = stack;
        _stackPointerDragStarted = false;
    }

    private void StackSurface_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            _pressedStack = null;
            _stackPointerDragStarted = false;
            return;
        }

        Windows.Foundation.Point point =
            e.GetCurrentPoint(border).Position;
        bool inside =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= border.ActualWidth &&
            point.Y <= border.ActualHeight;
        WidgetStackItem? releasedStack =
            border.DataContext as WidgetStackItem;
        bool shouldToggle =
            inside &&
            !_stackPointerDragStarted &&
            releasedStack is { IsExpanded: false } &&
            ReferenceEquals(_pressedStack, releasedStack);
        _pressedStack = null;
        _stackPointerDragStarted = false;
        ApplyStackSurfaceVisual(
            border,
            hovered: inside);

        if (shouldToggle && releasedStack is not null)
        {
            e.Handled = true;
            ToggleStackFromInput(releasedStack);
        }
    }

    private void StackSurface_DragOver(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border
            {
                DataContext: WidgetStackItem stack
            } border ||
            !TryGetStackDropItems(
                e.DataView,
                stack,
                out _))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearStackMemberDropTarget();
            return;
        }

        SetStackMemberDropTarget(border);
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption =
            _localizationService.Format(
                "Widget.Stack.DragCaption.Add",
                stack.Name);
    }

    private void StackSurface_DragLeave(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        if (ReferenceEquals(
                sender,
                _stackMemberDropTarget))
        {
            ClearStackMemberDropTarget();
        }
    }

    private void StackSurface_Drop(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border
            {
                DataContext: WidgetStackItem stack
            } ||
            !TryGetStackDropItems(
                e.DataView,
                stack,
                out WidgetItem[] items))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearStackMemberDropTarget();
            return;
        }

        ClearStackMemberDropTarget();
        bool added = ViewModel.AddItemsToStack(
            stack.StackKey,
            items);
        e.AcceptedOperation = added
            ? Windows.ApplicationModel.DataTransfer
                .DataPackageOperation.Link
            : Windows.ApplicationModel.DataTransfer
                .DataPackageOperation.None;
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        if (added)
        {
            ClearSelection();
        }
    }

    private bool TryGetStackDropItems(
        Windows.ApplicationModel.DataTransfer.DataPackageView dataView,
        WidgetStackItem targetStack,
        out WidgetItem[] items)
    {
        items = [];
        if (!IsInternalReorderDrag(dataView) ||
            !string.IsNullOrWhiteSpace(
                TryGetString(
                    dataView.Properties,
                    DeskBoxDragData.StackReorderKeyProperty)))
        {
            return false;
        }

        HashSet<string> targetPaths = targetStack.Members
            .Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sourcePaths = GetPackagePaths(dataView)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        items = ViewModel.Items
            .Where(item =>
                sourcePaths.Contains(
                    Path.GetFullPath(item.Path)) &&
                !targetPaths.Contains(
                    Path.GetFullPath(item.Path)))
            .ToArray();
        return items.Length > 0;
    }

    private void SetStackMemberDropTarget(
        Border border)
    {
        if (!ReferenceEquals(
                _stackMemberDropTarget,
                border))
        {
            ClearStackMemberDropTarget();
            _stackMemberDropTarget = border;
        }

        ApplyStackSurfaceDropVisual(border);
    }

    private void ClearStackMemberDropTarget()
    {
        Border? previous = _stackMemberDropTarget;
        _stackMemberDropTarget = null;
        if (previous?.XamlRoot is not null)
        {
            ApplyStackSurfaceVisual(
                previous,
                hovered: false);
        }
    }

    private void ApplyStackSurfaceDropVisual(
        Border border)
    {
        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        border.Background = new SolidColorBrush(
            WithAlpha(
                accent,
                Root.ActualTheme == ElementTheme.Dark
                    ? (byte)0x38
                    : (byte)0x28));
        border.BorderBrush = new SolidColorBrush(
            WithAlpha(accent, 0xD8));
        border.BorderThickness = new Thickness(1);
    }


    private void StackCollapseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WidgetStackItem stack
            })
        {
            ViewModel.SetStackExpanded(stack, false);
        }
    }

    private void SynchronizeItemSelectionState()
    {
        HashSet<WidgetItem> selected = GetActiveItemsView()
            .SelectedItems
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .ToHashSet();
        foreach (WidgetItem item in ViewModel.Items)
        {
            item.IsSelected = selected.Contains(item);
        }

        UpdateItemSurfaceVisuals();
    }

    private void ClearOtherWidgetSelections()
    {
        App.Current.WidgetManager?.ClearSelectionsExcept(WidgetId);
    }

    private void UpdateItemSurfaceVisuals()
    {
        foreach (Border border in _itemSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _itemSurfaces.Remove(border);
                continue;
            }

            ApplyItemSurfaceVisual(border, ItemVisualState.Normal);
        }
    }

    private void ApplyItemSurfaceVisual(
        Border border,
        ItemVisualState state)
    {
        bool isDark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        bool selected =
            border.DataContext is WidgetItem { IsSelected: true };
        bool cut =
            border.DataContext is WidgetItem { IsCut: true };

        Windows.UI.Color background = state switch
        {
            ItemVisualState.Hover when selected =>
                BuildItemSurfaceColor(
                    isDark,
                    accent,
                    selected: true,
                    hovered: true),
            ItemVisualState.Pressed when selected =>
                BuildItemSurfaceColor(
                    isDark,
                    accent,
                    selected: true,
                    hovered: true),
            ItemVisualState.Hover =>
                BuildItemSurfaceColor(
                    isDark,
                    accent,
                    selected: false,
                    hovered: true),
            ItemVisualState.Pressed =>
                BuildPressedSurfaceColor(isDark, accent),
            _ when selected =>
                BuildItemSurfaceColor(
                    isDark,
                    accent,
                    selected: true,
                    hovered: false),
            _ => Colors.Transparent
        };

        border.Background = new SolidColorBrush(background);
        border.BorderBrush = new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(0);
        border.Opacity = cut ? 0.58 : 1;
    }

    private void ApplyStackSurfaceVisual(
        Border border,
        bool hovered)
    {
        border.Background = hovered
            ? ResolveBrush("SubtleFillColorSecondaryBrush")
            : new SolidColorBrush(Colors.Transparent);
        border.BorderBrush = new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(0);
    }

    private static Windows.UI.Color BuildItemSurfaceColor(
        bool isDark,
        Windows.UI.Color accent,
        bool selected,
        bool hovered)
    {
        Windows.UI.Color baseColor = selected
            ? isDark
                ? ColorHelper.FromArgb(0xFF, 0x31, 0x36, 0x3E)
                : ColorHelper.FromArgb(0xFF, 0xF1, 0xF6, 0xFC)
            : isDark
                ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        double accentMix = selected
            ? hovered
                ? isDark ? 0.34 : 0.21
                : isDark ? 0.30 : 0.18
            : isDark ? 0.24 : 0.12;
        double overlayMix = selected
            ? isDark ? 0.08 : 0.05
            : isDark ? 0.04 : 0.02;
        byte alpha = selected
            ? hovered
                ? isDark ? (byte)0xC0 : (byte)0xB8
                : isDark ? (byte)0xA8 : (byte)0xA0
            : isDark ? (byte)0x6A : (byte)0x86;
        return WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accent,
                baseColor,
                accentMix,
                overlayMix),
            alpha);
    }

    private static Windows.UI.Color BuildPressedSurfaceColor(
        bool isDark,
        Windows.UI.Color accent)
    {
        return WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accent,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2D, 0x30, 0x37)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xFA),
                isDark ? 0.24 : 0.15,
                isDark ? 0.10 : 0.16),
            isDark ? (byte)0x48 : (byte)0x54);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accent,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        Windows.UI.Color tinted =
            BlendColors(baseColor, accent, accentMix);
        Windows.UI.Color overlay = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(tinted, overlay, overlayMix);
    }

    private static Windows.UI.Color BlendColors(
        Windows.UI.Color from,
        Windows.UI.Color to,
        double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Blend(byte first, byte second, double mix) =>
            (byte)Math.Clamp(
                Math.Round(first + ((second - first) * mix)),
                0,
                255);
        return ColorHelper.FromArgb(
            Blend(from.A, to.A, amount),
            Blend(from.R, to.R, amount),
            Blend(from.G, to.G, amount),
            Blend(from.B, to.B, amount));
    }

    private static Windows.UI.Color WithAlpha(
        Windows.UI.Color color,
        byte alpha)
    {
        return ColorHelper.FromArgb(
            alpha,
            color.R,
            color.G,
            color.B);
    }
}

using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void ItemsListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel? item = e.Items.OfType<QuickCaptureItemViewModel>().FirstOrDefault();
        _draggedQuickCaptureItemIds.Clear();
        if (item is null)
        {
            _draggedQuickCaptureItemId = null;
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragCanReorder = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
            return;
        }

        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            QuickCaptureDragPackage.ResolveDraggedItems(
                [item],
                GetSelectedQuickCaptureItemsInVisibleOrder());
        bool canReorder = draggedItems.Count == 1 &&
                          !item.IsRecent &&
                          ViewModel.SelectedView is QuickCaptureViewMode.Records or QuickCaptureViewMode.Pinned &&
                          !ViewModel.HasSearchText;
        _draggedQuickCaptureItemIds.AddRange(draggedItems.Select(entry => entry.Id));
        _draggedQuickCaptureItemId = canReorder ? item.Id : null;
        _isInternalQuickCaptureDrag = true;
        _internalQuickCaptureDragCanReorder = canReorder;
        _internalQuickCaptureDragView = canReorder ? ViewModel.SelectedView : null;
        // VisibleItemsSource is a fixed-size AOT projection. Row drop handlers
        // persist manual ordering without asking WinUI to mutate the array.
        ItemsListView.CanReorderItems = false;

        try
        {
            if (!QuickCaptureDragPackage.TryPrepare(
                    e.Data,
                    draggedItems,
                    _localizationService))
            {
                _draggedQuickCaptureItemIds.Clear();
                _draggedQuickCaptureItemId = null;
                _isInternalQuickCaptureDrag = false;
                _internalQuickCaptureDragCanReorder = false;
                _internalQuickCaptureDragView = null;
                e.Cancel = true;
                return;
            }

            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to start drag: {ex}");
            _draggedQuickCaptureItemId = null;
            _draggedQuickCaptureItemIds.Clear();
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragCanReorder = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
        }
    }

    private void ItemsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _draggedQuickCaptureItemId = null;
        _draggedQuickCaptureItemIds.Clear();
        _internalQuickCaptureDragView = null;
        _internalQuickCaptureDragCanReorder = false;
        ItemsListView.CanReorderItems = false;
        DispatcherQueue.TryEnqueue(() => _isInternalQuickCaptureDrag = false);
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            _draggedQuickCaptureItemIds.Count == 0 ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(
                GetDraggedQuickCaptureItems(),
                target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = target == QuickCaptureViewMode.Pinned
            ? _localizationService.T("QuickCapture.DropTab.Pin")
            : _localizationService.T("QuickCapture.DropTab.Records");
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        if (!_isInternalQuickCaptureDrag ||
            _draggedQuickCaptureItemIds.Count == 0 ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            GetDraggedQuickCaptureItems() is not { Count: > 0 } draggedItems ||
            !ViewModel.CanApplyTabDrop(draggedItems, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            int changedCount = await ViewModel.ApplyTabDropAsync(draggedItems, target);
            e.AcceptedOperation = changedCount > 0 ? DataPackageOperation.Move : DataPackageOperation.None;
            if (changedCount > 0)
            {
                SelectView(target);
                ShowStatusToast(_localizationService.T(target == QuickCaptureViewMode.Pinned
                    ? "QuickCapture.DropTab.Pinned"
                    : "QuickCapture.DropTab.Saved"));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryGetQuickCaptureTabTarget(string tag, out QuickCaptureViewMode target)
    {
        target = tag switch
        {
            "Pinned" => QuickCaptureViewMode.Pinned,
            "Records" => QuickCaptureViewMode.Records,
            _ => QuickCaptureViewMode.Recent
        };
        return target != QuickCaptureViewMode.Recent;
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetDraggedQuickCaptureItems()
    {
        HashSet<string> draggedIds = _draggedQuickCaptureItemIds.ToHashSet(
            StringComparer.Ordinal);
        return ViewModel.Items
            .Where(item => draggedIds.Contains(item.Id))
            .ToList();
    }

    private void ApplyItemMaterialSurface(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<Border>(itemRoot, "ItemMaterialBackground") is not { } surface)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        QuickCaptureAppearancePreset preset =
            QuickCaptureAppearancePolicy.ResolveListPreset(
                item.AppearancePreset,
                item.IsRecent);
        surface.Background = GetOrUpdateSolidColorBrush(surface.Background, GetMaterialColor(preset, isDark));
        surface.BorderBrush = preset == QuickCaptureAppearancePreset.Default
            ? GetMaterialBorderBrush(preset, isDark)
            : GetOrUpdateSolidColorBrush(
                surface.BorderBrush,
                isDark
                    ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private static Brush GetMaterialBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return new SolidColorBrush(GetMaterialColor(preset, isDark));
    }

    private static Windows.UI.Color GetMaterialColor(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return (preset, isDark) switch
        {
            (QuickCaptureAppearancePreset.Paper, true) => ColorHelper.FromArgb(0xB8, 0x3A, 0x36, 0x30),
            (QuickCaptureAppearancePreset.Paper, false) => ColorHelper.FromArgb(0xEC, 0xFA, 0xF5, 0xEA),
            (QuickCaptureAppearancePreset.StickyYellow, true) => ColorHelper.FromArgb(0xB8, 0x4A, 0x40, 0x25),
            (QuickCaptureAppearancePreset.StickyYellow, false) => ColorHelper.FromArgb(0xEC, 0xFF, 0xF0, 0xB3),
            (QuickCaptureAppearancePreset.Rose, true) => ColorHelper.FromArgb(0xB8, 0x47, 0x2E, 0x38),
            (QuickCaptureAppearancePreset.Rose, false) => ColorHelper.FromArgb(0xEC, 0xFC, 0xE3, 0xEA),
            (QuickCaptureAppearancePreset.Mint, true) => ColorHelper.FromArgb(0xB8, 0x28, 0x42, 0x35),
            (QuickCaptureAppearancePreset.Mint, false) => ColorHelper.FromArgb(0xEC, 0xDD, 0xF3, 0xE3),
            (QuickCaptureAppearancePreset.MistBlue, true) => ColorHelper.FromArgb(0xB8, 0x2B, 0x3D, 0x53),
            (QuickCaptureAppearancePreset.MistBlue, false) => ColorHelper.FromArgb(0xEC, 0xDF, 0xEC, 0xF8),
            _ => Colors.Transparent
        };
    }

    private Brush GetMaterialBorderBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        if (preset == QuickCaptureAppearancePreset.Default)
        {
            return GetBrushResourceOrFallback(
                "CardStrokeColorDefaultBrush",
                isDark
                    ? ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }

        return new SolidColorBrush(isDark
            ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private void RefreshItemMaterialSurfaces()
    {
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
            {
                ApplyItemMaterialSurface(container, item);
            }
        }

        if (DetailPage.Visibility == Visibility.Visible)
        {
            ApplyDetailMaterialSurface();
        }
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, true);
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, false);
        SetItemHoverState(sender as DependencyObject, false);
    }

    private void QuickCaptureItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: QuickCaptureItemViewModel
            } itemRoot)
        {
            return;
        }

        if (_isInternalQuickCaptureDrag)
        {
            if (!_internalQuickCaptureDragCanReorder ||
                string.IsNullOrWhiteSpace(_draggedQuickCaptureItemId))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool insertAfter = e.GetPosition(itemRoot).Y >= itemRoot.ActualHeight / 2;
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsGlyphVisible = true;
            SetItemHoverState(itemRoot, true);
            SetItemReorderDropState(itemRoot, active: true, insertAfter);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation =
            DeskBoxDragData.GetFileAssociationOperation(e.DataView);
        e.DragUIOverride.IsGlyphVisible = true;
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_DragLeave(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag || DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetItemHoverState(sender as DependencyObject, false);
            SetItemReorderDropState(
                sender as DependencyObject,
                active: false,
                insertAfter: false);
        }
    }

    private async void QuickCaptureItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: QuickCaptureItemViewModel item
            } itemRoot)
        {
            return;
        }

        if (_isInternalQuickCaptureDrag)
        {
            await DropQuickCaptureItemAtRowAsync(itemRoot, item, e);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        SetItemHoverState(sender as DependencyObject, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(item, batch.Files);
            e.AcceptedOperation = updated is null
                ? DataPackageOperation.None
                : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
            if (updated is not null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to attach dropped files: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static void SetItemActionButtonsVisible(DependencyObject? itemRoot, bool isVisible)
    {
        if (itemRoot is null ||
            FindVisualChild<Border>(itemRoot, "ItemActionHost") is not { } actions)
        {
            return;
        }

        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
        ApplyActionButtonHostTheme(actions, itemRoot);
        ElementCompositionPreview.GetElementVisual(actions).StopAnimation("Offset");
    }

    private static void ApplyActionButtonHostTheme(Border actions, DependencyObject itemRoot)
    {
        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        actions.Background = new SolidColorBrush(WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x1E, 0x23, 0x29) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.08,
                overlayMix: isDark ? 0.03 : 0.02),
            0xFF));
        actions.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x4A : (byte)0x30));
        actions.BorderThickness = new Thickness(1);

        foreach (var button in FindVisualChildren<Button>(actions))
        {
            ApplyActionButtonTheme(button, isDark, accentColor);
        }
    }

    private static void ApplyActionButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var foreground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF2 : (byte)0xE2));

        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private void SetItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "ItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "ImagePreviewBorder") is { } imageBorder)
        {
            imageBorder.BorderBrush = isHovered
                ? new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xE0 : (byte)0xCC))
                : GetBrushResourceOrFallback(
                    "CardStrokeColorDefaultBrush",
                    isDark
                        ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                        : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
    }

    private void SetItemActionButtonsVisibleForItem(object? item, bool isVisible)
    {
        if (item is null)
        {
            return;
        }

        if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
        {
            SetItemActionButtonsVisible(container, isVisible);
            return;
        }

        if (isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ItemsListView.ContainerFromItem(item) is DependencyObject queuedContainer)
                {
                    SetItemActionButtonsVisible(queuedContainer, true);
                }
            });
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await TogglePinnedWithFeedbackAsync(item);
        }
    }

    private async Task DropQuickCaptureItemAtRowAsync(
        FrameworkElement itemRoot,
        QuickCaptureItemViewModel targetItem,
        DragEventArgs e)
    {
        string? draggedItemId = _draggedQuickCaptureItemId;
        QuickCaptureViewMode? dragView = _internalQuickCaptureDragView;
        if (!_internalQuickCaptureDragCanReorder ||
            string.IsNullOrWhiteSpace(draggedItemId) ||
            dragView != ViewModel.SelectedView)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        bool insertAfter = e.GetPosition(itemRoot).Y >= itemRoot.ActualHeight / 2;
        int targetIndex = QuickCaptureDragPackage.ResolveManualDropTargetIndex(
            ViewModel.Items,
            draggedItemId,
            targetItem.Id,
            insertAfter);
        if (targetIndex < 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        SetItemHoverState(itemRoot, false);
        SetItemReorderDropState(itemRoot, active: false, insertAfter: false);
        var deferral = e.GetDeferral();
        try
        {
            QuickCaptureItemViewModel? draggedItem = ViewModel.Items.FirstOrDefault(
                entry => string.Equals(
                    entry.Id,
                    draggedItemId,
                    StringComparison.Ordinal));
            bool persisted = draggedItem is not null &&
                (dragView == QuickCaptureViewMode.Pinned
                    ? await ViewModel.MovePinnedItemToIndexAsync(
                        draggedItem,
                        targetIndex)
                    : await ViewModel.MoveItemAsync(draggedItem, targetIndex));
            await ViewModel.RefreshItemsAsync();
            e.AcceptedOperation = persisted
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Reorder failed: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            await ViewModel.RefreshItemsAsync();
        }
        finally
        {
            SetItemHoverState(itemRoot, false);
            SetItemReorderDropState(
                itemRoot,
                active: false,
                insertAfter: false);
            deferral.Complete();
        }
    }

    private static void SetItemReorderDropState(
        DependencyObject? itemRoot,
        bool active,
        bool insertAfter)
    {
        if (itemRoot is null ||
            FindVisualChild<Border>(itemRoot, "ItemMaterialBackground") is not
                { } materialBorder)
        {
            return;
        }

        materialBorder.BorderBrush = active
            ? new SolidColorBrush(
                App.Current.ThemeService?.GetEffectiveAccentColor() ??
                AccentColorHelper.DefaultAccentColor)
            : new SolidColorBrush(Colors.Transparent);
        materialBorder.BorderThickness = active
            ? insertAfter
                ? new Thickness(0, 0, 0, 2)
                : new Thickness(0, 2, 0, 0)
            : new Thickness(0);
    }

    private async Task<bool> TogglePinnedWithFeedbackAsync(
        QuickCaptureItemViewModel item)
    {
        bool willPin = item.IsRecent || !item.IsPinned;
        bool changed = item.IsRecent
            ? await ViewModel.PinRecentItemAsync(item)
            : await ViewModel.TogglePinnedAsync(item);
        if (changed)
        {
            ShowStatusToast(_localizationService.T(willPin
                ? "QuickCapture.PinnedSuccess"
                : "QuickCapture.UnpinnedSuccess"));
        }

        return changed;
    }

    private async void SaveRecentItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.SaveRecentItemAsync(item);
        }
    }

    private async void MovePinnedItemUpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, -1);
        }
    }

    private async void MovePinnedItemDownButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, 1);
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuickCaptureItemViewModel item })
        {
            await DeleteItemWithUndoAsync(item);
        }
    }

    private async Task DeleteItemWithUndoAsync(QuickCaptureItemViewModel item)
    {
        var snapshot = await ViewModel.DeleteItemAsync(item);
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = snapshot;
        ShowStatusToast(
            _localizationService.T("QuickCapture.Deleted"),
            _localizationService.T("Common.Undo"),
            StatusToastUndoMs);
    }

    private async Task DeleteSelectedQuickCaptureItemsAsync(
        IReadOnlyList<string> selectedIds,
        bool isRecent)
    {
        if (selectedIds.Count == 0)
        {
            return;
        }

        ClearQuickCaptureCopySelection();
        var deletedItems = await ViewModel.DeleteItemsAsync(selectedIds, isRecent);
        if (deletedItems.Count > 0)
        {
            ShowStatusToast(_localizationService.Format("QuickCapture.DeletedCount", deletedItems.Count));
        }
    }

}

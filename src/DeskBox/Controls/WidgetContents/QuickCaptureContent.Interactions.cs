using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private readonly List<string> _draggedQuickCaptureItemIds = [];
    private QuickCaptureItemViewModel[] _pendingPointerDragItems = [];
    private string? _draggedQuickCaptureItemId;
    private QuickCaptureViewMode? _internalQuickCaptureDragView;
    private bool _isInternalQuickCaptureDrag;
    private bool _internalQuickCaptureDragCanReorder;
    private bool _quickCaptureTabDropHandled;

    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Point _selectionStartPoint;
    private Point _selectionCurrentPoint;
    private List<QuickCaptureItemViewModel> _selectionSnapshot = [];
    private List<QuickCaptureSelectionHitTestItem> _selectionHitTestItems = [];

    private void QuickCaptureItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuickCaptureItemViewModel item } ||
            !e.GetCurrentPoint(ItemsList).Properties.IsLeftButtonPressed ||
            !ItemsList.SelectedItems.Contains(item))
        {
            _pendingPointerDragItems = [];
            return;
        }

        _pendingPointerDragItems = ItemsList.SelectedItems
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
    }

    private void QuickCaptureItem_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        _pendingPointerDragItems = [];

    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel[] eventItems = e.Items
            .OfType<QuickCaptureItemViewModel>()
            .ToArray();
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
            _pendingPointerDragItems.Length > 1
                ? _pendingPointerDragItems
                : GetBulkSelectedItems();
        _pendingPointerDragItems = [];
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems =
            QuickCaptureDragPackage.ResolveDraggedItems(eventItems, selectedItems);
        QuickCaptureItemViewModel? anchor = draggedItems.FirstOrDefault();
        bool canReorder = draggedItems.Count == 1 &&
            anchor is { IsRecent: false } &&
            ViewModel.SelectedView is QuickCaptureViewMode.Records or QuickCaptureViewMode.Pinned &&
            !ViewModel.HasSearchText;

        if (!QuickCaptureDragPackage.TryPrepare(e.Data, draggedItems, _localizationService))
        {
            e.Cancel = true;
            ResetInternalQuickCaptureDrag();
            return;
        }

        _draggedQuickCaptureItemIds.Clear();
        _draggedQuickCaptureItemIds.AddRange(draggedItems.Select(item => item.Id));
        _draggedQuickCaptureItemId = canReorder ? anchor?.Id : null;
        _internalQuickCaptureDragView = canReorder ? ViewModel.SelectedView : null;
        _internalQuickCaptureDragCanReorder = canReorder;
        _quickCaptureTabDropHandled = false;
        _isInternalQuickCaptureDrag = true;
        ItemsList.CanReorderItems = canReorder;
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private async void ItemsList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        string? itemId = _draggedQuickCaptureItemId;
        QuickCaptureViewMode? dragView = _internalQuickCaptureDragView;
        bool canReorder = _internalQuickCaptureDragCanReorder;
        bool tabDropHandled = _quickCaptureTabDropHandled;
        ResetInternalQuickCaptureDrag();
        ItemsList.CanReorderItems = true;
        DispatcherQueue.TryEnqueue(() => _isInternalQuickCaptureDrag = false);

        if (string.IsNullOrWhiteSpace(itemId) || tabDropHandled || !canReorder)
        {
            return;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        int targetIndex = ViewModel.Items.IndexOf(item);
        if (dragView == QuickCaptureViewMode.Pinned)
        {
            await ViewModel.MovePinnedItemToIndexAsync(item, targetIndex);
        }
        else
        {
            await ViewModel.MoveItemAsync(item, targetIndex);
        }
    }

    private void QuickCaptureTab_DragOver(object sender, DragEventArgs e)
    {
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems = GetDraggedQuickCaptureItems();
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(draggedItems, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = target == QuickCaptureViewMode.Pinned
            ? "松开以置顶"
            : "松开以保存到随记";
    }

    private async void QuickCaptureTab_Drop(object sender, DragEventArgs e)
    {
        IReadOnlyList<QuickCaptureItemViewModel> draggedItems = GetDraggedQuickCaptureItems();
        if (!_isInternalQuickCaptureDrag ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetQuickCaptureTabTarget(tag, out QuickCaptureViewMode target) ||
            !ViewModel.CanApplyTabDrop(draggedItems, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        _quickCaptureTabDropHandled = true;
        var deferral = e.GetDeferral();
        try
        {
            int changedCount = await ViewModel.ApplyTabDropAsync(draggedItems, target);
            e.AcceptedOperation = changedCount > 0
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
            if (changedCount > 0)
            {
                SwitchSection(target);
                RaiseFeedback(
                    target == QuickCaptureViewMode.Pinned ? "随记已置顶" : "已保存到随记",
                    WidgetFeedbackSeverity.Success,
                    "quick-capture-tab-drop");
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetDraggedQuickCaptureItems()
    {
        HashSet<string> ids = _draggedQuickCaptureItemIds.ToHashSet(StringComparer.Ordinal);
        return ViewModel.Items.Where(item => ids.Contains(item.Id)).ToArray();
    }

    private static bool TryGetQuickCaptureTabTarget(
        string tag,
        out QuickCaptureViewMode target)
    {
        target = tag switch
        {
            "Pinned" => QuickCaptureViewMode.Pinned,
            "Records" => QuickCaptureViewMode.Records,
            _ => QuickCaptureViewMode.Recent
        };
        return target != QuickCaptureViewMode.Recent;
    }

    private void ResetInternalQuickCaptureDrag()
    {
        _draggedQuickCaptureItemIds.Clear();
        _draggedQuickCaptureItemId = null;
        _internalQuickCaptureDragView = null;
        _internalQuickCaptureDragCanReorder = false;
        _quickCaptureTabDropHandled = false;
    }

    private void QuickCaptureItem_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag || !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DeskBoxDragData.GetFileAssociationOperation(e.DataView);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "关联到这条随记";
        SetItemActionsVisible(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_DragLeave(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetItemActionsVisible(sender as DependencyObject, false);
        }
    }

    private async void QuickCaptureItem_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            sender is not FrameworkElement { DataContext: QuickCaptureItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        SetItemActionsVisible(sender as DependencyObject, false);
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
                if (_selectedItem?.Id == updated.Id)
                {
                    _selectedItem = updated;
                    RenderReadingSurface();
                }
                RaiseFeedback("文件已关联", WidgetFeedbackSeverity.Success, "quick-capture-item-drop");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureContent] Item drop failed: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            RaiseFeedback("文件关联失败", WidgetFeedbackSeverity.Error, "quick-capture-item-drop-error");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetItemActionsVisible(sender as DependencyObject, true);

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetItemActionsVisible(sender as DependencyObject, false);

    private static void SetItemActionsVisible(DependencyObject? root, bool visible)
    {
        if (FindNamedDescendant<Border>(root, "ItemActionHost") is not { } host)
        {
            return;
        }

        host.Opacity = visible ? 1 : 0;
        host.IsHitTestVisible = visible;
        if (FindNamedDescendant<Border>(root, "ItemHoverBackground") is { } hover)
        {
            hover.Opacity = visible ? 1 : 0;
        }
    }

    private void ItemMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QuickCaptureItemViewModel item } anchor)
        {
            CreateItemFlyout(item).ShowAt(anchor);
        }
    }

    private void QuickCaptureItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuickCaptureItemViewModel item } anchor)
        {
            IReadOnlyList<QuickCaptureItemViewModel> selection = GetBulkSelectedItems();
            MenuFlyout flyout = _isBulkSelectionMode && selection.Count > 1 && selection.Contains(item)
                ? CreateMultiItemFlyout(selection)
                : CreateItemFlyout(item);
            flyout.ShowAt(anchor, e.GetPosition(anchor));
            e.Handled = true;
        }
    }

    private MenuFlyout CreateItemFlyout(QuickCaptureItemViewModel item)
    {
        MenuFlyout flyout = CreateCompactMenuFlyout();
        MenuFlyoutItem copy = CreateCompactMenuCommand(
            _localizationService.T("Common.Copy"),
            "\uE8C8");
        copy.Click += (_, _) => CopyItemsToClipboard([item]);
        flyout.Items.Add(copy);

        if (item.IsRecent)
        {
            MenuFlyoutItem save = CreateCompactMenuCommand(
                _localizationService.T("QuickCapture.SaveToRecords"),
                "\uE74E");
            save.Click += async (_, _) =>
            {
                await ViewModel.SaveRecentItemAsync(item);
                RaiseFeedback(
                    "已保存为随记",
                    WidgetFeedbackSeverity.Success,
                    "quick-capture-recent-saved");
            };
            flyout.Items.Add(save);

            MenuFlyoutItem pinRecent = CreateCompactMenuCommand(
                _localizationService.T("QuickCapture.PinToRecords"),
                "\uE718");
            pinRecent.Click += async (_, _) =>
            {
                await ViewModel.PinRecentItemAsync(item);
                RaiseFeedback(
                    "已保存并置顶",
                    WidgetFeedbackSeverity.Success,
                    "quick-capture-recent-pinned");
            };
            flyout.Items.Add(pinRecent);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateDeleteCommand(item));
            return flyout;
        }

        MenuFlyoutItem edit = CreateCompactMenuCommand(
            _localizationService.T("QuickCapture.Edit"),
            "\uE70F");
        edit.Click += async (_, _) => await OpenItemAsync(item, edit: true);
        flyout.Items.Add(edit);

        MenuFlyoutItem pin = CreateCompactMenuCommand(
            _localizationService.T(item.IsPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin"),
            item.IsPinned ? "\uE840" : "\uE718");
        pin.Click += async (_, _) => await ViewModel.TogglePinnedAsync(item);
        flyout.Items.Add(pin);
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (item.Type != QuickCaptureItemType.Image)
        {
            MenuFlyoutItem notepad = CreateCompactMenuCommand("记事本编辑", "\uE70F");
            notepad.Click += async (_, _) => await OpenTextInNotepadAsync(item);
            flyout.Items.Add(notepad);
        }

        flyout.Items.Add(CreateAppearanceFlyout([item], flyout));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateDeleteCommand(item));
        return flyout;
    }

    private MenuFlyout CreateMultiItemFlyout(IReadOnlyList<QuickCaptureItemViewModel> items)
    {
        MenuFlyout flyout = CreateCompactMenuFlyout();
        MenuFlyoutItem copy = CreateCompactMenuCommand($"复制 {items.Count} 项", "\uE8C8");
        copy.Click += (_, _) => CopyItemsToClipboard(items);
        flyout.Items.Add(copy);

        bool allRecent = items.All(item => item.IsRecent);
        if (!allRecent)
        {
            bool shouldPin = !items.All(item => item.IsPinned);
            MenuFlyoutItem pin = CreateCompactMenuCommand(
                shouldPin ? "置顶" : "取消置顶",
                shouldPin ? "\uE718" : "\uE840");
            pin.Click += async (_, _) => await ViewModel.SetPinnedAsync(
                items.Where(item => !item.IsRecent).Select(item => item.Id),
                shouldPin);
            flyout.Items.Add(pin);
            flyout.Items.Add(CreateAppearanceFlyout(
                items.Where(item => !item.IsRecent).ToArray(),
                flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = CreateCompactMenuCommand(
            _localizationService.T("Common.Delete"),
            "\uE74D");
        delete.Click += async (_, _) => await DeleteItemsWithUndoAsync(items);
        flyout.Items.Add(delete);
        return flyout;
    }

    private MenuFlyout CreateCompactMenuFlyout()
    {
        return new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)Resources["QuickCaptureCompactMenuPresenterStyle"],
            Placement = FlyoutPlacementMode.LeftEdgeAlignedTop
        };
    }

    private MenuFlyoutItem CreateCompactMenuCommand(string text, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = Math.Max(12, ViewModel.ActionIconSize)
            },
            Style = (Style)Resources["QuickCaptureCompactMenuItemStyle"]
        };
    }

    private MenuFlyoutItem CreateDeleteCommand(QuickCaptureItemViewModel item)
    {
        MenuFlyoutItem delete = CreateCompactMenuCommand(
            _localizationService.T("Common.Delete"),
            "\uE74D");
        delete.Click += async (_, _) => await DeleteItemsWithUndoAsync([item]);
        return delete;
    }

    private MenuFlyoutSubItem CreateAppearanceFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> items,
        MenuFlyout owner)
    {
        var appearance = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" },
            Style = (Style)Resources["QuickCaptureCompactMenuSubItemStyle"]
        };

        foreach ((QuickCaptureAppearancePreset preset, string textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var option = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(textKey),
                IsChecked = items.Count > 0 && items.All(item => item.AppearancePreset == preset),
                Style = (Style)Resources["QuickCaptureCompactToggleMenuItemStyle"]
            };
            option.Click += async (_, _) =>
            {
                owner.Hide();
                bool changed = items.Count == 1
                    ? await ViewModel.SetAppearanceAsync(items[0], preset)
                    : await ViewModel.SetAppearanceAsync(items.Select(item => item.Id), preset) > 0;
                if (!changed)
                {
                    RaiseFeedback(
                        "纸张样式未能更新",
                        WidgetFeedbackSeverity.Error,
                        "quick-capture-appearance-failed");
                    return;
                }

                string? selectedId = _selectedItem?.Id;
                await ViewModel.RefreshItemsAsync();
                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    _selectedItem = ViewModel.Items.FirstOrDefault(item => item.Id == selectedId)
                        ?? _selectedItem;
                }
                if (_selectedItem is not null && items.Any(item => item.Id == _selectedItem.Id))
                {
                    ApplyNoteAppearance(preset);
                    RenderReadingSurface();
                }
                RefreshItemMaterialSurfaces();
            };
            appearance.Items.Add(option);
        }

        return appearance;
    }

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsControlPressed() && e.Key == Windows.System.VirtualKey.A)
        {
            ItemsList.SelectAll();
            SetBulkSelectionMode(enable: true, preserveSelection: true);
            e.Handled = true;
            return;
        }

        if (IsControlPressed() && e.Key == Windows.System.VirtualKey.C)
        {
            CopyItemsToClipboard(GetBulkSelectedItems());
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            IReadOnlyList<QuickCaptureItemViewModel> selected = GetBulkSelectedItems();
            if (selected.Count > 0)
            {
                e.Handled = true;
                await DeleteItemsWithUndoAsync(selected);
                SetBulkSelectionMode(enable: false);
            }
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ItemsList.SelectedItems.Count > 0)
        {
            SetBulkSelectionMode(enable: false);
            e.Handled = true;
            return;
        }

        if ((e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.F2) &&
            ItemsList.SelectedItems.Count == 1 &&
            ItemsList.SelectedItem is QuickCaptureItemViewModel item)
        {
            e.Handled = true;
            await OpenItemAsync(item, edit: e.Key == Windows.System.VirtualKey.F2);
        }
    }

    private void CopyItemsToClipboard(IReadOnlyList<QuickCaptureItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        string text = items.Count == 1
            ? QuickCaptureClipboardFormatter.FormatSingle(items[0], _localizationService)
            : QuickCaptureClipboardFormatter.FormatBatch(items, _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        RaiseFeedback(
            items.Count == 1 ? "已复制" : $"已复制 {items.Count} 条随记",
            WidgetFeedbackSeverity.Success,
            "quick-capture-copy-selection");
    }

    private void ItemsList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ItemsList);
        if (!point.Properties.IsLeftButtonPressed || IsShiftPressed() ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = IsControlPressed()
            ? GetBulkSelectedItems().ToList()
            : [];
        _selectionHitTestItems = [];
        if (!IsControlPressed())
        {
            ItemsList.SelectedItems.Clear();
        }
    }

    private void ItemsList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDistance(_selectionStartPoint, _selectionCurrentPoint) < 6)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            ItemsList.CapturePointer(e.Pointer);
            CacheSelectionHitTestItems();
            SetBulkSelectionMode(enable: true, preserveSelection: true);
        }

        UpdateSelectionRectangle();
        ApplyBoxSelectionPreview();
        e.Handled = true;
    }

    private void ItemsList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool wasBoxSelecting = _isBoxSelecting;
        FinishBoxSelection();
        if (wasBoxSelecting)
        {
            ItemsList.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void ItemsList_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        FinishBoxSelection();

    private void FinishBoxSelection()
    {
        _selectionPointerPressed = false;
        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionHitTestItems = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
    }

    private void CacheSelectionHitTestItems()
    {
        _selectionHitTestItems = [];
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsList.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 || container.ActualHeight <= 0)
            {
                continue;
            }

            FrameworkElement target =
                FindNamedDescendant<Grid>(container, "QuickCaptureItemRoot") ??
                (FrameworkElement)container;
            Point topLeft = target.TransformToVisual(SelectionOverlay).TransformPoint(new Point());
            _selectionHitTestItems.Add(new QuickCaptureSelectionHitTestItem(
                item,
                new Rect(topLeft.X, topLeft.Y, target.ActualWidth, target.ActualHeight)));
        }
    }

    private void ApplyBoxSelectionPreview()
    {
        Rect selection = CreateSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selected = new HashSet<QuickCaptureItemViewModel>(_selectionSnapshot);
        foreach (QuickCaptureSelectionHitTestItem hit in _selectionHitTestItems)
        {
            if (Intersects(selection, hit.Bounds))
            {
                selected.Add(hit.Item);
            }
        }

        ItemsList.SelectedItems.Clear();
        foreach (QuickCaptureItemViewModel item in ViewModel.Items.Where(selected.Contains))
        {
            ItemsList.SelectedItems.Add(item);
        }
        UpdateBulkSelectionState();
    }

    private void UpdateSelectionRectangle()
    {
        Rect rect = CreateSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, rect.X);
        Canvas.SetTop(SelectionRectangle, rect.Y);
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
        SelectionRectangle.Visibility = rect.Width > 0 && rect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return FindAncestor<ListViewItem>(source) is null &&
            FindAncestor<ScrollBar>(source) is null &&
            FindAncestor<ButtonBase>(source) is null &&
            FindAncestor<TextBox>(source) is null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindNamedDescendant<T>(DependencyObject? root, string name)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }
            if (FindNamedDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }

    private static Rect CreateSelectionRect(Point start, Point end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X),
        Math.Abs(end.Y - start.Y));

    private static double GetSelectionDistance(Point start, Point end)
    {
        double x = end.X - start.X;
        double y = end.Y - start.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static bool Intersects(Rect first, Rect second) =>
        first.X < second.X + second.Width &&
        first.X + first.Width > second.X &&
        first.Y < second.Y + second.Height &&
        first.Y + first.Height > second.Y;

    private sealed record QuickCaptureSelectionHitTestItem(
        QuickCaptureItemViewModel Item,
        Rect Bounds);
}

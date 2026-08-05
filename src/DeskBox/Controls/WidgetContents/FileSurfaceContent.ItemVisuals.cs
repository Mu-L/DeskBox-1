using DeskBox.Controls;
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
    private readonly HashSet<Border> _itemSurfaces = [];
    private readonly FileItemSurfaceStyleCache _itemSurfaceStyleCache = new();

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
        if (sender is FileItemSurface surface)
        {
            surface.VisualStateChanged += ItemSurface_VisualStateChanged;
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            _itemSurfaces.Add(border);
            ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.Normal);
        }
    }

    private void ItemSurface_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FileItemSurface surface)
        {
            surface.VisualStateChanged -= ItemSurface_VisualStateChanged;
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            if (ReferenceEquals(border, _folderDropTarget))
            {
                _folderDropTarget = null;
            }

            _itemSurfaces.Remove(border);
        }
    }

    private void ItemSurface_VisualStateChanged(
        object? sender,
        FileItemSurfaceVisualStateChangedEventArgs e)
    {
        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            ApplyItemSurfaceVisual(border, e.State);
        }
    }

    private void ItemSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (FileItemSurface.TryGetInteractiveBorder(sender) is not { } border ||
            border.DataContext is not WidgetItem item ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ListViewBase listView = GetActiveItemsView();
        ClearOtherWidgetSelections();
        FileItemSelectionBehavior.ApplyPointerSelection(
            listView,
            item,
            Win32Helper.IsKeyPressed(
                Windows.System.VirtualKey.Control),
            Win32Helper.IsKeyPressed(
                Windows.System.VirtualKey.Shift));

        SynchronizeItemSelectionState();
    }

    private void ItemSurface_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out Border border, out WidgetItem targetFolder))
        {
            return;
        }

        e.Handled = true;
        // A folder item is an explicit filesystem destination. Cancel any
        // insertion preview that the root produced before the pointer entered
        // the folder so DragItemsCompleted cannot commit a stale reorder.
        PersistSurfaceReorder();

        if (_isImportBusy || !HasSurfacePathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ClearFolderDropTarget();
            return;
        }

        string[] sourcePaths = GetPackagePaths(e.DataView);
        if (IsUnsafeFolderDrop(sourcePaths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = T("Widget.CannotMoveToFolder");
            ClearFolderDropTarget();
            return;
        }

        DataPackageOperation operation = ResolveFolderDropOperation(e.DataView);
        e.AcceptedOperation = operation;
        e.DragUIOverride.IsGlyphVisible = operation != DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = operation != DataPackageOperation.None;
        if (operation == DataPackageOperation.None)
        {
            ClearFolderDropTarget();
            return;
        }

        SetFolderDropTarget(border);
        e.DragUIOverride.Caption = _localizationService.Format(
            operation == DataPackageOperation.Copy
                ? "Widget.CopyToFolder"
                : "Widget.MoveToFolder",
            targetFolder.Name);
    }

    private void ItemSurface_DragLeave(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out Border border, out _))
        {
            return;
        }

        e.Handled = true;
        if (ReferenceEquals(border, _folderDropTarget))
        {
            ClearFolderDropTarget();
        }
    }

    private async void ItemSurface_Drop(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out _, out WidgetItem targetFolder))
        {
            return;
        }

        e.Handled = true;
        ClearFolderDropTarget();
        PersistSurfaceReorder();
        ApplyDropVisual(FileDropVisualState.None);

        if (_isImportBusy || !HasSurfacePathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            DroppedFilePath[] droppedFiles = batch.Files
                .Where(file =>
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    (File.Exists(file.Path) || Directory.Exists(file.Path)))
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            string[] sourcePaths = droppedFiles
                .Select(file => file.Path)
                .ToArray();
            if (sourcePaths.Length == 0 ||
                IsUnsafeFolderDrop(sourcePaths, targetFolder.Path))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (sourcePaths.Length > 0)
                {
                    ShowFeedback(new(
                        T("Widget.CannotMoveToFolder"),
                        WidgetFeedbackSeverity.Warning,
                        "folder-drop-unsafe"));
                }
                return;
            }

            DataPackageOperation operation = e.AcceptedOperation == DataPackageOperation.None
                ? ResolveFolderDropOperation(e.DataView)
                : e.AcceptedOperation;
            if (operation == DataPackageOperation.None)
            {
                return;
            }

            e.AcceptedOperation = operation;
            bool move = operation != DataPackageOperation.Copy;
            string? sourceWidgetId = TryGetString(
                e.DataView.Properties,
                DeskBoxDragData.SourceWidgetIdProperty);

            // All DataPackageView values have been captured. Completing now
            // dismisses the shell drag glyph while the filesystem operation
            // continues under DeskBox's own progress overlay when necessary.
            deferral.Complete();
            deferral = null;

            bool showOverlay = DeskBoxDragData.ShouldShowImportOverlay(sourcePaths);
            if (showOverlay)
            {
                SetImportBusy(true);
            }

            try
            {
                var results = new List<FileService.FileTransferResult>();
                string[] regularPaths = droppedFiles
                    .Where(file => !file.ForceManagedCopy)
                    .Select(file => file.Path)
                    .ToArray();
                if (regularPaths.Length > 0)
                {
                    results.AddRange(await _fileService.TransferItemsWithResultAsync(
                        regularPaths,
                        targetFolder.Path,
                        move));
                }

                string[] forcedCopyPaths = droppedFiles
                    .Where(file => file.ForceManagedCopy)
                    .Select(file => file.Path)
                    .ToArray();
                if (forcedCopyPaths.Length > 0)
                {
                    results.AddRange(await _fileService.TransferItemsWithResultAsync(
                        forcedCopyPaths,
                        targetFolder.Path,
                        move: false));
                }

                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    await ViewModel.RefreshFromConfigAsync();
                }

                string[] movedSourcePaths = move
                    ? results
                        .Where(result => regularPaths.Contains(
                            result.SourcePath,
                            StringComparer.OrdinalIgnoreCase))
                        .Select(result => result.SourcePath)
                        .ToArray()
                    : [];
                if (movedSourcePaths.Length > 0 &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        movedSourcePaths);
                }

                if (move)
                {
                    _cutClipboardPaths = [];
                    ApplyCutState();
                }

                if (results.Count > 0)
                {
                    ShowFeedback(new(
                        _localizationService.Format(
                            move
                                ? "Widget.MovedToFolder"
                                : "Widget.CopiedToFolder",
                            targetFolder.Name,
                            results.Count),
                        WidgetFeedbackSeverity.Success,
                        move ? "folder-drop-move" : "folder-drop-copy"));
                }
            }
            finally
            {
                if (showOverlay)
                {
                    SetImportBusy(false);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Folder drop failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                $"{T("Widget.MoveToFolderFailed")}: {ex.Message}",
                WidgetFeedbackSeverity.Error,
                "folder-drop-error"));
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private static bool TryGetFolderDropTarget(
        object sender,
        out Border border,
        out WidgetItem folder)
    {
        if (sender is FileItemSurface surface &&
            surface.DataContext is WidgetItem
            {
                IsFolder: true,
                Path.Length: > 0
            } item)
        {
            border = surface.InteractiveBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private DataPackageOperation ResolveFolderDropOperation(
        DataPackageView dataView)
    {
        DataPackageOperation requested = dataView.RequestedOperation;
        bool copyRequested = Win32Helper.IsKeyPressed(
            Windows.System.VirtualKey.Control);
        if (copyRequested && requested.HasFlag(DataPackageOperation.Copy))
        {
            return DataPackageOperation.Copy;
        }

        if (requested == DataPackageOperation.None ||
            requested.HasFlag(DataPackageOperation.Move) ||
            requested.HasFlag(DataPackageOperation.Link))
        {
            return DataPackageOperation.Move;
        }

        return requested.HasFlag(DataPackageOperation.Copy)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private void SetFolderDropTarget(Border border)
    {
        if (!ReferenceEquals(_folderDropTarget, border))
        {
            ClearFolderDropTarget();
            _folderDropTarget = border;
        }

        ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.DropTarget);
    }

    private void ClearFolderDropTarget()
    {
        Border? previous = _folderDropTarget;
        _folderDropTarget = null;
        if (previous?.XamlRoot is not null)
        {
            ApplyItemSurfaceVisual(previous, FileItemSurfaceVisualState.Normal);
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
        // This is a stack-membership drop, not an ordering drop. Clear the
        // complete reorder session, including the cached insertion position.
        PersistSurfaceReorder();
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

            ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.Normal);
        }
    }

    private void ApplyItemSurfaceVisual(
        Border border,
        FileItemSurfaceVisualState state)
    {
        if (ReferenceEquals(border, _folderDropTarget) &&
            state != FileItemSurfaceVisualState.DropTarget)
        {
            state = FileItemSurfaceVisualState.DropTarget;
        }

        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        WidgetItem? item = border.DataContext as WidgetItem;
        _itemSurfaceStyleCache.Apply(
            border,
            state,
            Root.ActualTheme,
            accent,
            item?.IsSelected == true,
            item?.IsCut == true);
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

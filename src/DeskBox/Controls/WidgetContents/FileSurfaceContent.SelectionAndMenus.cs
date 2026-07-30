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
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private sealed record SelectionHit(
        WidgetItem Item,
        Border? Surface,
        Windows.Foundation.Rect Bounds);

    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _isSynchronizingSelection;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private List<SelectionHit> _selectionHits = [];

    private void Items_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView ||
            !e.GetCurrentPoint(listView).Properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        Root.Focus(FocusState.Programmatic);
        ClearOtherWidgetSelections();
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint =
            e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot =
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                ? GetSelectedItems().ToList()
                : [];
        _selectionHits = [];

        if (!Win32Helper.IsKeyPressed(
                Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            UpdateSelectionCommandBar();
        }
    }

    private void Items_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView ||
            !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint =
            e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting &&
            GetDragDistance(
                _selectionStartPoint,
                _selectionCurrentPoint) < 6)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            listView.CapturePointer(e.Pointer);
            CacheSelectionHits(listView);
        }

        UpdateSelectionRectangle();
        ApplySelectionPreview(listView);
        e.Handled = true;
    }

    private void Items_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        bool wasBoxSelecting = _isBoxSelecting;
        FinishBoxSelection(listView);
        if (wasBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void Items_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishBoxSelection(listView);
        }
    }

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !IsWithinItemSurface(source) &&
               !HasAncestor<ScrollBar>(source) &&
               !HasAncestor<ButtonBase>(source) &&
               !HasAncestor<TextBox>(source);
    }

    private void CacheSelectionHits(ListViewBase listView)
    {
        _selectionHits = [];
        foreach (WidgetItem item in listView.Items
                     .OfType<WidgetItem>()
                     .Where(item => item is not WidgetStackItem))
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            FrameworkElement target =
                FindItemSurface(item) ?? container;
            Windows.Foundation.Point topLeft =
                target.TransformToVisual(SelectionOverlay)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
            _selectionHits.Add(new SelectionHit(
                item,
                target as Border,
                new Windows.Foundation.Rect(
                    topLeft.X,
                    topLeft.Y,
                    target.ActualWidth,
                    target.ActualHeight)));
        }
    }

    private void ApplySelectionPreview(ListViewBase listView)
    {
        Windows.Foundation.Rect selectionRect =
            GetSelectionRect(
                _selectionStartPoint,
                _selectionCurrentPoint);
        var selected = new HashSet<WidgetItem>(_selectionSnapshot);
        foreach (SelectionHit hit in _selectionHits)
        {
            if (RectsIntersect(selectionRect, hit.Bounds))
            {
                selected.Add(hit.Item);
            }
        }

        _isSynchronizingSelection = true;
        try
        {
            listView.SelectedItems.Clear();
            foreach (WidgetItem item in listView.Items
                         .OfType<WidgetItem>()
                         .Where(selected.Contains))
            {
                listView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        UpdateSelectionCommandBar();
        SynchronizeItemSelectionState();
    }

    private void UpdateSelectionRectangle()
    {
        Windows.Foundation.Rect rect =
            GetSelectionRect(
                _selectionStartPoint,
                _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, rect.X);
        Canvas.SetTop(SelectionRectangle, rect.Y);
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
        SelectionRectangle.Visibility =
            rect.Width > 0 && rect.Height > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void FinishBoxSelection(ListViewBase listView)
    {
        if (!_selectionPointerPressed && !_isBoxSelecting)
        {
            return;
        }

        if (_isBoxSelecting)
        {
            ApplySelectionPreview(listView);
        }

        _selectionPointerPressed = false;
        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionHits = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        UpdateSelectionCommandBar();
        SynchronizeItemSelectionState();
    }

    public void ClearItemSelection()
    {
        ItemsGrid.SelectedItems.Clear();
        ItemsList.SelectedItems.Clear();
        UpdateSelectionCommandBar();
        SynchronizeItemSelectionState();
    }

    private void ClearSelection() => ClearItemSelection();

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point start,
        Windows.Foundation.Point end)
    {
        return new Windows.Foundation.Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private static double GetDragDistance(
        Windows.Foundation.Point start,
        Windows.Foundation.Point end)
    {
        double x = end.X - start.X;
        double y = end.Y - start.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static bool RectsIntersect(
        Windows.Foundation.Rect first,
        Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    private static bool HasAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsWithinItemSurface(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Border border &&
                border.Tag as string is "InteractiveSurface" or "StackSurface")
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        if (GetActiveItemsView().ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        return FindDescendantSurface(container);
    }

    private static Border? FindDescendantSurface(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Border border &&
                border.Tag as string is "InteractiveSurface" or "StackSurface")
            {
                return border;
            }

            if (FindDescendantSurface(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static WidgetItem? FindItemFromSource(object? source)
    {
        return FindItemElement(source)?.DataContext as WidgetItem;
    }

    private static FrameworkElement? FindItemElement(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement
                {
                    DataContext: WidgetItem
                } element)
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private MenuFlyout CreateItemFlyout(WidgetItem item)
    {
        var flyout = new MenuFlyout();

        MenuFlyoutItem open = CreateMenuItem(
            "Widget.Open",
            "\uE8E5");
        open.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.OpenItem(item);
        };
        flyout.Items.Add(open);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem cut = CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(
                () => CopySelectionToClipboardAsync(cut: true));
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(
                () => CopySelectionToClipboardAsync(cut: false));
        };
        flyout.Items.Add(copy);

        MenuFlyoutItem rename = CreateMenuItem(
            "Common.Rename",
            "\uE8AC");
        rename.Click += async (_, _) =>
        {
            flyout.Hide();
            await RenameItemAsync(item);
        };
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem copyPath = CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        MenuFlyoutItem showInExplorer = CreateMenuItem(
            "Widget.ShowInExplorer",
            "\uE838");
        showInExplorer.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.ShowInExplorer(item);
        };
        flyout.Items.Add(showInExplorer);

        MenuFlyoutItem properties = CreateMenuItem(
            "Common.Properties",
            "\uE946");
        properties.Click += (_, _) =>
        {
            flyout.Hide();
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(
                foreground,
                Win32Helper.GA_ROOT);
            if (!ShellContextMenuHelper.ShowProperties(
                    owner == IntPtr.Zero ? foreground : owner,
                    item.Path))
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "file-properties"));
            }
        };
        flyout.Items.Add(properties);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem moveBack = CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await MoveSelectedItemsBackToDesktopAsync();
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = CreateMenuItem(
            "Common.Delete",
            "\uE74D");
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteItemsAsync(GetSelectedItems());
        };
        flyout.Items.Add(delete);
        return flyout;
    }

    private MenuFlyout CreateMultiSelectionFlyout()
    {
        var flyout = new MenuFlyout();

        if (ViewModel.FileStacksEnabled)
        {
            MenuFlyoutItem startStack = CreateMenuItem(
                "Widget.Stack.Start",
                "\uE8B7");
            startStack.Click += (_, _) =>
            {
                flyout.Hide();
                ViewModel.CreateManualStack(
                    GetSelectedItems());
                ClearSelection();
            };
            flyout.Items.Add(startStack);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        MenuFlyoutItem cut = CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(
                () => CopySelectionToClipboardAsync(cut: true));
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(
                () => CopySelectionToClipboardAsync(cut: false));
        };
        flyout.Items.Add(copy);

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem copyPath = CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        if (CanMoveItemsBackToDesktop())
        {
            MenuFlyoutItem moveBack = CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await MoveSelectedItemsBackToDesktopAsync();
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = CreateMenuItem(
            "Common.Delete",
            "\uE74D");
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteItemsAsync(GetSelectedItems());
        };
        flyout.Items.Add(delete);
        return flyout;
    }

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();
        if (CanPasteFromClipboard())
        {
            var paste = CreateMenuItem("Common.Paste", "\uE77F");
            paste.Click += async (_, _) =>
            {
                flyout.Hide();
                await RunAsync(PasteFromClipboardAsync);
            };
            flyout.Items.Add(paste);
        }

        if (!string.IsNullOrWhiteSpace(
                ViewModel.MappedFolderPath))
        {
            MenuFlyoutItem newFolder =
                CreateMenuItem("Common.NewFolder", "\uE8B7");
            newFolder.Click += async (_, _) =>
            {
                flyout.Hide();
                await CreateFolderInMappedLocationAsync();
            };
            flyout.Items.Add(newFolder);

            MenuFlyoutItem openFolder = CreateMenuItem(
                ViewModel.FollowsDefaultStoragePath
                    ? "Widget.OpenStorageFolder"
                    : "Widget.OpenCurrentFolder",
                "\uE838");
            openFolder.Click += (_, _) =>
            {
                flyout.Hide();
                Win32Helper.OpenFile(ViewModel.MappedFolderPath);
            };
            flyout.Items.Add(openFolder);

            if (!ViewModel.FollowsDefaultStoragePath)
            {
                MenuFlyoutItem changePath = CreateMenuItem(
                    "Widget.ChangeMappedPath",
                    "\uE8B7");
                changePath.Click += async (_, _) =>
                {
                    flyout.Hide();
                    await PickMappedFolderAsync();
                };
                flyout.Items.Add(changePath);
            }
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var viewAndSort = new MenuFlyoutSubItem
        {
            Text = T("Widget.ViewAndSort"),
            Icon = new FontIcon { Glyph = "\uE8CB" }
        };
        var iconView = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.IconView"),
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += (_, _) =>
        {
            if (!ViewModel.IsIconMode)
            {
                ToggleViewButton_Click(this, new RoutedEventArgs());
            }
        };
        viewAndSort.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.ListView"),
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += (_, _) =>
        {
            if (!ViewModel.IsListMode)
            {
                ToggleViewButton_Click(this, new RoutedEventArgs());
            }
        };
        viewAndSort.Items.Add(listView);
        viewAndSort.Items.Add(new MenuFlyoutSeparator());
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Name",
            WidgetSortMode.Name);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Size",
            WidgetSortMode.Size);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Type",
            WidgetSortMode.Type);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.DateModified",
            WidgetSortMode.DateModified);
        flyout.Items.Add(viewAndSort);
        flyout.Items.Add(CreateStackSettingsMenu());

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem refresh =
            CreateMenuItem("Common.Refresh", "\uE72C");
        refresh.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(RefreshAsync);
        };
        flyout.Items.Add(refresh);
        return flyout;
    }

    private static bool CanPasteFromClipboard()
    {
        try
        {
            DataPackageView clipboard = Clipboard.GetContent();
            return clipboard.Contains(
                       StandardDataFormats.StorageItems) ||
                   GetPackagePaths(clipboard).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(
                ViewModel.MappedFolderPath))
        {
            return;
        }

        await RunAsync(async () =>
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(
                    ViewModel.MappedFolderPath,
                    T("Widget.NewFolderName")));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFolderContentsAsync();
            if (ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(
                        item.Path,
                        folderPath,
                        StringComparison.OrdinalIgnoreCase)) is { } newFolder)
            {
                await StartItemRenameAsync(newFolder);
            }
        });
    }

    private async Task PickMappedFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation =
                PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr owner = Win32Helper.GetAncestor(
            foreground,
            Win32Helper.GA_ROOT);
        InitializeWithWindow.Initialize(
            picker,
            owner == IntPtr.Zero ? foreground : owner);
        Windows.Storage.StorageFolder? folder =
            await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await RunAsync(
                () => ViewModel.UpdateMappedFolderPathAsync(
                    folder.Path));
        }
    }

    private void AddSortItem(
        MenuFlyoutSubItem parent,
        string localizationKey,
        WidgetSortMode mode)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked = ViewModel.Config.SortMode == mode
        };
        item.Click += (_, _) => ViewModel.SetSortMode(mode);
        parent.Items.Add(item);
    }

    private MenuFlyoutSubItem CreateStackSettingsMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Widget.Stack.Menu"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        var followDefaults = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.FollowDefaults"),
            IsChecked = ViewModel.FileStacksFollowGlobalDefaults
        };
        followDefaults.Click += (_, _) =>
            ViewModel.ClearFileStackOverrides();
        menu.Items.Add(followDefaults);

        var enabled = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.EnableForWidget"),
            IsChecked = ViewModel.FileStacksEnabled
        };
        enabled.Click += (_, _) =>
            ViewModel.SetFileStacksEnabledOverride(enabled.IsChecked);
        menu.Items.Add(enabled);
        menu.Items.Add(new MenuFlyoutSeparator());

        var defaultGrouping = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultGrouping"),
            IsChecked = ViewModel.FileStackGroupByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        defaultGrouping.Click += (_, _) =>
            ViewModel.SetFileStackGroupByOverride(null);
        menu.Items.Add(defaultGrouping);
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByKind,
            "Settings.FileStacks.GroupBy.Kind");
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByDateModified,
            "Settings.FileStacks.GroupBy.DateModified");
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByCustom,
            "Settings.FileStacks.GroupBy.Custom");

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateStackThresholdMenu());
        menu.Items.Add(CreateStackOrderMenu());
        if (ViewModel.HasDisabledStacks)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var restore = new MenuFlyoutSubItem
            {
                Text = T("Widget.Stack.RestoreGroups"),
                Icon = new FontIcon { Glyph = "\uE8EE" }
            };
            Dictionary<string, string> names =
                WidgetFileStackSettings.GetStackNameOverrides(
                    ViewModel.Config);
            foreach (string key in
                     WidgetFileStackSettings.GetDisabledStacks(
                         ViewModel.Config))
            {
                string label =
                    names.TryGetValue(key, out string? customName) &&
                    !string.IsNullOrWhiteSpace(customName)
                        ? customName
                        : key.StartsWith(
                            "Custom:",
                            StringComparison.Ordinal)
                            ? key["Custom:".Length..]
                            : T($"Widget.Stack.Category.{key}");
                var restoreItem = new MenuFlyoutItem
                {
                    Text = label
                };
                restoreItem.Click += (_, _) =>
                    ViewModel.SetStackDisabled(key, false);
                restore.Items.Add(restoreItem);
            }
            menu.Items.Add(restore);
        }

        return menu;
    }

    private void AddStackGroupingItem(
        MenuFlyoutSubItem menu,
        string groupBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked =
                !ViewModel.FileStackGroupByFollowsGlobal &&
                string.Equals(
                    ViewModel.FileStackGroupBy,
                    groupBy,
                    StringComparison.Ordinal),
            IsEnabled = ViewModel.FileStacksEnabled
        };
        item.Click += (_, _) =>
            ViewModel.SetFileStackGroupByOverride(groupBy);
        menu.Items.Add(item);
    }

    private MenuFlyoutSubItem CreateStackThresholdMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.FileStacks.Threshold.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultThreshold"),
            IsChecked = ViewModel.FileStackThresholdFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) =>
            ViewModel.SetFileStackThresholdOverride(null);
        menu.Items.Add(useDefault);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (int threshold in new[] { 2, 3, 5 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.Format(
                    "Settings.FileStacks.Threshold.Option",
                    threshold),
                IsChecked =
                    !ViewModel.FileStackThresholdFollowsGlobal &&
                    ViewModel.FileStackThreshold == threshold,
                IsEnabled = ViewModel.FileStacksEnabled
            };
            item.Click += (_, _) =>
                ViewModel.SetFileStackThresholdOverride(threshold);
            menu.Items.Add(item);
        }

        return menu;
    }

    private MenuFlyoutSubItem CreateStackOrderMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.FileStacks.OrderBy.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultOrder"),
            IsChecked = ViewModel.FileStackOrderByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) =>
            ViewModel.SetFileStackOrderByOverride(null);
        menu.Items.Add(useDefault);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByWidget,
            "Settings.FileStacks.OrderBy.Widget");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByName,
            "Settings.FileStacks.OrderBy.Name");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByDateAdded,
            "Settings.FileStacks.OrderBy.DateAdded");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByDateModified,
            "Settings.FileStacks.OrderBy.DateModified");
        return menu;
    }

    private void AddStackOrderItem(
        MenuFlyoutSubItem menu,
        string orderBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked =
                !ViewModel.FileStackOrderByFollowsGlobal &&
                string.Equals(
                    ViewModel.FileStackOrderBy,
                    orderBy,
                    StringComparison.Ordinal),
            IsEnabled = ViewModel.FileStacksEnabled
        };
        item.Click += (_, _) =>
            ViewModel.SetFileStackOrderByOverride(orderBy);
        menu.Items.Add(item);
    }

    private MenuFlyout CreateStackFlyout(WidgetStackItem stack)
    {
        var flyout = new MenuFlyout();
        MenuFlyoutItem toggle = new()
        {
            Text = T(stack.IsExpanded
                ? "Widget.Stack.Collapse"
                : "Widget.Stack.Expand"),
            Icon = new FontIcon { Glyph = stack.ChevronGlyph }
        };
        toggle.Click += (_, _) => ViewModel.ToggleStack(stack);
        flyout.Items.Add(toggle);

        MenuFlyoutItem rename = CreateMenuItem(
            "Widget.Stack.Rename",
            "\uE8AC");
        rename.Click += async (_, _) =>
            await RenameStackAsync(stack);
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem moveUp = CreateMenuItem(
            "Widget.Stack.MoveUp",
            "\uE74A");
        moveUp.Click += (_, _) =>
            ViewModel.MoveStackUp(stack.StackKey);
        flyout.Items.Add(moveUp);

        MenuFlyoutItem moveDown = CreateMenuItem(
            "Widget.Stack.MoveDown",
            "\uE74B");
        moveDown.Click += (_, _) =>
            ViewModel.MoveStackDown(stack.StackKey);
        flyout.Items.Add(moveDown);

        MenuFlyoutItem disable = CreateMenuItem(
            "Widget.Stack.DisableGroup",
            "\uE748");
        disable.Click += (_, _) =>
            ViewModel.SetStackDisabled(stack.StackKey, true);
        flyout.Items.Add(disable);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem selectContents = CreateMenuItem(
            "Widget.Stack.SelectContents",
            "\uE762");
        selectContents.Click += (_, _) =>
            SelectStackMembers(stack);
        flyout.Items.Add(selectContents);

        MenuFlyoutItem copyPaths = CreateMenuItem(
            "Widget.Stack.CopyContentPaths",
            "\uE8C8");
        copyPaths.Click += (_, _) =>
        {
            CopyPathsToClipboard(stack.Members);
        };
        flyout.Items.Add(copyPaths);
        return flyout;
    }

    private MenuFlyoutItem CreateMenuItem(
        string localizationKey,
        string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private void CopySelectedPathsToClipboard()
    {
        CopyPathsToClipboard(GetSelectedItems());
    }

    private void CopyPathsToClipboard(
        IEnumerable<WidgetItem> items)
    {
        string[] paths = items
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, paths);
        var package = new DataPackage();
        package.SetText(text);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: text,
            paths: paths);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.CopyPathCount",
                paths.Length),
            WidgetFeedbackSeverity.Success,
            "file-copy-path"));
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(
            ViewModel.MappedFolderPath);
    }

    private async Task MoveSelectedItemsBackToDesktopAsync()
    {
        IReadOnlyList<WidgetItem> items = GetSelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        await RunAsync(async () =>
        {
            int moved = await ViewModel.MoveItemsBackToDesktopAsync(
                items,
                useShellProgress: true);
            _cutClipboardPaths = [];
            ApplyCutState();
            ShowFeedback(new WidgetFeedbackRequest(
                moved > 0
                    ? _localizationService.Format(
                        "Widget.MovedBackToDesktop",
                        moved)
                    : T("Widget.NoItemsMoved"),
                moved > 0
                    ? WidgetFeedbackSeverity.Success
                    : WidgetFeedbackSeverity.Info,
                "file-move-desktop"));
        });
    }

    private async Task RenameStackAsync(WidgetStackItem stack)
    {
        var editor = new TextBox { Text = stack.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Widget.Stack.Rename"),
            Content = editor,
            PrimaryButtonText = T("Common.Save"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() ==
                ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(editor.Text))
        {
            ViewModel.SetStackNameOverride(
                stack.StackKey,
                editor.Text.Trim());
        }
    }

    private void SelectStackMembers(WidgetStackItem stack)
    {
        ViewModel.SetStackExpanded(stack, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewBase listView = GetActiveItemsView();
            listView.SelectedItems.Clear();
            foreach (WidgetItem member in stack.Members)
            {
                if (listView.Items.Contains(member))
                {
                    listView.SelectedItems.Add(member);
                }
            }

            UpdateSelectionCommandBar();
        });
    }
}

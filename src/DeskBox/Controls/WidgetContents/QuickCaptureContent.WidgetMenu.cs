using DeskBox.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    void IWidgetCommandMenuProvider.AppendWidgetCommands(MenuFlyout menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var add = new MenuFlyoutItem
        {
            Text = "新建随记",
            Icon = new FontIcon { Glyph = "\uE710" },
            KeyboardAcceleratorTextOverride = "Ctrl+N"
        };
        add.Click += async (_, _) =>
        {
            await ForceCommitAsync(returnToReading: false);
            BeginNewNote();
        };
        menu.Items.Add(add);

        var search = new MenuFlyoutItem
        {
            Text = "搜索",
            Icon = new FontIcon { Glyph = "\uE721" }
        };
        search.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
            SearchButton_Click(this, new RoutedEventArgs()));
        menu.Items.Add(search);

        var view = new MenuFlyoutSubItem
        {
            Text = "视图",
            Icon = new FontIcon { Glyph = "\uE890" }
        };
        view.Items.Add(CreateLayoutMenuItem("自动", QuickCaptureLayoutOverride.Auto));
        view.Items.Add(CreateLayoutMenuItem("单栏", QuickCaptureLayoutOverride.Single));
        view.Items.Add(CreateLayoutMenuItem("双栏", QuickCaptureLayoutOverride.Dual));
        view.Items.Add(new MenuFlyoutSeparator());
        var split = new ToggleMenuFlyoutItem
        {
            Text = "编辑器与预览并排",
            IsChecked = _splitPreviewEnabled,
            IsEnabled = ActualWidth >= SplitPreviewBreakpoint
        };
        split.Click += (_, _) =>
        {
            _splitPreviewEnabled = split.IsChecked;
            _hasSplitPreviewOverride = true;
            PersistPresentationOverrides();
            ApplyResponsiveLayout();
        };
        view.Items.Add(split);
        var focus = new ToggleMenuFlyoutItem
        {
            Text = "专注模式",
            IsChecked = _focusMode
        };
        focus.Click += (_, _) =>
        {
            _focusMode = focus.IsChecked;
            if (_focusMode && _selectedItem is null && !_isCreating)
            {
                BeginNewNote();
            }
            PersistPresentationOverrides();
            ApplyResponsiveLayout();
        };
        view.Items.Add(focus);
        menu.Items.Add(view);

        // Less common collection operations live behind one predictable entry
        // instead of competing with New/Search/View in the first menu level.
        var manage = new MenuFlyoutSubItem
        {
            Text = "管理",
            Icon = new FontIcon { Glyph = "\uE713" }
        };

        var select = new MenuFlyoutItem
        {
            Text = "多选",
            Icon = new FontIcon { Glyph = "\uE762" },
            KeyboardAcceleratorTextOverride = "Ctrl+A"
        };
        select.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            SetBulkSelectionMode(enable: true, preserveSelection: true);
            ItemsList.Focus(FocusState.Programmatic);
        });
        manage.Items.Add(select);

        var archived = new MenuFlyoutItem
        {
            Text = "已归档",
            Icon = new FontIcon { Glyph = "\uE7B8" }
        };
        archived.Click += (_, _) => ArchivedButton_Click(this, new RoutedEventArgs());
        manage.Items.Add(archived);

        var trash = new MenuFlyoutItem
        {
            Text = "回收站",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        trash.Click += (_, _) => TrashButton_Click(this, new RoutedEventArgs());
        manage.Items.Add(trash);

        var export = new MenuFlyoutItem
        {
            Text = "导出全部",
            Icon = new FontIcon { Glyph = "\uE74E" }
        };
        export.Click += (_, _) => ExportAllButton_Click(this, new RoutedEventArgs());
        manage.Items.Add(export);
        menu.Items.Add(manage);
    }

    private ToggleMenuFlyoutItem CreateLayoutMenuItem(
        string text,
        QuickCaptureLayoutOverride layout)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = _layoutOverride == layout,
            IsEnabled = layout != QuickCaptureLayoutOverride.Dual || ActualWidth >= DualPaneExitBreakpoint
        };
        item.Click += (_, _) =>
        {
            _layoutOverride = layout;
            _hasLayoutOverride = true;
            PersistPresentationOverrides();
            ApplyResponsiveLayout();
        };
        return item;
    }
}

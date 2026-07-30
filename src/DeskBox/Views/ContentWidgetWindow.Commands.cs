using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private bool _isCloseWidgetPending;

    private void ApplyTitleBarLayout()
    {
        WidgetChromeMode chromeMode =
            App.Current.WidgetManager?.ResolveWidgetChromeMode(
                _config,
                _descriptor) ??
            _chromeModeResolver.Resolve(_config, _descriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? SettingsService.NormalizeTextSize(SettingsService.Settings.TextSize)
            : _titleViewModel.TitleTextSize;
        var metrics = WidgetTitleBarMetricsCalculator.Create(
            _titleViewModel.TitleIconSize,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        ContentWidgetShell.ChromeMode = chromeMode;
        ContentWidgetShell.TitleIconElement.IconSize = metrics.TitleIconSize;
        ContentWidgetShell.TitleTextElement.FontSize = metrics.TitleTextSize;
        ApplyTitleActionButtonConfiguration();
        ApplyLockActionIconState();

        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.PositionLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.SizeLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.AddActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.MoreActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(ContentWidgetShell.CloseActionButton, metrics);

        WidgetActionIconHelper.ApplyPairSize(
            ContentWidgetShell.PositionLockActionIcon,
            ContentWidgetShell.PositionLockFilledActionIcon,
            metrics);
        WidgetActionIconHelper.ApplyPairSize(
            ContentWidgetShell.SizeLockActionIcon,
            ContentWidgetShell.SizeLockFilledActionIcon,
            metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.AddActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.MoreActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(ContentWidgetShell.CloseActionIcon, metrics);

        ContentWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        ContentWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
    }

    private void ApplyTitleActionButtonConfiguration()
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(SettingsService.Settings.WidgetHoverButtonActions);
        bool contentCanAdd = _contentHost.CurrentContent is IWidgetAddActionContent;
        ContentWidgetShell.PositionLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockPosition)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.SizeLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.ShowAddButton = contentCanAdd &&
            actions.Contains(SettingsService.WidgetHoverActionAdd);
        ContentWidgetShell.MoreActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionMore)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.CloseActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionDelete)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            ContentWidgetShell.PositionLockActionIcon,
            ContentWidgetShell.PositionLockFilledActionIcon,
            _config.IsPositionLocked,
            ContentWidgetShell.SizeLockActionIcon,
            ContentWidgetShell.SizeLockFilledActionIcon,
            _config.IsSizeLocked);
    }

    // ── Button click handlers ──────────────────────────────────

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contentHost.CurrentContent is IWidgetAddActionContent addActionContent)
        {
            await addActionContent.AddFromTitleButtonAsync();
        }
    }

    /// <summary>
    /// Programmatically triggers the "add new item" action (same as clicking the + button).
    /// Used by search actions to open the create-detail editor after showing the widget.
    /// </summary>
    internal void TriggerAddAction()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_contentHost.CurrentContent is IWidgetAddActionContent addActionContent)
            {
                await addActionContent.AddFromTitleButtonAsync();
            }
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // A feature member inside a group is only the current Surface page.
        // Closing hides the group and must not disable the global feature.
        if (App.Current.WidgetManager?.GetWidgetGroupPresentation(_config.Id) is not null)
        {
            HideWindow();
            return;
        }

        if (FeatureWidgetSettings.IsFeatureWidget(_config.WidgetKind) &&
            App.Current.WidgetManager is { } widgetManager)
        {
            var localization = App.Current.LocalizationService;
            var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
                new WidgetCompactConfirmationOptions(
                    localization.Format("Widget.FeatureWidget.DisableConfirmTitle", _config.Name),
                    localization.T("Widget.FeatureWidget.Disable"),
                    async () => await widgetManager.SetFeatureWidgetEnabledAsync(_config.WidgetKind, enabled: false, reveal: false))
                {
                    Message = localization.T("Widget.FeatureWidget.DisableConfirmNote"),
                    MessageGlyph = "\uE946",
                    IsDangerAction = false,
                    CancelText = localization.T("Common.Cancel")
                });
            ShowFlyoutWithInteraction(flyout, ContentWidgetShell.CloseActionButton);
            return;
        }

        HideWindow();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? ContentWidgetShell.MoreActionButton;
        ShowFlyoutWithInteraction(CreateMoreFlyout(), target);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!_config.IsPositionLocked);
        ApplyLockActionIconState();
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!_config.IsSizeLocked);
        ApplyLockActionIconState();
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ContentWidgetShell.IsCollapsed)
        {
            ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell, e.GetPosition(ContentWidgetShell));
        }
        else
        {
            ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell.TitleBar, e.GetPosition(ContentWidgetShell.TitleBar));
        }
        e.Handled = true;
    }

    private void ContentWidgetShell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Item-level context menus mark the event handled before it reaches the shell.
        // Any remaining right click is therefore on the content background and should
        // expose the same widget actions as the title bar.
        ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell, e.GetPosition(ContentWidgetShell));
        e.Handled = true;
    }

    private void ContentWidgetShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        CancelPendingTitleBarClickCollapse();
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsDragging || IsResizing || ContentWidgetShell.TitleEditorContent is not null)
            {
                return;
            }

            StartTitleRename();
        });
    }

    // ── Flyout ─────────────────────────────────────────────────

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        var rename = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        bool startRenameWhenClosed = false;
        bool showCloseWhenClosed = false;
        rename.Click += (_, _) => startRenameWhenClosed = true;
        flyout.Closed += (_, _) =>
        {
            if (startRenameWhenClosed)
            {
                DispatcherQueue.TryEnqueue(StartTitleRename);
            }
            else if (showCloseWhenClosed)
            {
                DispatcherQueue.TryEnqueue(ShowCloseWidgetFlyout);
            }
        };
        flyout.Items.Add(rename);

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            _config,
            _descriptor,
            App.Current.LocalizationService,
            App.Current.WidgetManager,
            SetChromeModeOverride));
        flyout.Items.Add(WidgetCollapseMenuBuilder.Create(
            _config,
            App.Current.LocalizationService,
            SetCollapseBehaviorOverride,
            ResetCompactWidthOverride));
        flyout.Items.Add(WidgetLockMenuBuilder.Create(
            App.Current.LocalizationService,
            _config.IsPositionLocked,
            _config.IsSizeLocked,
            SetPositionLocked,
            SetSizeLocked));

        WidgetGroupMenuBuilder.Append(
            flyout,
            _config,
            App.Current.WidgetManager,
            App.Current.LocalizationService);

        flyout.Items.Add(new MenuFlyoutSeparator());
        var disableWidget = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Widget.FeatureWidget.Disable"),
            Icon = new FontIcon { Glyph = "\uE7E8" }
        };
        disableWidget.Click += (_, _) => showCloseWhenClosed = true;
        flyout.Items.Add(disableWidget);

        return flyout;
    }

    private void ShowCloseWidgetFlyout()
    {
        if (_isCloseWidgetPending ||
            App.Current.WidgetManager is not { } widgetManager)
        {
            return;
        }

        MenuFlyout flyout = _config.WidgetKind == WidgetKind.File
            ? CreateFileWidgetCloseFlyout(widgetManager)
            : CreateFeatureWidgetCloseFlyout(widgetManager);
        ShowFlyoutWithInteraction(
            flyout,
            ContentWidgetShell.MoreActionButton);
    }

    private MenuFlyout CreateFeatureWidgetCloseFlyout(
        WidgetManager widgetManager)
    {
        LocalizationService localization = App.Current.LocalizationService;
        return WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                localization.Format(
                    "Widget.FeatureWidget.DisableConfirmTitle",
                    _config.Name),
                localization.T("Widget.FeatureWidget.Disable"),
                async () =>
                {
                    if (_isCloseWidgetPending)
                    {
                        return;
                    }

                    _isCloseWidgetPending = true;
                    try
                    {
                        await widgetManager.SetFeatureWidgetEnabledAsync(
                            _config.WidgetKind,
                            enabled: false,
                            reveal: false);
                    }
                    finally
                    {
                        _isCloseWidgetPending = false;
                    }
                })
            {
                Message = localization.T(
                    "Widget.FeatureWidget.DisableConfirmNote"),
                MessageGlyph = "\uE946",
                IsDangerAction = false,
                CancelText = localization.T("Common.Cancel")
            });
    }

    private MenuFlyout CreateFileWidgetCloseFlyout(
        WidgetManager widgetManager)
    {
        LocalizationService localization = App.Current.LocalizationService;
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = localization.Format(
                "Widget.DeleteWidgetTitle",
                _config.Name),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = false
        });
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!widgetManager.CanCleanupManagedStorageForWidget(_config.Id))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = localization.T("Widget.DeleteWidgetNote"),
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            });
            flyout.Items.Add(CreateFileWidgetCloseAction(
                localization.T("Widget.DeleteWidgetConfirm"),
                WidgetRemovalAction.RemoveWidgetOnly));
            flyout.Items.Add(CreateFileWidgetCloseCancel(flyout));
            return flyout;
        }

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = localization.T("Widget.DeleteManagedInfo"),
            Icon = new FontIcon { Glyph = "\uE8B7" },
            IsEnabled = false
        });
        flyout.Items.Add(CreateFileWidgetCloseAction(
            localization.T("Widget.KeepManagedFolder"),
            WidgetRemovalAction.RemoveWidgetOnly,
            "\uE8B7",
            isDanger: false));
        flyout.Items.Add(CreateFileWidgetCloseAction(
            localization.T("Widget.MoveBackThenDeleteFolder"),
            WidgetRemovalAction.MoveManagedFolderContentsToDesktop,
            "\uE8CA",
            isDanger: false));
        flyout.Items.Add(CreateFileWidgetCloseAction(
            localization.T("Widget.DeleteFolderToRecycleBin"),
            WidgetRemovalAction.DeleteManagedFolder));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateFileWidgetCloseCancel(flyout));
        return flyout;
    }

    private MenuFlyoutItem CreateFileWidgetCloseAction(
        string text,
        WidgetRemovalAction removalAction,
        string glyph = "\uE74D",
        bool isDanger = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        if (isDanger)
        {
            icon.Foreground = new SolidColorBrush(Colors.Red);
        }

        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon
        };
        item.Click += async (_, _) =>
        {
            if (_isCloseWidgetPending ||
                App.Current.WidgetManager is not { } widgetManager)
            {
                return;
            }

            _isCloseWidgetPending = true;
            try
            {
                await widgetManager.RemoveWidgetAsync(
                    _config.Id,
                    removalAction);
            }
            catch (Exception ex)
            {
                _isCloseWidgetPending = false;
                App.Log(
                    $"[ContentWidget] Close widget failed id={_config.Id}: {ex}");
                await ShowErrorDialogAsync(
                    App.Current.LocalizationService.T(
                        "Widget.DeleteWidgetFailed"),
                    ex.Message);
            }
        };
        return item;
    }

    private MenuFlyoutItem CreateFileWidgetCloseCancel(MenuFlyout flyout)
    {
        var item = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Cancel"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        item.Click += (_, _) => flyout.Hide();
        return item;
    }

    private void ShowTodoClearAllConfirmation()
    {
        if (_contentHost.CurrentContent?.View is TodoWidgetContent todoContent)
        {
            todoContent.ShowClearAllConfirmation(ContentWidgetShell.MoreActionButton);
        }
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        if (App.Current.WidgetManager is { } manager &&
            manager.IsWidgetGrouped(_config.Id))
        {
            if (manager.SetWidgetGroupChromeMode(_config, mode))
            {
                ApplyAppearancePreview();
            }
            return;
        }

        WidgetChromeModeNames.SetOverrideMode(_config, mode);
        SettingsService.UpdateWidget(_config);
        ApplyAppearancePreview();
    }

    private static string GetSettingsMenuTextKey(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.Todo => "Todo.OpenSettings",
            WidgetKind.Music => "Music.OpenSettings",
            _ => "Common.Configure"
        };
    }

    private static ToggleMenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsChecked = isChecked
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }

    private void SetPositionLocked(bool value)
    {
        if (_config.IsPositionLocked == value)
        {
            return;
        }

        _config.IsPositionLocked = value;
        SettingsService.UpdateWidget(_config);
        SynchronizeWidgetGroupLayout();
        ApplyLockActionIconState();
    }

    private void SetSizeLocked(bool value)
    {
        if (_config.IsSizeLocked == value)
        {
            return;
        }

        _config.IsSizeLocked = value;
        SettingsService.UpdateWidget(_config);
        SynchronizeWidgetGroupLayout();
        ApplyLockActionIconState();
    }

    // ── Title rename ───────────────────────────────────────────

    private void StartTitleRename()
    {
        if (IsDragging ||
            IsResizing ||
            ContentWidgetShell.TitleEditorContent is not null)
        {
            return;
        }

        _isCancellingTitleRename = false;
        BeginCompactInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction("content-title-rename-opened");
        var editor = CreateTitleRenameEditor();
        ContentWidgetShell.TitleEditorContent = editor;
        HoldTemporaryTopMost();
        AppWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(HWnd);
        FocusTitleRenameEditor(editor);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(ContentWidgetShell.TitleEditorContent, editor))
            {
                base.Activate();
                Win32Helper.SetForegroundWindow(HWnd);
                FocusTitleRenameEditor(editor);
            }
        });
    }

    private static void FocusTitleRenameEditor(TextBox editor)
    {
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private TextBox CreateTitleRenameEditor()
    {
        var localization = App.Current.LocalizationService;
        double titleWidth = ContentWidgetShell.TitleTextElement.ActualWidth > 0
            ? ContentWidgetShell.TitleTextElement.ActualWidth + 36
            : (_titleViewModel.DisplayName.Length * 9.5) + 36;

        var editor = new TextBox
        {
            Text = _titleViewModel.DisplayName,
            PlaceholderText = localization.T("Widget.TitlePlaceholder"),
            Width = Math.Clamp(titleWidth, 120, 220),
            MaxWidth = 220,
            FontSize = Math.Max(ContentWidgetShell.TitleTextElement.FontSize - 1, 11),
            Style = GetTextBoxStyleResource("WidgetTitleRenameTextBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        editor.KeyDown += TitleRenameEditor_KeyDown;
        editor.LostFocus += TitleRenameEditor_LostFocus;
        return editor;
    }

    private static Style? GetTextBoxStyleResource(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Style style
            ? style
            : null;
    }

    private async void TitleRenameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitTitleRenameAsync();
    }

    private async void TitleRenameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitTitleRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelTitleRename();
            e.Handled = true;
        }
    }

    private async Task CommitTitleRenameAsync()
    {
        if (_isCommittingTitleRename ||
            ContentWidgetShell.TitleEditorContent is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await App.Current.WidgetManager!.RenameWidgetAsync(_config.Id, newName);
                _titleViewModel.RefreshDisplayName();
            }

            CompleteTitleRename("content-title-rename-committed");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(App.Current.LocalizationService.T("Widget.RenameFailed"), ex.Message);
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelTitleRename()
    {
        _isCancellingTitleRename = true;
        CompleteTitleRename("content-title-rename-canceled");
    }

    private void CompleteTitleRename(string reason)
    {
        if (ContentWidgetShell.TitleEditorContent is TextBox editor)
        {
            editor.KeyDown -= TitleRenameEditor_KeyDown;
            editor.LostFocus -= TitleRenameEditor_LostFocus;
        }

        ContentWidgetShell.TitleEditorContent = null;
        EndCompactInteraction();
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayerFromManager();
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var localization = App.Current.LocalizationService;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxWidth = 320
            },
            CloseButtonText = localization.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void ShowFlyoutWithInteraction(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        BeginCompactInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction("content-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            EndCompactInteraction();
            App.Current.WidgetManager?.EndWidgetInteraction("content-flyout-closed");
            if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer("content-flyout-closed") == true)
            {
                return;
            }

            RestoreDesktopLayerFromManager();
        };

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    // ── Color helpers ──────────────────────────────────────────
}

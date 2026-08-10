using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { } item)
        {
            return;
        }

        if (item.IsRecent)
        {
            await RunAsync(() => ViewModel.PinRecentItemAsync(item));
            RaiseFeedback(
                "已保存并置顶",
                WidgetFeedbackSeverity.Success,
                "quick-capture-recent-pinned");
            return;
        }

        await RunAsync(() => ViewModel.TogglePinnedAsync(item));
        PinButton.Label = item.IsPinned ? "取消置顶" : "置顶";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentItem(includeHtml: true, markdownSourceOnly: false);
    }

    private void CopyMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        CopyCurrentItem(includeHtml: false, markdownSourceOnly: true);
    }

    private void CopyCurrentItem(bool includeHtml, bool markdownSourceOnly)
    {
        if (_selectedItem is not { } item)
        {
            return;
        }

        string source = _isEditing ? EditorBodyTextBox.Text : item.Body;
        string? title = _isEditing ? EditorTitleTextBox.Text : item.Title;
        QuickCaptureContentFormat format = _isEditing ? _editingFormat : item.ContentFormat;
        string plainText = _markdownService.ToPlainText(source, format);
        if (!string.IsNullOrWhiteSpace(title))
        {
            plainText = string.IsNullOrWhiteSpace(plainText)
                ? title.Trim()
                : title.Trim() + Environment.NewLine + Environment.NewLine + plainText;
        }

        string markdown = string.IsNullOrWhiteSpace(title)
            ? source
            : $"# {title.Trim()}\n\n{source}";
        string clipboardText = markdownSourceOnly ? markdown : plainText;
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(clipboardText);
        if (includeHtml)
        {
            string html = _markdownService.ToHtml(
                source,
                format,
                GetCurrentAttachmentModels(),
                _settingsService.Settings.QuickCaptureAllowRemoteImages);
            if (!string.IsNullOrWhiteSpace(title))
            {
                html = $"<h1>{System.Net.WebUtility.HtmlEncode(title.Trim())}</h1>{html}";
            }
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        }

        DeskBoxClipboardWriteScope.MarkWrite(text: clipboardText);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        App.Current.QuickCaptureService?.MarkClipboardTextWrittenByDeskBox(clipboardText);
        RaiseFeedback(
            markdownSourceOnly ? "已复制 Markdown" : "已复制纯文本和 HTML",
            WidgetFeedbackSeverity.Success,
            "quick-capture-copy");
    }

    private async void EditTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { IsRecent: false } item)
        {
            return;
        }

        var input = new TextBox
        {
            AcceptsReturn = true,
            Width = GetDialogContentWidth(300),
            MinWidth = 0,
            MinHeight = 72,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "用逗号或换行分隔标签",
            Text = string.Join(", ", item.Tags),
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑标签",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string[] tags = input.Text
            .Split([',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await ViewModel.SetTagsAsync(item.Id, tags);
        await ViewModel.RefreshItemsAsync();
        RaiseFeedback("标签已更新", WidgetFeedbackSeverity.Success, "quick-capture-tags");
    }

    private async void AppearanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse(tag, true, out QuickCaptureAppearancePreset preset))
        {
            return;
        }

        _editingAppearance = preset;
        ApplyNoteAppearance(preset);
        if (_isEditing)
        {
            MarkEditorChanged();
            return;
        }

        if (_selectedItem is { IsRecent: false } item)
        {
            await ViewModel.SetAppearanceAsync(item, preset);
            await ViewModel.RefreshItemsAsync();
            ApplyNoteAppearance(preset);
        }
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsService.Settings.QuickCaptureRevisionHistoryEnabled)
        {
            RaiseFeedback("版本历史已在设置中关闭。", deduplicationKey: "quick-capture-history-disabled");
            return;
        }

        if (_selectedItem is not { IsRecent: false } item)
        {
            return;
        }

        await ForceCommitAsync(returnToReading: true);
        IReadOnlyList<QuickCaptureRevision> revisions = await ViewModel.GetRevisionsAsync(item.Id, 50);
        if (revisions.Count == 0)
        {
            RaiseFeedback("还没有可恢复的历史版本。", deduplicationKey: "quick-capture-history-empty");
            return;
        }

        var list = new ListView
        {
            ItemsSource = revisions,
            DisplayMemberPath = nameof(QuickCaptureRevision.CreatedAt),
            SelectionMode = ListViewSelectionMode.Single,
            Width = GetDialogContentWidth(320),
            MinWidth = 0,
            MaxHeight = GetDialogContentMaxHeight(420),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        list.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "版本历史",
            Content = list,
            PrimaryButtonText = "恢复所选版本",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            list.SelectedItem is QuickCaptureRevision revision &&
            await ViewModel.RestoreRevisionAsync(item.Id, revision.Id))
        {
            await ViewModel.RefreshItemsAsync();
            RaiseFeedback("版本已恢复", WidgetFeedbackSeverity.Success, "quick-capture-history-restore");
        }
    }

    private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { IsRecent: false } item)
        {
            return;
        }

        await ForceCommitAsync(returnToReading: true);
        await ViewModel.SetArchivedAsync([item.Id], archived: true);
        _selectedItem = null;
        ItemsList.SelectedItem = null;
        _showDetailInSinglePane = false;
        ShowReadingSurface();
        ApplyResponsiveLayout();
        RaiseFeedback("已归档", WidgetFeedbackSeverity.Success, "quick-capture-archive");
    }

    private async void ArchivedButton_Click(object sender, RoutedEventArgs e)
    {
        QuickCaptureStoreData data = await ViewModel.GetDataAsync();
        QuickCaptureItem[] archived = data.Items
            .Where(item => !item.IsDeleted && item.ArchivedAt is not null)
            .OrderByDescending(item => item.ArchivedAt)
            .ToArray();
        if (archived.Length == 0)
        {
            RaiseFeedback("归档中没有随记。", deduplicationKey: "quick-capture-archive-empty");
            return;
        }

        var list = new ListView
        {
            ItemsSource = archived,
            DisplayMemberPath = nameof(QuickCaptureItem.Body),
            SelectionMode = ListViewSelectionMode.Single,
            Width = GetDialogContentWidth(320),
            MinWidth = 0,
            MaxHeight = GetDialogContentMaxHeight(420),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        list.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "已归档随记",
            Content = list,
            PrimaryButtonText = "移出归档",
            CloseButtonText = "关闭"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            list.SelectedItem is QuickCaptureItem item)
        {
            await ViewModel.SetArchivedAsync([item.Id], archived: false);
            await ViewModel.RefreshItemsAsync();
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not { } item)
        {
            return;
        }

        await DeleteItemsWithUndoAsync([item]);
    }

    private async Task DeleteItemsWithUndoAsync(IReadOnlyList<QuickCaptureItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        bool useTrash = _settingsService.Settings.QuickCaptureTrashEnabled;
        if (!useTrash)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = items.Count == 1 ? "永久删除这条随记？" : $"永久删除 {items.Count} 条随记？",
                Content = "正文、版本和托管附件都将删除，且无法恢复。",
                PrimaryButtonText = "永久删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ForceCommitAsync(returnToReading: true);
        var snapshots = new List<QuickCaptureDeletedItemSnapshot>();
        foreach (QuickCaptureItemViewModel item in items)
        {
            QuickCaptureDeletedItemSnapshot? snapshot = await ViewModel.DeleteItemAsync(item);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        if (snapshots.Count == 0)
        {
            return;
        }

        if (!useTrash)
        {
            foreach (QuickCaptureDeletedItemSnapshot snapshot in snapshots)
            {
                await ViewModel.DeletePermanentlyAsync(snapshot.Item.Id);
            }
            _selectedItem = null;
            ItemsList.SelectedItem = null;
            _showDetailInSinglePane = false;
            ShowReadingSurface();
            ApplyResponsiveLayout();
            RaiseFeedback("随记已永久删除", WidgetFeedbackSeverity.Success, "quick-capture-delete-permanent");
            return;
        }

        _selectedItem = null;
        ItemsList.SelectedItem = null;
        _showDetailInSinglePane = false;
        ShowReadingSurface();
        ApplyResponsiveLayout();
        RaiseFeedback(
            snapshots.Count == 1 ? "已移到回收站" : $"已将 {snapshots.Count} 条随记移到回收站",
            WidgetFeedbackSeverity.Info,
            "quick-capture-delete",
            "撤销",
            async () =>
            {
                foreach (QuickCaptureDeletedItemSnapshot snapshot in snapshots)
                {
                    await ViewModel.RestoreDeletedItemAsync(snapshot);
                }
                await ViewModel.RefreshItemsAsync();
            },
            TimeSpan.FromSeconds(8));
    }

    private async void TrashButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<QuickCaptureItem> trash = await ViewModel.GetTrashAsync();
        if (trash.Count == 0)
        {
            RaiseFeedback("回收站是空的。", deduplicationKey: "quick-capture-trash-empty");
            return;
        }

        var list = new ListView
        {
            ItemsSource = trash,
            DisplayMemberPath = nameof(QuickCaptureItem.Body),
            SelectionMode = ListViewSelectionMode.Single,
            Width = GetDialogContentWidth(320),
            MinWidth = 0,
            MaxHeight = GetDialogContentMaxHeight(420),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        list.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "回收站",
            Content = list,
            PrimaryButtonText = "恢复",
            SecondaryButtonText = "永久删除",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (list.SelectedItem is not QuickCaptureItem selected)
        {
            return;
        }

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RestoreTrashItemAsync(selected.Id);
            await ViewModel.RefreshItemsAsync();
            return;
        }

        if (result != ContentDialogResult.Secondary)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "永久删除这条随记？",
            Content = "正文、版本和托管附件都将删除，且无法恢复。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeletePermanentlyAsync(selected.Id);
        }
    }

    private async void BulkPinButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ids = GetBulkSelectedItems().Select(item => item.Id).ToArray();
        if (ids.Length > 0)
        {
            await ViewModel.SetPinnedAsync(ids, isPinned: true);
        }
    }

    private async void BulkArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ids = GetBulkSelectedItems().Select(item => item.Id).ToArray();
        if (ids.Length > 0)
        {
            await ViewModel.SetArchivedAsync(ids, archived: true);
            SetBulkSelectionMode(enable: false);
        }
    }

    private async void BulkDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteItemsWithUndoAsync(GetBulkSelectedItems());
        if (_isBulkSelectionMode)
        {
            SetBulkSelectionMode(enable: false);
        }
    }

    private async void BulkExportButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportItemsAsync(GetBulkSelectedItems());
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetBulkSelectedItems() =>
        ItemsList.SelectedItems.OfType<QuickCaptureItemViewModel>().ToArray();

    private double GetDialogContentWidth(double preferredWidth)
    {
        double rootWidth = XamlRoot?.Size.Width ?? ActualWidth;
        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            return preferredWidth;
        }

        return Math.Max(120, Math.Min(preferredWidth, rootWidth - 64));
    }

    private double GetDialogContentMaxHeight(double preferredHeight)
    {
        double rootHeight = XamlRoot?.Size.Height ?? ActualHeight;
        if (!double.IsFinite(rootHeight) || rootHeight <= 0)
        {
            return preferredHeight;
        }

        return Math.Max(140, Math.Min(preferredHeight, rootHeight - 190));
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettings("QuickCaptureSettings");
    }

    private async void AttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TodoAttachmentViewModel attachment } &&
            File.Exists(attachment.FilePath))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
    }
}

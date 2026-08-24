using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void DetailCopyButton_Click(object sender, RoutedEventArgs e)
    {
        string sourceContent = string.IsNullOrWhiteSpace(DetailBodyTextBox.Text)
            ? DetailTitleTextBox.Text
            : DetailBodyTextBox.Text;
        string copyContent = _detailContentFormat == TextContentFormat.Markdown
            ? _markdownDocumentService.ToPlainText(sourceContent)
            : sourceContent;
        IEnumerable<TodoAttachment> attachments = _detailItem?.Attachments
            .Select(attachment => attachment.Attachment) ??
            _pendingDetailAttachments.Select(file => new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path)
            });
        string text = QuickCaptureClipboardFormatter.FormatContent(
            copyContent,
            attachments,
            _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        if (_detailContentFormat == TextContentFormat.Markdown &&
            !string.IsNullOrWhiteSpace(sourceContent))
        {
            string html = _markdownDocumentService.ToSafeHtml(sourceContent);
            if (!string.IsNullOrWhiteSpace(html))
            {
                dataPackage.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
            }
        }
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
        App.Current.QuickCaptureService?.MarkClipboardTextWrittenByDeskBox(text);
        ShowCopyToast();
    }

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        var droppedFiles = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
            .ToList();
        await AddFilesToCurrentDetailAsync(droppedFiles);
    }

    private async Task AddFilesToCurrentDetailAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0 || !_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            foreach (DroppedFilePath file in files)
            {
                if (!_pendingDetailAttachments.Any(existing =>
                        string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingDetailAttachments.Add(file);
                }
            }
            RefreshDetailAttachmentList();
            MarkDetailDirty();
            _detailAutoSaveTimer?.Stop();
            _detailAutoSaveTimer?.Start();
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(_detailItem, files);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void DetailOpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        try
        {
            if (!File.Exists(attachment.FilePath))
            {
                ShowStatusToast(_localizationService.T("Todo.Detail.FileMissing"));
                return;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to open attachment: {ex}");
        }
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            int removed = _pendingDetailAttachments.RemoveAll(file =>
                string.Equals(file.Path, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshDetailAttachmentList();
            if (removed > 0)
            {
                MarkDetailDirty();
                _detailAutoSaveTimer?.Stop();
                _detailAutoSaveTimer?.Start();
            }
            return;
        }

        if (_detailItem.Attachments.Count == 1 && string.IsNullOrWhiteSpace(DetailBodyTextBox.Text))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.DeleteAttachmentAsync(
            _detailItem,
            attachment.Id);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void QuickCaptureAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private void RefreshDetailAttachmentList()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments = _detailItem?.Attachments ??
            _pendingDetailAttachments.Select(file => new TodoAttachmentViewModel(new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path),
                StorageMode = TodoAttachment.LinkedStorageMode
            })).ToList();
        // ItemsSource is object-valued at the WinRT ABI boundary. Use the same
        // concrete projection as the active embedded Quick Capture surface.
        DetailAttachmentsList.ItemsSource = attachments.Cast<object>().ToArray();
        DetailAttachmentScroller.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void QuickCaptureAttachmentCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            !attachment.IsImage ||
            !File.Exists(attachment.FilePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true, paths: [attachment.FilePath]);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy attachment image: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }
}

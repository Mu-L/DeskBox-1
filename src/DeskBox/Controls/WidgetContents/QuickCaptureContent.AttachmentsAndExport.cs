using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private IReadOnlyList<TodoAttachment> GetCurrentAttachmentModels()
    {
        return _selectedItem?.Attachments
            .Select(attachment => attachment.Attachment)
            .ToArray() ?? [];
    }

    private async void AddAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Win32Helper.GetForegroundWindow());
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        var droppedFiles = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
            .ToArray();
        await AddAttachmentsToEditorAsync(droppedFiles);
    }

    private bool ClipboardContainsImportableAttachment()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Bitmap) ||
                content.Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
        }
    }

    private async Task ImportClipboardAttachmentsAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(content);
        await AddAttachmentsToEditorAsync(batch.Files);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            e.DataView.Contains(DeskBoxDragData.TextFormat) ||
            e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            bool hasFiles = DeskBoxDragData.HasDroppedFiles(e.DataView);
            bool readOnlyClipboardTarget = hasFiles &&
                IsDropOnDetail(e) &&
                _selectedItem is { IsRecent: true };
            e.AcceptedOperation = readOnlyClipboardTarget
                ? DataPackageOperation.None
                : hasFiles
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                    : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = readOnlyClipboardTarget
                ? "先保存为随记，再关联文件"
                : hasFiles
                    ? IsDropOnDetail(e) && _selectedItem is not null
                        ? "关联到当前随记"
                        : "创建带附件的随记"
                    : "创建随记";
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count == 0)
                {
                    string? fallbackText = await DeskBoxDragData.TryGetTextAsync(e.DataView);
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        await ViewModel.AddTextAsync(fallbackText);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        e.Handled = true;
                    }
                    return;
                }

                QuickCaptureItemViewModel? result;
                if (IsDropOnDetail(e) && _selectedItem is { } selected)
                {
                    if (selected.IsRecent)
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                        e.Handled = true;
                        RaiseFeedback(
                            "剪贴板内容只读，请先保存为随记",
                            WidgetFeedbackSeverity.Info,
                            "quick-capture-recent-readonly-drop");
                        return;
                    }
                    result = await ViewModel.AddAttachmentsAsync(selected, batch.Files);
                    if (result is not null)
                    {
                        _selectedItem = result;
                        ItemsList.SelectedItem = result;
                        RenderReadingSurface();
                        RefreshMarkdownPreview();
                    }
                }
                else
                {
                    result = await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
                    if (result is not null)
                    {
                        await OpenItemAsync(result, edit: false);
                    }
                }

                e.AcceptedOperation = result is null
                    ? DataPackageOperation.None
                    : DeskBoxDragData.GetFileAssociationOperation(e.DataView);
                e.Handled = result is not null;
                if (result is not null)
                {
                    RaiseFeedback("文件已关联", WidgetFeedbackSeverity.Success, "quick-capture-root-drop");
                }
                return;
            }

            string? text = await DeskBoxDragData.TryGetTextAsync(e.DataView);
            if (!string.IsNullOrWhiteSpace(text))
            {
                await ViewModel.AddTextAsync(text);
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
                RaiseFeedback("已创建随记", WidgetFeedbackSeverity.Success, "quick-capture-text-drop");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureContent] Drop failed: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
            RaiseFeedback("拖入内容失败", WidgetFeedbackSeverity.Error, "quick-capture-drop-error");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool IsDropOnDetail(DragEventArgs e)
    {
        if (DetailPane.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (ListPane.Visibility != Visibility.Visible)
        {
            return true;
        }

        double detailStart = ListColumn.ActualWidth + DividerColumn.ActualWidth;
        return e.GetPosition(Root).X >= detailStart;
    }

    private async Task AddAttachmentsToEditorAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_selectedItem is { IsRecent: true })
        {
            RaiseFeedback(
                "剪贴板内容只读，请先保存为随记",
                WidgetFeedbackSeverity.Info,
                "quick-capture-recent-readonly-attachment");
            return;
        }

        if (!_isEditing)
        {
            if (_selectedItem is null)
            {
                BeginNewNote();
            }
            else
            {
                await EnterEditModeAsync();
            }
        }

        if (_isCreating || _selectedItem is null)
        {
            foreach (DroppedFilePath file in files)
            {
                if (!_pendingAttachments.Any(existing =>
                    string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingAttachments.Add(file);
                }
            }
            MarkEditorChanged();
            RaiseFeedback(
                $"已添加 {_pendingAttachments.Count} 个待保存附件",
                WidgetFeedbackSeverity.Info,
                "quick-capture-pending-attachments");
            return;
        }

        await SaveEditorAsync();
        QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(_selectedItem, files);
        if (updated is null)
        {
            RaiseFeedback(
                "附件添加失败。",
                WidgetFeedbackSeverity.Error,
                "quick-capture-attachment-failed");
            return;
        }

        _selectedItem = updated;
        _suppressEditorChanges = true;
        EditorBodyTextBox.Text = updated.Body;
        _previousEditorBodyText = updated.Body;
        _suppressEditorChanges = false;
        ItemsList.SelectedItem = updated;
        RenderReadingSurface();
        RefreshMarkdownPreview();
        RaiseFeedback(
            files.Count == 1 ? "附件已添加" : $"已添加 {files.Count} 个附件",
            WidgetFeedbackSeverity.Success,
            "quick-capture-attachment-added");
    }

    private async void ExportNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        await ForceCommitAsync(returnToReading: true);
        await ExportSingleItemWithPickerAsync(_selectedItem);
    }

    private async Task ExportSingleItemWithPickerAsync(QuickCaptureItemViewModel item)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = CreateSafeFileName(
                _markdownService.CreateDerivedTitle(item.Title, item.Body, item.ContentFormat),
                "随记")
        };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        InitializeWithWindow.Initialize(picker, Win32Helper.GetForegroundWindow());
        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        await ExportItemAsync(item.ToModel(), destination.Path);
        RaiseFeedback("随记已导出", WidgetFeedbackSeverity.Success, "quick-capture-export");
    }

    private async void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        QuickCaptureStoreData data = await ViewModel.GetDataAsync();
        QuickCaptureItemViewModel[] items = data.Items
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .Select(item => new QuickCaptureItemViewModel(
                item,
                _localizationService,
                ViewModel.TextSize,
                ViewModel.IconSize,
                null))
            .ToArray();
        await ExportItemsAsync(items);
    }

    private async Task ExportItemsAsync(IReadOnlyList<QuickCaptureItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Win32Helper.GetForegroundWindow());
        StorageFolder? parent = await picker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        string folderName = $"DeskBox 随记 {DateTime.Now:yyyyMMdd-HHmmss}";
        StorageFolder exportFolder = await parent.CreateFolderAsync(
            folderName,
            CreationCollisionOption.GenerateUniqueName);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (QuickCaptureItemViewModel item in items)
        {
            string baseName = CreateSafeFileName(
                _markdownService.CreateDerivedTitle(item.Title, item.Body, item.ContentFormat),
                "随记");
            string fileName = GetUniqueFileName(baseName, ".md", usedNames);
            await ExportItemAsync(item.ToModel(), Path.Combine(exportFolder.Path, fileName));
        }

        RaiseFeedback(
            $"已导出 {items.Count} 条随记",
            WidgetFeedbackSeverity.Success,
            "quick-capture-export-all");
    }

    private async Task ExportItemAsync(QuickCaptureItem item, string destinationPath)
    {
        string body = item.Body;
        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        string baseName = Path.GetFileNameWithoutExtension(destinationPath);
        string attachmentDirectoryName = baseName + "_files";
        string attachmentDirectory = Path.Combine(destinationDirectory, attachmentDirectoryName);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TodoAttachment attachment in item.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FilePath) || !File.Exists(attachment.FilePath))
            {
                continue;
            }

            Directory.CreateDirectory(attachmentDirectory);
            string originalName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? Path.GetFileName(attachment.FilePath)
                : attachment.DisplayName;
            string fileName = GetUniqueFileName(
                CreateSafeFileName(Path.GetFileNameWithoutExtension(originalName), "附件"),
                Path.GetExtension(originalName),
                usedNames);
            string target = Path.Combine(attachmentDirectory, fileName);
            File.Copy(attachment.FilePath, target, overwrite: false);
            string relativeUri = Uri.EscapeDataString(attachmentDirectoryName) + "/" +
                Uri.EscapeDataString(fileName);
            body = body.Replace(
                $"deskbox-attachment://{attachment.Id}",
                relativeUri,
                StringComparison.OrdinalIgnoreCase);
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            builder.Append("# ").AppendLine(item.Title.Trim()).AppendLine();
        }

        if (item.ContentFormat == QuickCaptureContentFormat.Markdown)
        {
            builder.Append(body);
        }
        else
        {
            builder.Append(body);
        }

        await File.WriteAllTextAsync(destinationPath, builder.ToString(), new UTF8Encoding(false));
    }

    private static string CreateSafeFileName(string? value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.');
        return name.Length switch
        {
            0 => fallback,
            > 80 => name[..80].TrimEnd(),
            _ => name
        };
    }

    private static string GetUniqueFileName(
        string baseName,
        string extension,
        ISet<string> usedNames)
    {
        extension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
        string candidate = baseName + extension;
        int suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName} ({suffix++}){extension}";
        }

        return candidate;
    }
}

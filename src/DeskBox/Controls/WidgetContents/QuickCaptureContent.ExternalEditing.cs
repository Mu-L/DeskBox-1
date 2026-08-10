using System.Diagnostics;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private static readonly string QuickCaptureTextPreviewDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "QuickCapture",
        "Preview");

    private async Task OpenTextInNotepadAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent || item.Type == QuickCaptureItemType.Image)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(QuickCaptureTextPreviewDirectory);
            string previewPath = Path.Combine(
                QuickCaptureTextPreviewDirectory,
                BuildTextPreviewFileName(item));
            await File.WriteAllTextAsync(previewPath, item.Body);

            var startInfo = new ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(previewPath);
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("无法启动记事本。");
            }

            await process.WaitForExitAsync();
            string editedBody = await File.ReadAllTextAsync(previewPath);
            if (string.Equals(editedBody, item.Body, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(editedBody))
            {
                RaiseFeedback(
                    "内容为空，未覆盖原随记",
                    WidgetFeedbackSeverity.Info,
                    "quick-capture-notepad-empty");
                return;
            }

            bool updated = await ViewModel.EditItemDetailsAsync(
                item,
                item.Title,
                editedBody,
                item.AppearancePreset,
                item.ContentFormat);
            if (!updated)
            {
                throw new InvalidOperationException("记事本中的更改未能保存。");
            }

            string selectedId = _selectedItem?.Id ?? string.Empty;
            await ViewModel.RefreshItemsAsync();
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                _selectedItem = ViewModel.Items.FirstOrDefault(candidate => candidate.Id == selectedId)
                    ?? _selectedItem;
            }
            if (_selectedItem?.Id == item.Id)
            {
                RenderReadingSurface();
            }
            RaiseFeedback(
                "已应用记事本中的更改",
                WidgetFeedbackSeverity.Success,
                "quick-capture-notepad-saved");
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureContent] Notepad editing failed: {ex}");
            RaiseFeedback(
                "无法使用记事本编辑这条随记",
                WidgetFeedbackSeverity.Error,
                "quick-capture-notepad-failed");
        }
    }

    private static string BuildTextPreviewFileName(QuickCaptureItemViewModel item)
    {
        string stem = !string.IsNullOrWhiteSpace(item.Title)
            ? item.Title
            : item.Body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "Quick Capture";
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalidChar, ' ');
        }

        stem = string.Join(' ', stem.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Quick Capture";
        }
        if (stem.Length > 42)
        {
            stem = stem[..42].Trim();
        }

        string extension = item.ContentFormat == QuickCaptureContentFormat.Markdown ? ".md" : ".txt";
        string idSuffix = item.Id[..Math.Min(8, item.Id.Length)];
        return $"{stem}-{idSuffix}{extension}";
    }
}

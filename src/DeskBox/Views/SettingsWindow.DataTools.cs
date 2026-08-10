using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using IOPath = System.IO.Path;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void OpenQuickCaptureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("QuickCaptureSettings");
    }

    private void OpenTodoSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("TodoSettings");
    }

    private async void OpenTodoWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.CreateTodoWidgetAsync(reuseExisting: true);
        }
    }

    private async void ManageTodoCalendarSourcesButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        TodoCalendarSettings calendar = App.Current.SettingsService.Settings.Todo.Calendar;
        var sourcePanel = new StackPanel { Spacing = 6 };
        var addButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = _localizationService.T("Settings.Todo2.CalendarSources.Add")
        };
        sourcePanel.Children.Add(addButton);
        var listPanel = new StackPanel { Spacing = 4 };
        sourcePanel.Children.Add(listPanel);

        void RefreshSources()
        {
            listPanel.Children.Clear();
            foreach (TodoCalendarSourceSettings source in calendar.Sources.ToArray())
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var enabled = new CheckBox { IsChecked = source.IsEnabled, VerticalAlignment = VerticalAlignment.Center };
                enabled.Checked += async (_, _) =>
                {
                    source.IsEnabled = true;
                    await App.Current.SettingsService.SaveAsync();
                    ViewModel.NotifyTodo2SettingsChanged();
                };
                enabled.Unchecked += async (_, _) =>
                {
                    source.IsEnabled = false;
                    await App.Current.SettingsService.SaveAsync();
                    ViewModel.NotifyTodo2SettingsChanged();
                };
                row.Children.Add(enabled);
                var labels = new StackPanel { Spacing = 1 };
                labels.Children.Add(new TextBlock { Text = source.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                labels.Children.Add(new TextBlock
                {
                    Text = source.SourcePath,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(labels, 1);
                row.Children.Add(labels);
                var remove = new Button { Content = new SymbolIcon(Symbol.Delete), MinWidth = 32, Padding = new Thickness(5) };
                remove.Click += async (_, _) =>
                {
                    calendar.Sources.Remove(source);
                    await App.Current.SettingsService.SaveAsync();
                    ViewModel.NotifyTodo2SettingsChanged();
                    RefreshSources();
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                listPanel.Children.Add(row);
            }

            if (calendar.Sources.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = _localizationService.T("Settings.Todo2.CalendarSources.None"),
                    Margin = new Thickness(2, 8, 2, 2),
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
            }
        }

        addButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".ics");
            InitializeWithWindow.Initialize(picker, _hWnd);
            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            foreach (StorageFile file in files)
            {
                if (calendar.Sources.Any(source => string.Equals(source.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                calendar.Sources.Add(new TodoCalendarSourceSettings
                {
                    Name = IOPath.GetFileNameWithoutExtension(file.Name),
                    SourcePath = file.Path,
                    IsEnabled = true
                });
            }
            await App.Current.SettingsService.SaveAsync();
            ViewModel.NotifyTodo2SettingsChanged();
            RefreshSources();
        };
        RefreshSources();

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Todo2.CalendarSources.Title"),
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                MinWidth = 420,
                Content = sourcePanel
            }
        };
        await dialog.ShowAsync();
    }

    private async void BackupTodoDataButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);
        StorageFolder? parent = await picker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        StorageFolder backupFolder = await parent.CreateFolderAsync(
            $"DeskBox Todo {DateTime.Now:yyyyMMdd-HHmmss}",
            CreationCollisionOption.GenerateUniqueName);
        await App.Current.TodoWorkspaceService.CreateBackupAsync(IOPath.Combine(backupFolder.Path, "todo.db"));
        string sourceAttachments = App.Current.TodoWorkspaceService.AttachmentDirectory;
        string destinationAttachments = IOPath.Combine(backupFolder.Path, "attachments");
        if (Directory.Exists(sourceAttachments))
        {
            CopyDirectory(sourceAttachments, destinationAttachments);
        }
        await ShowInfoDialogAsync(
            _localizationService.T("Settings.Todo2.Backup.Title"),
            _localizationService.Format("Settings.Todo2.Backup.Completed", backupFolder.Path));
    }

    private async void ClearTodoDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        TodoWorkspaceSnapshot snapshot = await App.Current.TodoWorkspaceService.LoadSnapshotAsync(includeDeleted: true);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Todo2.Clear.Title"),
            PrimaryButtonText = _localizationService.T("Settings.Todo2.Clear.Action"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format("Settings.Todo2.Clear.Description", snapshot.Tasks.Count),
                TextWrapping = TextWrapping.Wrap
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.Current.TodoWorkspaceService.ClearAsync();
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, IOPath.Combine(destinationDirectory, IOPath.GetFileName(file)), overwrite: false);
        }
        foreach (string child in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(child, IOPath.Combine(destinationDirectory, IOPath.GetFileName(child)));
        }
    }

    private void OpenAppearanceDetailButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("AppearanceDetail");
    }

    private async void ImportQuickCaptureMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        InitializeWithWindow.Initialize(picker, _hWnd);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        int imported = 0;
        var failures = new List<string>();
        foreach (StorageFile file in files)
        {
            try
            {
                if (await App.Current.QuickCaptureService.ImportMarkdownFileAsync(file.Path) is not null)
                {
                    imported++;
                }
                else
                {
                    failures.Add(file.Name);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file.Name}: {ex.Message}");
            }
        }

        string message = failures.Count == 0
            ? $"已导入 {imported} 条随记。"
            : $"已导入 {imported} 条，{failures.Count} 条失败。\n{string.Join(Environment.NewLine, failures.Take(5))}";
        await ShowInfoDialogAsync("导入 Markdown", message);
    }

    private async void ExportQuickCaptureMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);
        StorageFolder? parent = await picker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        QuickCaptureStoreData data = await App.Current.QuickCaptureService.GetDataAsync();
        QuickCaptureItem[] items = data.Items
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ToArray();
        StorageFolder exportFolder = await parent.CreateFolderAsync(
            $"DeskBox 随记 {DateTime.Now:yyyyMMdd-HHmmss}",
            CreationCollisionOption.GenerateUniqueName);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var markdown = new QuickCaptureMarkdownService();
        foreach (QuickCaptureItem item in items)
        {
            string baseName = CreateQuickCaptureExportFileName(
                markdown.CreateDerivedTitle(item.Title, item.Body, item.ContentFormat),
                "随记");
            string fileName = GetUniqueQuickCaptureExportName(baseName, ".md", usedNames);
            await ExportQuickCaptureItemAsync(item, IOPath.Combine(exportFolder.Path, fileName));
        }

        await ShowInfoDialogAsync("导出 Markdown", $"已导出 {items.Length} 条随记。\n{exportFolder.Path}");
    }

    private static async Task ExportQuickCaptureItemAsync(
        QuickCaptureItem item,
        string destinationPath)
    {
        string body = item.Body;
        string destinationDirectory = IOPath.GetDirectoryName(destinationPath)!;
        string baseName = IOPath.GetFileNameWithoutExtension(destinationPath);
        string attachmentFolderName = baseName + "_files";
        string attachmentFolder = IOPath.Combine(destinationDirectory, attachmentFolderName);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TodoAttachment attachment in item.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FilePath) || !File.Exists(attachment.FilePath))
            {
                continue;
            }

            Directory.CreateDirectory(attachmentFolder);
            string original = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? IOPath.GetFileName(attachment.FilePath)
                : attachment.DisplayName;
            string fileName = GetUniqueQuickCaptureExportName(
                CreateQuickCaptureExportFileName(IOPath.GetFileNameWithoutExtension(original), "附件"),
                IOPath.GetExtension(original),
                usedNames);
            File.Copy(attachment.FilePath, IOPath.Combine(attachmentFolder, fileName), overwrite: false);
            body = body.Replace(
                $"deskbox-attachment://{attachment.Id}",
                Uri.EscapeDataString(attachmentFolderName) + "/" + Uri.EscapeDataString(fileName),
                StringComparison.OrdinalIgnoreCase);
        }

        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            output.Append("# ").AppendLine(item.Title.Trim()).AppendLine();
        }
        output.Append(body);
        await File.WriteAllTextAsync(destinationPath, output.ToString(), new UTF8Encoding(false));
    }

    private static string CreateQuickCaptureExportFileName(string? value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in IOPath.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) return fallback;
        return name.Length > 80 ? name[..80].TrimEnd() : name;
    }

    private static string GetUniqueQuickCaptureExportName(
        string baseName,
        string extension,
        ISet<string> usedNames)
    {
        string candidate = baseName + extension;
        int suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName} ({suffix++}){extension}";
        }
        return candidate;
    }

    private async void ClearQuickCaptureDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recordCount = data.Items.Count(item => !item.IsDeleted);
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    recordCount,
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void ClearQuickCaptureRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearRecentAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void CleanupQuickCaptureImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = await App.Current.QuickCaptureService.CleanupUnusedImageCacheAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.QuickCapture.ImageCacheCleanupTitle"),
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.QuickCapture.ImageCacheCleanupDescription",
                    result.DeletedFileCount,
                    ViewModel.FormatBytes(result.DeletedBytes)),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void ShowOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowOnboarding();
    }

    private async void ShowProductReasonButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 560,
            Spacing = 16
        };

        for (int index = 1; index <= 5; index++)
        {
            content.Children.Add(CreateDialogParagraph(
                _localizationService.T($"Settings.Dialog.ProductReasonP{index}")));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.About.ReasonTitle"),
            CloseButtonText = _localizationService.T("Settings.Dialog.ProductReasonClose"),
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        await dialog.ShowAsync();
    }

    private void CleanupManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("ManagedStorage");
    }

    private void RefreshManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshManagedStorageFolderList();
    }

    private void RefreshManagedStorageFolderList()
    {
        ManagedStorageFolderList.Children.Clear();

        if (App.Current.WidgetManager is not { } widgetManager)
        {
            ManagedStorageEmptyState.Visibility = Visibility.Visible;
            ManagedStorageFolderList.Visibility = Visibility.Collapsed;
            ManagedStorageSummaryText.Text = _localizationService.T("Settings.ManagedStorage.SummaryUnavailable");
            return;
        }

        var candidates = widgetManager.GetOrphanManagedStorageFolders();
        bool hasCandidates = candidates.Count > 0;
        ManagedStorageEmptyState.Visibility = hasCandidates ? Visibility.Collapsed : Visibility.Visible;
        ManagedStorageFolderList.Visibility = hasCandidates ? Visibility.Visible : Visibility.Collapsed;
        ManagedStorageSummaryText.Text = hasCandidates
            ? _localizationService.Format("Settings.ManagedStorage.Summary", candidates.Count)
            : _localizationService.T("Settings.ManagedStorage.SummaryEmpty");

        for (int index = 0; index < candidates.Count; index++)
        {
            if (index > 0)
            {
                ManagedStorageFolderList.Children.Add(CreateSettingDivider());
            }

            ManagedStorageFolderList.Children.Add(CreateManagedStorageFolderRow(candidates[index]));
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            CollectResponsiveRows(SettingsRoot);
            UpdateResponsiveLayout(GetWindowWidth());
        });
    }

    private Grid CreateManagedStorageFolderRow(ManagedStorageFolderCleanupCandidate candidate)
    {
        var row = new Grid
        {
            Style = (Style)SettingsRoot.Resources["SettingRowStyle"]
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel
        {
            Style = (Style)SettingsRoot.Resources["SettingTextPanelStyle"]
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Name,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.Format("Settings.ManagedStorage.ItemCount", candidate.ItemCount),
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Path,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        string folderPath = candidate.Path;
        string folderName = candidate.Name;
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.RestoreAction",
            "Settings.ManagedStorage.RestoreTooltip",
            async () => await RestoreManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.OpenAction",
            "Settings.ManagedStorage.OpenTooltip",
            async () => await OpenManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.MoveAction",
            "Settings.ManagedStorage.MoveTooltip",
            async () => await MoveManagedStorageFolderToDesktopAsync(folderPath, folderName)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.DeleteAction",
            "Settings.ManagedStorage.DeleteTooltip",
            async () => await DeleteManagedStorageFolderAsync(folderPath, folderName)));

        row.Children.Add(textPanel);
        row.Children.Add(actionsPanel);
        Grid.SetColumn(actionsPanel, 1);

        return row;
    }

    private Button CreateManagedStorageActionButton(string textKey, string tooltipKey, Func<Task> action)
    {
        var button = new Button
        {
            Style = (Style)SettingsRoot.Resources["CompactTextActionButtonStyle"],
            Content = _localizationService.T(textKey)
        };
        ToolTipService.SetToolTip(button, _localizationService.T(tooltipKey));
        button.Click += async (_, _) => await action();
        return button;
    }

    private Border CreateSettingDivider()
    {
        return new Border
        {
            Style = (Style)SettingsRoot.Resources["SettingDividerStyle"]
        };
    }

    private async Task RestoreManagedStorageFolderAsync(string folderPath)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        try
        {
            int restoredCount = await App.Current.WidgetManager.RestoreOrphanManagedStorageFoldersAsync([folderPath]);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.RestoreCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.RestoreCompleteBody", restoredCount));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task OpenManagedStorageFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.T("Settings.ManagedStorage.MissingFolder"));
            return;
        }

        Win32Helper.OpenFile(folderPath);
    }

    private async Task MoveManagedStorageFolderToDesktopAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.MoveConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveConfirmBody", folderName),
                _localizationService.T("Common.Move")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.MoveCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task DeleteManagedStorageFolderAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteConfirmBody", folderName),
                _localizationService.T("Common.Delete")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.DeleteOrphanManagedStorageFolderAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task<bool> ConfirmManagedStorageActionAsync(string title, string message, string primaryButtonText)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }
}

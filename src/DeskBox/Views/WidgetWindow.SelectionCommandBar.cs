using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void ConfigureSelectionCommandBar()
    {
        FileOpenSelectionButton.Label = _localizationService.T("Common.Open");
        FileCopySelectionButton.Label = _localizationService.T("Common.Copy");
        FileCutSelectionButton.Label = _localizationService.T("Common.Cut");
        FileDeleteSelectionButton.Label = _localizationService.T("Common.Delete");
        FileRenameSelectionButton.Label = _localizationService.T("Common.Rename");
        ToolTipService.SetToolTip(
            FileOpenSelectionButton,
            FileOpenSelectionButton.Label);
        ToolTipService.SetToolTip(
            FileCopySelectionButton,
            FileCopySelectionButton.Label);
        ToolTipService.SetToolTip(
            FileCutSelectionButton,
            FileCutSelectionButton.Label);
        ToolTipService.SetToolTip(
            FileDeleteSelectionButton,
            FileDeleteSelectionButton.Label);
        ToolTipService.SetToolTip(
            FileRenameSelectionButton,
            FileRenameSelectionButton.Label);
        UpdateSelectionCommandBar();
    }

    private void UpdateSelectionCommandBar()
    {
        FileSelectionCommandBar.Visibility = Visibility.Collapsed;
    }

    private void FileOpenSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            ViewModel.OpenItem(item);
        }
    }

    private async void FileCopySelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CopySelectionToClipboardAsync(cut: false);
    }

    private async void FileCutSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CopySelectionToClipboardAsync(cut: true);
    }

    private async void FileDeleteSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await DeleteSelectedItemsAsync();
    }

    private async void FileRenameSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await StartItemRenameAsync(item);
        }
    }
}

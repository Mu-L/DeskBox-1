using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    public IntPtr OwnerWindowHandle { get; set; }

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || _plan is not { EligibleItemCount: > 0 } previewPlan)
        {
            return;
        }

        DesktopOrganizationPlan plan;
        try
        {
            plan = CreateCoordinator().CreateExecutionPlan(
                previewPlan,
                _targetSelections.Values.ToList());
            if (plan.EligibleItemCount == 0)
            {
                ResultInfo.Severity = InfoBarSeverity.Warning;
                ResultInfo.Title = T("DesktopOrganization.Preview.NothingSelectedTitle");
                ResultInfo.Message = T("DesktopOrganization.Preview.NothingSelectedBody");
                ResultInfo.IsOpen = true;
                return;
            }
        }
        catch (Exception ex)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
            return;
        }

        _executionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _executionCts = cts;
        _isExecuting = true;
        RefreshButton.IsEnabled = false;
        ChangePathButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ExecuteButton.IsEnabled = false;
        ExecutionProgressPanel.Visibility = Visibility.Visible;
        ExecutionProgressBar.Maximum = Math.Max(1, plan.EligibleItemCount);
        ExecutionProgressBar.Value = 0;
        ExecutionProgressText.Text = T("DesktopOrganization.Preview.Preparing");
        try
        {
            var progress = new Progress<DesktopOrganizationProgress>(value =>
            {
                ExecutionProgressBar.Value = value.CompletedCount;
                ExecutionProgressText.Text = Format(
                    "DesktopOrganization.Preview.Progress",
                    value.CompletedCount,
                    value.TotalCount,
                    value.TargetDisplayName);
            });
            DesktopOrganizationExecutionResult result =
                await CreateCoordinator().ExecuteAsync(plan, progress, cts.Token);
            _lastHistoryId = result.History.Id;
            ResultInfo.Severity = InfoBarSeverity.Success;
            ResultInfo.Title = T("DesktopOrganization.Result.SuccessTitle");
            ResultInfo.Message = Format(
                "DesktopOrganization.Result.SuccessBody",
                result.History.Items.Count,
                result.History.Targets.Count);
            ResultInfo.IsOpen = true;
            ExecutionProgressPanel.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            ExecuteButton.Visibility = Visibility.Collapsed;
            UndoButton.Visibility = Visibility.Visible;
            DoneButton.Visibility = Visibility.Visible;
            OrganizationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (!_closeAfterExecutionStops)
            {
                ResultInfo.Severity = InfoBarSeverity.Warning;
                ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
                ResultInfo.Message = string.Empty;
                ResultInfo.IsOpen = true;
                ExecuteButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
            ExecuteButton.IsEnabled = true;
        }
        finally
        {
            _isExecuting = false;
            RefreshButton.IsEnabled = true;
            ChangePathButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            if (_closeAfterExecutionStops)
            {
                _closeAfterExecutionStops = false;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || string.IsNullOrWhiteSpace(_lastHistoryId))
        {
            return;
        }

        _isExecuting = true;
        UndoButton.IsEnabled = false;
        DoneButton.IsEnabled = false;
        try
        {
            await CreateCoordinator().UndoAsync(_lastHistoryId);
            ResultInfo.Severity = InfoBarSeverity.Success;
            ResultInfo.Title = T("DesktopOrganization.Undo.Success");
            ResultInfo.Message = string.Empty;
            ResultInfo.IsOpen = true;
            OrganizationUndone?.Invoke(this, EventArgs.Empty);
            RefreshButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            ExecuteButton.Visibility = Visibility.Visible;
            UndoButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;
            await ScanAsync();
        }
        catch (Exception ex)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Undo.Failed");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
        }
        finally
        {
            _isExecuting = false;
            UndoButton.IsEnabled = true;
            DoneButton.IsEnabled = true;
        }
    }

    private async void ChangePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting)
        {
            return;
        }

        string? folderPath = FolderPickerService.PickFolder(OwnerWindowHandle);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        string currentPath = SettingsService.NormalizeManagedStorageRootPath(
            App.Current.SettingsService.Settings.DefaultManagedStorageRootPath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0 && XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = T("Settings.Dialog.MigrateButton"),
                CloseButtonText = T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        currentPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ChangePathButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            if (App.Current.WidgetManager is not null)
            {
                await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
            }

            App.Current.SettingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
            await App.Current.SettingsService.SaveAsync();
            BeginScan();
        }
        catch (Exception ex)
        {
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Window.ChangePathError");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
            ChangePathButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => BeginScan();

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void DoneButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}

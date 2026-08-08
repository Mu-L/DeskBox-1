using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan ImportCardShowDelay =
        TimeSpan.FromMilliseconds(220);
    private CancellationTokenSource? _activeImportCancellation;
    private CancellationTokenSource? _importCardDelayCancellation;
    private ImportCompletionState? _activeImportVisualState;
    private bool _importCardWasShown;

    private CancellationToken ActiveImportCancellationToken =>
        _activeImportCancellation?.Token ?? CancellationToken.None;

    private void BeginTrackedImport()
    {
        CancelAndResetTrackedImport();
        _isImportBusy = true;
        _importBusyStartedAtUtc = DateTimeOffset.UtcNow;
        _activeImportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _importCardDelayCancellation = new CancellationTokenSource();
        _activeImportVisualState = null;
        _importCardWasShown = false;

        ImportProgressCard.Visibility = Visibility.Collapsed;
        ImportCancelButton.Visibility = Visibility.Visible;
        ImportCancelButton.IsEnabled = true;
        ImportCancelButton.IsTabStop = true;
        ToolTipService.SetToolTip(ImportCancelButton, T("Common.Cancel"));
        AutomationProperties.SetName(ImportCancelButton, T("Common.Cancel"));
        ImportTitleText.Text = T("Widget.Import.Preparing");
        ImportDescriptionText.Text = T("Widget.Import.Description");
        ImportPercentText.Text = string.Empty;
        ImportProgressBar.IsIndeterminate = true;
        ImportProgressBar.Value = 0;
        ImportStateIcon.Foreground = ImportProgressBar.Foreground;
        VisualStateManager.GoToState(this, "ImportPreparingState", false);
        SelectionCommandBar.IsEnabled = false;
        ApplyDropVisual(FileDropVisualState.None);
        ImportBusyChanged?.Invoke(true);

        _ = ShowImportCardAfterDelayAsync(_importCardDelayCancellation.Token);
    }

    private async Task ShowImportCardAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ImportCardShowDelay, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !_isImportBusy)
            {
                return;
            }

            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ShowImportCard);
                return;
            }

            ShowImportCard();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowImportCard()
    {
        if (!_isImportBusy || _isDisposed)
        {
            return;
        }

        _importCardWasShown = true;
        ImportProgressCard.Visibility = Visibility.Visible;
    }

    private void ReportImportProgress(FileService.FileTransferProgress progress)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ReportImportProgress(progress));
            return;
        }

        if (!_isImportBusy || _isDisposed)
        {
            return;
        }

        switch (progress.Phase)
        {
            case FileService.FileTransferPhase.Preparing:
                ImportTitleText.Text = T("Widget.Import.Preparing");
                VisualStateManager.GoToState(
                    this,
                    "ImportPreparingState",
                    false);
                break;
            case FileService.FileTransferPhase.Finalizing:
                ImportTitleText.Text = T("Widget.Import.Finalizing");
                VisualStateManager.GoToState(
                    this,
                    "ImportLoadingState",
                    false);
                break;
            case FileService.FileTransferPhase.Transferring:
                ImportTitleText.Text = string.IsNullOrWhiteSpace(
                    progress.CurrentItemName)
                    ? T("Widget.Import.Title")
                    : progress.CurrentItemName;
                VisualStateManager.GoToState(
                    this,
                    "ImportLoadingState",
                    false);
                break;
        }

        double? percentage = progress.Percentage;
        ImportProgressBar.IsIndeterminate = percentage is null;
        if (percentage is { } value)
        {
            ImportProgressBar.Value = value;
            ImportPercentText.Text = $"{value:0}%";
        }
        else
        {
            ImportPercentText.Text = string.Empty;
        }

        ImportDescriptionText.Text = FormatImportProgressDetails(progress);
    }

    private string FormatImportProgressDetails(
        FileService.FileTransferProgress progress)
    {
        var parts = new List<string>();
        if (progress.TotalItems > 0)
        {
            parts.Add(_localizationService.Format(
                "Widget.Import.Progress.Items",
                progress.CompletedItems,
                progress.TotalItems));
        }

        if (progress.TotalBytes is { } totalBytes)
        {
            parts.Add(
                $"{FileMetaService.FormatSize(progress.BytesTransferred)} / " +
                FileMetaService.FormatSize(totalBytes));
        }
        else if (progress.BytesTransferred > 0)
        {
            parts.Add(FileMetaService.FormatSize(progress.BytesTransferred));
        }

        if (progress.BytesPerSecond is > 0)
        {
            parts.Add(
                FileMetaService.FormatSize(
                    (long)Math.Min(long.MaxValue, progress.BytesPerSecond.Value)) +
                "/s");
        }

        if (progress.EstimatedRemaining is { } remaining &&
            remaining < TimeSpan.FromDays(1))
        {
            string time = remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss")
                : remaining.ToString(@"m\:ss");
            parts.Add(_localizationService.Format(
                "Widget.Import.Progress.Remaining",
                time));
        }

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : T("Widget.Import.Description");
    }

    private async Task CompleteTrackedImportAsync(
        ImportCompletionState completionState)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await CompleteTrackedImportAsync(completionState);
                completion.TrySetResult(true);
            });
            await completion.Task;
            return;
        }

        _importCardDelayCancellation?.Cancel();
        _activeImportVisualState = completionState;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Value = completionState == ImportCompletionState.Completed
            ? 100
            : ImportProgressBar.Value;
        ImportPercentText.Text = completionState == ImportCompletionState.Completed
            ? "100%"
            : string.Empty;

        switch (completionState)
        {
            case ImportCompletionState.Completed:
                ImportTitleText.Text = T("Widget.Import.Completed");
                ImportDescriptionText.Text = string.Empty;
                VisualStateManager.GoToState(
                    this,
                    "ImportSuccessState",
                    false);
                ImportStateIcon.Foreground = ImportProgressBar.Foreground;
                break;
            case ImportCompletionState.Canceled:
                ImportTitleText.Text = T("Widget.Import.Canceled");
                ImportDescriptionText.Text = string.Empty;
                VisualStateManager.GoToState(
                    this,
                    "ImportCanceledState",
                    false);
                if (Application.Current.Resources.TryGetValue(
                        "TextFillColorSecondaryBrush",
                        out object? secondaryBrush) &&
                    secondaryBrush is Brush secondary)
                {
                    ImportStateIcon.Foreground = secondary;
                }
                break;
            case ImportCompletionState.Failed:
                ImportTitleText.Text = T("Widget.Import.Failed");
                ImportDescriptionText.Text = T("Widget.Import.Failed.Description");
                VisualStateManager.GoToState(
                    this,
                    "ImportErrorState",
                    false);
                if (Application.Current.Resources.TryGetValue(
                        "SystemFillColorCriticalBrush",
                        out object? criticalBrush) &&
                    criticalBrush is Brush brush)
                {
                    ImportStateIcon.Foreground = brush;
                }
                break;
        }

        if (_importCardWasShown)
        {
            await Task.Delay(completionState == ImportCompletionState.Failed
                ? TimeSpan.FromSeconds(1.8)
                : TimeSpan.FromMilliseconds(650));
        }

        CancelAndResetTrackedImport();
    }

    private void ImportCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeImportCancellation is null ||
            _activeImportCancellation.IsCancellationRequested)
        {
            return;
        }

        ImportTitleText.Text = T("Widget.Import.Canceling");
        ImportDescriptionText.Text = T("Widget.Import.Canceling.Description");
        ImportPercentText.Text = string.Empty;
        ImportProgressBar.IsIndeterminate = true;
        VisualStateManager.GoToState(this, "ImportCancelingState", false);
        _activeImportCancellation.Cancel();
    }

    private void ImportProgressCard_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        ImportDescriptionText.Visibility = e.NewSize.Width >= 250
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CancelAndResetTrackedImport()
    {
        _importCardDelayCancellation?.Cancel();
        _importCardDelayCancellation?.Dispose();
        _importCardDelayCancellation = null;

        if (_activeImportCancellation is not null)
        {
            if (!_activeImportCancellation.IsCancellationRequested)
            {
                _activeImportCancellation.Cancel();
            }

            _activeImportCancellation.Dispose();
            _activeImportCancellation = null;
        }

        ImportProgressCard.Visibility = Visibility.Collapsed;
        ImportCancelButton.Visibility = Visibility.Visible;
        ImportCancelButton.IsEnabled = true;
        SelectionCommandBar.IsEnabled = true;
        _importCardWasShown = false;
        _activeImportVisualState = null;

        bool wasBusy = _isImportBusy;
        _isImportBusy = false;
        _importBusyStartedAtUtc = null;
        if (wasBusy)
        {
            ImportBusyChanged?.Invoke(false);
        }
    }

    private enum ImportCompletionState
    {
        Completed,
        Canceled,
        Failed
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

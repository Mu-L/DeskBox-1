using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _runtimeHealthSummary = string.Empty;
    private string _runtimeHealthDetail = string.Empty;
    private bool _isRuntimeResyncing;

    public string RuntimeHealthSummary
    {
        get => _runtimeHealthSummary;
        private set => SetProperty(ref _runtimeHealthSummary, value);
    }

    public string RuntimeHealthDetail
    {
        get => _runtimeHealthDetail;
        private set => SetProperty(ref _runtimeHealthDetail, value);
    }

    public bool IsRuntimeResyncing
    {
        get => _isRuntimeResyncing;
        private set
        {
            if (SetProperty(ref _isRuntimeResyncing, value))
            {
                OnPropertyChanged(nameof(CanResyncRuntimeState));
            }
        }
    }

    public bool CanResyncRuntimeState => !IsRuntimeResyncing;

    public void RefreshRuntimeDiagnostics()
    {
        App? app = App.Current;
        AppRuntimeHealthSnapshot? snapshot = app?.DiagnosticsService?.GetRuntimeHealthSnapshot(
            app.EverythingSearchService,
            app.WidgetManager?.GetFolderWatcherHealthSnapshots());
        if (snapshot is null)
        {
            RuntimeHealthSummary = _localizationService.T("Settings.RuntimeHealth.Unavailable");
            RuntimeHealthDetail = string.Empty;
            return;
        }

        bool everythingEnabled = _settingsService.Settings.SearchEverythingEnabled;
        bool everythingChecking = everythingEnabled &&
            snapshot.EverythingState == EverythingConnectionState.Checking;
        bool everythingNeedsAttention = everythingEnabled &&
            snapshot.EverythingState != EverythingConnectionState.Connected &&
            !everythingChecking;

        RuntimeHealthSummary = everythingChecking
            ? _localizationService.T("Settings.RuntimeHealth.Summary.Scanning")
            : everythingNeedsAttention ||
              snapshot.OfflineFolderCount > 0 ||
              snapshot.DegradedFolderCount > 0 ||
              snapshot.AccessDeniedFolderCount > 0
                ? _localizationService.T("Settings.RuntimeHealth.Summary.Degraded")
                : _localizationService.T("Settings.RuntimeHealth.Summary.Healthy");

        string lastLifecycle = snapshot.LastLifecycleEventAt is { } lifecycleAt
            ? lifecycleAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");
        string everythingStatus = !everythingEnabled
            ? _localizationService.T("Settings.Search.Everything.Status.NotConfirmed")
            : snapshot.EverythingState switch
            {
                EverythingConnectionState.Checking =>
                    _localizationService.T("Settings.Search.Everything.Status.Checking"),
                EverythingConnectionState.NotInstalled =>
                    _localizationService.T("Settings.Search.Everything.Status.NotInstalled"),
                EverythingConnectionState.NotRunning =>
                    _localizationService.T("Settings.Search.Everything.Status.NotRunning"),
                EverythingConnectionState.PermissionMismatch =>
                    _localizationService.T("Settings.Search.Everything.Status.PermissionMismatch"),
                EverythingConnectionState.IpcUnavailable =>
                    _localizationService.T("Settings.Search.Everything.Status.IpcUnavailable"),
                EverythingConnectionState.SdkUnavailable =>
                    _localizationService.T("Settings.Search.Everything.Status.SdkUnavailable"),
                EverythingConnectionState.Connected => _localizationService.Format(
                    "Settings.Search.Everything.Status.Connected",
                    snapshot.EverythingVersion ??
                    _localizationService.T("Settings.Search.Everything.VersionUnknown")),
                EverythingConnectionState.Error =>
                    _localizationService.T("Settings.Search.Everything.Status.Error"),
                _ => _localizationService.T("Settings.Search.Everything.Status.Unknown")
            };

        RuntimeHealthDetail = _localizationService.Format(
            "Settings.RuntimeHealth.Detail",
            snapshot.LifecycleEventCount,
            lastLifecycle,
            everythingStatus,
            snapshot.OfflineFolderCount,
            snapshot.DegradedFolderCount,
            snapshot.AccessDeniedFolderCount,
            string.IsNullOrWhiteSpace(snapshot.LastLifecycleReason)
                ? _localizationService.T("Settings.RuntimeHealth.Never")
                : snapshot.LastLifecycleReason);
    }

    public async Task ResyncRuntimeStateAsync()
    {
        if (IsRuntimeResyncing)
        {
            return;
        }

        IsRuntimeResyncing = true;
        try
        {
            if (App.Current is { } app)
            {
                await app.ForceExternalStateRecoveryAsync();
            }

            RefreshRuntimeDiagnostics();
        }
        finally
        {
            IsRuntimeResyncing = false;
        }
    }
}

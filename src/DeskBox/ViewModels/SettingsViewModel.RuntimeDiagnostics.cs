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
            app.SearchIndexService,
            app.SearchEngineService,
            app.WidgetManager?.GetFolderWatcherHealthSnapshots());
        if (snapshot is null)
        {
            RuntimeHealthSummary = _localizationService.T("Settings.RuntimeHealth.Unavailable");
            RuntimeHealthDetail = string.Empty;
            return;
        }

        RuntimeHealthSummary = snapshot.IsSearchScanning
            ? _localizationService.T("Settings.RuntimeHealth.Summary.Scanning")
            : snapshot.FailedSearchWatcherCount > 0 ||
              snapshot.OfflineSearchRootCount > 0 ||
              snapshot.PartialSearchRootCount > 0 ||
              snapshot.SearchScanCapacityLimited ||
              snapshot.OfflineFolderCount > 0 ||
              snapshot.DegradedFolderCount > 0 ||
              snapshot.AccessDeniedFolderCount > 0
                ? _localizationService.T("Settings.RuntimeHealth.Summary.Degraded")
                : _localizationService.T("Settings.RuntimeHealth.Summary.Healthy");

        string lastLifecycle = snapshot.LastLifecycleEventAt is { } lifecycleAt
            ? lifecycleAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");
        string lastSearchRecovery = snapshot.LastSearchWatcherRecoveryTime is { } recoveryAt
            ? recoveryAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");
        string lastScan = snapshot.LastSearchScanTime is { } scanAt
            ? scanAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");

        RuntimeHealthDetail = _localizationService.Format(
            "Settings.RuntimeHealth.Detail",
            snapshot.LifecycleEventCount,
            lastLifecycle,
            snapshot.SearchWatcherCount,
            snapshot.SearchWatcherRecoveryCount,
            snapshot.IndexedEntryCount,
            lastSearchRecovery,
            lastScan,
            snapshot.OfflineSearchRootCount,
            snapshot.PartialSearchRootCount,
            snapshot.FailedSearchWatcherCount,
            snapshot.SearchScanCapacityLimited
                ? _localizationService.T("Settings.RuntimeHealth.CapacityReached")
                : _localizationService.T("Settings.RuntimeHealth.CapacityNotReached"),
            snapshot.IsUsnIndexAvailable
                ? (snapshot.IsUsnIndexIncrementalSyncing
                    ? _localizationService.T("Settings.RuntimeHealth.UsnIncremental")
                    : snapshot.IsUsnIndexScanning
                    ? _localizationService.T("Settings.RuntimeHealth.UsnScanning")
                    : _localizationService.T("Settings.RuntimeHealth.UsnAvailable"))
                : _localizationService.T("Settings.RuntimeHealth.UsnUnavailable"),
            snapshot.OfflineFolderCount,
            snapshot.DegradedFolderCount,
            snapshot.AccessDeniedFolderCount);
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

using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _selectedPerformanceMode =
        PerformanceSettingsPolicy.DefaultMode;
    private int _selectedHiddenCacheCleanupDelaySeconds =
        PerformanceSettingsPolicy.DefaultHiddenCacheCleanupDelaySeconds;
    private bool _enableContinuousDecorativeAnimations =
        PerformanceSettingsPolicy.DefaultContinuousDecorativeAnimationsEnabled;

    public IReadOnlyList<SettingsOption> AvailablePerformanceModeOptions =>
    [
        new(
            PerformanceSettingsPolicy.ModeBestVisual,
            _localizationService.T("Settings.Performance.Mode.BestVisual")),
        new(
            PerformanceSettingsPolicy.ModeBalanced,
            _localizationService.T("Settings.Performance.Mode.Balanced")),
        new(
            PerformanceSettingsPolicy.ModeResourceSaver,
            _localizationService.T("Settings.Performance.Mode.ResourceSaver")),
        new(
            PerformanceSettingsPolicy.ModeCustom,
            _localizationService.T("Settings.Performance.Mode.Custom"))
    ];

    public IReadOnlyList<SettingsOption>
        AvailableHiddenCacheCleanupDelayOptions =>
    [
        new(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            _localizationService.T(
                "Settings.Performance.HiddenCleanup.30Seconds")),
        new(
            PerformanceSettingsPolicy.CleanupAfter1Minute,
            _localizationService.T(
                "Settings.Performance.HiddenCleanup.1Minute")),
        new(
            PerformanceSettingsPolicy.CleanupAfter5Minutes,
            _localizationService.T(
                "Settings.Performance.HiddenCleanup.5Minutes")),
        new(
            PerformanceSettingsPolicy.CleanupNever,
            _localizationService.T(
                "Settings.Performance.HiddenCleanup.Never"))
    ];

    public string SelectedPerformanceMode
    {
        get => _selectedPerformanceMode;
        set
        {
            string normalized = PerformanceSettingsPolicy.NormalizeMode(value);
            if (!SetProperty(ref _selectedPerformanceMode, normalized))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            PerformanceSettingsPolicy.ApplyPreset(settings, normalized);
            SynchronizePerformanceDetailSelection(settings);
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedHiddenCacheCleanupDelaySeconds
    {
        get => _selectedHiddenCacheCleanupDelaySeconds;
        set
        {
            int normalized =
                PerformanceSettingsPolicy
                    .NormalizeHiddenCacheCleanupDelaySeconds(value);
            if (!SetProperty(
                    ref _selectedHiddenCacheCleanupDelaySeconds,
                    normalized))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            settings.HiddenCacheCleanupDelaySeconds = normalized;
            SwitchPerformanceModeToCustom(settings);
            _settingsService.SaveDebounced();
        }
    }

    public bool EnableContinuousDecorativeAnimations
    {
        get => _enableContinuousDecorativeAnimations;
        set
        {
            if (!SetProperty(
                    ref _enableContinuousDecorativeAnimations,
                    value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            settings.EnableContinuousDecorativeAnimations = value;
            SwitchPerformanceModeToCustom(settings);
            _settingsService.SaveDebounced();
        }
    }

    private void InitializePerformanceSettings(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        _selectedPerformanceMode = effective.Mode;
        _selectedHiddenCacheCleanupDelaySeconds =
            effective.HiddenCacheCleanupDelaySeconds;
        _enableContinuousDecorativeAnimations =
            effective.AllowContinuousDecorativeAnimations;
    }

    private void ApplyPerformanceSettingsSnapshot(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        SelectedPerformanceMode = effective.Mode;
        SelectedHiddenCacheCleanupDelaySeconds =
            effective.HiddenCacheCleanupDelaySeconds;
        EnableContinuousDecorativeAnimations =
            effective.AllowContinuousDecorativeAnimations;
    }

    private void RefreshPerformanceSelectionProperties(
        bool refreshLocalizedOptions)
    {
        if (!refreshLocalizedOptions)
        {
            return;
        }

        OnPropertyChanged(nameof(AvailablePerformanceModeOptions));
        OnPropertyChanged(nameof(AvailableHiddenCacheCleanupDelayOptions));
    }

    private void SynchronizePerformanceDetailSelection(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        if (_selectedHiddenCacheCleanupDelaySeconds !=
            effective.HiddenCacheCleanupDelaySeconds)
        {
            _selectedHiddenCacheCleanupDelaySeconds =
                effective.HiddenCacheCleanupDelaySeconds;
            OnPropertyChanged(
                nameof(SelectedHiddenCacheCleanupDelaySeconds));
        }

        if (_enableContinuousDecorativeAnimations !=
            effective.AllowContinuousDecorativeAnimations)
        {
            _enableContinuousDecorativeAnimations =
                effective.AllowContinuousDecorativeAnimations;
            OnPropertyChanged(
                nameof(EnableContinuousDecorativeAnimations));
        }
    }

    private void SwitchPerformanceModeToCustom(AppSettings settings)
    {
        settings.PerformanceMode = PerformanceSettingsPolicy.ModeCustom;
        if (string.Equals(
                _selectedPerformanceMode,
                PerformanceSettingsPolicy.ModeCustom,
                StringComparison.Ordinal))
        {
            return;
        }

        _selectedPerformanceMode = PerformanceSettingsPolicy.ModeCustom;
        OnPropertyChanged(nameof(SelectedPerformanceMode));
    }
}

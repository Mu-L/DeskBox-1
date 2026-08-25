using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _selectedWidgetForegroundMode = WidgetForegroundSettings.ModeFollowTheme;
    private string _selectedWidgetTextEdgeMode = WidgetForegroundSettings.EdgeOff;
    private Color _selectedWidgetForegroundColor =
        AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);

    public IReadOnlyList<SettingsOption> AvailableWidgetForegroundModeOptions =>
    [
        new(
            WidgetForegroundSettings.ModeFollowTheme,
            _localizationService.T("Settings.WidgetForeground.FollowTheme")),
        new(
            WidgetForegroundSettings.ModeLight,
            _localizationService.T("Settings.WidgetForeground.Light")),
        new(
            WidgetForegroundSettings.ModeDark,
            _localizationService.T("Settings.WidgetForeground.Dark")),
        new(
            WidgetForegroundSettings.ModeCustom,
            _localizationService.T("Settings.WidgetForeground.Custom"))
    ];

    public IReadOnlyList<SettingsOption> AvailableWidgetTextEdgeModeOptions =>
    [
        new(
            WidgetForegroundSettings.EdgeOff,
            _localizationService.T("Settings.WidgetTextEdge.Off")),
        new(
            WidgetForegroundSettings.EdgeSoft,
            _localizationService.T("Settings.WidgetTextEdge.Soft")),
        new(
            WidgetForegroundSettings.EdgeStrong,
            _localizationService.T("Settings.WidgetTextEdge.Strong"))
    ];

    public string SelectedWidgetForegroundMode
    {
        get => _selectedWidgetForegroundMode;
        set
        {
            string normalized = WidgetForegroundSettings.NormalizeMode(value);
            if (!SetProperty(ref _selectedWidgetForegroundMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetForegroundCustomColorVisibility));
            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetForegroundMode = normalized;
            SaveAppearanceChange();
        }
    }

    public Color SelectedWidgetForegroundColor
    {
        get => _selectedWidgetForegroundColor;
        set
        {
            Color opaque = Color.FromArgb(0xFF, value.R, value.G, value.B);
            if (!SetProperty(ref _selectedWidgetForegroundColor, opaque))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetForegroundColor =
                AccentColorHelper.ToHex(opaque);
            SaveAppearanceChange();
        }
    }

    public string SelectedWidgetTextEdgeMode
    {
        get => _selectedWidgetTextEdgeMode;
        set
        {
            string normalized = WidgetForegroundSettings.NormalizeEdgeMode(value);
            if (!SetProperty(ref _selectedWidgetTextEdgeMode, normalized))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetTextEdgeMode = normalized;
            SaveAppearanceChange();
        }
    }

    public Visibility WidgetForegroundCustomColorVisibility =>
        string.Equals(
            SelectedWidgetForegroundMode,
            WidgetForegroundSettings.ModeCustom,
            StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void InitializeWidgetForegroundSettings(AppSettings settings)
    {
        _selectedWidgetForegroundMode =
            WidgetForegroundSettings.NormalizeMode(settings.WidgetForegroundMode);
        _selectedWidgetTextEdgeMode =
            WidgetForegroundSettings.NormalizeEdgeMode(settings.WidgetTextEdgeMode);
        _selectedWidgetForegroundColor = AccentColorHelper.TryParseHex(
            settings.WidgetForegroundColor,
            out Color color)
            ? Color.FromArgb(0xFF, color.R, color.G, color.B)
            : AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);
    }

    private void ApplyWidgetForegroundSettingsSnapshot(AppSettings settings)
    {
        SelectedWidgetForegroundMode = settings.WidgetForegroundMode;
        SelectedWidgetTextEdgeMode = settings.WidgetTextEdgeMode;
        SelectedWidgetForegroundColor = AccentColorHelper.TryParseHex(
            settings.WidgetForegroundColor,
            out Color color)
            ? color
            : AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);
    }

    private void RefreshWidgetForegroundSelectionProperties(bool refreshLocalizedOptions)
    {
        if (refreshLocalizedOptions)
        {
            OnPropertyChanged(nameof(AvailableWidgetForegroundModeOptions));
            OnPropertyChanged(nameof(AvailableWidgetTextEdgeModeOptions));
        }

        OnPropertyChanged(nameof(WidgetForegroundCustomColorVisibility));
    }
}

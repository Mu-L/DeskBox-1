using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel
{
    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyMusicSettings(_settingsService.Settings);
        RaiseMusicAccentPropertiesChanged();
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateProgressTimerState();
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Stops all timers when hidden to avoid unnecessary CPU/GPU usage,
    /// and restarts them when the window becomes visible again.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowVisible = visible;
        if (visible)
        {
            _ = RefreshAsync();
        }
        UpdateProgressTimerState();
    }

    /// <summary>
    /// Keeps capsule progress live at a lower cadence while avoiding the
    /// expanded surface's 500 ms refresh cost when it is fully covered.
    /// </summary>
    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isDisposed || _isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        UpdateProgressTimerState();
        if (!collapsed && _isWindowVisible)
        {
            _ = RefreshTimelineAsync();
        }
    }
}

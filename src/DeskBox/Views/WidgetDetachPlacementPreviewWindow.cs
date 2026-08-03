using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// A click-through, non-activating placement silhouette used while a member is
/// dragged out of a widget group. Native positioning keeps it responsive while
/// OLE owns the UI thread's modal drag loop.
/// </summary>
internal sealed class WidgetDetachPlacementPreviewWindow : IDisposable
{
    private const byte TrackingOpacity = 220;
    private const byte CommittedOpacity = 238;
    private readonly object _gate = new();
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private bool _closed;

    public WidgetDetachPlacementPreviewWindow(string caption, double cornerRadius)
    {
        Color accent = ResolveAccentColor();
        double surfaceRadius = Math.Clamp(cornerRadius, 0, 32);
        var root = new Grid();
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(
                0x18,
                accent.R,
                accent.G,
                accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(
                0xD8,
                accent.R,
                accent.G,
                accent.B)),
            // Keep a quiet landing surface and one bottom accent only. The
            // previous full 2px outline made the detached preview read like a
            // warning/error state and competed with the corner badge.
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(surfaceRadius)
        });
        root.Children.Add(new Border
        {
            Margin = new Thickness(10, 0, 10, 8),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(
                0xE8,
                accent.R,
                accent.G,
                accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Clamp(surfaceRadius * 0.45, 2, 4)),
            Child = new TextBlock
            {
                Text = caption,
                MaxWidth = 260,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        _window = new Window
        {
            Content = root
        };

        _hWnd = WindowNative.GetWindowHandle(_window);
        Microsoft.UI.WindowId windowId =
            Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            _hWnd,
            Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
                         Win32Helper.WS_EX_NOACTIVATE |
                         Win32Helper.WS_EX_TRANSPARENT |
                         Win32Helper.WS_EX_LAYERED |
                         Win32Helper.WS_EX_TOPMOST;
        _ = Win32Helper.SetWindowLongPtr(
            _hWnd,
            Win32Helper.GWL_EXSTYLE,
            new IntPtr(extendedStyle));
        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        _ = Win32Helper.SetLayeredWindowAttributes(
            _hWnd,
            0,
            TrackingOpacity,
            Win32Helper.LWA_ALPHA);
        Win32Helper.SetWindowBorderColor(
            _hWnd,
            unchecked((int)0xFFFFFFFE));
        int cornerPreference = 2;
        _ = Win32Helper.TrySetDwmWindowAttribute(
            _hWnd,
            Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference);
    }

    public void Update(RectInt32 bounds, bool visible)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!visible)
            {
                _ = Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
                return;
            }

            _ = Win32Helper.SetWindowPos(
                _hWnd,
                Win32Helper.HWND_TOPMOST,
                bounds.X,
                bounds.Y,
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height),
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_SHOWWINDOW);
        }
    }

    public void MarkCommitted(RectInt32 bounds)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _ = Win32Helper.SetLayeredWindowAttributes(
                _hWnd,
                0,
                CommittedOpacity,
                Win32Helper.LWA_ALPHA);
            _ = Win32Helper.SetWindowPos(
                _hWnd,
                Win32Helper.HWND_TOPMOST,
                bounds.X - 2,
                bounds.Y - 2,
                Math.Max(1, bounds.Width + 4),
                Math.Max(1, bounds.Height + 4),
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_SHOWWINDOW);
        }
    }

    public async Task FadeOutAndCloseAsync()
    {
        foreach (byte opacity in new byte[] { 176, 118, 58, 16 })
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _ = Win32Helper.SetLayeredWindowAttributes(
                    _hWnd,
                    0,
                    opacity,
                    Win32Helper.LWA_ALPHA);
            }

            await Task.Delay(28);
        }

        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _ = Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        }

        _window.Close();
    }

    private static Color ResolveAccentColor()
    {
        if (Application.Current.Resources.TryGetValue(
                "SystemAccentColor",
                out object? value) &&
            value is Color color)
        {
            return color;
        }

        return Colors.DeepSkyBlue;
    }
}

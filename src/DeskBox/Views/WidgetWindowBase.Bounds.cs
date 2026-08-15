// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    protected void ConfigureWindowCore()
    {
        if (!_isTrackedForDiagnostics)
        {
            PerformanceLogger.TrackWindowOpen(LogPrefix);
            _isTrackedForDiagnostics = true;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        InstallDesktopPinnedActivationGuard();
        InstallDesktopPinnedPointerRouting();
        ConfigureWindowExtra();

        int style = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            HWnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;

        var config = Config;
        // Use center point for consistent monitor determination.
        var initBounds = new RectInt32(
            (int)Math.Round(config.X),
            (int)Math.Round(config.Y),
            (int)Math.Round(config.Width),
            (int)Math.Round(config.Height));
        var initCenter = new PointInt32(
            initBounds.X + Math.Max(1, initBounds.Width) / 2,
            initBounds.Y + Math.Max(1, initBounds.Height) / 2);
        var workArea = DisplayArea.GetFromPoint(
            initCenter,
            DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WidgetPositioningService.ResolveBounds(
            config,
            workArea,
            WidgetPositioningService.GetAvailableWorkAreas());
        bounds = ExpandContentBoundsToHost(bounds);
        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false);

        ApplyDwmBorderStyle(RootElement.ActualTheme == ElementTheme.Dark);
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(HWnd);
        ApplyBackdropPreference();
        InitializeWidgetCollapse();
        InitializeWidgetGrouping();
        WidgetShellControl.HostedContentChanged -= WidgetShellControl_HostedContentChanged;
        WidgetShellControl.HostedContentChanged += WidgetShellControl_HostedContentChanged;

        RootElement.Loaded += (_, _) =>
        {
            OnRootElementLoaded();
            SettleCompactBoundsAfterHostShown();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(HWnd);
            if (SupportsBackdropRefresh)
            {
                QueueBackdropRefresh();
            }
        };
        RootElement.ActualThemeChanged += (_, _) =>
        {
            ApplyBackdropPreference();
            OnRootElementThemeChanged();
        };
    }

    private void InstallDesktopPinnedActivationGuard()
    {
        _desktopPinnedActivationSubclassProc ??= DesktopPinnedActivationSubclassProc;
        if (_isDesktopPinnedActivationSubclassInstalled)
        {
            return;
        }

        _isDesktopPinnedActivationSubclassInstalled = Win32Helper.SetWindowSubclass(
            HWnd,
            _desktopPinnedActivationSubclassProc,
            DesktopPinnedActivationSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[ZOrder] {LogPrefix} desktop-pinned activation guard installed=" +
            $"{_isDesktopPinnedActivationSubclassInstalled} hwnd=0x{HWnd.ToInt64():X}");
    }

    private void RemoveDesktopPinnedActivationGuard()
    {
        if (!_isDesktopPinnedActivationSubclassInstalled ||
            _desktopPinnedActivationSubclassProc is null)
        {
            return;
        }

        _ = Win32Helper.RemoveWindowSubclass(
            HWnd,
            _desktopPinnedActivationSubclassProc,
            DesktopPinnedActivationSubclassId);
        _isDesktopPinnedActivationSubclassInstalled = false;
    }

    private void InstallDesktopPinnedPointerRouting()
    {
        _desktopPinnedPointerPressedHandler ??=
            RootElement_PointerPressedForDesktopPinnedLayer;
        RootElement.AddHandler(
            UIElement.PointerPressedEvent,
            _desktopPinnedPointerPressedHandler,
            handledEventsToo: true);
        Activated -= WidgetWindowBase_ActivatedForDesktopPinnedLayer;
        Activated += WidgetWindowBase_ActivatedForDesktopPinnedLayer;
    }

    private void RemoveDesktopPinnedPointerRouting()
    {
        if (_desktopPinnedPointerPressedHandler is not null)
        {
            RootElement.RemoveHandler(
                UIElement.PointerPressedEvent,
                _desktopPinnedPointerPressedHandler);
            _desktopPinnedPointerPressedHandler = null;
        }

        Activated -= WidgetWindowBase_ActivatedForDesktopPinnedLayer;
    }

    private void RootElement_PointerPressedForDesktopPinnedLayer(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        if (!WidgetLayerService.TryAllowDesktopPinnedPointerActivation(HWnd))
        {
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            IsAtDesktopLayer = true;
            IsRaisedFromManager = false;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            App.LogVerbose(
                $"[ZOrder] {LogPrefix} pinned routed pointer kept behind foreground " +
                $"hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        if (Win32Helper.GetForegroundWindow() != HWnd)
        {
            base.Activate();
            _ = Win32Helper.SetForegroundWindow(HWnd);
        }
    }

    private void WidgetWindowBase_ActivatedForDesktopPinnedLayer(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated ||
            WidgetLayerService.IsWindowNoActivate(HWnd))
        {
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            IsAtDesktopLayer = true;
            IsRaisedFromManager = false;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
        }
    }

    private IntPtr DesktopPinnedActivationSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == Win32Helper.WM_MOUSEACTIVATE &&
            WidgetLayerService.ShouldSuppressPointerActivation(hWnd))
        {
            // MA_NOACTIVATE keeps the existing foreground application active
            // but still lets the widget receive the mouse message. Reasserting
            // the desktop owner here also repairs any stale owner/Z-order state
            // without a visible raise-and-restore flash.
            WidgetLayerService.MoveToDesktopBottom(hWnd);
            IsAtDesktopLayer = true;
            IsRaisedFromManager = false;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            App.LogVerbose(
                $"[ZOrder] {LogPrefix} desktop-pinned pointer activation suppressed " +
                $"hwnd=0x{hWnd.ToInt64():X}");
            return new IntPtr(Win32Helper.MA_NOACTIVATE);
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    // ── Bounds management ──────────────────────────────────────

    protected void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        if (!IsCompactBoundsStateActive && !UsesCompactExpansionGeometry())
        {
            var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
            width = Math.Max(minSize.Width, width);
            height = Math.Max(minSize.Height, height);
        }

        IsApplyingBounds = true;
        try
        {
            var bounds = new RectInt32(x, y, width, height);
            if (IsCompactBoundsStateActive)
            {
                bool moved = Win32Helper.SetWindowPos(
                    HWnd,
                    IntPtr.Zero,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
                if (!moved)
                {
                    AppWindow.MoveAndResize(bounds);
                }
            }
            else
            {
                AppWindow.MoveAndResize(bounds);
            }
        }
        finally
        {
            IsApplyingBounds = false;
        }

        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: false);
        }
    }

    protected SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    protected void CapturePositionAnchor(
        int x,
        int y,
        int width,
        int height,
        bool preserveCurrentEdge = false)
    {
        var bounds = CollapseHostBoundsToContent(
            new RectInt32(x, y, width, height));
        if (IsCompactBoundsStateActive)
        {
            CaptureCompactPlacement(new RectInt32(x, y, width, height), persist: false);
            return;
        }

        // Use the window center point to determine the owning display.
        // This prevents incorrect anchor capture when the window straddles
        // two monitors during a cross-screen drag.
        var center = new PointInt32(
            bounds.X + Math.Max(1, bounds.Width) / 2,
            bounds.Y + Math.Max(1, bounds.Height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        if (preserveCurrentEdge)
        {
            WidgetPositioningService.CaptureAnchorPreservingCurrentEdge(
                Config,
                bounds,
                workArea);
        }
        else
        {
            WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
        }
    }

    // ── Display change restoration ─────────────────────────────

    protected bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (IsClosing || IsHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (IsDragging || IsResizing || TrayAnimation.IsPositionTransitionActive)
        {
            return false;
        }

        var bounds = ResolveWidgetBoundsForCurrentState();
        RectInt32 actual = GetActualWindowBounds();
        if (actual.X == bounds.X &&
            actual.Y == bounds.Y &&
            actual.Width == bounds.Width &&
            actual.Height == bounds.Height)
        {
            return true;
        }

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: true);
        return true;
    }

    protected bool RestoreBoundsAfterDisplayChange()
    {
        InvalidateStableCompactBounds();
        if (!Visible)
        {
            var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
            // Use center point for consistent monitor determination.
            var center = new PointInt32(
                bounds.X + Math.Max(1, bounds.Width) / 2,
                bounds.Y + Math.Max(1, bounds.Height) / 2);
            var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
            WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(Config, bounds, workArea);
            SettingsService.SaveDebounced();
            return true;
        }

        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    // ── AppWindow change handling ──────────────────────────────

    protected void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            NotifyCompactHostVisibilityChanged(sender.IsVisible);
        }

        if (IsApplyingBounds || TrayAnimation.IsApplyingBounds || (!IsDragging && !IsResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    // ── Window corner & DWM border ─────────────────────────────

    protected void ApplyWindowCornerPreference()
    {
        int cornerPreference = SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.TrySetDwmWindowAttribute(HWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference);
    }

    protected void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(HWnd, borderNone);
    }

    protected double GetCornerRadiusFromPreference()
    {
        return SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    protected double GetCurrentSurfaceCornerRadius()
    {
        if (_isCollapseAnimationRendering)
        {
            return WidgetShellControl.BackgroundSurface.CornerRadius.TopLeft;
        }

        return IsWidgetCollapsedBoundsActive
            ? WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
                SettingsService.Settings.WidgetCornerPreference)
            : GetCornerRadiusFromPreference();
    }

    // ── Backdrop preference ────────────────────────────────────
}

using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow : Window, IDesktopWidgetWindow
{
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;

    public QuickCaptureWidgetViewModel ViewModel { get; }
    public QuickCaptureWidgetContent WidgetContent { get; }
    
    // Satisfy interface
    public IntPtr WindowHandle => _hWnd;
    public Windows.Foundation.Rect AnimationBounds => new(ViewModel.Config.X, ViewModel.Config.Y, Math.Max(1.0, ViewModel.Config.Width), Math.Max(1.0, ViewModel.Config.Height));
    public new bool Visible { get; private set; }

    public QuickCaptureWidgetWindow(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ViewModel = viewModel;
        InitializeComponent();
        
        WidgetContent = new QuickCaptureWidgetContent(this, viewModel, settingsService, localizationService);
        Shell.InnerContent = WidgetContent;

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        var presenter = _appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
        }

        Shell.CloseRequested += Shell_CloseRequested;
        Shell.MoreRequested += Shell_MoreRequested;
        Shell.AddRequested += Shell_AddRequested;
        Shell.DragRequested += Shell_DragRequested;
        Shell.ResizeRequested += Shell_ResizeRequested;
    }

    private void Shell_CloseRequested(object sender, EventArgs e)
    {
        Visible = false;
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
    }

    private void Shell_MoreRequested(object sender, EventArgs e) { }
    private void Shell_AddRequested(object sender, EventArgs e) { }
    private void Shell_DragRequested(object sender, EventArgs e) { }
    private void Shell_ResizeRequested(object sender, string edge) { }

    // Fulfilling IDesktopWidgetWindow methods
    public new void Activate()
    {
        base.Activate();
        Visible = true;
    }
    public void PushToBottom() { }
    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true) { Visible = true; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE); }
    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY) { }
    public void ShowPreparedRaisedFromTray(bool persistVisibility = true) { Visible = true; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE); }
    public void EnsureRaisedFromTrayTopMost() { }
    public void ActivateRaisedFromTrayBatch() { }
    public void PrepareTrayShowAnimation() { }
    public void PlayTrayShowAnimation() { }
    public void CompleteTrayShowWithoutAnimation() { }
    public void EnsureWindowPlacement() { }
    public void PlayHideAnimationToTray() { Visible = false; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE); }
    public void EnsureHiddenImmediate() { Visible = false; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE); }
    public void ToggleVisibility() { if (Visible) PlayHideAnimationToTray(); else ShowPreparedRaisedFromTray(); }
    public void Cleanup() { Close(); }
    public void UpdateDpiScaling(double scaleFactor) { }
    public void SuppressNativeBackdrop() { }
    public void RestoreNativeBackdrop() { }
    public void CancelAnimations() { }
    
    // Newly missing ones
    public void ApplyAppearancePreview() { }
    public bool PrepareTrayHideAnimation(bool persistVisibility = true) { return true; }
    public void PlayPreparedTrayHideAnimation() { Visible = false; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE); }
    public void ForceRestoreDesktopLayerFromManager() { }
    public void RestoreDesktopLayerFromManager() { }
    public void HideWindow() { Visible = false; Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE); }
    public void RevealFromTray(bool autoRestore = true) { ShowPreparedRaisedFromTray(); }
}

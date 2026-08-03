using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private bool _desktopOrganizationCompleted;
    private DesktopOrganizationWindow? _desktopOrganizationWindow;

    private void SetupOrganizationStep()
    {
        RefreshOrganizationPath();
    }

    private void InvalidateDesktopOrganizationPlan()
    {
        RefreshOrganizationPath();
    }

    private void RefreshOrganizationPath()
    {
        OrganizationPathText.Text = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
    }

    private void OrganizationChangePath_Click(object sender, RoutedEventArgs e)
    {
        _ = ChangeStoragePathAsync();
    }

    private void OpenDesktopOrganizationWindow()
    {
        DesktopOrganizationWindow window =
            global::DeskBox.App.Current.ShowDesktopOrganizationWindow(_hWnd);
        if (ReferenceEquals(_desktopOrganizationWindow, window))
        {
            return;
        }

        DetachDesktopOrganizationWindow();
        _desktopOrganizationWindow = window;
        window.OrganizationCompleted += DesktopOrganizationWindow_OrganizationCompleted;
        window.OrganizationUndone += DesktopOrganizationWindow_OrganizationUndone;
        window.Closed += DesktopOrganizationWindow_Closed;
    }

    private void DesktopOrganizationWindow_OrganizationCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _desktopOrganizationCompleted = true;
            UpdateFooterState();
        });
    }

    private void DesktopOrganizationWindow_OrganizationUndone(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _desktopOrganizationCompleted = false;
            UpdateFooterState();
        });
    }

    private void DesktopOrganizationWindow_Closed(object sender, WindowEventArgs args)
    {
        DetachDesktopOrganizationWindow();
    }

    private void DetachDesktopOrganizationWindow()
    {
        if (_desktopOrganizationWindow is not { } window)
        {
            return;
        }

        window.OrganizationCompleted -= DesktopOrganizationWindow_OrganizationCompleted;
        window.OrganizationUndone -= DesktopOrganizationWindow_OrganizationUndone;
        window.Closed -= DesktopOrganizationWindow_Closed;
        _desktopOrganizationWindow = null;
    }
}

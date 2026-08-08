using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private bool _isPracticePlacementActive;

    private void SetupTaskStep1()
    {
    }

    private void SetupTaskStep2()
    {
        string storagePath = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        string action = string.Equals(
            _settingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionCopy,
            StringComparison.Ordinal)
            ? _localizationService.T("Common.Copy")
            : _localizationService.T("Common.Move");

        TaskStep2StoragePathText.Text = storagePath;
        TaskStep2TransferText.Text = _localizationService.Format(
            "Onboarding.Task.Step2.TransferSummary",
            action);

        ManagedStoragePathAssessment assessment = ManagedStoragePathService.AssessPath(storagePath);
        string freeSpace = assessment.AvailableFreeSpace is long availableFreeSpace
            ? FileMetaService.FormatSize(availableFreeSpace)
            : _localizationService.T("Onboarding.Task.Step2.SpaceUnknown");
        string metaKey = assessment.DriveType switch
        {
            DriveType.Network => "Onboarding.Task.Step2.PathMeta.Network",
            DriveType.Removable => "Onboarding.Task.Step2.PathMeta.Removable",
            _ when assessment.IsSystemDrive => "Onboarding.Task.Step2.PathMeta.System",
            DriveType.Fixed => "Onboarding.Task.Step2.PathMeta.NonSystem",
            _ => "Onboarding.Task.Step2.PathMeta.Unknown"
        };
        TaskStep2PathMetaText.Text = metaKey is
            "Onboarding.Task.Step2.PathMeta.Network" or
            "Onboarding.Task.Step2.PathMeta.Unknown"
            ? _localizationService.T(metaKey)
            : _localizationService.Format(metaKey, freeSpace);

        var warnings = new List<string>();
        if (assessment.IsSystemDrive)
        {
            warnings.Add(_localizationService.T(assessment.HasSuitableNonSystemDrive
                ? "Onboarding.Task.Step2.Warning.SystemDrive"
                : "Onboarding.Task.Step2.Warning.SystemDriveOnly"));
        }
        if (assessment.IsCloudSynced)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.CloudSync"));
        }
        if (assessment.DriveType == DriveType.Removable)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Removable"));
        }
        else if (assessment.DriveType == DriveType.Network)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Network"));
        }

        TaskStep2PathWarningText.Text = string.Join(Environment.NewLine, warnings);
        TaskStep2PathWarningBorder.Visibility = warnings.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool confirmed = IsTaskStoragePathConfirmed(storagePath);
        TaskStep2ConfirmPathButton.Content = _localizationService.T(confirmed
            ? "Onboarding.Task.Step2.PathConfirmed"
            : "Onboarding.Task.Step2.ConfirmPath");
        TaskStep2ConfirmPathButton.IsEnabled = !confirmed;
        NextButton.IsEnabled = confirmed;
    }

    private void SetupTaskStep3()
    {
        TaskStep3StatusText.Text = _localizationService.T(
            "Onboarding.Task.Step3.StatusReady");
    }

    private void SetupTaskStep4()
    {
        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);
        TaskStep4HotkeyText.Text = _localizationService.Format(
            "Onboarding.Task.Step4.ToggleBody",
            hotkeyText);
        TaskStep4StatusText.Text = _localizationService.T(
            "Onboarding.Task.Step4.StatusReady");
    }

    private void SetupTaskStep5()
    {
    }

    private async void TaskStep2ChangePath_Click(object sender, RoutedEventArgs e)
    {
        TaskStep2ChangePathButton.IsEnabled = false;
        bool changed = await ChangeStoragePathAsync();
        TaskStep2ChangePathButton.IsEnabled = true;
        if (changed)
        {
            _confirmedTaskStoragePath = null;
        }

        SetupTaskStep2();
    }

    private void TaskStep2ConfirmPath_Click(object sender, RoutedEventArgs e)
    {
        _confirmedTaskStoragePath = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        SetupTaskStep2();
    }

    private bool IsTaskStoragePathConfirmed(string? storagePath = null)
    {
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(
            storagePath ?? _settingsService.Settings.DefaultManagedStorageRootPath);
        return !string.IsNullOrWhiteSpace(_confirmedTaskStoragePath) &&
               string.Equals(
                   normalizedPath,
                   _confirmedTaskStoragePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async void TaskStep3TryWidget_Click(object sender, RoutedEventArgs e)
    {
        TaskStep3TryButton.IsEnabled = false;
        PlaceWindowForWidgetPractice();
        bool shown = await global::DeskBox.App.Current.ShowFirstFileWidgetForOnboardingAsync();
        TaskStep3TryButton.IsEnabled = true;
        TaskStep3StatusText.Text = _localizationService.T(shown
            ? "Onboarding.Task.Step3.StatusShown"
            : "Onboarding.Task.Step2.StatusUnavailable");
    }

    private async void TaskStep4ToggleWidgets_Click(object sender, RoutedEventArgs e)
    {
        TaskStep4ToggleButton.IsEnabled = false;
        bool hasVisibleWidgets = await global::DeskBox.App.Current.ToggleWidgetsForOnboardingAsync();
        TaskStep4ToggleButton.IsEnabled = true;
        TaskStep4StatusText.Text = _localizationService.T(hasVisibleWidgets
            ? "Onboarding.Task.Step4.StatusShown"
            : "Onboarding.Task.Step4.StatusHidden");
    }

    private void TaskStep4OpenTrayMenu_Click(object sender, RoutedEventArgs e)
    {
        global::DeskBox.App.Current.ShowTrayContextMenuForOnboarding();
        TaskStep4StatusText.Text = _localizationService.T(
            "Onboarding.Task.Step4.StatusTrayOpened");
    }

    private async void TaskStep5OrganizeDesktop_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync();
        global::DeskBox.App.Current.ShowDesktopOrganizationWindow();
    }

    private async void TaskStep5OpenAppearance_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync();
        global::DeskBox.App.Current.ShowSettings("Appearance");
    }

    private async void TaskStep5OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync();
        global::DeskBox.App.Current.ShowSettings();
    }

    private void PlaceWindowForWidgetPractice()
    {
        if (_isPracticePlacementActive)
        {
            return;
        }

        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        RectInt32 workArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary).WorkArea;
        double scale = GetCurrentDpiScale();
        int margin = ToPhysicalPixels(24, scale);
        int reservedWidgetWidth = ToPhysicalPixels(360, scale);
        int minWidth = ToPhysicalPixels(MinWindowWidth, scale);
        int availableWidth = Math.Max(
            minWidth,
            workArea.Width - reservedWidgetWidth - (margin * 2));
        int width = Math.Min(_appWindow.Size.Width, availableWidth);
        int height = Math.Min(_appWindow.Size.Height, workArea.Height - (margin * 2));

        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            workArea.X + margin,
            workArea.Y + Math.Max(margin, (workArea.Height - height) / 2)));
        _isPracticePlacementActive = true;
    }

    private void RestoreWindowAfterWidgetPractice()
    {
        if (!_isPracticePlacementActive)
        {
            return;
        }

        _isPracticePlacementActive = false;
        ResizeAndCenterForDisplay(Win32Interop.GetWindowIdFromWindow(_hWnd));
    }
}

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
    private const string StatusInfoGlyph = "\uE946";
    private const string StatusCompleteGlyph = "\uE73E";
    private const string StatusHiddenGlyph = "\uE890";
    private const string StatusVisibleGlyph = "\uE8A7";

    private bool _isPracticePlacementActive;
    private bool _hasCompletedFilePractice;
    private bool _hasHiddenWidgetsDuringPractice;
    private bool _hasCompletedVisibilityPractice;

    private void SetupTaskStep1()
    {
    }

    private void SetupTaskStep2()
    {
        string storagePath = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);

        TaskStep2StoragePathText.Text = storagePath;

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

    }

    private void SetupTaskStep3()
    {
        SetTaskStep3Status(
            _hasCompletedFilePractice
                ? "Onboarding.Task.Step3.StatusCompleted"
                : "Onboarding.Task.Step3.StatusReady",
            _hasCompletedFilePractice ? StatusCompleteGlyph : StatusInfoGlyph);
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
        if (!_hasCompletedVisibilityPractice &&
            global::DeskBox.App.Current.HasVisibleWidgetsForOnboarding == false)
        {
            _hasHiddenWidgetsDuringPractice = true;
        }

        SetTaskStep4Status(
            _hasCompletedVisibilityPractice
                ? "Onboarding.Task.Step4.StatusCompleted"
                : _hasHiddenWidgetsDuringPractice
                    ? "Onboarding.Task.Step4.StatusHidden"
                    : "Onboarding.Task.Step4.StatusReady",
            _hasCompletedVisibilityPractice
                ? StatusCompleteGlyph
                : _hasHiddenWidgetsDuringPractice
                    ? StatusHiddenGlyph
                    : StatusInfoGlyph);
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
            SetupTaskStep2();
        }
    }

    private async void TaskStep3TryWidget_Click(object sender, RoutedEventArgs e)
    {
        TaskStep3TryButton.IsEnabled = false;
        PlaceWindowForWidgetPractice();
        bool shown = await global::DeskBox.App.Current.ShowFirstFileWidgetForOnboardingAsync();
        TaskStep3TryButton.IsEnabled = true;
        if (!_hasCompletedFilePractice)
        {
            SetTaskStep3Status(
                shown
                    ? "Onboarding.Task.Step3.StatusShown"
                    : "Onboarding.Task.Step2.StatusUnavailable",
                shown ? StatusVisibleGlyph : StatusInfoGlyph);
        }
    }

    private async void TaskStep4ToggleWidgets_Click(object sender, RoutedEventArgs e)
    {
        TaskStep4ToggleButton.IsEnabled = false;
        await global::DeskBox.App.Current.ToggleWidgetsForOnboardingAsync();
        TaskStep4ToggleButton.IsEnabled = true;
    }

    private void TaskStep4OpenTrayMenu_Click(object sender, RoutedEventArgs e)
    {
        global::DeskBox.App.Current.ShowTrayContextMenuForOnboarding();
        SetTaskStep4Status("Onboarding.Task.Step4.StatusTrayOpened", StatusVisibleGlyph);
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

    private void OnOnboardingFileImportCompleted(int importedItemCount)
    {
        if (_stepIndex != 1 || importedItemCount <= 0)
        {
            return;
        }

        _hasCompletedFilePractice = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            SetTaskStep3Status("Onboarding.Task.Step3.StatusCompleted", StatusCompleteGlyph);
            UpdateFooterState();
        });
    }

    private void OnOnboardingWidgetsVisibilityChanged(bool hasVisibleWidgets)
    {
        if (_stepIndex != 2)
        {
            return;
        }

        if (!hasVisibleWidgets)
        {
            _hasHiddenWidgetsDuringPractice = true;
        }
        else if (_hasHiddenWidgetsDuringPractice)
        {
            _hasCompletedVisibilityPractice = true;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            string statusKey = _hasCompletedVisibilityPractice
                ? "Onboarding.Task.Step4.StatusCompleted"
                : hasVisibleWidgets
                    ? "Onboarding.Task.Step4.StatusShown"
                    : "Onboarding.Task.Step4.StatusHidden";
            SetTaskStep4Status(
                statusKey,
                _hasCompletedVisibilityPractice
                    ? StatusCompleteGlyph
                    : hasVisibleWidgets
                        ? StatusVisibleGlyph
                        : StatusHiddenGlyph);
            UpdateFooterState();
        });
    }

    private void SetTaskStep3Status(string localizationKey, string glyph)
    {
        TaskStep3StatusIcon.Glyph = glyph;
        TaskStep3StatusText.Text = _localizationService.T(localizationKey);
    }

    private void SetTaskStep4Status(string localizationKey, string glyph)
    {
        TaskStep4StatusIcon.Glyph = glyph;
        TaskStep4StatusText.Text = _localizationService.T(localizationKey);
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

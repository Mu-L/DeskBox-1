using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private const string StatusInfoGlyph = "\uE946";
    private const string StatusCompleteGlyph = "\uE73E";
    private const string StatusHiddenGlyph = "\uE890";
    private const string StatusVisibleGlyph = "\uE8A7";

    private bool _isFilePracticeWidgetRaised;
    private bool _hasCompletedFilePractice;
    private bool _hasHiddenWidgetsDuringPractice;
    private bool _hasCompletedVisibilityPractice;

    private void SetupTaskStep1()
    {
    }

    private void SetupTaskStep2()
    {
    }

    private void SetupTaskStep3()
    {
        SetTaskStep3Status(
            _hasCompletedFilePractice
                ? "Onboarding.Task.Step3.StatusCompleted"
                : "Onboarding.Task.Step3.StatusReady",
            _hasCompletedFilePractice ? StatusCompleteGlyph : StatusInfoGlyph);

        if (!_hasCompletedFilePractice && !_isFilePracticeWidgetRaised)
        {
            _ = ShowFilePracticeWidgetAsync();
        }
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
        TaskStep4ShortcutText.Text = hotkeyText;
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
        TaskStep4ToggleButton.Content = _localizationService.T(
            _hasHiddenWidgetsDuringPractice && !_hasCompletedVisibilityPractice
                ? "Tray.ShowAll"
                : "Onboarding.Task.Step4.ToggleButton");
        TaskStep4ToggleButton.Visibility = _hasCompletedVisibilityPractice
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateVisibilityPreview(global::DeskBox.App.Current.HasVisibleWidgetsForOnboarding);
    }

    private void SetupTaskStep5()
    {
        if (_hasInitializedFeatureToggles)
        {
            return;
        }

        SynchronizeFeatureTogglesFromSettings();
    }

    private void TaskStep5FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_hasInitializedFeatureToggles ||
            _isSynchronizingFeatureToggles ||
            sender is not ToggleSwitch { Tag: string kindName } toggle ||
            !Enum.TryParse(kindName, ignoreCase: false, out WidgetKind kind) ||
            !FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        _featureWidgetSelectionUpdateTask = PersistFeatureWidgetSelectionAfterAsync(
            _featureWidgetSelectionUpdateTask,
            kind,
            toggle.IsOn);
    }

    private async Task PersistFeatureWidgetSelectionAfterAsync(
        Task previousUpdate,
        WidgetKind kind,
        bool enabled)
    {
        try
        {
            await previousUpdate;

            if (global::DeskBox.App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(
                    kind,
                    enabled,
                    reveal: enabled);
                return;
            }

            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to persist feature selection kind={kind} enabled={enabled}: {ex}");
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
    }

    private void OnFeatureWidgetSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnFeatureWidgetSettingsChanged);
            return;
        }

        if (_hasInitializedFeatureToggles)
        {
            SynchronizeFeatureTogglesFromSettings();
        }
    }

    private void SynchronizeFeatureTogglesFromSettings()
    {
        _isSynchronizingFeatureToggles = true;
        try
        {
            TaskStep5TodoToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Todo);
            TaskStep5QuickCaptureToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.QuickCapture);
            TaskStep5SearchToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Search);
            TaskStep5WeatherToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Weather);
            TaskStep5MusicToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Music);
            _hasInitializedFeatureToggles = true;
        }
        finally
        {
            _isSynchronizingFeatureToggles = false;
        }
    }

    private async Task ShowFilePracticeWidgetAsync()
    {
        _isFilePracticeWidgetRaised = true;
        bool shown = await global::DeskBox.App.Current.ShowFirstFileWidgetForOnboardingAsync();
        if (!shown)
        {
            _isFilePracticeWidgetRaised = false;
        }

        if (_stepIndex == 0 && !_hasCompletedFilePractice)
        {
            SetTaskStep3Status(
                shown
                    ? "Onboarding.Task.Step3.StatusShown"
                    : "Onboarding.Task.Step2.StatusUnavailable",
                shown ? StatusVisibleGlyph : StatusInfoGlyph);
        }
    }

    private void ReleaseFilePracticeWidget()
    {
        if (!_isFilePracticeWidgetRaised)
        {
            return;
        }

        _isFilePracticeWidgetRaised = false;
        global::DeskBox.App.Current.ReleaseOnboardingFileWidgetRaise();
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
    }

    private void OnOnboardingFileImportCompleted(int importedItemCount)
    {
        if (_stepIndex != 0 || importedItemCount <= 0)
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
        if (_stepIndex != 1)
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
            TaskStep4ToggleButton.Content = _localizationService.T(
                _hasHiddenWidgetsDuringPractice && !_hasCompletedVisibilityPractice
                    ? "Tray.ShowAll"
                    : "Onboarding.Task.Step4.ToggleButton");
            TaskStep4ToggleButton.Visibility = _hasCompletedVisibilityPractice
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateVisibilityPreview(hasVisibleWidgets);
            UpdateFooterState();
        });
    }

    private void SetTaskStep3Status(string localizationKey, string glyph)
    {
        TaskStep3StatusIcon.Glyph = glyph;
        TaskStep3StatusText.Text = _localizationService.T(localizationKey);
        AnimateStatusFeedback(TaskStep3StatusBadge);
    }

    private void SetTaskStep4Status(string localizationKey, string glyph)
    {
        TaskStep4StatusIcon.Glyph = glyph;
        TaskStep4StatusText.Text = _localizationService.T(localizationKey);
        AnimateStatusFeedback(TaskStep4StatusBadge);
    }

    private void UpdateVisibilityPreview(bool hasVisibleWidgets)
    {
        _stepAmbientStoryboard?.Stop();
        var transform = GetElementTransform(TaskStep4PreviewWidgets);
        transform.TranslateY = hasVisibleWidgets ? 0 : 14;
        TaskStep4PreviewWidgets.Opacity = hasVisibleWidgets ? 1 : 0.22;
        TaskStep4HotkeyHalo.Opacity = hasVisibleWidgets ? 0.18 : 0.34;
        if (hasVisibleWidgets && _stepIndex == 1)
        {
            StartStepAmbientAnimation(1);
        }
    }

}

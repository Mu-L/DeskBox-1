using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep2Features()
    {
        // Feature widgets are intentionally configured after the core setup.
        // The onboarding page is informational only, so a first-time user is
        // not asked to make five independent feature choices before learning
        // the file-widget model.
    }

    private void OpenFeatureWidgetsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettings("FeatureWidgets");
    }

    // ════════════════════════════════════════════════════════════
    //  Step 3: Appearance (capsule toggle handler)
    // ════════════════════════════════════════════════════════════

    private void Step3CapsuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        _settingsService.Settings.WidgetCapsuleModeEnabled = toggle.IsOn;
        _settingsService.SaveDebounced();
    }
}

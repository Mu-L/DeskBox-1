using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace DeskBox.Services;

/// <summary>
/// Centralizes OS and user visual capability checks.  The project is compiled
/// against a current Windows SDK, so every Win11-only call must still be
/// guarded at runtime for the Win10 compatibility floor.
/// </summary>
public static class WindowsCompatibilityService
{
    public const int Windows11Build = 22000;

    private static readonly Lazy<int> s_osBuild = new(GetOsBuild);

    public static int OsBuild => s_osBuild.Value;

    public static bool IsWindows11OrLater => OsBuild >= Windows11Build;

    public static bool SupportsWin11DwmAttributes => IsWindows11OrLater;

    public static bool SupportsNativeWindowCorners => IsWindows11OrLater;

    public static bool SupportsMica => IsWindows11OrLater && IsSupported(() => MicaController.IsSupported());

    public static bool SupportsDesktopAcrylic =>
        IsSupported(() => DesktopAcrylicController.IsSupported());

    public static bool UsesLegacyWindowAcrylic => !IsWindows11OrLater;

    public static string ResolveWidgetMaterialType(string? requestedMaterialType)
    {
        return ResolveWidgetMaterialTypeForBuild(requestedMaterialType, OsBuild);
    }

    internal static string ResolveWidgetMaterialTypeForBuild(
        string? requestedMaterialType,
        int osBuild)
    {
        string normalized = requestedMaterialType is
            SettingsService.WidgetMaterialTypeMica or
            SettingsService.WidgetMaterialTypeMicaAlt or
            SettingsService.WidgetMaterialTypeAcrylic or
            SettingsService.WidgetMaterialTypeAcrylicBase or
            SettingsService.WidgetMaterialTypeSolid
                ? requestedMaterialType
                : SettingsService.WidgetMaterialTypeAcrylic;

        if (osBuild < Windows11Build && SettingsService.IsMicaMaterial(normalized))
        {
            return SettingsService.WidgetMaterialTypeAcrylic;
        }

        return normalized;
    }

    /// <summary>
    /// Applies a secondary-window backdrop without invoking Win11-only APIs on
    /// the Windows 10 compatibility floor. If no system material is available,
    /// SystemBackdrop is cleared and the window's opaque XAML surface remains
    /// the solid-color fallback.
    /// </summary>
    public static string ApplySafeBackdrop(Window window, bool preferMica = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            if (preferMica && SupportsMica)
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return "Mica";
            }

            if (SupportsDesktopAcrylic)
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                return "Acrylic";
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Backdrop] System material unavailable: {ex.Message}");
        }

        window.SystemBackdrop = null;
        return "Solid";
    }

    public static bool AreAnimationsEnabled => ReadUiSetting(
        static settings => settings.AnimationsEnabled,
        fallback: true);

    /// <summary>
    /// Advanced effects is not available on every supported Windows contract;
    /// reflection keeps the Win10 fallback safe while still honoring the user
    /// setting on newer systems.
    /// </summary>
    public static bool AreAdvancedEffectsEnabled
    {
        get
        {
            try
            {
                var property = typeof(UISettings).GetProperty("AdvancedEffectsEnabled");
                return property?.GetValue(new UISettings()) as bool? ?? true;
            }
            catch
            {
                return true;
            }
        }
    }

    public static bool IsHighContrast => ReadAccessibilitySetting(
        static settings => settings.HighContrast,
        fallback: false);

    public static bool ShouldAnimate => ResolveShouldAnimate(
        AreAnimationsEnabled,
        AreAdvancedEffectsEnabled,
        IsHighContrast);

    internal static bool ResolveShouldAnimate(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrast)
    {
        return animationsEnabled && advancedEffectsEnabled && !highContrast;
    }

    private static int GetOsBuild()
    {
        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch
        {
            // Capability detection must fail closed. Assuming Windows 11 here
            // could invoke unsupported DWM/backdrop APIs on the Win10 floor.
            return 0;
        }
    }

    private static bool IsSupported(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadUiSetting(Func<UISettings, bool> read, bool fallback)
    {
        try
        {
            return read(new UISettings());
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadAccessibilitySetting(
        Func<AccessibilitySettings, bool> read,
        bool fallback)
    {
        try
        {
            return read(new AccessibilitySettings());
        }
        catch
        {
            return fallback;
        }
    }
}

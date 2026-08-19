using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    public DeskBoxDiagnosticsBundleService DiagnosticsBundleService { get; private set; } = null!;

    public DeskBoxDiagnosticSnapshot CreateDiagnosticSnapshot()
    {
        AppRuntimeHealthSnapshot? runtimeHealth = DiagnosticsService?.GetRuntimeHealthSnapshot(
            SearchIndexService,
            SearchEngineService,
            WidgetManager?.GetFolderWatcherHealthSnapshots());
        DeskBoxWidgetManagerDiagnostic widgetManager =
            WidgetManager?.CreateDiagnosticsSnapshot() ?? DeskBoxWidgetManagerDiagnostic.Empty;
        IReadOnlyList<DeskBoxDisplayDiagnostic> displays = GetDisplayDiagnostics();
        SettingsPersistenceFailure? saveFailure = SettingsService.LastPersistenceFailure;
        GlobalHotkeyGesture toggleGesture = GlobalHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                SettingsService.Settings.GlobalHotkeyModifiers,
                SettingsService.Settings.GlobalHotkeyKey);
        GlobalHotkeyGesture searchGesture = SearchHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                SettingsService.Settings.SearchHotkeyModifiers,
                SettingsService.Settings.SearchHotkeyKey);

        return new DeskBoxDiagnosticSnapshot(
            SchemaVersion: 4,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            AppVersion: GetDiagnosticVersion(),
            DistributionChannel: DistributionService.ChannelName,
            IsPackaged: DistributionService.IsPackaged,
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            UiCulture: CultureInfo.CurrentUICulture.Name,
            Hotkeys: new DeskBoxHotkeyDiagnostic(
                SettingsService.Settings.GlobalHotkeyEnabled,
                GlobalHotkeyService?.IsRegistered == true,
                (int)toggleGesture.Modifiers,
                toggleGesture.VirtualKey,
                GlobalHotkeyService?.ReceivedCount ?? 0,
                GlobalHotkeyService?.InvocationCount ?? 0,
                GlobalHotkeyService?.DispatchFailureCount ?? 0,
                GlobalHotkeyService?.UsesReservedHook == true,
                GlobalHotkeyService?.ReservedHookThreadId ?? 0,
                GlobalHotkeyService?.ReservedHookLastErrorCode ?? 0,
                GlobalHotkeyService?.ReservedHookTriggerCount ?? 0,
                GlobalHotkeyService?.ReservedHookPostFailureCount ?? 0,
                GlobalHotkeyService?.ReservedHookInputFailureCount ?? 0,
                !string.IsNullOrWhiteSpace(GlobalHotkeyService?.LastError),
                SettingsService.Settings.SearchHotkeyEnabled,
                SearchHotkeyService?.IsRegistered == true,
                (int)searchGesture.Modifiers,
                searchGesture.VirtualKey),
            Settings: new DeskBoxSettingsDiagnostic(
                SettingsService.LastLoadRecoveryState,
                SettingsService.HasPendingSave,
                saveFailure?.Operation,
                saveFailure?.OccurredAt.ToUniversalTime()),
            RuntimeHealth: runtimeHealth,
            WidgetManager: widgetManager,
            Displays: displays);
    }

    private static IReadOnlyList<DeskBoxDisplayDiagnostic> GetDisplayDiagnostics()
    {
        try
        {
            return Win32Helper.GetMonitorWorkAreaInfos()
                .Select((display, index) => new DeskBoxDisplayDiagnostic(
                    index + 1,
                    display.IsPrimary,
                    display.DpiScale,
                    ToDiagnosticRect(display.Monitor),
                    ToDiagnosticRect(display.WorkArea)))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log($"[DiagnosticsBundle] Display enumeration failed: {ex.Message}");
            return [];
        }
    }

    private static DeskBoxDiagnosticRect ToDiagnosticRect(Win32Helper.RECT bounds)
    {
        return new DeskBoxDiagnosticRect(
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top);
    }

    private static string GetDiagnosticVersion()
    {
        Assembly assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}

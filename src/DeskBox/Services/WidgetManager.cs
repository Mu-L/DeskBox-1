﻿using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount,
    string OldRootPath,
    string NewRootPath);

public sealed record QuickCaptureFileWidgetTarget(
    string WidgetId,
    string Name,
    string FolderPath);

public enum WidgetRemovalAction
{
    RemoveWidgetOnly,
    MoveManagedFolderContentsToDesktop,
    DeleteManagedFolder
}

public sealed record ManagedStorageFolderCleanupCandidate(
    string Name,
    string Path,
    int ItemCount);

internal sealed record FeatureWidgetHandler(
    WidgetKind WidgetKind,
    Func<bool, Task<IDesktopWidgetWindow?>> CreateOrShowAsync,
    Func<bool, bool, Task> SetEnabledAsync,
    Action HideLoaded);

internal sealed record WidgetWindowCreationRequest(
    WidgetConfig Config,
    bool KeepPreparedForAnimation,
    bool RevealAfterCreate,
    bool ShowRaisedWhileInitializing,
    CancellationToken CancellationToken);

internal sealed record WidgetWindowProvider(
    WidgetKind WidgetKind,
    Func<WidgetWindowCreationRequest, Task<IDesktopWidgetWindow>> CreateWindowAsync);

internal interface IDesktopWidgetWindow
{
    WidgetWindowIdentity Identity { get; }
    WidgetConfig Config { get; }
    IntPtr WindowHandle { get; }
    bool Visible { get; }
    bool IsRaisedAboveDesktopLayer { get; }
    bool IsCompactArrangementActive { get; }
    Windows.Foundation.Rect AnimationBounds { get; }
    Windows.Foundation.Rect RestingAnimationBounds { get; }
    void ApplyAppearancePreview();
    void RestoreBoundsForCurrentTopology();
    void ApplyCompactArrangement(Windows.Graphics.RectInt32 bounds, bool constrainSize);
    void PreviewCompactArrangement(Windows.Graphics.RectInt32 bounds);
    void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY);
    void PrepareTrayShowAnimation();
    void ShowPreparedAtDesktopLayer(bool persistVisibility = true);
    void ShowPreparedRaisedFromTray(bool persistVisibility = true);
    void PlayTrayShowAnimation();
    void CompleteTrayShowWithoutAnimation();
    bool PrepareTrayHideAnimation(bool persistVisibility = true);
    void PlayPreparedTrayHideAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayShowAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayHideAnimation();
    void ActivateRaisedFromTrayBatch();
    void EnsureRaisedFromTrayTopMost();
    void RaiseTemporarilyFromManager();
    void ForceRestoreDesktopLayerFromManager();
    void RestoreDesktopLayerFromManager();
    Task WaitForFirstPresentedFrameAsync(CancellationToken cancellationToken);
    void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null);
    Windows.Graphics.RectInt32? GetGroupMergeTitleScreenBounds();
    void HideWindow();
    void CloseWindow();
}

/// <summary>
/// Manages the lifecycle of all desktop organizer widgets.
/// </summary>
public sealed partial class WidgetManager
{
    private const string ManagedShortcutDescriptionPrefix = "DeskBox mapped widget shortcut:";

    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly ThemeService _themeService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly LocalizationService _localizationService;
    private readonly Func<string> _desktopPathProvider;
    private readonly bool _recycleManagedFolderDeletes;
    private readonly WidgetRegistry _widgetRegistry;
    private readonly WidgetSessionManager _sessionManager;
    private readonly Dictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> _widgets = new();
    private readonly Dictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> _quickCaptureWidgets = new();
    private readonly Dictionary<string, ContentWidgetWindow> _contentWidgets = new();
    private readonly HashSet<IntPtr> _widgetWindowHandles = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly HashSet<string> _suppressClosedVisibilityPersistence = [];
    private readonly SemaphoreSlim _widgetRenameGate = new(1, 1);

    public IReadOnlyDictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> Widgets => _widgets;
    public IReadOnlyDictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> QuickCaptureWidgets => _quickCaptureWidgets;
    internal IReadOnlyDictionary<string, ContentWidgetWindow> ContentWidgets => _contentWidgets;

    public bool WidgetsRaisedFromTray => _widgetsRaisedFromTray;
    public WidgetSessionState SessionState => _sessionManager.State;
    public bool IsWidgetInteractionActive => _sessionManager.IsInteractionActive;

    public bool HasVisibleWidgets =>
        GetLoadedDesktopWindows().Any(window => window.Visible);

    internal int LoadedWidgetCount => _widgetSurfaces.Count;

    internal int VisibleWidgetCount => GetLoadedDesktopWindows().Count(window => window.Visible);

    public bool IsWidgetWindow(IntPtr hwnd)
    {
        return _widgetWindowHandles.Contains(hwnd);
    }

    /// <summary>
    /// Returns the HWND of every currently-loaded widget window.
    /// Used by the resize guide service to detect alignment targets.
    /// </summary>
    public IReadOnlyList<IntPtr> GetAllWidgetWindowHandles()
    {
        return _widgetWindowHandles.ToList();
    }

    /// <summary>
    /// Finds the root FrameworkElement of a widget window by its HWND.
    /// Used by the resize guide service to show edge highlights on target widgets.
    /// </summary>
    public FrameworkElement? GetWidgetRootElementByHandle(IntPtr hwnd)
    {
        foreach (var entry in _widgets.Values)
        {
            if (entry.Window.WindowHandle == hwnd)
            {
                return entry.Window.Content as FrameworkElement;
            }
        }

        foreach (var entry in _quickCaptureWidgets.Values)
        {
            if (entry.Window.WindowHandle == hwnd)
            {
                return entry.Window.Content as FrameworkElement;
            }
        }

        foreach (var window in _contentWidgets.Values)
        {
            if (window.WindowHandle == hwnd)
            {
                return window.Content as FrameworkElement;
            }
        }

        return null;
    }

    private IReadOnlyList<IDesktopWidgetWindow> GetLoadedDesktopWindows()
    {
        return _widgetSurfaces.GetSessions()
            .Select(session => session.Host)
            .GroupBy(host => host.WindowHandle)
            .Select(group => group.First())
            .ToList();
    }

    public void BeginWidgetInteraction(string reason)
    {
        _sessionManager.BeginInteraction(reason);
    }

    public void EndWidgetInteraction(string reason)
    {
        _sessionManager.EndInteraction(reason);
    }

    public event Action<WidgetWindow>? WidgetCreated;
    public event Action<string>? WidgetRemoved;
    public event Action<bool>? TrayLayerStateChanged;

    private static bool HasUiThreadAccess()
    {
        var dispatcherQueue = App.UiDispatcherQueue;
        return dispatcherQueue is null || dispatcherQueue.HasThreadAccess;
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        var dispatcherQueue = App.UiDispatcherQueue;
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Unable to dispatch widget lifecycle operation to the UI thread."));
        }

        return completion.Task;
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        return RunOnUiThreadAsync(async () =>
        {
            await action();
            return true;
        });
    }

    public bool ShouldHideWidgetsForTrayToggle()
    {
        if (_widgetsRaisedFromTray)
        {
            App.LogVerbose("[TrayBatch] ToggleDecision=hide reason=raised-session");
            return true;
        }

        if (!HasVisibleWidgets)
        {
            App.LogVerbose("[TrayBatch] ToggleDecision=raise reason=no-visible-windows");
            return false;
        }

        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        if (IsDeskBoxForegroundWindow(foregroundWindow) ||
            IsDesktopShellWindow(foregroundWindow) ||
            IsTaskbarWindow(foregroundWindow))
        {
            App.LogVerbose(
                $"[TrayBatch] ToggleDecision=hide reason=foreground-local hwnd=0x{foregroundWindow.ToInt64():X}");
            return true;
        }

        App.LogVerbose(
            $"[TrayBatch] ToggleDecision=raise reason=visible-widgets-behind hwnd=0x{foregroundWindow.ToInt64():X}");
        return false;
    }

    public WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        LocalizationService? localizationService = null)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
            quickCaptureService,
            localizationService ?? new LocalizationService(settingsService),
            () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            recycleManagedFolderDeletes: true)
    {
    }

    internal WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
            quickCaptureService,
            null,
            desktopPathProvider,
            recycleManagedFolderDeletes)
    {
    }

    internal WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        LocalizationService? localizationService,
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _themeService = themeService;
        _quickCaptureService = quickCaptureService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _desktopPathProvider = desktopPathProvider;
        _recycleManagedFolderDeletes = recycleManagedFolderDeletes;
        _widgetRegistry = WidgetRegistry.Default;
        _sessionManager = new WidgetSessionManager(App.LogVerbose);
        InitializeCapsuleArrangementState();
        _featureWidgetHandlers = CreateFeatureWidgetHandlers();
        _windowProviders = CreateWindowProviders();
        foreach (var kind in FeatureWidgetSettings.FeatureKinds)
        {
            _lastFeatureWidgetEnabledStates[kind] = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
        }
        _lastWidgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(_settingsService.Settings.WidgetLayerMode);
        InitializeWidgetGroupPresentationDefaults();
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settingsService.AppearancePreviewChanged += ApplyAppearancePreview;
        _themeService.AppearanceChanged += ApplyAppearancePreview;
    }

    private Dictionary<WidgetKind, FeatureWidgetHandler> CreateFeatureWidgetHandlers()
    {
        FeatureWidgetHandler[] handlers =
        [
            new(
                WidgetKind.QuickCapture,
                async reveal => await CreateOrShowQuickCaptureWidgetAsync(reveal),
                SetQuickCaptureEnabledAsync,
                CloseLoadedQuickCaptureWidgets),
            new(
                WidgetKind.Todo,
                async _ => await CreateTodoWidgetAsync(),
                SetTodoEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Todo)),
            new(
                WidgetKind.Music,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Music),
                SetContentFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Music)),
            new(
                WidgetKind.Weather,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Weather),
                SetWeatherFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Weather)),
            new(
                WidgetKind.Search,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Search),
                SetSearchFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Search))
        ];

        return handlers.ToDictionary(handler => handler.WidgetKind);
    }

    private Dictionary<WidgetKind, WidgetWindowProvider> CreateWindowProviders()
    {
        WidgetWindowProvider[] providers =
        [
            new(
                WidgetKind.File,
                async request => await CreateCancellableWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.QuickCapture,
                async request => await CreateQuickCaptureWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Todo,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Music,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Weather,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Search,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken))
        ];

        return providers.ToDictionary(provider => provider.WidgetKind);
    }

    private void OnSettingsChanged()
    {
        ApplyWidgetLayerModeIfChanged();
        ApplyCapsuleArrangementIfChanged();

        foreach (var kind in FeatureWidgetSettings.FeatureKinds)
        {
            bool enabled = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
            if (_lastFeatureWidgetEnabledStates.TryGetValue(kind, out bool lastEnabled) &&
                lastEnabled == enabled)
            {
                continue;
            }

            _lastFeatureWidgetEnabledStates[kind] = enabled;
            ApplyFeatureWidgetEnabledState(kind, enabled);
        }

        RefreshWidgetGroupPresentationDefaultsIfChanged();
    }

    private void ApplyWidgetLayerModeIfChanged()
    {
        string layerMode = SettingsService.NormalizeWidgetLayerModeSetting(_settingsService.Settings.WidgetLayerMode);
        if (string.Equals(layerMode, _lastWidgetLayerMode, StringComparison.Ordinal))
        {
            return;
        }

        string previousMode = _lastWidgetLayerMode;
        _lastWidgetLayerMode = layerMode;
        WidgetLayerService.InvalidateDesktopIconViewCache();
        App.Log($"[WidgetManager] Widget layer mode changed {previousMode}->{layerMode}");
        RefreshVisibleWidgetDesktopLayers("layer-mode-changed");
    }

    public void RefreshVisibleWidgetDesktopLayers(string reason)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(() => RefreshVisibleWidgetDesktopLayers(reason));
            return;
        }

        App.Log($"[WidgetManager] Refresh visible widget desktop layers reason={reason}");
        foreach (var window in GetLoadedDesktopWindows())
        {
            if (!window.Visible)
            {
                continue;
            }

            try
            {
                window.ForceRestoreDesktopLayerFromManager();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to refresh widget desktop layer {FormatHostWindow(window)}: {ex}");
            }
        }
    }

    private void ApplyAppearancePreview()
    {
        if (_isApplyingAppearancePreview)
        {
            return;
        }

        _isApplyingAppearancePreview = true;
        try
        {
            foreach (var (_, (window, _)) in _widgets.ToList())
            {
                window.ApplyAppearancePreview();
            }

            foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
            {
                window.ApplyAppearancePreview();
            }

            foreach (var (_, window) in _contentWidgets.ToList())
            {
                window.ApplyAppearancePreview();
            }
        }
        finally
        {
            _isApplyingAppearancePreview = false;
        }
    }

    /// <summary>
    /// Restore all visible file widgets from saved configuration.
    /// </summary>
    public async Task RestoreWidgetsAsync()
    {
        RepairLegacyContentFeatureFileShells();

        // Dedup feature widgets: each kind should only have one config
        DeduplicateFeatureWidgets();
        NormalizeWidgetGroupsForRuntime();

        var visibleConfigs = _settingsService.Settings.Widgets.Where(widget =>
                widget.IsVisible &&
                !widget.IsDisabled &&
                !IsDeleted(widget.Id) &&
                WidgetGroupSettings.IsActiveMember(_settingsService.Settings, widget.Id))
            .ToList();

        foreach (var unsupportedConfig in visibleConfigs.Where(widget => !_widgetRegistry.CanCreateWindow(widget.WidgetKind)))
        {
            string reason = _widgetRegistry.IsKnown(unsupportedConfig.WidgetKind)
                ? "not-implemented-yet"
                : "unknown-kind";
            App.Log($"[WidgetManager] Skipping widget restore reason={reason} widget={FormatWidget(unsupportedConfig)}");
        }

        var configs = visibleConfigs.Where(widget =>
                _widgetRegistry.IsAvailableForSession(widget, _settingsService.Settings))
            .ToList();

        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgets", $"count={configs.Count}");
        foreach (var config in configs)
        {
            try
            {
                using var widgetPerfScope = PerformanceLogger.Measure(
                    "WidgetManager.RestoreWidget",
                    $"id={config.Id} name={config.Name}");
                await CreateRegisteredWidgetFromConfigAsync(config);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore widget '{config.Name}' ({config.Id}): {ex}");
            }

            await Task.Yield();
        }

        // A grouped widget owns one persistent content surface.  Restoring
        // only the active member above is normally sufficient, but the host
        // can still be hidden or positioned by WinUI after its content tree
        // finishes loading.  Re-show each persisted visible group once the
        // complete restore pass has settled so a group cannot silently lose
        // its surface during startup.
        await RestoreVisibleWidgetGroupsAsync();

        // Window creation can temporarily apply compact/capsule geometry
        // before the surface host has finished loading. Reconcile every
        // restored host once more from the persisted placement so a grouped
        // surface cannot remain stranded just outside the current work area.
        RestoreLoadedWidgetBoundsAfterStartup();

        QueueDeferredStartupWidgetBoundsReconciliation();

        if (configs.Count > 0)
        {
            RaiseVisibleWidgetsTemporarily("startup-restore");
            _sessionManager.MarkDesktopResting("restore-widgets");
            QueueVisibleGroupedFileIconRecoveryAfterStartup();
        }
    }

    /// <summary>
    /// Create a new widget backed by the default managed storage root.
    /// </summary>
    public async Task<WidgetWindow> CreateManagedWidgetAsync(string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name)
            ? _localizationService.T("Widget.DefaultName")
            : name;
        string managedFolderName = CreateManagedFolderName(name);
        string folderPath = BuildManagedFolderPath(managedFolderName);
        Directory.CreateDirectory(folderPath);

        var config = new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = managedFolderName,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public async Task CreateWidgetOfKindAsync(WidgetKind widgetKind)
    {
        if (!_widgetRegistry.CanCreateWindow(widgetKind))
        {
            throw new NotSupportedException($"Widget kind '{widgetKind}' is not registered as creatable.");
        }

        switch (widgetKind)
        {
            case WidgetKind.File:
                await CreateManagedWidgetAsync(_localizationService.T("Widget.DefaultNameShort"));
                break;
            case WidgetKind.Todo:
                await CreateTodoWidgetAsync();
                break;
            case WidgetKind.Music:
                await CreateSingletonContentFeatureWidgetAsync(widgetKind);
                break;
            default:
                if (IsContentFeatureWidgetKind(widgetKind))
                {
                    await CreateSingletonContentFeatureWidgetAsync(widgetKind);
                    break;
                }

                await CreateRegisteredWidgetFromConfigAsync(new WidgetConfig
                {
                    Name = GetDefaultFeatureWidgetTitle(
                        widgetKind,
                        new WidgetContentFactory(_localizationService).GetDescriptor(widgetKind)),
                    WidgetKind = widgetKind,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    Width = _settingsService.Settings.DefaultWidgetWidth,
                    Height = _settingsService.Settings.DefaultWidgetHeight
                }, revealAfterCreate: true);
                break;
        }
    }

    /// <summary>
    /// Create a widget mapped to an arbitrary folder.
    /// </summary>
    public async Task<WidgetWindow> CreateFolderWidgetAsync(string folderPath)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        EnsureFileWidgetPathAvailable(normalizedPath);

        string folderName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = normalizedPath;
        }

        var config = new WidgetConfig
        {
            Name = folderName,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = normalizedPath,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        SyncMappedWidgetShortcut(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public void EnsureFileWidgetPathAvailable(string folderPath, string? excludedWidgetId = null)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        WidgetConfig? conflict = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.File &&
            !IsDeleted(widget.Id) &&
            !string.Equals(widget.Id, excludedWidgetId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
            FileService.PathsOverlap(normalizedPath, widget.MappedFolderPath));

        if (conflict is null)
        {
            return;
        }

        throw new InvalidOperationException(_localizationService.Format(
            "Widget.Error.FileWidgetPathConflict",
            conflict.Name));
    }

    /// <summary>
    /// Show a specific widget by id.
    /// </summary>
    public async Task<bool> ShowWidgetAsync(string widgetId, bool reveal = true, bool autoRestoreOnReveal = true)
    {
        if (IsDeleted(widgetId))
        {
            return false;
        }

        var config = FindConfig(widgetId);
        if (config is null || config.IsDisabled)
        {
            return false;
        }

        if (!_widgetRegistry.IsAvailableForSession(config, _settingsService.Settings))
        {
            return false;
        }

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            group.IsVisible = true;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } memberConfig)
                {
                    memberConfig.IsVisible = true;
                }
            }
            _settingsService.SaveDebounced(notifySubscribers: false);

            if (!string.Equals(group.ActiveMemberId, widgetId, StringComparison.Ordinal))
            {
                return await SwitchWidgetGroupMemberAsync(widgetId);
            }

            ApplyGroupLayoutToMember(group, config);
        }

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
            {
                quickCaptureEntry.Window.RestoreBoundsForCurrentTopology();
                if (reveal)
                {
                    quickCaptureEntry.Window.RevealFromTray(autoRestoreOnReveal);
                }
                else
                {
                    quickCaptureEntry.Window.PrepareTrayShowAnimation();
                    quickCaptureEntry.Window.ShowPreparedAtDesktopLayer();
                    quickCaptureEntry.Window.CompleteTrayShowWithoutAnimation();
                }

                return true;
            }

            var quickCaptureWindow = (QuickCaptureWidgetWindow)await CreateRegisteredWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation: !reveal);
            quickCaptureWindow.RestoreBoundsForCurrentTopology();
            if (reveal)
            {
                quickCaptureWindow.RevealFromTray(autoRestoreOnReveal);
            }
            else
            {
                quickCaptureWindow.PrepareTrayShowAnimation();
                quickCaptureWindow.ShowPreparedAtDesktopLayer();
                quickCaptureWindow.CompleteTrayShowWithoutAnimation();
            }

            return true;
        }

        if (IsContentFeatureWidgetKind(config.WidgetKind))
        {
            return await ShowContentWidgetAsync(config, reveal);
        }

        if (config.WidgetKind != WidgetKind.File)
        {
            App.Log($"[WidgetManager] Show skipped reason=unsupported-kind widget={FormatWidget(config)}");
            return false;
        }

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            entry.Window.RestoreBoundsForCurrentTopology();
            if (reveal)
            {
                entry.Window.RevealFromTray(autoRestoreOnReveal);
            }
            else
            {
                entry.Window.PrepareTrayShowAnimation();
                entry.Window.ShowPreparedAtDesktopLayer();
                entry.Window.CompleteTrayShowWithoutAnimation();
            }

            return true;
        }

        var window = await CreateWidgetFromConfigAsync(config, keepPreparedForAnimation: !reveal);
        window.RestoreBoundsForCurrentTopology();
        if (reveal)
        {
            window.RevealFromTray(autoRestoreOnReveal);
        }
        else
        {
            window.PrepareTrayShowAnimation();
            window.ShowPreparedAtDesktopLayer();
            window.CompleteTrayShowWithoutAnimation();
        }

        return true;
    }

    private async Task<bool> ShowContentWidgetAsync(WidgetConfig config, bool reveal)
    {
        if (_contentWidgets.TryGetValue(config.Id, out var contentWindow))
        {
            contentWindow.RestoreBoundsForCurrentTopology();
            contentWindow.PrepareTrayShowAnimation();
            if (reveal)
            {
                contentWindow.ShowPreparedRaisedFromTray();
                contentWindow.PlayTrayShowAnimation();
            }
            else
            {
                contentWindow.ShowPreparedAtDesktopLayer();
                contentWindow.CompleteTrayShowWithoutAnimation();
            }

            return true;
        }

        var createdWindow = await CreateContentWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: !reveal,
            revealAfterCreate: reveal);
        createdWindow.RestoreBoundsForCurrentTopology();
        if (!reveal)
        {
            createdWindow.PrepareTrayShowAnimation();
            createdWindow.ShowPreparedAtDesktopLayer();
            createdWindow.CompleteTrayShowWithoutAnimation();
        }

        return true;
    }

    /// <summary>
    /// Show or hide all currently managed widgets.
    /// </summary>
    public async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.SetAllWidgetsVisible", $"visible={visible}");
        App.LogVerbose(
            $"[TrayBatch] SetAllVisible requested visible={visible} raised={_widgetsRaisedFromTray} " +
            $"loadedFile={_widgets.Count} loadedQuick={_quickCaptureWidgets.Count} loadedContent={_contentWidgets.Count}");
        _trayBatchAnimationDriver.Cancel();
        if (visible)
        {
            App.CancelBackgroundMemoryCleanup();
            var candidates = _settingsService.Settings.Widgets
                .Where(IsSessionCandidate)
                .ToList();
            App.LogVerbose($"[TrayBatch] SetAllVisible candidates={candidates.Count} widgets={FormatWidgetList(candidates)}");

            var windowsToShow = new List<IDesktopWidgetWindow>();
            foreach (var widget in candidates)
            {
                try
                {
                    var window = await PrepareWidgetForBatchShowAsync(widget, showRaisedWhileInitializing: true);
                    if (window is null)
                    {
                        continue;
                    }

                    windowsToShow.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget for visible state '{widget.Name}' ({widget.Id}): {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] SetAllVisible preparedShow={windowsToShow.Count}/{candidates.Count}");
            var windowsToAnimate = windowsToShow
                .Where(window => !window.Visible)
                .ToList();
            PrepareTrayShowAnimations(windowsToAnimate);

            var shownWindows = new List<IDesktopWidgetWindow>();
            foreach (var window in windowsToShow)
            {
                try
                {
                    if (window.Visible)
                    {
                        shownWindows.Add(window);
                        continue;
                    }

                    window.ShowPreparedAtDesktopLayer(persistVisibility: false);
                    shownWindows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to show prepared widget at desktop layer {FormatHostWindow(window)}: {ex}");
                }
            }

            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=true prepared={windowsToShow.Count} shown={shownWindows.Count}");
            return;
        }

        CancelAllWidgetSurfaceSwitches();
        var hideCandidates = GetLoadedDesktopWindows()
            .Where(window => window.Visible)
            .ToList();
        
        // ⭐ 终极优化：将整个 Prepare + Play 流程都放在 TryEnqueue 中
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dispatcher.TryEnqueue(() =>
        {
            ApplyTrayAnimationGroupOffset(hideCandidates);
            
            var windowsToHide = new List<IDesktopWidgetWindow>();
            foreach (var window in hideCandidates)
            {
                try
                {
                    if (window.PrepareTrayHideAnimation(persistVisibility: false))
                    {
                        windowsToHide.Add(window);
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget hide {FormatHostWindow(window)}: {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] SetAllVisible preparedHide={windowsToHide.Count}");
            PlayPreparedTrayHideAnimations(windowsToHide);

            SetWidgetsRaisedFromTray(false);
            _sessionManager.MarkHidden("set-all-hidden");
            _trayRaiseBatchGeneration++;
            StopTrayLayerRestoreMonitor();
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=false prepared={windowsToHide.Count}");
            App.ScheduleBackgroundMemoryCleanup();
        });

        return;
    }

    /// <summary>
    /// Restores all loaded widget windows to their correct positions for
    /// the current display topology.  Called when displays are added,
    /// removed, or reconfigured (hot-plug, resolution change, DPI change).
    /// </summary>
    public async Task RestoreWidgetPositionsAsync()
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgetPositions");
        App.Log("[WidgetManager] Restoring widget positions for current display topology");

        foreach (var entry in _widgets.Values.ToList())
        {
            try
            {
                entry.Window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for widget '{entry.Window.Identity.WidgetId}': {ex.Message}");
            }
        }

        foreach (var window in _contentWidgets.Values.ToList())
        {
            try
            {
                window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for content widget: {ex.Message}");
            }
        }

        foreach (var entry in _quickCaptureWidgets.Values.ToList())
        {
            try
            {
                entry.Window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for quick capture widget: {ex.Message}");
            }
        }

        await Task.Yield();
    }

    /// <summary>
    /// Remove a widget and close its window.
    /// </summary>
    public async Task RemoveWidgetAsync(string widgetId, WidgetRemovalAction removalAction = WidgetRemovalAction.RemoveWidgetOnly)
    {
        var config = FindConfig(widgetId);
        if (config is not null)
        {
            await RemoveWidgetFromGroupAsync(widgetId, revealStandalone: false);
        }
        _deletedWidgetIds.Add(widgetId);

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            App.Log($"[WidgetManager] Retiring widget window for delete: {widgetId}");
            entry.ViewModel.Dispose();
            _widgets.Remove(widgetId);
            _widgetWindowHandles.Remove(entry.Window.WindowHandle);
            try { entry.Window.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { entry.Window.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            App.Log($"[WidgetManager] Retiring quick capture widget window for delete: {widgetId}");
            quickCaptureEntry.ViewModel.Dispose();
            _quickCaptureWidgets.Remove(widgetId);
            _widgetWindowHandles.Remove(quickCaptureEntry.Window.WindowHandle);
            try { quickCaptureEntry.Window.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { quickCaptureEntry.Window.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
        }

        if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            App.Log($"[WidgetManager] Retiring content widget window for delete: {widgetId}");
            _contentWidgets.Remove(widgetId);
            _widgetWindowHandles.Remove(contentWindow.WindowHandle);
            // Explicitly dispose content (e.g. MusicWidgetViewModel) BEFORE
            // closing the window.  The Closed event handler also calls
            // DisposeContent, but if the event is delayed or fails, the
            // MusicSessionService's event subscriptions on the WinRT
            // singleton would keep the old ViewModel alive indefinitely.
            try
            {
                if (contentWindow.CurrentContent is IDisposable disposableContent)
                {
                    disposableContent.Dispose();
                }
            }
            catch (Exception ex) { App.Log($"[WidgetManager] Content dispose failed during delete: {ex.Message}"); }
            try { contentWindow.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { contentWindow.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
        }

        if (config is not null)
        {
            try
            {
                await ApplyWidgetRemovalActionAsync(config, removalAction);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Managed folder cleanup failed while deleting widget '{widgetId}'. The widget will be removed and the folder will be kept. {ex}");
            }

            RemoveMappedWidgetShortcut(config);
        }

        _settingsService.RemoveWidgetImmediate(widgetId);
        ClearWidgetGroupTransientState(widgetId);
        if (config is not null && FeatureWidgetSettings.IsFeatureWidget(config.WidgetKind))
        {
            SetFeatureWidgetEnabledState(config.WidgetKind, false);
        }
        await _settingsService.SaveAsync();
        _deletedWidgetIds.Remove(widgetId);
        App.Log($"[WidgetManager] Widget delete persisted: {widgetId} kind={config?.WidgetKind} featureEnabled={GetFeatureWidgetEnabledState(config?.WidgetKind)}");
        WidgetRemoved?.Invoke(widgetId);
    }

    public async Task RenameWidgetAsync(string widgetId, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        await _widgetRenameGate.WaitAsync();
        try
        {
            await RenameWidgetCoreAsync(widgetId, newName);
        }
        finally
        {
            _widgetRenameGate.Release();
        }
    }

    private async Task RenameWidgetCoreAsync(string widgetId, string newName)
    {
        var config = FindConfig(widgetId);
        if (config is null || IsDeleted(widgetId))
        {
            return;
        }

        if (config.FollowsDefaultStoragePath)
        {
            await RenameManagedWidgetFolderAsync(config, newName);
        }
        else
        {
            SyncMappedWidgetShortcut(config, newName);
        }

        config.Name = newName;
        config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(config);
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is not null)
        {
            RaiseWidgetGroupsChanged();
        }
    }

    private void SyncStorageFolderEntries(string oldRootPath)
    {
        if (!string.IsNullOrWhiteSpace(oldRootPath))
        {
            RemoveAllMappedWidgetShortcuts(oldRootPath);
        }

        SyncStorageFolderEntries();
    }

    public void RemoveWidget(string widgetId)
    {
        _ = RemoveWidgetAsync(widgetId);
    }

    public void ClearSelectionsExcept(string activeWidgetId)
    {
        foreach (var (widgetId, (window, _)) in _widgets.ToList())
        {
            if (string.Equals(widgetId, activeWidgetId, StringComparison.Ordinal))
            {
                continue;
            }

            window.ClearItemSelection();
        }

        foreach (ContentWidgetWindow window in _contentWidgets.Values.Distinct())
        {
            if (window.CurrentContent is not FileSurfaceContent fileContent ||
                string.Equals(
                    fileContent.WidgetId,
                    activeWidgetId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            fileContent.ClearItemSelection();
        }
    }

    private void RestoreLoadedWidgetBoundsAfterStartup()
    {
        var windows = _widgets.Values
            .Select(entry => (IDesktopWidgetWindow)entry.Window)
            .Concat(_quickCaptureWidgets.Values.Select(entry => (IDesktopWidgetWindow)entry.Window))
            .Concat(_contentWidgets.Values.Select(window => (IDesktopWidgetWindow)window))
            .Distinct()
            .ToList();

        foreach (IDesktopWidgetWindow window in windows)
        {
            try
            {
                window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Startup bounds reconciliation failed " +
                    $"widget={window.Config.Id}: {ex}");
            }
        }
    }

    private async Task RestoreVisibleWidgetGroupsAsync()
    {
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups.ToList())
        {
            if (!group.IsVisible ||
                FindConfig(group.ActiveMemberId) is not { } config ||
                IsDeleted(config.Id) ||
                config.IsDisabled ||
                !_widgetRegistry.IsAvailableForSession(
                    config,
                    _settingsService.Settings))
            {
                continue;
            }

            // This runs both during initial restoration and during the deferred
            // bounds pass. Re-showing an already-visible group sends its surface
            // through the desktop-layer show path, which would undo the temporary
            // foreground raise applied after startup. Only re-show a group when
            // its native host has actually been hidden or lost.
            IDesktopWidgetWindow? existingWindow = GetLoadedWindow(group.ActiveMemberId);
            if (existingWindow is { Visible: true } &&
                existingWindow.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(existingWindow.WindowHandle))
            {
                existingWindow.RestoreBoundsForCurrentTopology();
                App.Log(
                    $"[WidgetGroup] Kept visible group surface during restore: " +
                    $"group={group.Id}, active={group.ActiveMemberId}, " +
                    $"hwnd=0x{existingWindow.WindowHandle.ToInt64():X}");
                continue;
            }

            try
            {
                await ShowGroupActiveWindowAsync(group);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetGroup] Visible group restore failed " +
                    $"group={group.Id} active={group.ActiveMemberId}: {ex}");
            }
        }
    }

    private void QueueDeferredStartupWidgetBoundsReconciliation()
    {
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                // Let RootElement.Loaded and the first composition/layout pass
                // complete before the final native bounds reconciliation.
                await Task.Yield();
                await Task.Delay(120);
                await RestoreVisibleWidgetGroupsAsync();
                RestoreLoadedWidgetBoundsAfterStartup();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Deferred startup bounds reconciliation failed: {ex}");
            }
        });
    }

    private void QueueVisibleGroupedFileIconRecoveryAfterStartup()
    {
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                // A persistent group surface can be shown without activation
                // while the startup layer handoff is still settling. In that
                // window, the initial asynchronous icon hydration can be
                // interrupted and leave every item on its fallback glyph.
                // Match the existing manual refresh once, but only for a
                // visible grouped file surface that still has no loaded icons.
                await Task.Delay(900);

                foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups.ToList())
                {
                    if (!group.IsVisible ||
                        FindConfig(group.ActiveMemberId) is not { WidgetKind: WidgetKind.File } config ||
                        IsDeleted(config.Id) ||
                        config.IsDisabled ||
                        !_widgetRegistry.IsAvailableForSession(
                            config,
                            _settingsService.Settings))
                    {
                        continue;
                    }

                    if (GetLoadedWindow(group.ActiveMemberId) is not ContentWidgetWindow window ||
                        !window.Visible ||
                        window.CurrentContent is not FileSurfaceContent fileSurface ||
                        !string.Equals(
                            fileSurface.WidgetId,
                            group.ActiveMemberId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int itemCount = fileSurface.ViewModel.Items.Count;
                    if (itemCount == 0 ||
                        fileSurface.ViewModel.Items.Any(item => item.Icon is not null))
                    {
                        continue;
                    }

                    await fileSurface.RefreshAsync();
                    App.Log(
                        $"[StartupIconRecovery] Refreshed visible grouped file surface " +
                        $"group={group.Id} widget={fileSurface.WidgetId} items={itemCount}");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Startup grouped file icon recovery failed: {ex}");
            }
        });
    }

    private bool IsSessionCandidate(WidgetConfig widget)
    {
        return !widget.IsDisabled &&
               !IsDeleted(widget.Id) &&
               WidgetGroupSettings.IsActiveMember(_settingsService.Settings, widget.Id) &&
               _widgetRegistry.IsAvailableForSession(widget, _settingsService.Settings);
    }

    private void QueueTrayRaiseTopMostConfirmation(
        IReadOnlyList<IDesktopWidgetWindow> windows,
        long generation,
        TimeSpan delay)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            ConfirmTrayRaiseTopMost(windows, generation);
        });
    }

    private static bool CanCreateWidgetWindowOnCurrentThread()
    {
        return App.UiDispatcherQueue is not null;
    }

    /// <summary>
    /// Hide a widget if it is currently loaded.
    /// </summary>
    public bool HideWidget(string widgetId)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            _widgetGroupSwitchRequests.Cancel(group.SurfaceId);
        }

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            entry.Window.HideWindow();
            SetWidgetGroupVisibility(entry.Window.Config, isVisible: false);
            return true;
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            quickCaptureEntry.Window.HideWindow();
            SetWidgetGroupVisibility(quickCaptureEntry.Window.Config, isVisible: false);
            return true;
        }

        if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            contentWindow.HideWindow();
            SetWidgetGroupVisibility(contentWindow.Config, isVisible: false);
            return true;
        }

        return false;
    }

    private void RestoreRaisedWidgetsToDesktopLayer(bool force)
    {
        if (!force &&
            (_isTogglingWidgetsDesktopLayer ||
             DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc))
        {
            App.LogVerbose($"[TrayBatch] RestoreDesktopLayer skipped force={force} reason=busy-or-suppressed");
            return;
        }

        App.Log(
            $"[TrayBatch] RestoreDesktopLayer force={force} file={_widgets.Count} quick={_quickCaptureWidgets.Count} content={_contentWidgets.Count}");
        foreach (var window in GetLoadedDesktopWindows())
        {
            try
            {
                window.ForceRestoreDesktopLayerFromManager();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore widget desktop layer {FormatHostWindow(window)}: {ex}");
            }
        }

        SetWidgetsRaisedFromTray(false);
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
    }

    /// <summary>
    /// Update the persisted position lock state for a widget.
    /// </summary>
    public bool SetWidgetPositionLocked(string widgetId, bool locked)
    {
        if (_widgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetPositionLocked(locked);
            SynchronizeGroupLayoutFromMember(loadedEntry.ViewModel.Config);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsPositionLocked = locked;
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is { } group)
        {
            group.IsPositionLocked = locked;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } member)
                {
                    member.IsPositionLocked = locked;
                }
            }
        }
        _settingsService.UpdateWidget(config);
        return true;
    }

    /// <summary>
    /// Update the persisted size lock state for a widget.
    /// </summary>
    public bool SetWidgetSizeLocked(string widgetId, bool locked)
    {
        if (_widgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetSizeLocked(locked);
            SynchronizeGroupLayoutFromMember(loadedEntry.ViewModel.Config);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsSizeLocked = locked;
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is { } group)
        {
            group.IsSizeLocked = locked;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } member)
                {
                    member.IsSizeLocked = locked;
                }
            }
        }
        _settingsService.UpdateWidget(config);
        return true;
    }

    /// <summary>
    /// Toggle visibility across all file widgets.
    /// </summary>
    public async Task ToggleAllWidgetsAsync()
    {
        bool anyVisible = _settingsService.Settings.Widgets.Any(widget =>
            widget.IsVisible &&
            IsSessionCandidate(widget));

        await SetAllWidgetsVisibleAsync(!anyVisible);
    }

    /// <summary>
    /// Close all widget windows for shutdown.
    /// </summary>
    public void CloseAll()
    {
        CancelAllWidgetSurfaceSwitches();
        StopTrayLayerRestoreMonitor();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _settingsService.AppearancePreviewChanged -= ApplyAppearancePreview;
        _themeService.AppearanceChanged -= ApplyAppearancePreview;

        foreach (var (_, (window, viewModel)) in _widgets.ToList())
        {
            viewModel.Dispose();
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _widgets.Clear();

        foreach (var (_, (window, viewModel)) in _quickCaptureWidgets)
        {
            viewModel.Dispose();
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _quickCaptureWidgets.Clear();

        foreach (var (_, window) in _contentWidgets.ToList())
        {
            try
            {
                if (window.CurrentContent is IDisposable disposableContent)
                {
                    disposableContent.Dispose();
                }
            }
            catch
            {
            }
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _contentWidgets.Clear();
        _widgetWindowHandles.Clear();
        _widgetSurfaces.Clear();
        _sessionManager.MarkHidden("close-all");
    }

    public int GetDefaultManagedStorageWidgetCount()
    {
        return _settingsService.Settings.Widgets.Count(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id));
    }

    public async Task RefreshFileWidgetAsync(string widgetId)
    {
        if (!HasUiThreadAccess())
        {
            await RunOnUiThreadAsync(() => RefreshFileWidgetAsync(widgetId));
            return;
        }

        if (_widgets.TryGetValue(widgetId, out var fileEntry))
        {
            await fileEntry.ViewModel.RefreshFromConfigAsync();
            return;
        }

        ContentWidgetWindow? contentWindow = _contentWidgets.Values
            .Distinct()
            .FirstOrDefault(window =>
                window.CurrentContent is FileSurfaceContent surface &&
                string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
        if (contentWindow?.CurrentContent is FileSurfaceContent fileSurface)
        {
            await fileSurface.ViewModel.RefreshFromConfigAsync();
        }
    }

    public void SetDesktopOrganizationBusy(
        IEnumerable<string> widgetIds,
        bool isBusy)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue?.TryEnqueue(() =>
                SetDesktopOrganizationBusy(widgetIds.ToArray(), isBusy));
            return;
        }

        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_widgets.TryGetValue(widgetId, out var fileEntry))
            {
                fileEntry.Window.SetDesktopOrganizationBusy(isBusy);
                continue;
            }

            ContentWidgetWindow? contentWindow = _contentWidgets.Values
                .Distinct()
                .FirstOrDefault(window =>
                    window.CurrentContent is FileSurfaceContent surface &&
                    string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
            if (contentWindow?.CurrentContent is FileSurfaceContent fileSurface)
            {
                fileSurface.SetDesktopOrganizationBusy(isBusy);
            }
        }
    }

    private WidgetConfig? FindConfig(string widgetId)
    {
        return _settingsService.Settings.Widgets.FirstOrDefault(widget => widget.Id == widgetId);
    }

    private void SyncMappedWidgetShortcut(WidgetConfig config, string? displayNameOverride = null)
    {
        if (config.FollowsDefaultStoragePath ||
            string.IsNullOrWhiteSpace(config.MappedFolderPath))
        {
            RemoveMappedWidgetShortcut(config);
            return;
        }

        try
        {
            string rootPath = GetManagedStorageRootPath();
            Directory.CreateDirectory(rootPath);

            string targetPath = Path.GetFullPath(config.MappedFolderPath);
            string shortcutPath = GetExistingMappedWidgetShortcutPath(config, rootPath);
            string desiredShortcutPath = BuildAvailableMappedShortcutPath(
                displayNameOverride ?? config.Name,
                config.Id,
                rootPath,
                shortcutPath);

            if (!string.Equals(shortcutPath, desiredShortcutPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteMappedWidgetShortcut(shortcutPath, config.Id);
                shortcutPath = desiredShortcutPath;
            }

            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                targetPath,
                BuildMappedWidgetShortcutDescription(config.Id));
        }
        catch (Exception ex)
        {
            App.Log($"[MappedShortcut] Failed to sync shortcut for widget '{config.Id}': {ex}");
        }
    }

    private bool IsDeleted(string widgetId)
    {
        return _deletedWidgetIds.Contains(widgetId) ||
               _settingsService.Settings.DeletedWidgetIds.Contains(widgetId);
    }

    private (double Width, double Height) GetDefaultFeatureWidgetSize(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.Todo => (
                Math.Max(_settingsService.Settings.DefaultWidgetWidth, 320),
                Math.Max(_settingsService.Settings.DefaultWidgetHeight, 420)),
            WidgetKind.Music => (380, 190),
            WidgetKind.Weather => (200, 200),
            _ => (
                _settingsService.Settings.DefaultWidgetWidth,
                _settingsService.Settings.DefaultWidgetHeight)
        };
    }

    private Task SetContentFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Music, enabled, reveal);
    }

    private Task<WidgetWindow> CreateWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        return CreateCancellableWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation,
            revealAfterCreate,
            showRaisedWhileInitializing,
            CancellationToken.None);
    }

    private async Task<WidgetWindow> CreateCancellableWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (config.WidgetKind != WidgetKind.File)
        {
            throw new InvalidOperationException(
                $"File widget window creation requires a File config. Actual kind: {config.WidgetKind}.");
        }

        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new WidgetViewModel(config, _fileService, _organizerService, _settingsService, _localizationService, dispatcherQueue);
        var window = new WidgetWindow(viewModel, _settingsService, _localizationService);

        _themeService.TrackWindow(window);
        _widgets[config.Id] = (window, viewModel);
        RegisterCreatedSurfaceHost(config, window);
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) => OnFileWidgetWindowClosed(window);

        Task? initializationTask = null;
        try
        {
            window.PrepareTrayShowAnimation();
            if (!keepPreparedForAnimation)
            {
                window.Activate();
                window.PushToBottom();
            }
            else if (showRaisedWhileInitializing)
            {
                viewModel.IsLoading = true;
                QueueDeferredWidgetInitialization(config, window, viewModel);
                WidgetCreated?.Invoke(window);
                return window;
            }

            initializationTask = viewModel.InitializeAsync();
            await initializationTask.WaitAsync(cancellationToken);
            if (!keepPreparedForAnimation)
            {
                window.CompleteTrayShowWithoutAnimation();
                if (revealAfterCreate)
                {
                    window.RevealFromTray(autoRestore: false);
                }
            }
        }
        catch
        {
            _widgets.Remove(config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);
            UnregisterSurfaceHost(window);
            _ = ObserveAndDisposeWidgetInitializationAsync(
                initializationTask ?? Task.CompletedTask,
                viewModel,
                config);
            CloseFailedCreatedWindow(
                config.Id,
                window,
                preserveVisibility: cancellationToken.CanBeCanceled);
            throw;
        }

        WidgetCreated?.Invoke(window);
        return window;
    }

    private void OnFileWidgetWindowClosed(WidgetWindow window)
    {
        List<string> registeredIds = _widgets
            .Where(entry => ReferenceEquals(entry.Value.Window, window))
            .Select(entry => entry.Key)
            .ToList();
        foreach (string registeredId in registeredIds)
        {
            _widgets.Remove(registeredId);
        }

        UnregisterSurfaceHost(window);
        _widgetWindowHandles.Remove(window.WindowHandle);
        WidgetConfig closedConfig = window.Config;
        if (IsDeleted(closedConfig.Id) || FindConfig(closedConfig.Id) is null)
        {
            return;
        }

        if (_suppressClosedVisibilityPersistence.Contains(closedConfig.Id) ||
            registeredIds.Any(_suppressClosedVisibilityPersistence.Contains))
        {
            return;
        }

        if (_widgets.Values.Any(entry => ReferenceEquals(entry.Window, window)))
        {
            return;
        }

        closedConfig.IsVisible = false;
        SetWidgetGroupVisibility(closedConfig, isVisible: false);
        _settingsService.SaveDebounced();
    }

    private async Task<IDesktopWidgetWindow> CreateRegisteredWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false,
        CancellationToken cancellationToken = default)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);
        if (group is not null &&
            config.WidgetKind is WidgetKind.File or
                WidgetKind.QuickCapture)
        {
            return await CreateContentWidgetFromConfigAsync(
                config, keepPreparedForAnimation, revealAfterCreate,
                showRaisedWhileInitializing, cancellationToken);
        }

        if (!_windowProviders.TryGetValue(config.WidgetKind, out var provider))
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' is not registered as creatable.");
        }

        return await provider.CreateWindowAsync(new WidgetWindowCreationRequest(
            config,
            keepPreparedForAnimation,
            revealAfterCreate,
            showRaisedWhileInitializing,
            cancellationToken));
    }

    private async Task<ContentWidgetWindow> CreateContentWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => CreateContentWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation,
                revealAfterCreate,
                showRaisedWhileInitializing,
                cancellationToken));
        }

        if (_contentWidgets.TryGetValue(config.Id, out var existing))
        {
            if (!showRaisedWhileInitializing)
            {
                await existing.ContentReadyTask.WaitAsync(cancellationToken);
            }

            return existing;
        }

        ContentWidgetWindowFactory factory = CreateSurfaceContentWindowFactory();
        if (!factory.CanCreateContentWindow(config.WidgetKind))
        {
            throw new NotSupportedException(
                $"Widget kind '{config.WidgetKind}' does not support content window creation.");
        }

        if (!_widgetRegistry.IsAvailableForSession(config, _settingsService.Settings))
        {
            throw new InvalidOperationException($"Widget kind '{config.WidgetKind}' is disabled for the current session.");
        }

        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var window = factory.CreateContentWindow(config);
        _themeService.TrackWindow(window);
        _contentWidgets[config.Id] = window;
        RegisterCreatedSurfaceHost(config, window);
        RestoreWidgetGroupTransientState(config.Id);
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) =>
        {
            List<string> registeredIds = _contentWidgets
                .Where(entry => ReferenceEquals(entry.Value, window))
                .Select(entry => entry.Key)
                .ToList();
            foreach (string registeredId in registeredIds)
            {
                _contentWidgets.Remove(registeredId);
            }

            UnregisterSurfaceHost(window);
            _widgetWindowHandles.Remove(window.WindowHandle);
            WidgetConfig closedConfig = window.Config;
            if (IsDeleted(closedConfig.Id) || FindConfig(closedConfig.Id) is null)
            {
                return;
            }

            if (_suppressClosedVisibilityPersistence.Contains(closedConfig.Id) ||
                registeredIds.Any(_suppressClosedVisibilityPersistence.Contains))
            {
                return;
            }

            if (_contentWidgets.Values.Any(candidate => ReferenceEquals(candidate, window)))
            {
                return;
            }

            closedConfig.IsVisible = false;
            SetWidgetGroupVisibility(closedConfig, isVisible: false);
            _settingsService.SaveDebounced();
        };

        try
        {
            if (keepPreparedForAnimation && showRaisedWhileInitializing)
            {
                window.PrepareTrayShowAnimation();
                window.ShowPreparedRaisedFromTray();
                QueueDeferredContentInitialization(config, window);
                return window;
            }

            await window.ContentReadyTask.WaitAsync(cancellationToken);
            window.PrepareTrayShowAnimation();
            if (!keepPreparedForAnimation)
            {
                window.ShowPreparedAtDesktopLayer();
                window.CompleteTrayShowWithoutAnimation();
            }

            if (revealAfterCreate)
            {
                window.ShowPreparedRaisedFromTray();
                window.PlayTrayShowAnimation();
            }
        }
        catch
        {
            _contentWidgets.Remove(config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);
            UnregisterSurfaceHost(window);
            CloseFailedCreatedWindow(
                config.Id,
                window,
                preserveVisibility: cancellationToken.CanBeCanceled);
            throw;
        }

        return window;
    }

    private void CloseFailedCreatedWindow(
        string widgetId,
        IDesktopWidgetWindow window,
        bool preserveVisibility)
    {
        if (preserveVisibility)
        {
            _suppressClosedVisibilityPersistence.Add(widgetId);
        }

        try
        {
            window.CloseWindow();
        }
        catch
        {
        }
        finally
        {
            if (preserveVisibility)
            {
                _suppressClosedVisibilityPersistence.Remove(widgetId);
            }
        }
    }

    private static async Task ObserveAndDisposeWidgetInitializationAsync(
        Task initializationTask,
        WidgetViewModel viewModel,
        WidgetConfig config)
    {
        try
        {
            await initializationTask;
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetManager] Retired file widget initialization completed with error " +
                $"id={config.Id}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                viewModel.Dispose();
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Retired file widget cleanup failed " +
                    $"id={config.Id}: {ex}");
            }
        }
    }

    private void QueueDeferredContentInitialization(
        WidgetConfig config,
        ContentWidgetWindow window)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();
            try
            {
                await window.ContentReadyTask;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Failed to initialize content widget " +
                    $"'{config.Name}' ({config.Id}) after show: {ex}");
                if (_contentWidgets.TryGetValue(config.Id, out var currentWindow) &&
                    ReferenceEquals(currentWindow, window))
                {
                    _contentWidgets.Remove(config.Id);
                    _widgetWindowHandles.Remove(window.WindowHandle);
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
            }
        });
    }

    private void QueueDeferredWidgetInitialization(
        WidgetConfig config,
        WidgetWindow window,
        WidgetViewModel viewModel)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to initialize widget '{config.Name}' ({config.Id}) after show: {ex}");
                if (_widgets.TryGetValue(config.Id, out var entry) &&
                    ReferenceEquals(entry.Window, window))
                {
                    _widgets.Remove(config.Id);
                    viewModel.Dispose();
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
            }
        });
    }

    private void NormalizeWidgetBounds(WidgetConfig config)
    {
        int width = (int)Math.Round(Math.Max(SettingsService.MinWidgetWidth, config.Width));
        int height = (int)Math.Round(Math.Max(SettingsService.MinWidgetHeight, config.Height));
        int x = (int)Math.Round(config.X);
        int y = (int)Math.Round(config.Y);
        double previousX = config.X;
        double previousY = config.Y;
        double previousWidth = config.Width;
        double previousHeight = config.Height;
        string? previousAnchor = config.PositionAnchor;
        double previousMarginX = config.PositionMarginX;
        double previousMarginY = config.PositionMarginY;
        string? previousMonitorKey = config.PositionMonitorKey;
        string? previousMonitorDeviceName = config.PositionMonitorDeviceName;
        bool? previousMonitorWasPrimary = config.PositionMonitorWasPrimary;
        int previousBoundsCoordinateVersion = config.BoundsCoordinateVersion;

        var area = DisplayArea.GetFromRect(
            new Windows.Graphics.RectInt32(x, y, width, height),
            DisplayAreaFallback.Nearest);
        var workArea = area.WorkArea;
        WidgetPositioningService.EnsureCurrentBoundsCoordinateVersionForCurrentTopology(config, workArea);

        var safeBounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(config);
        var selectedWorkArea = DisplayArea.GetFromRect(safeBounds, DisplayAreaFallback.Nearest).WorkArea;
        bool shouldCaptureAnchor = string.IsNullOrWhiteSpace(config.PositionAnchor) ||
                                   string.IsNullOrWhiteSpace(config.PositionMonitorKey) ||
                                   string.IsNullOrWhiteSpace(config.PositionMonitorDeviceName) ||
                                   !config.PositionMonitorWasPrimary.HasValue ||
                                   config.PositionMonitorWasPrimary == true ||
                                   string.Equals(
                                       config.PositionMonitorKey,
                                       WidgetPositioningService.CreateMonitorKey(selectedWorkArea),
                                       StringComparison.Ordinal);
        if (shouldCaptureAnchor)
        {
            WidgetPositioningService.CaptureAnchor(config, safeBounds, selectedWorkArea);
        }

        WidgetPositioningService.UpdateConfigFromPhysicalBounds(config, safeBounds, selectedWorkArea);

        bool changed =
            Math.Abs(config.Width - previousWidth) > double.Epsilon ||
            Math.Abs(config.Height - previousHeight) > double.Epsilon ||
            Math.Abs(config.X - previousX) > double.Epsilon ||
            Math.Abs(config.Y - previousY) > double.Epsilon ||
            previousBoundsCoordinateVersion != config.BoundsCoordinateVersion ||
            !string.Equals(config.PositionAnchor, previousAnchor, StringComparison.Ordinal) ||
            Math.Abs(config.PositionMarginX - previousMarginX) > double.Epsilon ||
            Math.Abs(config.PositionMarginY - previousMarginY) > double.Epsilon ||
            !string.Equals(config.PositionMonitorKey, previousMonitorKey, StringComparison.Ordinal) ||
            !string.Equals(config.PositionMonitorDeviceName, previousMonitorDeviceName, StringComparison.OrdinalIgnoreCase) ||
            config.PositionMonitorWasPrimary != previousMonitorWasPrimary;

        if (!changed)
        {
            return;
        }

        _settingsService.UpdateWidget(config, notifySubscribers: false);
    }

}

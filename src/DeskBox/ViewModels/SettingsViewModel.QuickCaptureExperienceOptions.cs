using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string QuickCaptureDefaultFormat
    {
        get => _settingsService.Settings.QuickCaptureDefaultFormat;
        set => SetQuickCaptureString(
            nameof(QuickCaptureDefaultFormat),
            value,
            SettingsService.QuickCaptureFormatMarkdown,
            [SettingsService.QuickCaptureFormatPlainText, SettingsService.QuickCaptureFormatMarkdown],
            normalized => _settingsService.Settings.QuickCaptureDefaultFormat = normalized);
    }

    public string QuickCaptureExistingNoteOpenMode
    {
        get => _settingsService.Settings.QuickCaptureExistingNoteOpenMode;
        set => SetQuickCaptureString(
            nameof(QuickCaptureExistingNoteOpenMode),
            value,
            SettingsService.QuickCaptureOpenModeRead,
            [SettingsService.QuickCaptureOpenModeRead, SettingsService.QuickCaptureOpenModeEdit],
            normalized => _settingsService.Settings.QuickCaptureExistingNoteOpenMode = normalized);
    }

    public string QuickCaptureDefaultLayout
    {
        get => _settingsService.Settings.QuickCaptureDefaultLayout;
        set => SetQuickCaptureString(
            nameof(QuickCaptureDefaultLayout),
            value,
            SettingsService.QuickCaptureLayoutAuto,
            [SettingsService.QuickCaptureLayoutAuto, SettingsService.QuickCaptureLayoutSingle, SettingsService.QuickCaptureLayoutDual],
            normalized => _settingsService.Settings.QuickCaptureDefaultLayout = normalized);
    }

    public string QuickCaptureWideEditorView
    {
        get => _settingsService.Settings.QuickCaptureWideEditorView;
        set => SetQuickCaptureString(
            nameof(QuickCaptureWideEditorView),
            value,
            SettingsService.QuickCaptureWideEditorSource,
            [SettingsService.QuickCaptureWideEditorSource, SettingsService.QuickCaptureWideEditorSplit],
            normalized => _settingsService.Settings.QuickCaptureWideEditorView = normalized);
    }

    public string QuickCaptureListDensity
    {
        get => _settingsService.Settings.QuickCaptureListDensity;
        set => SetQuickCaptureString(
            nameof(QuickCaptureListDensity),
            value,
            SettingsService.QuickCaptureListDensityStandard,
            [SettingsService.QuickCaptureListDensityCompact, SettingsService.QuickCaptureListDensityStandard, SettingsService.QuickCaptureListDensityComfortable],
            normalized => _settingsService.Settings.QuickCaptureListDensity = normalized);
    }

    public string QuickCaptureTimeDisplay
    {
        get => _settingsService.Settings.QuickCaptureTimeDisplay;
        set => SetQuickCaptureString(
            nameof(QuickCaptureTimeDisplay),
            value,
            SettingsService.QuickCaptureTimeDisplayUpdated,
            [SettingsService.QuickCaptureTimeDisplayUpdated, SettingsService.QuickCaptureTimeDisplayCreated, SettingsService.QuickCaptureTimeDisplayHidden],
            normalized => _settingsService.Settings.QuickCaptureTimeDisplay = normalized);
    }

    public bool QuickCaptureAllowRemoteImages
    {
        get => _settingsService.Settings.QuickCaptureAllowRemoteImages;
        set => SetQuickCaptureValue(
            nameof(QuickCaptureAllowRemoteImages),
            _settingsService.Settings.QuickCaptureAllowRemoteImages,
            value,
            normalized => _settingsService.Settings.QuickCaptureAllowRemoteImages = normalized);
    }

    public bool QuickCaptureTrashEnabled
    {
        get => _settingsService.Settings.QuickCaptureTrashEnabled;
        set => SetQuickCaptureValue(
            nameof(QuickCaptureTrashEnabled),
            _settingsService.Settings.QuickCaptureTrashEnabled,
            value,
            normalized => _settingsService.Settings.QuickCaptureTrashEnabled = normalized);
    }

    public int QuickCaptureTrashRetentionDays
    {
        get => _settingsService.Settings.QuickCaptureTrashRetentionDays;
        set => SetQuickCaptureInt(
            nameof(QuickCaptureTrashRetentionDays),
            value,
            1,
            3650,
            normalized => _settingsService.Settings.QuickCaptureTrashRetentionDays = normalized);
    }

    public bool QuickCaptureRevisionHistoryEnabled
    {
        get => _settingsService.Settings.QuickCaptureRevisionHistoryEnabled;
        set => SetQuickCaptureValue(
            nameof(QuickCaptureRevisionHistoryEnabled),
            _settingsService.Settings.QuickCaptureRevisionHistoryEnabled,
            value,
            normalized => _settingsService.Settings.QuickCaptureRevisionHistoryEnabled = normalized);
    }

    public int QuickCaptureRevisionRetentionDays
    {
        get => _settingsService.Settings.QuickCaptureRevisionRetentionDays;
        set => SetQuickCaptureInt(
            nameof(QuickCaptureRevisionRetentionDays),
            value,
            1,
            3650,
            normalized => _settingsService.Settings.QuickCaptureRevisionRetentionDays = normalized);
    }

    public int QuickCaptureRevisionLimitPerNote
    {
        get => _settingsService.Settings.QuickCaptureRevisionLimitPerNote;
        set => SetQuickCaptureInt(
            nameof(QuickCaptureRevisionLimitPerNote),
            value,
            1,
            500,
            normalized => _settingsService.Settings.QuickCaptureRevisionLimitPerNote = normalized);
    }

    public int QuickCaptureClipboardRetentionDays
    {
        get => _settingsService.Settings.QuickCaptureClipboardRetentionDays;
        set => SetQuickCaptureInt(
            nameof(QuickCaptureClipboardRetentionDays),
            value,
            1,
            3650,
            normalized => _settingsService.Settings.QuickCaptureClipboardRetentionDays = normalized);
    }

    public string QuickCaptureClipboardExcludedAppsText
    {
        get => string.Join(Environment.NewLine, _settingsService.Settings.QuickCaptureClipboardExcludedApps ?? []);
        set
        {
            List<string> normalized = (value ?? string.Empty)
                .Split([',', '，', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if ((_settingsService.Settings.QuickCaptureClipboardExcludedApps ?? [])
                .SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _settingsService.Settings.QuickCaptureClipboardExcludedApps = normalized;
            _settingsService.SaveDebounced();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureFormatOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureFormatMarkdown, SettingsService.QuickCaptureFormatPlainText],
            ["Settings.QuickCapture.Format.Markdown", "Settings.QuickCapture.Format.PlainText"]);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureOpenModeOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureOpenModeRead, SettingsService.QuickCaptureOpenModeEdit],
            ["Settings.QuickCapture.OpenMode.Read", "Settings.QuickCapture.OpenMode.Edit"]);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureLayoutOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureLayoutAuto, SettingsService.QuickCaptureLayoutSingle, SettingsService.QuickCaptureLayoutDual],
            ["Settings.QuickCapture.Layout.Auto", "Settings.QuickCapture.Layout.Single", "Settings.QuickCapture.Layout.Dual"]);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureWideEditorOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureWideEditorSource, SettingsService.QuickCaptureWideEditorSplit],
            ["Settings.QuickCapture.WideEditor.Source", "Settings.QuickCapture.WideEditor.Split"]);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureDensityOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureListDensityCompact, SettingsService.QuickCaptureListDensityStandard, SettingsService.QuickCaptureListDensityComfortable],
            ["Settings.QuickCapture.Density.Compact", "Settings.QuickCapture.Density.Standard", "Settings.QuickCapture.Density.Comfortable"]);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureTimeDisplayOptions =>
        QuickCaptureOptions(
            [SettingsService.QuickCaptureTimeDisplayUpdated, SettingsService.QuickCaptureTimeDisplayCreated, SettingsService.QuickCaptureTimeDisplayHidden],
            ["Settings.QuickCapture.Time.Updated", "Settings.QuickCapture.Time.Created", "Settings.QuickCapture.Time.Hidden"]);

    private IReadOnlyList<SettingsOption> QuickCaptureOptions(
        IReadOnlyList<string> values,
        IReadOnlyList<string> localizationKeys) =>
        CreateSelectionOptions(
            values,
            localizationKeys.Select(_localizationService.T).ToArray());

    private void SetQuickCaptureString(
        string propertyName,
        string? value,
        string fallback,
        IReadOnlyList<string> allowed,
        Action<string> apply)
    {
        string normalized = allowed.FirstOrDefault(candidate =>
            string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
        string current = propertyName switch
        {
            nameof(QuickCaptureDefaultFormat) => QuickCaptureDefaultFormat,
            nameof(QuickCaptureExistingNoteOpenMode) => QuickCaptureExistingNoteOpenMode,
            nameof(QuickCaptureDefaultLayout) => QuickCaptureDefaultLayout,
            nameof(QuickCaptureWideEditorView) => QuickCaptureWideEditorView,
            nameof(QuickCaptureListDensity) => QuickCaptureListDensity,
            nameof(QuickCaptureTimeDisplay) => QuickCaptureTimeDisplay,
            _ => string.Empty
        };
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return;
        }

        apply(normalized);
        _settingsService.SaveDebounced();
        OnPropertyChanged(propertyName);
    }

    private void SetQuickCaptureInt(
        string propertyName,
        int value,
        int minimum,
        int maximum,
        Action<int> apply)
    {
        int normalized = Math.Clamp(value, minimum, maximum);
        int current = propertyName switch
        {
            nameof(QuickCaptureTrashRetentionDays) => QuickCaptureTrashRetentionDays,
            nameof(QuickCaptureRevisionRetentionDays) => QuickCaptureRevisionRetentionDays,
            nameof(QuickCaptureRevisionLimitPerNote) => QuickCaptureRevisionLimitPerNote,
            nameof(QuickCaptureClipboardRetentionDays) => QuickCaptureClipboardRetentionDays,
            _ => int.MinValue
        };
        if (current == normalized)
        {
            return;
        }

        apply(normalized);
        _settingsService.SaveDebounced();
        OnPropertyChanged(propertyName);
    }

    private void SetQuickCaptureValue<T>(
        string propertyName,
        T current,
        T value,
        Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        apply(value);
        _settingsService.SaveDebounced();
        OnPropertyChanged(propertyName);
    }
}

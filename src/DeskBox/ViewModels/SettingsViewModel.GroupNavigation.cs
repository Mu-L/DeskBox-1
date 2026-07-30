using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private bool _isChangingWidgetGroupingAvailability;

    public bool IsWidgetGroupsEnabled
    {
        get => _settingsService.Settings.WidgetGroupsEnabled;
        set
        {
            if (_settingsService.Settings.WidgetGroupsEnabled == value)
            {
                return;
            }

            if (value)
            {
                _settingsService.Settings.WidgetGroupsEnabled = true;
                _settingsService.SaveDebounced();
                OnPropertyChanged();
                OnPropertyChanged(nameof(WidgetGroupOptionsVisibility));
                App.Current?.WidgetManager?
                    .NotifyWidgetGroupingAvailabilityChanged();
                return;
            }

            if (!_isChangingWidgetGroupingAvailability)
            {
                _ = DisableWidgetGroupingAsync();
            }
        }
    }

    public Visibility WidgetGroupOptionsVisibility =>
        IsWidgetGroupsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async Task DisableWidgetGroupingAsync()
    {
        _isChangingWidgetGroupingAvailability = true;
        bool dissolved = false;
        try
        {
            WidgetManager? manager = App.Current?.WidgetManager;
            dissolved = manager is null
                ? _settingsService.Settings.WidgetGroups.Count == 0
                : await manager.DissolveAllWidgetGroupsAsync();
            if (!dissolved)
            {
                return;
            }

            _settingsService.Settings.WidgetGroupsEnabled = false;
            await _settingsService.SaveAsync();
            manager?.NotifyWidgetGroupingAvailabilityChanged();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Failed to disable widget grouping: {ex}");
        }
        finally
        {
            _isChangingWidgetGroupingAvailability = false;
            OnPropertyChanged(nameof(IsWidgetGroupsEnabled));
            OnPropertyChanged(nameof(WidgetGroupOptionsVisibility));
            NotifyExistingWidgetGroupPropertiesChanged();
        }
    }

    public string SelectedWidgetGroupDefaultNavigationStyle
    {
        get => WidgetGroupNavigationStyles.Normalize(
            _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
            allowFollowDefault: false);
        set
        {
            string normalized = WidgetGroupNavigationStyles.Normalize(
                value,
                allowFollowDefault: false);
            if (string.Equals(
                    _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Settings.WidgetGroupDefaultNavigationStyle = normalized;
            _settingsService.SaveDebounced();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetGroupNavigationStyleOptions =>
    [
        new(WidgetGroupNavigationStyles.Auto, T("Settings.WidgetGroupNavigation.Auto")),
        new(WidgetGroupNavigationStyles.Tabs, T("Settings.WidgetGroupNavigation.Tabs")),
        new(WidgetGroupNavigationStyles.Stack, T("Settings.WidgetGroupNavigation.Stack"))
    ];


    public string SelectedWidgetGroupDefaultTitleDisplayMode
    {
        get => WidgetGroupTitleDisplayModes.Normalize(
            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
            allowFollowDefault: false);
        set
        {
            string normalized = WidgetGroupTitleDisplayModes.Normalize(
                value,
                allowFollowDefault: false);
            if (string.Equals(
                    _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode = normalized;
            _settingsService.SaveDebounced();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetGroupTitleDisplayModeOptions =>
    [
        new(WidgetGroupTitleDisplayModes.IconAndText, T("Settings.WidgetGroupTitle.IconAndText")),
        new(WidgetGroupTitleDisplayModes.IconOnly, T("Settings.WidgetGroupTitle.IconOnly")),
        new(WidgetGroupTitleDisplayModes.TextOnly, T("Settings.WidgetGroupTitle.TextOnly"))
    ];

    public bool IsWidgetGroupWheelSwitchEnabled
    {
        get => _settingsService.Settings.WidgetGroupWheelSwitchEnabled;
        set
        {
            if (_settingsService.Settings.WidgetGroupWheelSwitchEnabled == value)
            {
                return;
            }

            _settingsService.Settings.WidgetGroupWheelSwitchEnabled = value;
            _settingsService.SaveDebounced();
            OnPropertyChanged();
        }
    }

    public bool IsWidgetGroupHoverSwitchEnabled
    {
        get => _settingsService.Settings.WidgetGroupHoverSwitchEnabled;
        set
        {
            if (_settingsService.Settings.WidgetGroupHoverSwitchEnabled == value)
            {
                return;
            }

            _settingsService.Settings.WidgetGroupHoverSwitchEnabled = value;
            _settingsService.SaveDebounced();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<WidgetGroupSettingsItem> ExistingWidgetGroupItems =>
        _settingsService.Settings.WidgetGroups
            .Where(group => group.MemberIds.Count >= 2)
            .Select(CreateWidgetGroupSettingsItem)
            .ToArray();

    public Visibility ExistingWidgetGroupsVisibility =>
        ExistingWidgetGroupItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ExistingWidgetGroupsEmptyVisibility =>
        ExistingWidgetGroupItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void RefreshWidgetGroupSettings()
    {
        OnPropertyChanged(nameof(IsWidgetGroupsEnabled));
        OnPropertyChanged(nameof(WidgetGroupOptionsVisibility));
        OnPropertyChanged(nameof(SelectedWidgetGroupDefaultNavigationStyle));
        OnPropertyChanged(nameof(SelectedWidgetGroupDefaultTitleDisplayMode));
        OnPropertyChanged(nameof(IsWidgetGroupWheelSwitchEnabled));
        OnPropertyChanged(nameof(IsWidgetGroupHoverSwitchEnabled));
        NotifyExistingWidgetGroupPropertiesChanged();
    }

    public bool ResetWidgetGroupOverrides(string groupId)
    {
        WidgetGroupConfig? group = _settingsService.Settings.WidgetGroups
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, groupId, StringComparison.Ordinal));
        if (group is null)
        {
            return false;
        }

        bool changed =
            !string.Equals(
                group.NavigationStyle,
                WidgetGroupNavigationStyles.FollowDefault,
                StringComparison.Ordinal) ||
            !string.Equals(
                group.TitleDisplayMode,
                WidgetGroupTitleDisplayModes.FollowDefault,
                StringComparison.Ordinal) ||
            group.WheelSwitchEnabled is not null ||
            group.HoverSwitchEnabled is not null;
        if (!changed)
        {
            return false;
        }

        group.NavigationStyle = WidgetGroupNavigationStyles.FollowDefault;
        group.TitleDisplayMode = WidgetGroupTitleDisplayModes.FollowDefault;
        group.WheelSwitchEnabled = null;
        group.HoverSwitchEnabled = null;
        _settingsService.SaveDebounced();
        NotifyExistingWidgetGroupPropertiesChanged();
        return true;
    }

    private WidgetGroupSettingsItem CreateWidgetGroupSettingsItem(
        WidgetGroupConfig group)
    {
        var overrideDetails = new List<string>(4);
        string navigationStyle = WidgetGroupNavigationStyles.Normalize(
            group.NavigationStyle,
            allowFollowDefault: true);
        if (navigationStyle != WidgetGroupNavigationStyles.FollowDefault)
        {
            overrideDetails.Add(_localizationService.Format(
                "Settings.WidgetGroups.Existing.Navigation",
                GetWidgetGroupNavigationDisplayName(navigationStyle)));
        }

        string titleMode = WidgetGroupTitleDisplayModes.Normalize(
            group.TitleDisplayMode,
            allowFollowDefault: true);
        if (titleMode != WidgetGroupTitleDisplayModes.FollowDefault)
        {
            overrideDetails.Add(_localizationService.Format(
                "Settings.WidgetGroups.Existing.TitleStyle",
                GetWidgetGroupTitleDisplayName(titleMode)));
        }

        if (group.WheelSwitchEnabled is { } wheelEnabled)
        {
            overrideDetails.Add(_localizationService.Format(
                "Settings.WidgetGroups.Existing.Wheel",
                T(wheelEnabled ? "Common.On" : "Common.Off")));
        }

        if (group.HoverSwitchEnabled is { } hoverEnabled)
        {
            overrideDetails.Add(_localizationService.Format(
                "Settings.WidgetGroups.Existing.Hover",
                T(hoverEnabled ? "Common.On" : "Common.Off")));
        }

        WidgetConfig? activeMember = _settingsService.Settings.Widgets
            .FirstOrDefault(widget =>
                string.Equals(
                    widget.Id,
                    group.ActiveMemberId,
                    StringComparison.Ordinal));
        string displayName = !string.IsNullOrWhiteSpace(group.Name)
            ? group.Name.Trim()
            : !string.IsNullOrWhiteSpace(activeMember?.Name)
                ? activeMember.Name.Trim()
                : T("Settings.WidgetGroups.Existing.Unnamed");
        string memberCount = _localizationService.Format(
            "Settings.WidgetGroups.Existing.MemberCount",
            group.MemberIds.Count);
        string overrides = overrideDetails.Count == 0
            ? T("Settings.WidgetGroups.Existing.FollowsDefault")
            : string.Join(" · ", overrideDetails);

        return new WidgetGroupSettingsItem(
            group.Id,
            displayName,
            $"{memberCount} · {overrides}",
            overrideDetails.Count > 0);
    }

    private string GetWidgetGroupNavigationDisplayName(string style) =>
        WidgetGroupNavigationStyles.Normalize(
            style,
            allowFollowDefault: false) switch
        {
            WidgetGroupNavigationStyles.Tabs =>
                T("Settings.WidgetGroupNavigation.Tabs"),
            WidgetGroupNavigationStyles.Stack =>
                T("Settings.WidgetGroupNavigation.Stack"),
            _ => T("Settings.WidgetGroupNavigation.Auto")
        };

    private string GetWidgetGroupTitleDisplayName(string style) =>
        WidgetGroupTitleDisplayModes.Normalize(
            style,
            allowFollowDefault: false) switch
        {
            WidgetGroupTitleDisplayModes.IconOnly =>
                T("Settings.WidgetGroupTitle.IconOnly"),
            WidgetGroupTitleDisplayModes.TextOnly =>
                T("Settings.WidgetGroupTitle.TextOnly"),
            _ => T("Settings.WidgetGroupTitle.IconAndText")
        };

    private void NotifyExistingWidgetGroupPropertiesChanged()
    {
        OnPropertyChanged(nameof(ExistingWidgetGroupItems));
        OnPropertyChanged(nameof(ExistingWidgetGroupsVisibility));
        OnPropertyChanged(nameof(ExistingWidgetGroupsEmptyVisibility));
    }

    private string T(string key) => _localizationService.T(key);
}

public sealed record WidgetGroupSettingsItem(
    string GroupId,
    string DisplayName,
    string Summary,
    bool HasOverrides);

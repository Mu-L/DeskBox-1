using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class TodoPresentationSettingsStore
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SettingsService? _settingsService;

    public TodoPresentationSettingsStore(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public TodoWidgetPresentationSettings Load(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata.TryGetValue(TodoWidgetPresentationSettings.MetadataKey, out string? json) &&
            !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                TodoWidgetPresentationSettings? settings =
                    JsonSerializer.Deserialize<TodoWidgetPresentationSettings>(json, s_options);
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
            catch (JsonException ex)
            {
                App.Log($"[TodoPresentation] Invalid metadata for widget '{config.Id}': {ex.Message}");
            }
        }

        TodoSettings? global = _settingsService?.Settings.Todo;
        return Normalize(new TodoWidgetPresentationSettings
        {
            SmartView = global?.QuickRecord.DefaultSmartView ?? TodoSmartView.Today,
            DisplayMode = global?.Calendar.DefaultDisplayMode ?? TodoDisplayMode.List,
            CompletedVisibility = global?.CompletionAndData.CompletedVisibility ?? TodoCompletedVisibility.Collapsed,
            CalendarSlotMinutes = global?.Calendar.CalendarSlotMinutes ?? 30,
            DefaultDurationMinutes = global?.Calendar.DefaultDurationMinutes ?? TodoWorkspaceDefaults.DefaultDurationMinutes,
            WorkdayStartHour = global?.Calendar.WorkdayStartHour ?? 8,
            WorkdayEndHour = global?.Calendar.WorkdayEndHour ?? 20,
            ShowWeekNumbers = global?.Calendar.ShowWeekNumbers ?? false,
            ShowUnscheduledPool = global?.Calendar.ShowUnscheduledPool ?? true,
            LiveMarkdownPreview = global?.NotesAndAttachments.LiveMarkdownPreview ?? true
        });
    }

    public async Task SaveAsync(
        WidgetConfig config,
        TodoWidgetPresentationSettings presentation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(presentation);
        config.Metadata[TodoWidgetPresentationSettings.MetadataKey] =
            JsonSerializer.Serialize(Normalize(presentation), s_options);
        if (_settingsService is not null)
        {
            // Presentation changes are local to this widget and callers update
            // their own surface immediately. Broadcasting a global settings
            // change here causes every Todo surface to reload and can turn a
            // simple calendar selection into a full workspace refresh.
            await _settingsService.SaveAsync(notifySubscribers: false);
        }
    }

    public void SaveDebounced(
        WidgetConfig config,
        TodoWidgetPresentationSettings presentation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(presentation);
        config.Metadata[TodoWidgetPresentationSettings.MetadataKey] =
            JsonSerializer.Serialize(Normalize(presentation), s_options);
        _settingsService?.SaveDebounced(notifySubscribers: false);
    }

    public static TodoWidgetPresentationSettings Normalize(TodoWidgetPresentationSettings value)
    {
        value.ListSplitRatio = Math.Clamp(
            double.IsFinite(value.ListSplitRatio) ? value.ListSplitRatio : 0.40,
            0.25,
            0.75);
        double calendarSplitRatio = double.IsFinite(value.CalendarSplitRatio)
            ? value.CalendarSplitRatio
            : 0.58;
        // 0.64 was the original hard-coded default. Migrate that untouched
        // value once so existing widgets receive the refined month balance;
        // genuinely customized ratios remain intact.
        if (Math.Abs(calendarSplitRatio - 0.64) < 0.0001)
        {
            calendarSplitRatio = 0.58;
        }
        value.CalendarSplitRatio = Math.Clamp(
            calendarSplitRatio,
            0.35,
            0.80);
        value.DensityScale = Math.Clamp(
            double.IsFinite(value.DensityScale) ? value.DensityScale : 1.0,
            0.75,
            1.35);
        value.CalendarSlotMinutes = value.CalendarSlotMinutes <= 15 ? 15 : 30;
        value.DefaultDurationMinutes = Math.Clamp(value.DefaultDurationMinutes, 15, 480);
        value.WorkdayStartHour = Math.Clamp(value.WorkdayStartHour, 0, 22);
        value.WorkdayEndHour = Math.Clamp(value.WorkdayEndHour, value.WorkdayStartHour + 1, 24);
        value.ListId = string.IsNullOrWhiteSpace(value.ListId) ? null : value.ListId.Trim();
        if (string.Equals(value.ListId, TodoWorkspaceDefaults.LegacyDefaultListId, StringComparison.Ordinal))
        {
            value.ListId = TodoWorkspaceDefaults.InboxListId;
        }
        value.SectionId = string.IsNullOrWhiteSpace(value.SectionId) ? null : value.SectionId.Trim();
        value.TagId = string.IsNullOrWhiteSpace(value.TagId) ? null : value.TagId.Trim();
        value.SavedViewId = string.IsNullOrWhiteSpace(value.SavedViewId) ? null : value.SavedViewId.Trim();
        value.SelectedDate ??= DateOnly.FromDateTime(DateTime.Today);
        return value;
    }
}

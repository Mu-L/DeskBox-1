using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Owns the singleton Glance preferences. The store is deliberately separate
/// from AppSettings so a future sync layer can classify portable preferences,
/// device-local paths, and disposable media independently.
/// </summary>
public sealed class GlanceWidgetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath;
    private GlanceWidgetData? _cached;

    public static GlanceWidgetStore Shared { get; } = new();

    public GlanceWidgetStore()
        : this(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "glance"))
    {
    }

    internal GlanceWidgetStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "glance.json");
    }

    internal string StorePath => _storePath;

    public event EventHandler? Changed;

    public async Task<GlanceWidgetData> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _cached ??= await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize<GlanceWidgetData>(json, JsonOptions)),
                () => new GlanceWidgetData(),
                nameof(GlanceWidgetStore));
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GlanceWidgetData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _gate.WaitAsync();
        try
        {
            _cached = Normalize(Clone(data));
            await PersistLockedAsync();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task UpdateAsync(Action<GlanceWidgetData> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync();
        try
        {
            _cached ??= await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize<GlanceWidgetData>(json, JsonOptions)),
                () => new GlanceWidgetData(),
                nameof(GlanceWidgetStore));
            update(_cached);
            _cached = Normalize(_cached);
            await PersistLockedAsync();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task ResetAsync()
    {
        await SaveAsync(new GlanceWidgetData());
    }

    private async Task PersistLockedAsync()
    {
        string json = JsonSerializer.Serialize(_cached, JsonOptions);
        await ResilientJsonStore.SaveAsync(_storePath, json);
    }

    private static GlanceWidgetData Normalize(GlanceWidgetData? data)
    {
        data ??= new GlanceWidgetData();
        data.Version = GlanceWidgetData.CurrentVersion;
        data.LocalImagePaths ??= [];
        data.LocalImagePaths = data.LocalImagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        data.LocalFolderPath = string.IsNullOrWhiteSpace(data.LocalFolderPath)
            ? null
            : data.LocalFolderPath.Trim();
        if (!data.ShowDate)
        {
            data.ShowYear = false;
        }
        data.RotationIntervalMinutes = data.RotationIntervalMinutes is 0 or 10 or 30 or 60 or 360 or 1440
            ? data.RotationIntervalMinutes
            : 30;
        data.TimeScale = Math.Clamp(data.TimeScale, 0.75, 1.35);
        data.TimeFontFamily = string.IsNullOrWhiteSpace(data.TimeFontFamily)
            ? null
            : data.TimeFontFamily.Trim();
        data.Layout = Enum.IsDefined(data.Layout) ? data.Layout : GlanceLayoutMode.Centered;
        data.BackgroundSource = Enum.IsDefined(data.BackgroundSource)
            ? data.BackgroundSource
            : GlanceBackgroundSource.Bing;
        data.OnlineImageCategory = Enum.IsDefined(data.OnlineImageCategory)
            ? data.OnlineImageCategory
            : GlanceOnlineImageCategory.Featured;
        data.Transition = Enum.IsDefined(data.Transition) ? data.Transition : GlanceTransitionMode.CrossFade;
        data.TransitionSpeed = Enum.IsDefined(data.TransitionSpeed) ? data.TransitionSpeed : GlanceTransitionSpeed.Standard;
        data.Readability = Enum.IsDefined(data.Readability) ? data.Readability : GlanceReadabilityMode.Soft;
        data.CalendarMaterialMode = Enum.IsDefined(data.CalendarMaterialMode)
            ? data.CalendarMaterialMode
            : GlanceCalendarMaterialMode.FollowSystem;
        data.CalendarImageMaterialTransparency = double.IsFinite(data.CalendarImageMaterialTransparency)
            ? Math.Clamp(data.CalendarImageMaterialTransparency, 0.0, 1.0)
            : 0.32;
        data.TraditionalCalendarMode = Enum.IsDefined(data.TraditionalCalendarMode)
            ? data.TraditionalCalendarMode
            : GlanceTraditionalCalendarMode.None;
        data.ImageFit = Enum.IsDefined(data.ImageFit) ? data.ImageFit : GlanceImageFitMode.Fill;
        data.ImageFocus = Enum.IsDefined(data.ImageFocus) ? data.ImageFocus : GlanceImageFocus.Center;
        return data;
    }

    private static GlanceWidgetData Clone(GlanceWidgetData data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        return JsonSerializer.Deserialize<GlanceWidgetData>(json, JsonOptions) ?? new GlanceWidgetData();
    }

    private void RaiseChanged()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.Log($"[GlanceWidgetStore] Observer failed: {ex}");
            }
        }
    }
}

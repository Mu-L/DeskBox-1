using System.Globalization;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views.SettingsSections;

public sealed partial class GlanceWidgetSettingsSection : UserControl
{
    private static readonly string[] DisplayOptions = ["Time", "Date", "Year", "Weekday", "Calendar"];
    private sealed record Option(string Label, object Value);
    private readonly GlanceWidgetStore _store = GlanceWidgetStore.Shared;
    private readonly GlanceImageService _imageService = new();
    private readonly GlanceTraditionalCalendarService _traditionalCalendarService = new();
    private readonly SystemFontCatalogService _fontCatalogService = new();
    private readonly DispatcherTimer _scaleSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _calendarTransparencySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private GlanceWidgetData _settings = new();
    private IntPtr _ownerWindow;
    private bool _isLoading;

    public GlanceWidgetSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _scaleSaveTimer.Tick += ScaleSaveTimer_Tick;
        _calendarTransparencySaveTimer.Tick += CalendarTransparencySaveTimer_Tick;
    }

    private LocalizationService Localization => App.Current.LocalizationService;

    public void SetOwnerWindow(IntPtr ownerWindow) => _ownerWindow = ownerWindow;

    public async Task RefreshFromStoreAsync()
    {
        _settings = await _store.LoadAsync();
        PopulateOptions();
        ApplySettingsToControls();
        UpdateCacheSize();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshFromStoreAsync();
    }

    private void PopulateOptions()
    {
        LayoutComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Layout.Immersive"), GlanceLayoutMode.Immersive),
            new Option(Localization.T("Glance.Layout.Centered"), GlanceLayoutMode.Centered),
            new Option(Localization.T("Glance.Layout.Editorial"), GlanceLayoutMode.Editorial),
            new Option(Localization.T("Glance.Layout.Calendar"), GlanceLayoutMode.Calendar)
        };
        BackgroundSourceComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Background.Files"), GlanceBackgroundSource.LocalFiles),
            new Option(Localization.T("Glance.Background.Folder"), GlanceBackgroundSource.LocalFolder),
            new Option(Localization.T("Glance.Background.Bing"), GlanceBackgroundSource.Bing),
            new Option(Localization.T("Glance.Background.Online"), GlanceBackgroundSource.Online)
        };
        OnlineImageCategoryComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Background.Category.Featured"), GlanceOnlineImageCategory.Featured),
            new Option(Localization.T("Glance.Background.Category.Landscapes"), GlanceOnlineImageCategory.Landscapes),
            new Option(Localization.T("Glance.Background.Category.Cities"), GlanceOnlineImageCategory.Cities),
            new Option(Localization.T("Glance.Background.Category.Architecture"), GlanceOnlineImageCategory.Architecture),
            new Option(Localization.T("Glance.Background.Category.Animals"), GlanceOnlineImageCategory.Animals),
            new Option(Localization.T("Glance.Background.Category.Plants"), GlanceOnlineImageCategory.Plants),
            new Option(Localization.T("Glance.Background.Category.Astronomy"), GlanceOnlineImageCategory.Astronomy),
            new Option(Localization.T("Glance.Background.Category.People"), GlanceOnlineImageCategory.People)
        };
        RotationComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Rotation.Manual"), 0),
            new Option(Localization.T("Glance.Rotation.10Minutes"), 10),
            new Option(Localization.T("Glance.Rotation.30Minutes"), 30),
            new Option(Localization.T("Glance.Rotation.1Hour"), 60),
            new Option(Localization.T("Glance.Rotation.6Hours"), 360),
            new Option(Localization.T("Glance.Rotation.Daily"), 1440)
        };
        TransitionComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Transition.None"), GlanceTransitionMode.None),
            new Option(Localization.T("Glance.Transition.CrossFade"), GlanceTransitionMode.CrossFade),
            new Option(Localization.T("Glance.Transition.SlideFade"), GlanceTransitionMode.SlideFade),
            new Option(Localization.T("Glance.Transition.ZoomFade"), GlanceTransitionMode.ZoomFade)
        };
        SpeedComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Speed.Fast"), GlanceTransitionSpeed.Fast),
            new Option(Localization.T("Glance.Speed.Standard"), GlanceTransitionSpeed.Standard),
            new Option(Localization.T("Glance.Speed.Relaxed"), GlanceTransitionSpeed.Relaxed)
        };
        ReadabilityComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.Readability.None"), GlanceReadabilityMode.None),
            new Option(Localization.T("Glance.Readability.Soft"), GlanceReadabilityMode.Soft),
            new Option(Localization.T("Glance.Readability.Strong"), GlanceReadabilityMode.Strong)
        };
        CalendarMaterialComboBox.ItemsSource = new[]
        {
            new Option(Localization.T("Glance.CalendarMaterial.FollowSystem"), GlanceCalendarMaterialMode.FollowSystem),
            new Option(Localization.T("Glance.CalendarMaterial.FollowImage"), GlanceCalendarMaterialMode.FollowImage)
        };
        GlanceTraditionalCalendarMode resolvedTraditionalCalendar =
            _traditionalCalendarService.ResolveMode(
                GlanceTraditionalCalendarMode.Auto,
                Localization.CurrentCultureName);
        TraditionalCalendarComboBox.ItemsSource = new[]
        {
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.None), GlanceTraditionalCalendarMode.None),
            new Option(
                string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.T("Glance.TraditionalCalendar.Auto"),
                    GetTraditionalCalendarLabel(resolvedTraditionalCalendar)),
                GlanceTraditionalCalendarMode.Auto),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.ChineseLunar), GlanceTraditionalCalendarMode.ChineseLunar),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.UmAlQura), GlanceTraditionalCalendarMode.UmAlQura),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Hijri), GlanceTraditionalCalendarMode.Hijri),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.IndianSaka), GlanceTraditionalCalendarMode.IndianSaka),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.JapaneseEra), GlanceTraditionalCalendarMode.JapaneseEra),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Bangla), GlanceTraditionalCalendarMode.Bangla),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Julian), GlanceTraditionalCalendarMode.Julian),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Hebrew), GlanceTraditionalCalendarMode.Hebrew),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Persian), GlanceTraditionalCalendarMode.Persian),
            new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.ThaiBuddhist), GlanceTraditionalCalendarMode.ThaiBuddhist)
        };

        var fonts = new List<Option>
        {
            new(Localization.T("Glance.Typography.SystemDefault"), string.Empty)
        };
        fonts.AddRange(_fontCatalogService.GetFontFamilies().Select(name => new Option(name, name)));
        FontComboBox.ItemsSource = fonts;
    }

    private void ApplySettingsToControls()
    {
        _isLoading = true;
        try
        {
            SelectOption(LayoutComboBox, _settings.Layout);
            SelectOption(BackgroundSourceComboBox, _settings.BackgroundSource);
            SelectOption(OnlineImageCategoryComboBox, _settings.OnlineImageCategory);
            SelectOption(RotationComboBox, _settings.RotationIntervalMinutes);
            SelectOption(TransitionComboBox, _settings.Transition);
            SelectOption(SpeedComboBox, _settings.TransitionSpeed);
            SelectOption(ReadabilityComboBox, _settings.Readability);
            SelectOption(CalendarMaterialComboBox, _settings.CalendarMaterialMode);
            SelectOption(TraditionalCalendarComboBox, _settings.TraditionalCalendarMode);
            SelectOption(FontComboBox, _settings.TimeFontFamily ?? string.Empty);
            RandomOrderToggle.IsOn = _settings.RandomOrder;
            TimeScaleSlider.Value = _settings.TimeScale;
            CalendarImageTransparencySlider.Value = _settings.CalendarImageMaterialTransparency;
            ShowPhotoControlsToggle.IsOn = _settings.ShowPhotoControls;
            UpdateDisplaySelectionSummary();
            UpdateLocalSourceState();
            UpdateCalendarMaterialState();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static void SelectOption(ComboBox comboBox, object value)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<Option>().FirstOrDefault(option => Equals(option.Value, value));
    }

    private async Task SaveAsync(Action<GlanceWidgetData> update)
    {
        if (_isLoading)
        {
            return;
        }

        update(_settings);
        await _store.SaveAsync(_settings);
    }

    private void DisplayContentDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            DisplayOptions,
            GetDisplayOptionLabel,
            IsDisplayOptionSelected,
            _ => true,
            ToggleDisplayOption);
    }

    private async void ToggleDisplayOption(string option)
    {
        GlanceDisplayElement? element = option switch
        {
            "Time" => GlanceDisplayElement.Time,
            "Date" => GlanceDisplayElement.Date,
            "Year" => GlanceDisplayElement.Year,
            "Weekday" => GlanceDisplayElement.Weekday,
            "Calendar" => GlanceDisplayElement.Calendar,
            _ => null
        };
        if (element is null)
        {
            return;
        }

        bool isVisible = GlanceWidgetSettingsPolicy.IsDisplayElementVisible(
            _settings,
            element.Value);
        GlanceWidgetSettingsPolicy.SetDisplayElement(
            _settings,
            element.Value,
            !isVisible);

        SynchronizeLayoutSelection();
        UpdateDisplaySelectionSummary();
        UpdateCalendarMaterialState();
        await _store.SaveAsync(_settings);
    }

    private async void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutComboBox.SelectedItem is Option { Value: GlanceLayoutMode layout })
        {
            await SaveAsync(settings =>
                GlanceWidgetSettingsPolicy.SetLayout(settings, layout));
            UpdateDisplaySelectionSummary();
            UpdateCalendarMaterialState();
        }
    }

    private void SynchronizeLayoutSelection()
    {
        bool wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            SelectOption(LayoutComboBox, _settings.Layout);
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    private async void CalendarMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalendarMaterialComboBox.SelectedItem is Option { Value: GlanceCalendarMaterialMode mode })
        {
            await SaveAsync(settings => settings.CalendarMaterialMode = mode);
            UpdateCalendarMaterialState();
        }
    }

    private async void TraditionalCalendarComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TraditionalCalendarComboBox.SelectedItem is Option { Value: GlanceTraditionalCalendarMode mode })
        {
            await SaveAsync(settings => settings.TraditionalCalendarMode = mode);
        }
    }

    private async void BackgroundSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackgroundSourceComboBox.SelectedItem is Option { Value: GlanceBackgroundSource source })
        {
            await SaveAsync(settings => settings.BackgroundSource = source);
            UpdateLocalSourceState();
        }
    }

    private async void OnlineImageCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnlineImageCategoryComboBox.SelectedItem is Option { Value: GlanceOnlineImageCategory category })
        {
            await SaveAsync(settings => settings.OnlineImageCategory = category);
        }
    }

    private async void RotationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RotationComboBox.SelectedItem is Option { Value: int minutes })
        {
            await SaveAsync(settings => settings.RotationIntervalMinutes = minutes);
        }
    }

    private async void TransitionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionComboBox.SelectedItem is Option { Value: GlanceTransitionMode transition })
        {
            await SaveAsync(settings => settings.Transition = transition);
        }
    }

    private async void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is Option { Value: GlanceTransitionSpeed speed })
        {
            await SaveAsync(settings => settings.TransitionSpeed = speed);
        }
    }

    private async void ReadabilityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReadabilityComboBox.SelectedItem is Option { Value: GlanceReadabilityMode readability })
        {
            await SaveAsync(settings => settings.Readability = readability);
        }
    }

    private void CalendarImageTransparencySlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.CalendarImageMaterialTransparency = Math.Clamp(e.NewValue, 0.0, 1.0);
        UpdateCalendarMaterialState();
        _calendarTransparencySaveTimer.Stop();
        _calendarTransparencySaveTimer.Start();
    }

    private async void CalendarTransparencySaveTimer_Tick(object? sender, object e)
    {
        _calendarTransparencySaveTimer.Stop();
        await _store.SaveAsync(_settings);
    }

    private async void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontComboBox.SelectedItem is Option { Value: string font })
        {
            await SaveAsync(settings => settings.TimeFontFamily = string.IsNullOrWhiteSpace(font) ? null : font);
        }
    }

    private async void RandomOrderToggle_Toggled(object sender, RoutedEventArgs e)
        => await SaveAsync(settings => settings.RandomOrder = RandomOrderToggle.IsOn);

    private async void ShowPhotoControlsToggle_Toggled(object sender, RoutedEventArgs e)
        => await SaveAsync(settings => settings.ShowPhotoControls = ShowPhotoControlsToggle.IsOn);

    private void TimeScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.TimeScale = e.NewValue;
        _scaleSaveTimer.Stop();
        _scaleSaveTimer.Start();
    }

    private async void ScaleSaveTimer_Tick(object? sender, object e)
    {
        _scaleSaveTimer.Stop();
        await _store.SaveAsync(_settings);
    }

    private async void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, _ownerWindow);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        _settings.BackgroundSource = GlanceBackgroundSource.LocalFiles;
        _settings.LocalImagePaths = files.Select(file => file.Path).ToList();
        await _store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? folder = FolderPickerService.PickFolder(_ownerWindow);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _settings.BackgroundSource = GlanceBackgroundSource.LocalFolder;
        _settings.LocalFolderPath = folder;
        await _store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ClearLocalSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.BackgroundSource == GlanceBackgroundSource.LocalFolder)
        {
            _settings.LocalFolderPath = null;
        }
        else
        {
            _settings.LocalImagePaths.Clear();
        }

        await _store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await _imageService.ClearCacheAsync();
        UpdateCacheSize();
    }

    private void UpdateLocalSourceState()
    {
        OnlineImageCategoryCard.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.Online
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalSourceCard.Visibility = _settings.BackgroundSource is
            GlanceBackgroundSource.LocalFiles or GlanceBackgroundSource.LocalFolder
                ? Visibility.Visible
                : Visibility.Collapsed;
        ChooseFilesButton.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.LocalFiles
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChooseFolderButton.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.LocalFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearLocalSourceButton.IsEnabled = _settings.BackgroundSource switch
        {
            GlanceBackgroundSource.LocalFiles => _settings.LocalImagePaths.Count > 0,
            GlanceBackgroundSource.LocalFolder => !string.IsNullOrWhiteSpace(_settings.LocalFolderPath),
            _ => false
        };
        LocalSourceSummary.Text = _settings.BackgroundSource switch
        {
            GlanceBackgroundSource.LocalFiles when _settings.LocalImagePaths.Count > 0 => string.Format(
                CultureInfo.CurrentCulture,
                Localization.T("Glance.Background.LocalSummary"),
                _settings.LocalImagePaths.Count),
            GlanceBackgroundSource.LocalFiles => Localization.T("Glance.Status.NoLocalImages"),
            GlanceBackgroundSource.LocalFolder => _settings.LocalFolderPath ?? Localization.T("Glance.Status.NoLocalImages"),
            _ => string.Empty
        };
    }

    private void UpdateCalendarMaterialState()
    {
        bool calendarVisible = _settings.Layout == GlanceLayoutMode.Calendar && _settings.ShowCalendar;
        TraditionalCalendarCard.Visibility = calendarVisible ? Visibility.Visible : Visibility.Collapsed;
        CalendarMaterialCard.Visibility = calendarVisible ? Visibility.Visible : Visibility.Collapsed;
        CalendarImageTransparencyCard.Visibility = calendarVisible &&
            _settings.CalendarMaterialMode == GlanceCalendarMaterialMode.FollowImage
                ? Visibility.Visible
                : Visibility.Collapsed;
        CalendarImageTransparencyValue.Text = $"{Math.Round(_settings.CalendarImageMaterialTransparency * 100):0}%";
    }

    private void UpdateDisplaySelectionSummary()
    {
        var selected = new List<string>(5);
        if (_settings.ShowTime) selected.Add(Localization.T("Glance.Display.Time"));
        if (_settings.ShowDate) selected.Add(Localization.T("Glance.Display.Date"));
        if (_settings.ShowYear) selected.Add(Localization.T("Glance.Display.Year"));
        if (_settings.ShowWeekday) selected.Add(Localization.T("Glance.Display.Weekday"));
        if (_settings.ShowCalendar) selected.Add(Localization.T("Glance.Display.Calendar"));
        DisplayContentDropDownButton.Content = selected.Count == 0
            ? Localization.T("Glance.Display.PhotoOnly")
            : string.Join(Localization.T("Glance.Display.Separator"), selected);
    }

    private string GetDisplayOptionLabel(string option) => option switch
    {
        "Time" => Localization.T("Glance.Display.Time"),
        "Date" => Localization.T("Glance.Display.Date"),
        "Year" => Localization.T("Glance.Display.Year"),
        "Weekday" => Localization.T("Glance.Display.Weekday"),
        "Calendar" => Localization.T("Glance.Display.Calendar"),
        _ => option
    };

    private bool IsDisplayOptionSelected(string option) => option switch
    {
        "Time" => _settings.ShowTime,
        "Date" => _settings.ShowDate,
        "Year" => _settings.ShowYear,
        "Weekday" => _settings.ShowWeekday,
        "Calendar" => _settings.ShowCalendar,
        _ => false
    };

    private void UpdateCacheSize()
    {
        long bytes = _imageService.GetCacheSizeBytes();
        CacheSizeText.Text = bytes < 1024 * 1024
            ? $"{bytes / 1024d:0.#} KB"
            : $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private string GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode mode) =>
        Localization.T(mode switch
        {
            GlanceTraditionalCalendarMode.None => "Glance.TraditionalCalendar.None",
            GlanceTraditionalCalendarMode.ChineseLunar => "Glance.TraditionalCalendar.ChineseLunar",
            GlanceTraditionalCalendarMode.UmAlQura => "Glance.TraditionalCalendar.UmAlQura",
            GlanceTraditionalCalendarMode.Hijri => "Glance.TraditionalCalendar.Hijri",
            GlanceTraditionalCalendarMode.IndianSaka => "Glance.TraditionalCalendar.IndianSaka",
            GlanceTraditionalCalendarMode.JapaneseEra => "Glance.TraditionalCalendar.JapaneseEra",
            GlanceTraditionalCalendarMode.Bangla => "Glance.TraditionalCalendar.Bangla",
            GlanceTraditionalCalendarMode.Julian => "Glance.TraditionalCalendar.Julian",
            GlanceTraditionalCalendarMode.Hebrew => "Glance.TraditionalCalendar.Hebrew",
            GlanceTraditionalCalendarMode.Persian => "Glance.TraditionalCalendar.Persian",
            GlanceTraditionalCalendarMode.ThaiBuddhist => "Glance.TraditionalCalendar.ThaiBuddhist",
            _ => "Glance.TraditionalCalendar.None"
        });
}

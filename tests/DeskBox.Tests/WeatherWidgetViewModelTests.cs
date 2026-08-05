using DeskBox.Helpers;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WeatherWidgetViewModelTests
{
    [Theory]
    [InlineData(0, true, "\u2600\uFE0F")]
    [InlineData(0, false, "\U0001F319")]
    [InlineData(45, true, "\u2601\uFE0F")]
    [InlineData(48, true, "\u2601\uFE0F")]
    public void WeatherEmoji_UsesUnboxedIcons(
        int weatherCode,
        bool isDay,
        string expectedEmoji)
    {
        Assert.Equal(expectedEmoji, WeatherCodeMapper.GetEmoji(weatherCode, isDay));
    }

    [Theory]
    [InlineData(150, 104)]
    [InlineData(168, 116)]
    public void DetermineLayoutMode_KeepsSmallWidgetsInMini(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Mini", layout);
    }

    [Theory]
    [InlineData(180, 134)]
    [InlineData(200, 154)]
    public void DetermineLayoutMode_UsesCompactLayoutFromMediumWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Mini");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearMediumBoundary()
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(174, 122, "Compact");

        Assert.Equal("Compact", layout);
    }

    [Fact]
    public void ResponsiveTransition_ExpandingPreselectsFinalLayoutAndIgnoresIntermediateSizes()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(150, 104);
        viewModel.BeginResponsiveLayoutTransition(320, 260, isCollapsing: false);
        viewModel.UpdateAvailableSize(190, 140);
        viewModel.UpdateAvailableSize(255, 180);

        Assert.Equal("Expanded", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(320, 260);
        Assert.Equal("Expanded", viewModel.LayoutMode);
    }

    [Fact]
    public void ResponsiveTransition_CollapsingKeepsExpandedLayoutUntilContentIsHidden()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 260);
        viewModel.BeginResponsiveLayoutTransition(150, 104, isCollapsing: true);
        viewModel.UpdateAvailableSize(255, 180);
        viewModel.UpdateAvailableSize(190, 140);

        Assert.Equal("Expanded", viewModel.LayoutMode);

        viewModel.CompleteResponsiveLayoutTransition(150, 104);
        Assert.Equal("Mini", viewModel.LayoutMode);
    }

    [Theory]
    [InlineData(250, 169)]
    [InlineData(255, 180)]
    [InlineData(320, 260)]
    public void DetermineLayoutMode_UsesExpandedLayoutFromLargeWidgetSize(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Compact");

        Assert.Equal("Expanded", layout);
    }

    [Fact]
    public void DetermineLayoutMode_UsesHysteresisNearExpandedBoundary()
    {
        // Between the downgrade (230x154) and upgrade (250x169) thresholds an
        // already-Expanded widget stays Expanded, avoiding layout flicker.
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(240, 160, "Expanded");

        Assert.Equal("Expanded", layout);
    }

    [Theory]
    [InlineData(225, 150)]
    [InlineData(230, 154)]
    public void DetermineLayoutMode_DowngradesExpandedToCompactWhenShrunk(double width, double contentHeight)
    {
        string layout = WeatherWidgetViewModel.DetermineLayoutMode(width, contentHeight, "Expanded");

        Assert.Equal("Compact", layout);
    }

    [Theory]
    [InlineData(300, 290, 92)]
    [InlineData(300, 230, 80)]
    [InlineData(300, 180, 64)]
    public void ExpandedHourlyCardHeight_AdaptsToAvailableHeight(double width, double height, double expectedCardHeight)
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(width, height);

        Assert.Equal("Expanded", viewModel.LayoutMode);
        Assert.Equal(expectedCardHeight, viewModel.ExpandedHourlyCardHeight);
    }

    [Fact]
    public void ExpandedSunriseVisibility_ShowsOnlyWhenTallEnough()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(320, 310);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ExpandedSunriseVisibility);

        viewModel.UpdateAvailableSize(300, 290);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.ExpandedSunriseVisibility);
    }

    [Fact]
    public void ExpandedHourlyPrecipVisibility_ShowsOnlyWhenTallEnough()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();

        viewModel.UpdateAvailableSize(300, 230);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ExpandedHourlyPrecipVisibility);

        viewModel.UpdateAvailableSize(300, 180);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.ExpandedHourlyPrecipVisibility);
    }

    [Fact]
    public void UpdateAvailableSize_DoesNotNotifyWhenResponsiveValuesAreUnchanged()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();
        viewModel.UpdateAvailableSize(300, 290);

        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateAvailableSize(301, 291);

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void UpdateAvailableSize_NotifiesOnlyHeightDerivedValuesThatActuallyChange()
    {
        WeatherWidgetViewModel viewModel = CreateViewModel();
        viewModel.UpdateAvailableSize(300, 290);

        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateAvailableSize(300, 270);

        Assert.DoesNotContain(nameof(WeatherWidgetViewModel.ExpandedSunriseVisibility), changedProperties);
        Assert.DoesNotContain(nameof(WeatherWidgetViewModel.ExpandedHourlyPrecipVisibility), changedProperties);
        Assert.Contains(nameof(WeatherWidgetViewModel.ExpandedHourlyCardHeight), changedProperties);
    }

    [Fact]
    public void ViewMode_UsesPersistedWidgetOverride()
    {
        var config = CreateConfig();
        config.Metadata[DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey] =
            DeskBox.Services.WeatherWidgetViewModeSettings.WeekValue;

        using var viewModel = CreateViewModel(config);

        Assert.True(viewModel.IsWeekView);
    }

    [Fact]
    public void ViewModeChange_IsStoredInWidgetMetadata()
    {
        var config = CreateConfig();
        using (var viewModel = CreateViewModel(config))
        {
            viewModel.SetViewMode(useWeekView: true);
        }

        using var restored = CreateViewModel(config);
        Assert.True(restored.IsWeekView);
        Assert.Equal(
            DeskBox.Services.WeatherWidgetViewModeSettings.WeekValue,
            config.Metadata[
                DeskBox.Services.WeatherWidgetViewModeSettings.MetadataKey]);
    }

    [Fact]
    public async Task ViewModeChange_SurvivesSettingsReload()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new DeskBox.Services.SettingsService(root);
            var config = CreateConfig();
            settings.UpdateWidget(config, notifySubscribers: false);
            using (var viewModel = new WeatherWidgetViewModel(
                       config,
                       new DeskBox.Services.WeatherService(),
                       TestServices.CreateLocalizationService(),
                       settings))
            {
                viewModel.SetViewMode(useWeekView: true);
            }

            Assert.True(await settings.FlushPendingSaveAsync());

            var reloaded = new DeskBox.Services.SettingsService(root);
            await reloaded.LoadAsync();
            DeskBox.Models.WidgetConfig restoredConfig = Assert.Single(
                reloaded.Settings.Widgets,
                widget => widget.Id == config.Id);
            using var restored = new WeatherWidgetViewModel(
                restoredConfig,
                new DeskBox.Services.WeatherService(),
                TestServices.CreateLocalizationService(),
                reloaded);

            Assert.True(restored.IsWeekView);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WeatherWidgetViewModel CreateViewModel()
    {
        return CreateViewModel(CreateConfig());
    }

    private static WeatherWidgetViewModel CreateViewModel(
        DeskBox.Models.WidgetConfig config)
    {
        return new WeatherWidgetViewModel(
            config,
            new DeskBox.Services.WeatherService(),
            TestServices.CreateLocalizationService());
    }

    private static DeskBox.Models.WidgetConfig CreateConfig()
    {
        return new DeskBox.Models.WidgetConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Weather",
            WidgetKind = DeskBox.Models.WidgetKind.Weather
        };
    }
}

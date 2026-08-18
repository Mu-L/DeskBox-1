using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlanceCalendarAndImageServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CalendarMonth_AlwaysBuildsSixCompleteWeeks()
    {
        var source = new LocalCalendarPresentationSource();

        GlanceCalendarMonth month = await source.GetMonthAsync(
            new DateOnly(2026, 8, 1),
            CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Equal(new DateOnly(2026, 8, 1), month.Month);
        Assert.Equal(7, month.WeekdayHeaders.Count);
        Assert.Equal(["一", "二", "三", "四", "五", "六", "日"], month.WeekdayHeaders);
        Assert.Equal(42, month.Days.Count);
        Assert.Contains(month.Days, day => day.Date == new DateOnly(2026, 8, 1) && day.IsCurrentMonth);
        Assert.All(month.Days, day => Assert.False(string.IsNullOrWhiteSpace(day.DayText)));
        Assert.Empty(await source.GetAgendaAsync(
            new DateOnly(2026, 8, 1),
            7,
            CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void CalendarSurface_UsesAdaptiveGlassLayoutAndSystemAccent()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string settingsCodeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        Assert.Contains("x:Name=\"CalendarGlassSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AccentFillColorDefaultBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{Binding CalendarPanelHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding CalendarPanelMaxWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding CalendarPanelWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinItemHeight=\"{Binding CalendarDayHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinItemWidth=\"{Binding CalendarDayWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"{Binding CalendarCornerRadius}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GlanceCalendarAcrylicBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarMaterialSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarSystemBackdropSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<controls:SystemBackdropElement", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TintOpacity=\"0.06\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TintLuminosityOpacity=\"0.24\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.88\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateAcrylic", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new MicaBackdrop", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildEmbeddedMicaTintOverlayColor", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildContentSolidSurfaceColor", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialOpacity", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialIntensity", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialType", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialMode", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarImageMaterialTransparency", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialComboBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("CalendarImageTransparencySlider", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("TraditionalCalendarComboBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisplayContentDropDownButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DisplayContentDropDown_Click\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LocalSourceCard\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Background.Files\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("svc:Localized.HeaderKey=\"Glance.Background.LocalSummary\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsMultiSelectMenu.Show", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("when _settings.LocalImagePaths.Count > 0", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Localization.T(\"Glance.Status.NoLocalImages\")", settingsCodeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("<toolkit:SettingsExpander", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.LayoutGroup.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.AppearanceGroup.Title", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewCalendar", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SectionTitleTextStyle", settingsXaml, StringComparison.Ordinal);
        int traditionalNoneOption = settingsCodeBehind.IndexOf(
            "new Option(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.None)",
            StringComparison.Ordinal);
        int traditionalAutoOption = settingsCodeBehind.IndexOf(
            "Localization.T(\"Glance.TraditionalCalendar.Auto\")",
            StringComparison.Ordinal);
        Assert.True(traditionalNoneOption >= 0 && traditionalNoneOption < traditionalAutoOption);
        Assert.DoesNotContain("SliderSettingValueTextStyle", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.CalendarMaterial.FollowImage", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildImagePaletteGradient", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateAcrylic", backdrop, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateMica", backdrop, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarReadabilityLayer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#34516F", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#726B85", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#C18A72", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowNonCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowExpandedCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ImageForegroundThemeScope\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyImageAwareTheme", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Width=\"30\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TraditionalText", xaml, StringComparison.Ordinal);
        Assert.Contains("TraditionalCalendarTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("LineHeight=\"14\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LineHeight=\"10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"4\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemControlAcrylicElementBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.68\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#38FFFFFF\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness=\"1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"0.8*\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactBoundsCalculator.ResolveOuterCornerRadius", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarPanelMaximumWidth = 360", viewModel, StringComparison.Ordinal);
        Assert.Contains("(CalendarPanelWidth - CalendarPanelContentInset) / 7", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsCompactCalendarPresentation", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsExpandedCalendarPresentation", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void TraditionalCalendar_AutomaticModeUsesDeskBoxLanguage()
    {
        var service = new GlanceTraditionalCalendarService();

        Assert.Equal(GlanceTraditionalCalendarMode.ChineseLunar,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "zh-CN"));
        Assert.Equal(GlanceTraditionalCalendarMode.UmAlQura,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ar-SA"));
        Assert.Equal(GlanceTraditionalCalendarMode.IndianSaka,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "hi-IN"));
        Assert.Equal(GlanceTraditionalCalendarMode.JapaneseEra,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ja-JP"));
        Assert.Equal(GlanceTraditionalCalendarMode.Bangla,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "bn-BD"));
        Assert.Equal(GlanceTraditionalCalendarMode.Julian,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ru-RU"));
        Assert.Equal(GlanceTraditionalCalendarMode.None,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "en-US"));
        Assert.Equal(GlanceTraditionalCalendarMode.Persian,
            service.ResolveMode(GlanceTraditionalCalendarMode.Persian, "en-US"));
    }

    [Fact]
    public void TraditionalCalendar_FormatsChineseIndianAndBanglaNewYear()
    {
        var service = new GlanceTraditionalCalendarService();
        CultureInfo chinese = CultureInfo.GetCultureInfo("zh-CN");

        Assert.Equal("正月", service.FormatDay(
            new DateOnly(2024, 2, 10),
            GlanceTraditionalCalendarMode.ChineseLunar,
            chinese));
        Assert.Contains("甲辰年", service.FormatTitle(
            new DateOnly(2024, 2, 10),
            GlanceTraditionalCalendarMode.ChineseLunar,
            chinese), StringComparison.Ordinal);
        Assert.Equal("१/१", service.FormatDay(
            new DateOnly(2024, 3, 21),
            GlanceTraditionalCalendarMode.IndianSaka,
            CultureInfo.GetCultureInfo("hi-IN")));
        Assert.Contains("१९४६", service.FormatTitle(
            new DateOnly(2024, 3, 21),
            GlanceTraditionalCalendarMode.IndianSaka,
            CultureInfo.GetCultureInfo("hi-IN")), StringComparison.Ordinal);
        Assert.Equal("১/১", service.FormatDay(
            new DateOnly(2026, 4, 14),
            GlanceTraditionalCalendarMode.Bangla,
            CultureInfo.GetCultureInfo("bn-BD")));
        Assert.Contains("১৪৩৩", service.FormatTitle(
            new DateOnly(2026, 4, 14),
            GlanceTraditionalCalendarMode.Bangla,
            CultureInfo.GetCultureInfo("bn-BD")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraditionalCalendar_AppliesSecondaryLabelsAndCanBeDisabled()
    {
        var source = new LocalCalendarPresentationSource();
        var service = new GlanceTraditionalCalendarService();
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        DateOnly today = new(2026, 8, 18);
        GlanceCalendarMonth month = await source.GetMonthAsync(today, culture);

        GlanceCalendarMonth decorated = service.Apply(
            month,
            GlanceTraditionalCalendarMode.ChineseLunar,
            culture,
            today);
        Assert.False(string.IsNullOrWhiteSpace(decorated.TraditionalTitle));
        Assert.All(decorated.Days, day => Assert.False(string.IsNullOrWhiteSpace(day.TraditionalText)));

        GlanceCalendarMonth disabled = service.Apply(
            decorated,
            GlanceTraditionalCalendarMode.None,
            culture,
            today);
        Assert.Equal(string.Empty, disabled.TraditionalTitle);
        Assert.All(disabled.Days, day => Assert.Equal(string.Empty, day.TraditionalText));
    }

    [Theory]
    [InlineData(GlanceTraditionalCalendarMode.UmAlQura)]
    [InlineData(GlanceTraditionalCalendarMode.Hijri)]
    [InlineData(GlanceTraditionalCalendarMode.JapaneseEra)]
    [InlineData(GlanceTraditionalCalendarMode.Julian)]
    [InlineData(GlanceTraditionalCalendarMode.Hebrew)]
    [InlineData(GlanceTraditionalCalendarMode.Persian)]
    [InlineData(GlanceTraditionalCalendarMode.ThaiBuddhist)]
    public void TraditionalCalendar_SystemCalendarsProduceAHeader(GlanceTraditionalCalendarMode mode)
    {
        var service = new GlanceTraditionalCalendarService();
        string title = service.FormatTitle(
            new DateOnly(2026, 8, 18),
            mode,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Fact]
    public void CalendarAcrylic_UsesTheSameOpacityCurveAsWidgetBackdrops()
    {
        WidgetMaterialOpacityProfile clearest = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: false,
            useBase: false,
            surfaceOpacity: 0,
            materialIntensity: 0);
        WidgetMaterialOpacityProfile strongest = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: false,
            useBase: false,
            surfaceOpacity: 1,
            materialIntensity: 1);
        WidgetMaterialOpacityProfile baseAcrylic = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: true,
            useBase: true,
            surfaceOpacity: 1,
            materialIntensity: 1);

        Assert.Equal(0.0016, clearest.TintOpacity, precision: 4);
        Assert.Equal(0.0176, clearest.LuminosityOpacity, precision: 4);
        Assert.Equal(0.34, strongest.TintOpacity, precision: 4);
        Assert.Equal(0.64, strongest.LuminosityOpacity, precision: 4);
        Assert.Equal(0.72, baseAcrylic.TintOpacity, precision: 4);
        Assert.Equal(0.82, baseAcrylic.LuminosityOpacity, precision: 4);
    }

    [Fact]
    public void EmbeddedMicaTint_RespondsToMicaVariantAndIntensity()
    {
        Windows.UI.Color accent = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        Windows.UI.Color subtle = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
            isDark: false,
            accentColor: accent,
            useAlt: false,
            materialIntensity: 0);
        Windows.UI.Color strongAlt = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
            isDark: false,
            accentColor: accent,
            useAlt: true,
            materialIntensity: 1);

        Windows.UI.Color hostTint = WidgetMaterialVisualCalculator.BuildContentTintColor(
            isDark: false,
            accent);
        Assert.Equal(hostTint.R, subtle.R);
        Assert.Equal(hostTint.G, subtle.G);
        Assert.Equal(hostTint.B, subtle.B);
        Assert.Equal(hostTint.R, strongAlt.R);
        Assert.Equal(hostTint.G, strongAlt.G);
        Assert.Equal(hostTint.B, strongAlt.B);
        Assert.True(strongAlt.A > subtle.A);
        Assert.Equal(
            (byte)Math.Round(WidgetMaterialVisualCalculator.CalculateMica(
                isDark: false,
                useAlt: true,
                materialIntensity: 1).TintOpacity * 255),
            strongAlt.A);

        Windows.UI.Color wallpaperRed = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
            isDark: true,
            accentColor: Windows.UI.Color.FromArgb(0xFF, 0xE2, 0x24, 0x1A),
            useAlt: false,
            materialIntensity: 0.66);
        Windows.UI.Color expectedDarkTint = WidgetMaterialVisualCalculator.BuildContentTintColor(
            isDark: true,
            Windows.UI.Color.FromArgb(0xFF, 0xE2, 0x24, 0x1A));
        Assert.Equal(expectedDarkTint.R, wallpaperRed.R);
        Assert.Equal(expectedDarkTint.G, wallpaperRed.G);
        Assert.Equal(expectedDarkTint.B, wallpaperRed.B);
        Assert.Equal(
            (byte)Math.Round(WidgetMaterialVisualCalculator.CalculateMica(
                isDark: true,
                useAlt: false,
                materialIntensity: 0.66).TintOpacity * 255),
            wallpaperRed.A);
    }

    [Fact]
    public void ImagePalette_SeparatesTwoDominantColorFamilies()
    {
        byte[] pixels = new byte[80 * 4];
        for (int pixel = 0; pixel < 80; pixel++)
        {
            int offset = pixel * 4;
            bool red = pixel < 50;
            pixels[offset] = red ? (byte)24 : (byte)220;
            pixels[offset + 1] = red ? (byte)40 : (byte)70;
            pixels[offset + 2] = red ? (byte)220 : (byte)35;
            pixels[offset + 3] = 255;
        }

        GlanceImagePalette? extracted = GlanceImagePaletteService.ExtractPalette(pixels);
        Assert.True(extracted.HasValue);
        GlanceImagePalette palette = extracted.Value;

        Assert.True(palette.Primary.R > palette.Primary.B);
        Assert.True(palette.Secondary.B > palette.Secondary.R);
    }

    [Fact]
    public void ImagePaletteGradient_FusesPaletteWithLightAndDarkThemeBases()
    {
        var palette = new GlanceImagePalette(
            Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x46, 0x3D),
            Windows.UI.Color.FromArgb(0xFF, 0x26, 0x72, 0xCA));

        WidgetMaterialGradientProfile light =
            WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark: false, palette: palette);
        WidgetMaterialGradientProfile dark =
            WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark: true, palette: palette);

        Assert.True(Luminance(light.StartColor) > Luminance(dark.StartColor));
        Assert.True(Luminance(light.EndColor) > Luminance(dark.EndColor));
        Assert.NotEqual(light.StartColor, light.EndColor);
        Assert.NotEqual(dark.StartColor, dark.EndColor);
    }

    private static double Luminance(Windows.UI.Color color) =>
        (color.R * 0.2126) + (color.G * 0.7152) + (color.B * 0.0722);

    [Fact]
    public async Task LocalFiles_KeepSupportedExistingImagesOnly()
    {
        Directory.CreateDirectory(_tempRoot);
        string first = Path.Combine(_tempRoot, "first.jpg");
        string second = Path.Combine(_tempRoot, "second.png");
        string ignored = Path.Combine(_tempRoot, "notes.txt");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6]);
        await File.WriteAllTextAsync(ignored, "not an image");
        var service = new GlanceImageService(Path.Combine(_tempRoot, "cache"));

        IReadOnlyList<GlanceImageInfo> images = await service.GetAvailableImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.LocalFiles,
            LocalImagePaths = [first, ignored, second, Path.Combine(_tempRoot, "missing.webp")]
        });

        Assert.Equal(2, images.Count);
        Assert.Equal([first, second], images.Select(image => image.LocalPath));
        Assert.All(images, image => Assert.False(image.IsOnline));
    }

    [Fact]
    public async Task LocalFolder_DoesNotScanNestedDirectories()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_tempRoot, "nested")).FullName;
        string top = Path.Combine(_tempRoot, "top.jpg");
        string child = Path.Combine(nested, "child.jpg");
        await File.WriteAllBytesAsync(top, [1]);
        await File.WriteAllBytesAsync(child, [2]);
        var service = new GlanceImageService(Path.Combine(_tempRoot, "cache"));

        IReadOnlyList<GlanceImageInfo> images = await service.GetAvailableImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.LocalFolder,
            LocalFolderPath = _tempRoot
        });

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(top, image.LocalPath);
    }

    [Fact]
    public async Task OnlineRefresh_ClosesTemporaryFileBeforePublishingCacheEntry()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture fixture = new("first.jpg", "https://images.test/first.jpg");
        using HttpClient httpClient = CreateOnlineClient(
            [fixture],
            _ => CreateBytesResponse([1, 2, 3, 4]));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo image = Assert.Single(images);
        Assert.True(File.Exists(image.LocalPath));
        Assert.True(File.Exists(Path.Combine(cacheDirectory, "catalog.json")));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(image.LocalPath!));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories));
        using var exclusiveProbe = new FileStream(
            image.LocalPath!,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task OnlineRefresh_ContinuesWhenOneImageDownloadFails()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture first = new("first.jpg", "https://images.test/first.jpg");
        RemoteImageFixture second = new("second.jpg", "https://images.test/second.jpg");
        using HttpClient httpClient = CreateOnlineClient(
            [first, second],
            request => request.RequestUri == new Uri(first.ImageUrl)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : CreateBytesResponse([9, 8, 7]));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(second.ImageUrl, image.RemoteImageUrl);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(image.LocalPath!));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BingRefresh_UsesChinaEndpointFiltersRestrictedImagesAndKeepsAttribution()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        int archiveRequests = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("HPImageArchive.aspx", StringComparison.Ordinal) == true)
            {
                archiveRequests++;
                return Task.FromResult(CreateJsonResponse(new
                {
                    images = new object[]
                    {
                        new
                        {
                            url = "/th?id=OHR.Allowed_1920x1080.jpg",
                            urlbase = "/th?id=OHR.Allowed",
                            copyright = "A beautiful place (© Example Photographer)",
                            copyrightlink = "https://cn.bing.com/search?q=allowed",
                            title = "A beautiful place",
                            wp = true,
                            hsh = "allowed-image"
                        },
                        new
                        {
                            url = "/th?id=OHR.Restricted_1920x1080.jpg",
                            urlbase = "/th?id=OHR.Restricted",
                            copyright = "Restricted image",
                            copyrightlink = "https://cn.bing.com/search?q=restricted",
                            title = "Restricted image",
                            wp = false,
                            hsh = "restricted-image"
                        }
                    }
                }));
            }

            return Task.FromResult(CreateBytesResponse([4, 5, 6]));
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.Bing
        });

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(BingArchiveBatchCountForTest, archiveRequests);
        Assert.Equal(GlanceOnlineImageProvider.Bing, image.OnlineProvider);
        Assert.Equal("A beautiful place", image.Title);
        Assert.Contains("Example Photographer", image.Author, StringComparison.Ordinal);
        Assert.Equal("cn.bing.com", new Uri(image.RemoteImageUrl!).Host);
        Assert.Equal("https://cn.bing.com/search?q=allowed", image.SourcePageUrl);
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Wikimedia,
            GlanceOnlineImageCategory.Featured));
        Assert.Single(await service.LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Bing,
            GlanceOnlineImageCategory.Featured));
    }

    [Fact]
    public async Task OnlineRefresh_UsesSelectedCategoryAndKeepsItsCacheIsolated()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        var requestedUris = new List<Uri>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requestedUris.Add(request.RequestUri!);
            string query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            if (query.Contains("list=categorymembers", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        categorymembers = new[] { new { title = "File:city.jpg" } }
                    }
                }));
            }

            if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        pages = new[]
                        {
                            new
                            {
                                imageinfo = new[]
                                {
                                    new
                                    {
                                        thumbwidth = 1600,
                                        thumbheight = 900,
                                        mime = "image/jpeg",
                                        descriptionurl = "https://commons.wikimedia.org/wiki/File:city.jpg",
                                        thumburl = "https://images.test/city.jpg"
                                    }
                                }
                            }
                        }
                    }
                }));
            }

            return Task.FromResult(CreateBytesResponse([7, 8, 9]));
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync(
            GlanceOnlineImageCategory.Cities);

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(GlanceOnlineImageCategory.Cities, image.OnlineCategory);
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(GlanceOnlineImageCategory.Featured));
        Assert.Single(await service.LoadCachedOnlineImagesAsync(GlanceOnlineImageCategory.Cities));
        Assert.Contains(
            requestedUris,
            uri => Uri.UnescapeDataString(uri.Query).Contains(
                "Category:Quality images of cityscapes",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlineRefresh_ReturnsExistingCacheWhenRemoteCatalogFails()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture fixture = new("cached.jpg", "https://images.test/cached.jpg");
        using (HttpClient populateClient = CreateOnlineClient(
                   [fixture],
                   _ => CreateBytesResponse([5, 4, 3])))
        {
            var populateService = new GlanceImageService(cacheDirectory, populateClient, () => true);
            Assert.Single(await populateService.RefreshOnlineImagesAsync());
        }

        using var failingClient = new HttpClient(new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var service = new GlanceImageService(cacheDirectory, failingClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo cached = Assert.Single(images);
        Assert.Equal([5, 4, 3], await File.ReadAllBytesAsync(cached.LocalPath!));
    }

    [Fact]
    public async Task OnlineRefresh_PropagatesCancellationWithoutLeavingTemporaryFiles()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshOnlineImagesAsync(cancellation.Token));

        Assert.False(Directory.Exists(cacheDirectory) &&
                     Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories).Any());
    }

    private static HttpClient CreateOnlineClient(
        IReadOnlyList<RemoteImageFixture> fixtures,
        Func<HttpRequestMessage, HttpResponseMessage> imageResponder)
    {
        return new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            string query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("list=categorymembers", StringComparison.Ordinal))
            {
                object[] categorymembers = fixtures
                    .Select(fixture => (object)new { title = $"File:{fixture.FileName}" })
                    .ToArray();
                return Task.FromResult(CreateJsonResponse(new { query = new { categorymembers } }));
            }

            if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                object[] pages = fixtures
                    .Select(fixture => (object)new
                    {
                        imageinfo = new[]
                        {
                            new
                            {
                                thumbwidth = 1600,
                                thumbheight = 900,
                                mime = "image/jpeg",
                                descriptionurl = $"https://commons.wikimedia.org/wiki/File:{fixture.FileName}",
                                thumburl = fixture.ImageUrl
                            }
                        }
                    })
                    .ToArray();
                return Task.FromResult(CreateJsonResponse(new { query = new { pages } }));
            }

            return Task.FromResult(imageResponder(request));
        }));
    }

    private static HttpResponseMessage CreateJsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage CreateBytesResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }

    private sealed record RemoteImageFixture(string FileName, string ImageUrl);

    private const int BingArchiveBatchCountForTest = 3;

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}

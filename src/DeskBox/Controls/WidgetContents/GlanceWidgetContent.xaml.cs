using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class GlanceWidgetContent : UserControl
{
    private readonly GlanceWidgetViewModel _viewModel;
    private readonly GlanceImagePaletteService _paletteService = new();
    private readonly DispatcherTimer _loadingDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly SolidColorBrush _calendarSolidMaterialBrush = new();
    private readonly LinearGradientBrush _calendarImageGradientBrush = new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1)
    };
    private readonly GradientStop _calendarImageGradientStart = new() { Offset = 0 };
    private readonly GradientStop _calendarImageGradientEnd = new() { Offset = 1 };
    private Storyboard? _transitionStoryboard;
    private bool _isAActive;
    private bool _isLoaded;
    private int _imageLoadVersion;
    private string? _calendarSystemBackdropMaterial;
    private string? _calendarImagePalettePath;
    private GlanceImagePalette? _calendarImagePalette;
    private CancellationTokenSource? _paletteCts;

    public GlanceWidgetContent(GlanceWidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _calendarImageGradientBrush.GradientStops.Add(_calendarImageGradientStart);
        _calendarImageGradientBrush.GradientStops.Add(_calendarImageGradientEnd);
        DataContext = viewModel;
        _loadingDelayTimer.Tick += LoadingDelayTimer_Tick;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _viewModel.UpdateAvailableSize(ActualWidth, ActualHeight);
        ApplyBackgroundBrushOptions();
        ApplyImageAwareTheme();
        ApplyCalendarMaterial();
        QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
        BeginLoadImage(_viewModel.CurrentImagePath);
        UpdateLoadingIndicator();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        CancelPaletteUpdate();
        _loadingDelayTimer.Stop();
        DelayedLoadingRing.IsActive = false;
        DelayedLoadingRing.Visibility = Visibility.Collapsed;
        _transitionStoryboard?.Stop();
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.UpdateAvailableSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (e.PropertyName == nameof(GlanceWidgetViewModel.CurrentImagePath))
        {
            BeginLoadImage(_viewModel.CurrentImagePath);
            QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
            ApplyImageAwareTheme();
        }
        else if (e.PropertyName is nameof(GlanceWidgetViewModel.ImageFit) or nameof(GlanceWidgetViewModel.ImageFocus))
        {
            ApplyBackgroundBrushOptions();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.IsLoading))
        {
            UpdateLoadingIndicator();
        }
        else if (e.PropertyName is
            nameof(GlanceWidgetViewModel.CalendarMaterialType) or
            nameof(GlanceWidgetViewModel.CalendarMaterialOpacity) or
            nameof(GlanceWidgetViewModel.CalendarMaterialIntensity) or
            nameof(GlanceWidgetViewModel.CalendarMaterialMode) or
            nameof(GlanceWidgetViewModel.CalendarImageMaterialTransparency))
        {
            ApplyCalendarMaterial();
            if (e.PropertyName == nameof(GlanceWidgetViewModel.CalendarMaterialMode))
            {
                QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
            }
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_isLoaded)
        {
            ApplyImageAwareTheme();
            ApplyCalendarMaterial();
        }
    }

    private void ApplyImageAwareTheme()
    {
        ImageForegroundThemeScope.RequestedTheme = _viewModel.HasCurrentImage
            ? ElementTheme.Dark
            : ElementTheme.Default;
        CalendarGlassSurface.RequestedTheme = RootGrid.ActualTheme;
    }

    private void ApplyCalendarMaterial()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _viewModel.CalendarMaterialType;

        if (_viewModel.CalendarMaterialMode == GlanceCalendarMaterialMode.FollowImage)
        {
            DisableCalendarSystemBackdrop();
            var fallbackTint = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            GlanceImagePalette palette = _calendarImagePalette ?? new GlanceImagePalette(
                accentColor,
                fallbackTint);
            WidgetMaterialGradientProfile gradient =
                WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark, palette);
            _calendarImageGradientStart.Color = gradient.StartColor;
            _calendarImageGradientEnd.Color = gradient.EndColor;
            CalendarMaterialSurface.Background = _calendarImageGradientBrush;
            CalendarMaterialSurface.Opacity = 1.0 -
                _viewModel.CalendarImageMaterialTransparency;
            return;
        }

        CalendarMaterialSurface.Opacity = 1;

        if (SettingsService.IsMicaMaterial(materialType))
        {
            bool useAlt = materialType == SettingsService.WidgetMaterialTypeMicaAlt;
            if (!string.Equals(
                    _calendarSystemBackdropMaterial,
                    materialType,
                    StringComparison.Ordinal))
            {
                CalendarSystemBackdropSurface.SystemBackdrop = new MicaBackdrop
                {
                    Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base
                };
                _calendarSystemBackdropMaterial = materialType;
            }

            CalendarSystemBackdropSurface.Visibility = Visibility.Visible;
            Windows.UI.Color overlayColor =
                WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
                    isDark,
                    accentColor,
                    useAlt,
                    _viewModel.CalendarMaterialIntensity);
            _calendarSolidMaterialBrush.Color = overlayColor;
            CalendarMaterialSurface.Background = _calendarSolidMaterialBrush;
            return;
        }

        DisableCalendarSystemBackdrop();

        if (SettingsService.IsAcrylicMaterial(materialType) &&
            Resources["GlanceCalendarAcrylicBrush"] is AcrylicBrush acrylicBrush)
        {
            WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
                isDark,
                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                _viewModel.CalendarMaterialOpacity,
                _viewModel.CalendarMaterialIntensity);
            var tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            acrylicBrush.TintColor = tintColor;
            acrylicBrush.FallbackColor = Windows.UI.Color.FromArgb(
                0xFF,
                tintColor.R,
                tintColor.G,
                tintColor.B);
            acrylicBrush.TintOpacity = profile.TintOpacity;
            acrylicBrush.TintLuminosityOpacity = profile.LuminosityOpacity;
            CalendarMaterialSurface.Background = acrylicBrush;
            return;
        }

        Windows.UI.Color surfaceColor = materialType switch
        {
            SettingsService.WidgetMaterialTypeSolid =>
                WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                    isDark,
                    accentColor,
                    _viewModel.CalendarMaterialOpacity),
            _ => WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accentColor,
                _viewModel.CalendarMaterialOpacity)
        };
        _calendarSolidMaterialBrush.Color = surfaceColor;
        CalendarMaterialSurface.Background = _calendarSolidMaterialBrush;
    }

    private void DisableCalendarSystemBackdrop()
    {
        if (_calendarSystemBackdropMaterial is not null)
        {
            CalendarSystemBackdropSurface.SystemBackdrop = null;
            _calendarSystemBackdropMaterial = null;
        }

        CalendarSystemBackdropSurface.Visibility = Visibility.Collapsed;
    }

    private void QueueCalendarImagePaletteUpdate(string? path)
    {
        if (!_isLoaded || _viewModel.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage)
        {
            return;
        }

        if (_calendarImagePalette is not null &&
            string.Equals(_calendarImagePalettePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelPaletteUpdate();
        if (string.IsNullOrWhiteSpace(path))
        {
            _calendarImagePalettePath = null;
            _calendarImagePalette = null;
            ApplyCalendarMaterial();
            return;
        }

        var paletteCts = new CancellationTokenSource();
        _paletteCts = paletteCts;
        _ = UpdateCalendarImagePaletteAsync(path, paletteCts);
    }

    private async Task UpdateCalendarImagePaletteAsync(
        string path,
        CancellationTokenSource paletteCts)
    {
        try
        {
            GlanceImagePalette? palette = await _paletteService.GetPaletteAsync(
                path,
                paletteCts.Token);
            if (paletteCts.IsCancellationRequested ||
                !_isLoaded ||
                _viewModel.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage ||
                !string.Equals(_viewModel.CurrentImagePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _calendarImagePalettePath = path;
            _calendarImagePalette = palette;
            ApplyCalendarMaterial();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_paletteCts, paletteCts))
            {
                _paletteCts = null;
            }

            paletteCts.Dispose();
        }
    }

    private void CancelPaletteUpdate()
    {
        CancellationTokenSource? paletteCts = _paletteCts;
        _paletteCts = null;
        if (paletteCts is null)
        {
            return;
        }

        try
        {
            paletteCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void BeginLoadImage(string? path)
    {
        int version = ++_imageLoadVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearBackgroundImage();
            return;
        }

        Border incoming = _isAActive ? BackgroundB : BackgroundA;
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = Math.Clamp(
                (int)Math.Ceiling(Math.Max(ActualWidth, 360) * (XamlRoot?.RasterizationScale ?? 1)),
                480,
                1920)
        };
        bitmap.ImageOpened += (_, _) =>
        {
            if (version == _imageLoadVersion && _isLoaded)
            {
                RunTransition(incoming);
            }
        };
        bitmap.ImageFailed += (_, args) =>
            App.Log($"[GlanceWidgetContent] Image decode failed for '{path}': {args.ErrorMessage}");

        ImageBrush brush = CreateImageBrush(bitmap);
        incoming.Background = brush;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
    }

    private void ClearBackgroundImage()
    {
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;

        foreach (Border background in new[] { BackgroundA, BackgroundB })
        {
            background.Background = null;
            background.Opacity = 0;
            ResetTransform(background);
        }

        _isAActive = false;
    }

    private void RunTransition(Border incoming)
    {
        Border outgoing = ReferenceEquals(incoming, BackgroundA) ? BackgroundB : BackgroundA;
        _transitionStoryboard?.Stop();
        ResetTransform(incoming);
        ResetTransform(outgoing);

        bool animate = WindowsCompatibilityService.ShouldAnimate &&
            _viewModel.Transition != GlanceTransitionMode.None &&
            outgoing.Background is not null;
        if (!animate)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.Background = null;
            _isAActive = ReferenceEquals(incoming, BackgroundA);
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(_viewModel.TransitionSpeed switch
        {
            GlanceTransitionSpeed.Fast => 170,
            GlanceTransitionSpeed.Relaxed => 520,
            _ => 300
        });
        incoming.Opacity = 0;
        outgoing.Opacity = 1;

        var storyboard = new Storyboard();
        AddAnimation(storyboard, incoming, "Opacity", 0, 1, duration);
        AddAnimation(storyboard, outgoing, "Opacity", 1, 0, duration);

        if (_viewModel.Transition == GlanceTransitionMode.SlideFade && incoming.RenderTransform is CompositeTransform slide)
        {
            slide.TranslateY = 16;
            AddAnimation(storyboard, slide, "TranslateY", 16, 0, duration);
        }
        else if (_viewModel.Transition == GlanceTransitionMode.ZoomFade && incoming.RenderTransform is CompositeTransform zoom)
        {
            zoom.ScaleX = 1.035;
            zoom.ScaleY = 1.035;
            AddAnimation(storyboard, zoom, "ScaleX", 1.035, 1, duration);
            AddAnimation(storyboard, zoom, "ScaleY", 1.035, 1, duration);
        }

        storyboard.Completed += (_, _) =>
        {
            outgoing.Background = null;
            outgoing.Opacity = 0;
            ResetTransform(incoming);
            _isAActive = ReferenceEquals(incoming, BackgroundA);
        };
        _transitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void ApplyBackgroundBrushOptions()
    {
        foreach (Border background in new[] { BackgroundA, BackgroundB })
        {
            if (background.Background is ImageBrush brush)
            {
                ApplyBackgroundBrushOptions(brush);
            }
        }
    }

    private ImageBrush CreateImageBrush(ImageSource source)
    {
        var brush = new ImageBrush { ImageSource = source };
        ApplyBackgroundBrushOptions(brush);
        return brush;
    }

    private void ApplyBackgroundBrushOptions(ImageBrush brush)
    {
        brush.Stretch = _viewModel.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        brush.AlignmentX = _viewModel.ImageFocus switch
        {
            GlanceImageFocus.Left => AlignmentX.Left,
            GlanceImageFocus.Right => AlignmentX.Right,
            _ => AlignmentX.Center
        };
        brush.AlignmentY = _viewModel.ImageFocus switch
        {
            GlanceImageFocus.Top => AlignmentY.Top,
            GlanceImageFocus.Bottom => AlignmentY.Bottom,
            _ => AlignmentY.Center
        };
    }

    private static void ResetTransform(Border border)
    {
        if (border.RenderTransform is CompositeTransform transform)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }

    private void UpdateLoadingIndicator()
    {
        _loadingDelayTimer.Stop();
        if (!_viewModel.IsLoading)
        {
            DelayedLoadingRing.IsActive = false;
            DelayedLoadingRing.Visibility = Visibility.Collapsed;
            return;
        }

        _loadingDelayTimer.Start();
    }

    private void LoadingDelayTimer_Tick(object? sender, object e)
    {
        _loadingDelayTimer.Stop();
        if (_viewModel.IsLoading && _isLoaded)
        {
            DelayedLoadingRing.Visibility = Visibility.Visible;
            DelayedLoadingRing.IsActive = true;
        }
    }

    private void Root_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActionLayer.Opacity = 1;
    }

    private void Root_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActionLayer.Opacity = 0;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _viewModel.TogglePause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _viewModel.NextImage();
    private async void PhotoInfoButton_Click(object sender, RoutedEventArgs e) => await _viewModel.OpenPhotoInfoAsync();
}

public sealed class GlanceBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class GlanceInverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class GlanceBoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1d : 0.42d;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

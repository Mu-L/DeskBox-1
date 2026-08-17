using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class WeatherWidgetContentAdapter :
    IWidgetContent,
    IWidgetResponsiveLayoutContent,
    IWidgetFeedbackSource,
    IDisposable
{
    private readonly Func<WeatherWidgetViewModel, FrameworkElement> _viewFactory;
    private readonly LocalizationService _localizationService;
    private FrameworkElement? _view;

    public WeatherWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        WeatherService? weatherService = null,
        Func<WeatherWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Weather)
        {
            throw new ArgumentException("Weather content requires a Weather widget config.", nameof(config));
        }

        Config = config;
        _localizationService = localizationService;
        ViewModel = new WeatherWidgetViewModel(
            config,
            weatherService ?? new WeatherService(),
            localizationService,
            settingsService);
        _viewFactory = viewFactory ?? (vm => new WeatherWidgetContent(vm));
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View => _view ??= _viewFactory(ViewModel);

    public WeatherWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private void ViewModel_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WeatherWidgetViewModel.ShowRefreshStatus) ||
            !ViewModel.ShowRefreshStatus)
        {
            return;
        }

        bool success = string.Equals(
            ViewModel.RefreshStatusText,
            _localizationService.T("Weather.RefreshSuccess"),
            StringComparison.Ordinal);
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    ViewModel.RefreshStatusText,
                    success
                        ? WidgetFeedbackSeverity.Success
                        : WidgetFeedbackSeverity.Error,
                    "weather-refresh")));
    }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public void OnDeactivated()
    {
        ViewModel.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
    }

    public void OnWindowRevealCompleted()
    {
        ViewModel.OnWindowRevealCompleted();
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        ViewModel.BeginResponsiveLayoutTransition(
            targetContentWidth,
            targetContentHeight,
            isCollapsing);
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        ViewModel.CompleteResponsiveLayoutTransition(finalContentWidth, finalContentHeight);
    }

    public void CancelResponsiveLayoutTransition()
    {
        ViewModel.CancelResponsiveLayoutTransition();
    }

    public void Dispose()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }
}

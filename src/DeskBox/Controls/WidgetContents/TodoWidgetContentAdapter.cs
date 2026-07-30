using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Content adapter for the future Todo widget. This keeps Todo in the shared
/// content pipeline without making the widget kind user-creatable yet.
/// </summary>
public sealed class TodoWidgetContentAdapter :
    IWidgetContent,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private readonly Func<TodoWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;

    public TodoWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService)
        : this(config, new TodoWidgetStore(config.Id), localizationService)
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config))
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService, SettingsService settingsService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config, settingsService))
    {
    }

    internal TodoWidgetContentAdapter(
        WidgetConfig config,
        TodoWidgetViewModel viewModel,
        Func<TodoWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Todo)
        {
            throw new ArgumentException("Todo content requires a Todo widget config.", nameof(config));
        }

        Config = config;
        ViewModel = viewModel;
        _viewFactory = viewFactory ?? (vm => new TodoWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View
    {
        get
        {
            if (_view is null)
            {
                _view = _viewFactory(ViewModel);
                if (_view is TodoWidgetContent todoContent)
                {
                    todoContent.FeedbackRequested += TodoContent_FeedbackRequested;
                }
            }

            return _view;
        }
    }

    public TodoWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private void TodoContent_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public Task RefreshAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
    }

    public object? CaptureTransientState()
    {
        return new TodoTransientState(
            ViewModel.InputText,
            ViewModel.DraftImportant,
            ViewModel.DraftDueDate);
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not TodoTransientState todoState)
        {
            return;
        }

        ViewModel.InputText = todoState.InputText;
        ViewModel.DraftImportant = todoState.DraftImportant;
        ViewModel.DraftDueDate = todoState.DraftDueDate;
    }

    public Task AddFromTitleButtonAsync()
    {
        if (View is TodoWidgetContent todoContent)
        {
            todoContent.OpenAddEditor();
        }

        return Task.CompletedTask;
    }

    private sealed record TodoTransientState(
        string InputText,
        bool DraftImportant,
        DateTimeOffset? DraftDueDate);

    internal bool CanImportExternalDrop(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.CanImportExternalDrop(dataView)
            : false;
    }

    internal Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.ImportExternalDropAsync(dataView)
            : Task.FromResult(false);
    }

    public void Dispose()
    {
        if (_view is TodoWidgetContent todoContent)
        {
            todoContent.FeedbackRequested -= TodoContent_FeedbackRequested;
        }

        if (ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

/// <summary>
/// Prepares lightweight content widget windows for non-file widget kinds.
/// </summary>
public sealed class ContentWidgetWindowFactory
{
    private readonly WidgetContentFactory _contentFactory;
    private readonly SettingsService _settingsService;
    private readonly Func<WidgetConfig, IWidgetContent, SettingsService, WidgetContentDescriptor, ContentWidgetWindow> _windowFactory;
    private readonly Func<WidgetConfig, TodoWidgetStore>? _todoStoreFactory;
    private readonly Func<WidgetConfig, IWidgetContent>? _quickCaptureContentFactory;
    private readonly Func<WidgetConfig, IWidgetContent>? _fileContentFactory;

    public ContentWidgetWindowFactory(
        WidgetContentFactory contentFactory,
        SettingsService settingsService,
        Func<WidgetConfig, IWidgetContent, SettingsService, WidgetContentDescriptor, ContentWidgetWindow>? windowFactory = null,
        Func<WidgetConfig, TodoWidgetStore>? todoStoreFactory = null,
        Func<WidgetConfig, IWidgetContent>? quickCaptureContentFactory = null,
        Func<WidgetConfig, IWidgetContent>? fileContentFactory = null)
    {
        _contentFactory = contentFactory;
        _settingsService = settingsService;
        _windowFactory = windowFactory ?? ((config, content, settings, descriptor) =>
            new ContentWidgetWindow(config, content, settings, descriptor));
        _todoStoreFactory = todoStoreFactory;
        _quickCaptureContentFactory = quickCaptureContentFactory;
        _fileContentFactory = fileContentFactory;
    }

    internal bool CanCreateContentWindow(WidgetKind widgetKind)
    {
        return widgetKind switch
        {
            WidgetKind.QuickCapture => _quickCaptureContentFactory is not null,
            WidgetKind.File => _fileContentFactory is not null,
            _ => _contentFactory.CanCreateDetachedContent(widgetKind)
        };
    }

    internal ContentWidgetWindow CreateContentWindow(WidgetConfig config)
    {
        var plan = CreateContentWindowPlan(config);
        return _windowFactory(plan.Config, plan.Content, _settingsService, plan.Descriptor);
    }

    internal ContentWidgetWindowPlan CreateContentWindowPlan(WidgetConfig config)
    {
        if (!CanCreateContentWindow(config.WidgetKind))
        {
            throw new NotSupportedException(
                $"Widget kind '{config.WidgetKind}' does not support content windows.");
        }

        var descriptor = _contentFactory.GetDescriptor(config.WidgetKind);
        IWidgetContent content = config.WidgetKind switch
        {
            WidgetKind.QuickCapture => _quickCaptureContentFactory!(config),
            WidgetKind.File => _fileContentFactory!(config),
            _ => _contentFactory.CreateDetachedContent(
                config,
                _todoStoreFactory,
                _settingsService)
        };
        return new ContentWidgetWindowPlan(config, content, descriptor);
    }
}

internal sealed record ContentWidgetWindowPlan(
    WidgetConfig Config,
    IWidgetContent Content,
    WidgetContentDescriptor Descriptor);

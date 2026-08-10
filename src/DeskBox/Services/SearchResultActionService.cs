using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Performs secondary actions on search results: attaching a file to a todo,
/// saving a file as a quick-capture note, and copying paths to the clipboard.
/// These actions are surfaced through the result context menu in the search popup.
/// </summary>
public sealed class SearchResultActionService
{
    private readonly SettingsService _settingsService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly TodoWorkspaceService _todoWorkspaceService;

    public SearchResultActionService(
        SettingsService settingsService,
        TodoWorkspaceService todoWorkspaceService,
        QuickCaptureService? quickCaptureService = null)
    {
        _settingsService = settingsService;
        _todoWorkspaceService = todoWorkspaceService;
        _quickCaptureService = quickCaptureService ?? new QuickCaptureService();
    }

    /// <summary>
    /// Creates a todo in the first available Todo widget with the file attached.
    /// Returns a human-readable outcome for display in a tooltip/toast.
    /// </summary>
    public async Task<bool> AttachFileToTodoAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var todoWidget = _settingsService.Settings.Widgets
                .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);

            if (todoWidget is null)
            {
                App.Log("[SearchAction] No Todo widget available to attach to.");
                return false;
            }

            string fileName = Path.GetFileName(path);
            await _todoWorkspaceService.CreateTaskFromLinkedFileAsync(path);

            App.Log($"[SearchAction] Attached '{fileName}' to todo widget '{todoWidget.Id}'.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to attach file to todo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves a file as a quick-capture note with the file attached.
    /// </summary>
    public async Task<bool> SaveFileToNoteAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            string fileName = Path.GetFileName(path);
            QuickCaptureItem? item = await _quickCaptureService.AddItemWithAttachmentsAsync(
                [path],
                copyToManagedStorage: false);
            if (item is null)
            {
                return false;
            }

            await _quickCaptureService.UpdateItemDetailsAsync(
                item.Id,
                fileName,
                path,
                QuickCaptureAppearancePreset.Default,
                QuickCaptureContentFormat.Markdown);

            App.Log($"[SearchAction] Saved '{fileName}' to quick capture.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to save file to note: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the given result can be attached to a todo (requires an existing file
    /// and at least one enabled Todo widget).
    /// </summary>
    public bool CanAttachToTodo(SearchResultItem? item)
    {
        return item is not null &&
               item.Kind == SearchResultKind.File &&
               !string.IsNullOrWhiteSpace(item.DetailPath) &&
               File.Exists(item.DetailPath) &&
               _settingsService.Settings.Widgets
                   .Any(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);
    }

    /// <summary>
    /// Whether the given result can be saved as a note (requires an existing file).
    /// </summary>
    public bool CanSaveToNote(SearchResultItem? item)
    {
        return item is not null &&
               item.Kind == SearchResultKind.File &&
               !string.IsNullOrWhiteSpace(item.DetailPath) &&
               File.Exists(item.DetailPath);
    }
}

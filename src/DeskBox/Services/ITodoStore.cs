using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Compatibility surface used by the existing Todo view model while storage
/// is moved from per-widget JSON into the shared workspace repository.
/// </summary>
public interface ITodoStore
{
    string AttachmentDirectory { get; }

    Task<TodoWidgetData> LoadAsync();

    Task SaveAsync(TodoWidgetData data);

    Task ClearAsync();
}

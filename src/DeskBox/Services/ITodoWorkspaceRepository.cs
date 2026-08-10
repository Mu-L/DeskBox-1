using DeskBox.Models;

namespace DeskBox.Services;

public enum TodoWorkspaceChangeKind
{
    Initialized,
    TasksChanged,
    StructureChanged,
    Cleared
}

public sealed class TodoWorkspaceChangedEventArgs(
    TodoWorkspaceChangeKind kind,
    IReadOnlyCollection<string>? taskIds = null) : EventArgs
{
    public TodoWorkspaceChangeKind Kind { get; } = kind;

    public IReadOnlyCollection<string> TaskIds { get; } = taskIds ?? Array.Empty<string>();
}

public sealed record TodoMigrationSource(string SourcePath, string SourceHash);

public interface ITodoWorkspaceRepository : IDisposable
{
    event EventHandler<TodoWorkspaceChangedEventArgs>? Changed;

    string DatabasePath { get; }

    string AttachmentDirectory { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<TodoWorkspaceSnapshot> LoadSnapshotAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    Task<TodoTask?> GetTaskAsync(
        string taskId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    Task UpsertTaskAsync(TodoTask task, CancellationToken cancellationToken = default);

    Task ReplaceTasksAsync(
        IReadOnlyCollection<TodoTask> tasks,
        bool softDeleteMissing,
        CancellationToken cancellationToken = default);

    Task<bool> SoftDeleteTaskAsync(string taskId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);

    Task<bool> RestoreTaskAsync(string taskId, CancellationToken cancellationToken = default);

    Task<int> SetTasksDeletedAtAsync(
        IReadOnlyCollection<string> taskIds,
        DateTimeOffset? deletedAt,
        CancellationToken cancellationToken = default);

    Task<int> PurgeDeletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task<bool> PurgeTaskAsync(string taskId, CancellationToken cancellationToken = default);

    Task<int> PurgeTasksAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default);

    Task UpsertListAsync(TodoList list, CancellationToken cancellationToken = default);

    Task UpsertSectionAsync(TodoSection section, CancellationToken cancellationToken = default);

    Task UpsertTagAsync(TodoTag tag, CancellationToken cancellationToken = default);

    Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default);

    Task UpsertSavedViewAsync(TodoSavedView savedView, CancellationToken cancellationToken = default);

    Task DeleteSavedViewAsync(string savedViewId, CancellationToken cancellationToken = default);

    Task UpsertRecurrenceExceptionAsync(
        TodoRecurrenceException exception,
        CancellationToken cancellationToken = default);

    Task DeleteRecurrenceExceptionsFromAsync(
        string seriesId,
        DateOnly fromDate,
        CancellationToken cancellationToken = default);

    Task ApplyRecurrenceMutationAsync(
        IReadOnlyCollection<TodoTask> tasks,
        TodoRecurrenceException? exception = null,
        string? clearSeriesId = null,
        DateOnly? clearFromDate = null,
        CancellationToken cancellationToken = default);

    Task RemoveRecurrenceExceptionAsync(
        string seriesId,
        string occurrenceKey,
        CancellationToken cancellationToken = default);

    Task<bool> HasMigrationSourceAsync(string sourceHash, CancellationToken cancellationToken = default);

    Task RecordMigrationSourceAsync(string sourcePath, string sourceHash, CancellationToken cancellationToken = default);

    Task ImportMigrationBatchAsync(
        IReadOnlyCollection<TodoTask> tasks,
        IReadOnlyCollection<TodoMigrationSource> sources,
        CancellationToken cancellationToken = default);

    Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

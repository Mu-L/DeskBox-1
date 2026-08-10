using DeskBox.Models;

namespace DeskBox.Services;

public interface IQuickCaptureRepository
{
    string DatabasePath { get; }

    Task<QuickCaptureStoreData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(QuickCaptureStoreData data, CancellationToken cancellationToken = default);

    Task SaveItemAsync(
        QuickCaptureItem item,
        bool isRecent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuickCaptureSearchHit>> SearchAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task SaveDraftAsync(QuickCaptureDraft draft, CancellationToken cancellationToken = default);

    Task<QuickCaptureDraft?> GetDraftAsync(string noteId, CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(string noteId, CancellationToken cancellationToken = default);

    Task<long> SaveRevisionAsync(
        QuickCaptureItem item,
        int retentionDays = 30,
        int maxRevisions = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuickCaptureRevision>> GetRevisionsAsync(
        string noteId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuickCaptureItem>> GetTrashAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task PurgeDeletedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    Task DeletePermanentlyAsync(
        string noteId,
        CancellationToken cancellationToken = default);

    Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}

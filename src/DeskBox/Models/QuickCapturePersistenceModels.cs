namespace DeskBox.Models;

public sealed record QuickCaptureRevision(
    long Id,
    string NoteId,
    string? Title,
    string Body,
    QuickCaptureContentFormat ContentFormat,
    DateTimeOffset CreatedAt);

public sealed record QuickCaptureDraft(
    string NoteId,
    string? Title,
    string Body,
    QuickCaptureContentFormat ContentFormat,
    DateTimeOffset UpdatedAt);

public sealed record QuickCaptureSearchHit(
    QuickCaptureItem Item,
    string PlainText,
    double Rank);

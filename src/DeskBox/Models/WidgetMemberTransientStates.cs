namespace DeskBox.Models;

public sealed record QuickCaptureWidgetTransientState(
    string InputText,
    string SearchText,
    QuickCaptureViewMode SelectedView,
    string FocusTarget,
    string? SelectedDetailItemId = null,
    bool IsDetailEditing = false,
    string? DetailDraft = null);

public sealed record FileWidgetTransientState(
    string[] SelectedPaths,
    string[] CutPaths);

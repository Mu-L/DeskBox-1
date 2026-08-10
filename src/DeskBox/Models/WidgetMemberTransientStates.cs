namespace DeskBox.Models;

public sealed record QuickCaptureWidgetTransientState(
    string InputText,
    string SearchText,
    QuickCaptureViewMode SelectedView,
    string FocusTarget,
    string? SelectedItemId = null,
    bool IsDetailVisible = false,
    bool IsEditing = false,
    int CaretIndex = 0,
    double ListPaneWidth = 284,
    double ListScrollOffset = 0,
    double DetailScrollOffset = 0,
    string LayoutOverride = "Auto");

public sealed record FileWidgetTransientState(
    string[] SelectedPaths,
    string[] CutPaths);

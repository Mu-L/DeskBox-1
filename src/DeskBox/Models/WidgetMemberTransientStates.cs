namespace DeskBox.Models;

public sealed record QuickCaptureWidgetTransientState(
    string InputText,
    string SearchText,
    QuickCaptureViewMode SelectedView,
    string FocusTarget);

public sealed record FileWidgetTransientState(
    string[] SelectedPaths,
    string[] CutPaths);

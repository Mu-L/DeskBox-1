namespace DeskBox.Tests;

public sealed class QuickCaptureSettingsRuntimeContractTests
{
    [Fact]
    public void SharedSurface_ConsumesWideOpenModeAndTabBarVisibility()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains(
            "_settingsService.Settings.QuickCaptureWideOpenMode",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsService.QuickCaptureWideOpenEditing",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void SynchronizeSegmentedVisibility()",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.TabBarVisibility != Visibility.Visible",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(QuickCaptureWidgetViewModel.TabBarVisibility)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEditors_ConsumeTheirFeatureEnterBehavior()
    {
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));
        string sharedQuickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string standaloneQuickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));

        Assert.Contains(
            "SettingsService.ShouldSubmitEditorOnEnter(EditorEnterBehavior, control)",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorEnterBehavior=\"{Binding EditorEnterBehavior}\"",
            sharedQuickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorEnterBehavior=\"{Binding EditorEnterBehavior}\"",
            standaloneQuickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorEnterBehavior=\"{Binding ElementName=RootGrid, Path=DataContext.EditorEnterBehavior}\"",
            todo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureTextInputs_UseTheConfiguredSubmitHelper()
    {
        string shared = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string standaloneInput = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Detail.cs"));
        string standaloneEdit = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Editing.cs"));

        Assert.Contains("QuickCaptureEditorEnterBehavior", shared, StringComparison.Ordinal);
        Assert.Contains("SettingsService.ShouldSubmitEditorOnEnter", shared, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureEditorEnterBehavior", standaloneInput, StringComparison.Ordinal);
        Assert.Contains("SettingsService.ShouldSubmitEditorOnEnter", standaloneInput, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureEditorEnterBehavior", standaloneEdit, StringComparison.Ordinal);
        Assert.Contains("SettingsService.ShouldSubmitEditorOnEnter", standaloneEdit, StringComparison.Ordinal);
    }
}

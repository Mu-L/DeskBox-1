using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class QuickCaptureSettingsRuntimeContractTests
{
    [Fact]
    public void SharedSurface_SearchReplacesTabsInlineAndHasExplicitCancel()
    {
        string xamlPath = TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement listPage = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ListPage");
        XElement searchButton = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SearchButton");
        XElement searchBox = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "SearchTextBox");
        XElement closeButton = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "CloseSearchButton");
        XElement segmented = listPage.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") ==
            "QuickCaptureViewSegmented");

        Assert.Same(segmented.Parent, searchButton.Parent);
        Assert.Same(segmented.Parent, searchBox.Parent);
        Assert.Same(segmented.Parent, closeButton.Parent);
        Assert.Equal(
            "{Binding SearchButtonVisibility}",
            (string?)searchButton.Attribute("Visibility"));
        Assert.Equal(
            "{Binding SearchBoxVisibility}",
            (string?)searchBox.Attribute("Visibility"));
        Assert.Equal(
            "CloseSearchButton_Click",
            (string?)closeButton.Attribute("Click"));
        Assert.Equal(
            "{Binding SearchCancelText}",
            (string?)closeButton
                .Element(presentation + "TextBlock")?
                .Attribute("Text"));

        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        Assert.Contains("CloseSearchAndRestoreFocus();", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CollapseSearch();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearSearchButton_Click", code, StringComparison.Ordinal);
    }

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
    public void ExistingNotes_UseGlobalFormatWhenEnteringEitherEditorHost()
    {
        string shared = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string standaloneDetail = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Detail.cs"));
        string standaloneResponsive = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.ResponsiveDetail.cs"));
        shared = shared.ReplaceLineEndings("\n");
        standaloneDetail = standaloneDetail.ReplaceLineEndings("\n");
        standaloneResponsive = standaloneResponsive.ReplaceLineEndings("\n");

        Assert.Contains(
            "_detailContentFormat = _isDetailEditing\n            ? ViewModel.EditorContentFormat\n            : item.ContentFormat;",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailContentFormat = ViewModel.EditorContentFormat;\n        SetDetailEditorText(_detailItem?.Body ?? string.Empty);\n        _isDetailEditing = true;",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailContentFormat = _isDetailEditing\n            ? ViewModel.EditorContentFormat\n            : item.ContentFormat;",
            standaloneDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailContentFormat = ViewModel.EditorContentFormat;\n        _isDetailEditing = true;",
            standaloneResponsive,
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

    [Fact]
    public void NewNoteDraft_DoesNotBlockSelectingAnotherNote()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "if (_isCreatingDetail && !HasNewDetailContent())\n        {\n            // A blank draft has nothing to preserve.",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "await SaveDetailAsync(completeEditing: false);",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "private bool HasNewDetailContent() =>",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "await FlushPendingDetailSaveAsync();\n        if (_detailHasUnsavedChanges)\n        {\n            return;\n        }\n\n        OpenDetail(item);",
            code,
            StringComparison.Ordinal);
    }
}

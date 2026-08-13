namespace DeskBox.Tests;

public sealed class ContentDeletionDirectContractTests
{
    [Fact]
    public void TodoDeletionEntryPoints_DoNotRouteThroughConfirmation()
    {
        string source = ReadRepositoryFiles(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs",
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs",
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ListInteraction.cs",
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Menus.cs",
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs");

        Assert.Contains("DeleteItemAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedItemsAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDeleteItemConfirmation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDeleteSelectedConfirmation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowTodoConfirmMenu", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmBeforeDelete", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureDeletionEntryPoints_DoNotRouteThroughConfirmation()
    {
        string source = ReadRepositoryFiles(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Detail.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.ItemActions.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Items.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.WindowInteraction.cs");

        Assert.Contains("DeleteQuickCaptureItemAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeleteItemWithUndoAsync(", source, StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedQuickCaptureItemsAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmDeleteItemAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowQuickCaptureDeleteConfirmFlyout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowQuickCaptureDeleteSelectedConfirmFlyout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCapture.DeleteConfirm", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoDeleteConfirmationSetting_IsRemovedFromRuntimeAndSettingsUi()
    {
        string source = ReadRepositoryFiles(
            "src/DeskBox/Models/AppSettings.cs",
            "src/DeskBox/Services/SettingsService.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureCallbacks.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs",
            "src/DeskBox/ViewModels/TodoWidgetViewModel.cs",
            "src/DeskBox/ViewModels/TodoWidgetViewModel.FilteringAndAppearance.cs",
            "src/DeskBox/Views/SettingsWindow.xaml");

        Assert.DoesNotContain("TodoConfirmBeforeDelete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmBeforeDelete", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFiles(params string[] relativePaths)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "DeskBox")))
        {
            directory = directory.Parent;
        }

        string repositoryRoot = directory?.FullName ??
            throw new DirectoryNotFoundException();
        return string.Join(
            Environment.NewLine,
            relativePaths.Select(path => File.ReadAllText(Path.Combine(
                repositoryRoot,
                path.Replace('/', Path.DirectorySeparatorChar)))));
    }
}

using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Tests;

public sealed class FileItemMultiDragTests
{
    [Theory]
    [InlineData(true, false, false, FileItemPointerSelectionAction.Preserve)]
    [InlineData(true, true, false, FileItemPointerSelectionAction.Preserve)]
    [InlineData(false, false, true, FileItemPointerSelectionAction.Preserve)]
    [InlineData(false, true, false, FileItemPointerSelectionAction.Add)]
    [InlineData(false, false, false, FileItemPointerSelectionAction.Replace)]
    public void ResolvePointerSelectionAction_PreservesSelectedDragAnchor(
        bool itemIsSelected,
        bool controlPressed,
        bool shiftPressed,
        FileItemPointerSelectionAction expected)
    {
        Assert.Equal(
            expected,
            FileItemSelectionBehavior.ResolvePointerSelectionAction(
                itemIsSelected,
                controlPressed,
                shiftPressed));
    }

    [Fact]
    public void ResolveDraggedItems_UsesFullSelectionWhenEventOnlyContainsAnchor()
    {
        WidgetItem first = CreateItem("first.txt");
        WidgetItem second = CreateItem("second.txt");
        WidgetItem third = CreateItem("third.txt");

        IReadOnlyList<WidgetItem> resolved = FileItemDragPackage.ResolveDraggedItems(
            [second],
            [first, second, third]);

        Assert.Equal([first, second, third], resolved);
    }

    [Fact]
    public void ResolveDraggedItems_DoesNotBorrowUnrelatedSelection()
    {
        WidgetItem dragged = CreateItem("dragged.txt");
        WidgetItem selectedFirst = CreateItem("selected-first.txt");
        WidgetItem selectedSecond = CreateItem("selected-second.txt");

        IReadOnlyList<WidgetItem> resolved = FileItemDragPackage.ResolveDraggedItems(
            [dragged],
            [selectedFirst, selectedSecond]);

        Assert.Equal([dragged], resolved);
    }

    [Fact]
    public void TryPrepare_WritesEveryResolvedPathToInternalDragPayload()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string firstPath = Path.Combine(tempDirectory, "first.txt");
        string secondPath = Path.Combine(tempDirectory, "second.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");

        try
        {
            WidgetItem first = CreateItem(firstPath);
            WidgetItem second = CreateItem(secondPath);
            var dataPackage = new DataPackage();

            bool prepared = FileItemDragPackage.TryPrepare(
                dataPackage,
                [first, second],
                "source-widget",
                _ => Array.Empty<IStorageItem>(),
                paths => paths.Count.ToString(),
                out FileItemDragPackageResult result);

            Assert.True(prepared);
            Assert.Equal([firstPath, secondPath], result.SourcePaths);
            Assert.True(dataPackage.Properties.TryGetValue(
                DeskBoxDragData.SourcePathsProperty,
                out object? payload));
            Assert.Equal([firstPath, secondPath], Assert.IsType<string[]>(payload));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static WidgetItem CreateItem(string path) => new()
    {
        Name = path,
        Path = path
    };
}

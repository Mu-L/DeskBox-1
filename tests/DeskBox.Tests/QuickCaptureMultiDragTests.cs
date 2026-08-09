using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class QuickCaptureMultiDragTests
{
    [Fact]
    public void ResolveDraggedItems_UsesFullSelectionWhenAnchorIsSelected()
    {
        QuickCaptureItemViewModel first = CreateItem("first", "First");
        QuickCaptureItemViewModel second = CreateItem("second", "Second");
        QuickCaptureItemViewModel third = CreateItem("third", "Third");

        Assert.Equal(
            [first, second, third],
            QuickCaptureDragPackage.ResolveDraggedItems(
                [second],
                [first, second, third]));
        Assert.Equal(
            [second],
            QuickCaptureDragPackage.ResolveDraggedItems(
                [second],
                [first, third]));
    }

    [Fact]
    public void TryPrepare_MultiSelectionCreatesOneBatchPayload()
    {
        QuickCaptureItemViewModel first = CreateItem("first", "First");
        QuickCaptureItemViewModel second = CreateItem("second", "Second");
        var dataPackage = new DataPackage();

        bool prepared = QuickCaptureDragPackage.TryPrepare(
            dataPackage,
            [first, second],
            TestServices.CreateLocalizationService());

        Assert.True(prepared);
        Assert.Contains(DeskBoxDragData.TextFormat, dataPackage.GetView().AvailableFormats);
        Assert.Equal(
            DataPackageOperation.Copy,
            dataPackage.RequestedOperation);
    }

    [Fact]
    public void GroupedSurface_AdvertisesExtendedSelectionAndDragHandlers()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("SelectionMode=\"Extended\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanDragItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DragItemsStarting=\"ItemsList_DragItemsStarting\"", xaml, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureDragPackage.ResolveDraggedItems", source, StringComparison.Ordinal);
        Assert.Contains("ApplyTabDropAsync", source, StringComparison.Ordinal);
    }

    private static QuickCaptureItemViewModel CreateItem(string id, string body)
    {
        return new QuickCaptureItemViewModel(
            new QuickCaptureItem
            {
                Id = id,
                Body = body
            },
            TestServices.CreateLocalizationService(),
            textSize: 14,
            iconSize: 16,
            searchText: null);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "DeskBox")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}

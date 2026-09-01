using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileOpenInteractionContractTests
{
    [Fact]
    public async Task OpenItemAsync_EmptyTargetReturnsFailureWithoutShellDispatch()
    {
        var item = new WidgetItem
        {
            Path = string.Empty,
            TargetPath = string.Empty,
            IsShortcut = false
        };

        FileService.OpenItemResult result = await FileService.OpenItemAsync(
            item,
            IntPtr.Zero);

        Assert.Equal(FileService.OpenItemResult.Failed, result);
    }

    [Fact]
    public async Task OpenItemAsync_HonorsCancellationBeforeQueueing()
    {
        var item = new WidgetItem
        {
            Path = string.Empty,
            TargetPath = string.Empty,
            IsShortcut = false
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileService.OpenItemAsync(
                item,
                IntPtr.Zero,
                cancellation.Token));
    }

    [Fact]
    public void FileSurfaceOpenPath_UsesAsyncLaunchAndResultFeedback()
    {
        string navigation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        string opening = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"));

        Assert.Contains("await OpenFileItemAsync(item)", navigation, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.OpenItemAsync(", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemFailed", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemBusy", opening, StringComparison.Ordinal);
        Assert.Contains("Widget.OpenItemDispatched", opening, StringComparison.Ordinal);
        Assert.Contains("OpenItem.DuplicateSuppressed", opening, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield()", opening, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenWorker_PreservesStaAndBoundedDispatch()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.OpenItem.cs"));
        string runner = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/BoundedStaOperationRunner.cs"));

        Assert.Contains("maxConcurrency: 2", source, StringComparison.Ordinal);
        Assert.Contains("maxQueued: 6", source, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", runner, StringComparison.Ordinal);
        Assert.Contains("thread.SetApartmentState(ApartmentState.STA)", runner, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.OpenFile(ownerHwnd", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FileItemActivityBadge_ReusesExistingTransferVisual()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));

        Assert.Contains("ActivityBadgeVisibility", xaml, StringComparison.Ordinal);
        Assert.Contains("IsActivityActive", xaml, StringComparison.Ordinal);
        Assert.Contains("SetOpeningState", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenItemSurface", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenRequestGate_BoundsHistoryAndAllowsRetryAfterFailure()
    {
        var gate = new FileOpenRequestGate();
        const long firstTick = 1000;

        Assert.True(gate.TryBegin("C:\\Temp\\Report.txt", firstTick, 500));
        Assert.True(gate.IsActive("c:\\temp\\report.txt"));
        Assert.False(gate.TryBegin("c:\\temp\\report.txt", firstTick + 1, 500));

        gate.Complete("C:\\Temp\\Report.txt", dispatched: false);
        Assert.False(gate.IsActive("c:\\temp\\report.txt"));
        Assert.True(gate.TryBegin("c:\\temp\\report.txt", firstTick + 2, 500));

        for (int index = 0; index < FileOpenRequestGate.HistoryLimit + 8; index++)
        {
            gate.Complete($"C:\\Temp\\{index}.txt", dispatched: false);
            gate.TryBegin($"C:\\Temp\\{index}.txt", firstTick + index + 3, 1);
        }

        Assert.True(gate.HistoryCount <= FileOpenRequestGate.HistoryLimit);
    }
}

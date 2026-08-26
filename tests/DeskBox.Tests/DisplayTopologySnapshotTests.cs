using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DisplayTopologySnapshotTests
{
    [Fact]
    public void SemanticSignature_IgnoresTransientDisplayAlias()
    {
        string first = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY1")]);
        string reEnumerated = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY5")]);

        Assert.Equal(first, reEnumerated);
    }

    [Fact]
    public void SemanticSignature_ChangesForGeometryWorkAreaOrDpi()
    {
        string baseline = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY1")]);
        string geometry = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY1", right: 2560)]);
        string workArea = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY1", workBottom: 1000)]);
        string dpi = DisplayAreaWatcherService.CreateSemanticSignature(
            [Monitor(@"\\.\DISPLAY1", dpiScale: 1.25)]);

        Assert.NotEqual(baseline, geometry);
        Assert.NotEqual(baseline, workArea);
        Assert.NotEqual(baseline, dpi);
    }

    [Fact]
    public async Task SnapshotProvider_SharesOneInFlightNativeCapture()
    {
        using var releaseCapture = new ManualResetEventSlim(false);
        int captureCount = 0;
        var provider = new DisplayTopologySnapshotProvider(() =>
        {
            Interlocked.Increment(ref captureCount);
            releaseCapture.Wait();
            return DisplayTopologySnapshot.Invalid("test-completed");
        });

        Task<DisplayTopologySnapshot> first = provider.CaptureAsync();
        Task<DisplayTopologySnapshot>? second = null;
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref captureCount) == 1,
                TimeSpan.FromSeconds(2)));
            second = provider.CaptureAsync();

            Assert.Same(first, second);
            Assert.Equal(1, Volatile.Read(ref captureCount));
        }
        finally
        {
            releaseCapture.Set();
        }

        Assert.NotNull(second);
        await Task.WhenAll(first, second);
    }

    private static Win32Helper.MonitorWorkAreaInfo Monitor(
        string deviceName,
        int right = 1920,
        int workBottom = 1032,
        double dpiScale = 1) => new(
        new Win32Helper.RECT
        {
            Left = 0,
            Top = 0,
            Right = right,
            Bottom = 1080
        },
        new Win32Helper.RECT
        {
            Left = 0,
            Top = 0,
            Right = right,
            Bottom = workBottom
        },
        deviceName,
        IsPrimary: true,
        DpiScale: dpiScale);
}

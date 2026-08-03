using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox-SearchIndex-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryLoadPersistedIndex_WhenIndexerDisabled_DoesNotLoadEntries()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = false;

        using var service = new SearchIndexService(settings, storePath);
        service.TryLoadPersistedIndex();

        Assert.Equal(0, service.EntryCount);
    }

    [Fact]
    public void Search_ReturnsSameTopNOrder_AndHonorsCancellation()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using var service = new SearchIndexService(settings, storePath);
        service.TryLoadPersistedIndex();

        var results = service.Search("alpha", maxResults: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("alpha.txt", results[0].Title);
        Assert.Equal("alphabet.txt", results[1].Title);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => service.Search("alpha", maxResults: 2, cts.Token));
    }

    [Fact]
    public void StopIndexing_ClearsLoadedEntries()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using var service = new SearchIndexService(settings, storePath);
        service.TryLoadPersistedIndex();
        Assert.Equal(3, service.EntryCount);

        service.StopIndexing();

        Assert.Equal(0, service.EntryCount);
        Assert.False(service.IsScanning);
    }

    [Theory]
    [InlineData("C:\\foo", "C:\\", true)]
    [InlineData("C:\\foo\\bar.txt", "C:\\foo", true)]
    [InlineData("C:\\foobar", "C:\\foo", false)]
    public void IsSameOrDescendant_UsesBoundaryAwareNormalizedRoots(
        string candidate,
        string parent,
        bool expected)
    {
        Assert.Equal(expected, SearchIndexService.IsSameOrDescendant(candidate, parent));
    }

    [Theory]
    [InlineData((int)SearchIndexService.RootScanStatus.Completed, true)]
    [InlineData((int)SearchIndexService.RootScanStatus.Offline, false)]
    [InlineData((int)SearchIndexService.RootScanStatus.Partial, false)]
    [InlineData((int)SearchIndexService.RootScanStatus.ScanOnly, false)]
    [InlineData((int)SearchIndexService.RootScanStatus.CapacityLimited, false)]
    [InlineData((int)SearchIndexService.RootScanStatus.Canceled, false)]
    public void WatcherRecovery_ReconcilesOnlyCompletedRoots(
        int status,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchIndexService.ShouldReconcileRoot((SearchIndexService.RootScanStatus)status));
    }

    [Theory]
    [InlineData(4, 4, true, false, true)]
    [InlineData(4, 5, true, false, false)]
    [InlineData(4, 4, false, false, false)]
    [InlineData(4, 4, true, true, false)]
    public void SearchSessionCurrent_RequiresEpochEnabledAndUncancelled(
        long expectedEpoch,
        long currentEpoch,
        bool indexingEnabled,
        bool cancellationRequested,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchIndexService.IsSessionCurrent(
                expectedEpoch,
                currentEpoch,
                indexingEnabled,
                cancellationRequested));
    }

    [Fact]
    public void FreshIndexManifest_IdentifiesExplicitlyRemovedRoots()
    {
        List<string> removed = SearchIndexService.GetExplicitlyRemovedRoots(
            [@"C:\Users\A", @"Z:\Mapped", @"z:\mapped"],
            [@"C:\Users\A", @"D:\Current"]);

        Assert.Equal([@"Z:\Mapped"], removed);
    }

    [Fact]
    public void SaveIndex_MigratesLegacyJsonToCompactBinary_AndPreservesResults()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using (var service = new SearchIndexService(settings, storePath))
        {
            service.TryLoadPersistedIndex();
            service.SaveIndex();
            Assert.Equal(3, service.EntryCount);
        }

        byte[] header = File.ReadAllBytes(storePath)[..4];
        Assert.Equal(new byte[] { (byte)'D', (byte)'B', (byte)'I', (byte)'X' }, header);

        using var reloaded = new SearchIndexService(settings, storePath);
        reloaded.TryLoadPersistedIndex();
        var results = reloaded.Search("alpha", maxResults: 3);

        Assert.Equal(3, reloaded.EntryCount);
        Assert.Equal(
            ["alpha.txt", "alphabet.txt", "my-alpha.txt"],
            results.Select(result => result.Title).ToArray());
    }

    [Fact]
    public async Task IdleUnload_ReleasesResidentIndex_AndPopupPreloadRestoresIt()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using var service = new SearchIndexService(settings, storePath);
        service.TryLoadPersistedIndex();
        service.SaveIndex();

        bool unloaded = await service.TryUnloadForIdleAsync();

        Assert.True(unloaded);
        Assert.False(service.IsIndexResident);
        Assert.Equal(3, service.EntryCount);
        Assert.Empty(service.Search("alpha", maxResults: 3));

        bool loaded = await service.EnsureLoadedAsync();
        var results = service.Search("alpha", maxResults: 3);

        Assert.True(loaded);
        Assert.True(service.IsIndexResident);
        Assert.Equal(3, results.Count);
    }

    private string CreatePersistedIndex()
    {
        Directory.CreateDirectory(_root);
        string storePath = Path.Combine(_root, "index.json");
        var payload = new
        {
            entries = new[]
            {
                new
                {
                    fullPath = Path.Combine(_root, "alpha.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 3)
                },
                new
                {
                    fullPath = Path.Combine(_root, "alphabet.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 2)
                },
                new
                {
                    fullPath = Path.Combine(_root, "my-alpha.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 1)
                }
            }
        };
        File.WriteAllText(storePath, JsonSerializer.Serialize(payload));
        return storePath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test data.
        }
    }
}

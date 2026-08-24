using System.Text.Json;
using System.Reflection;
using DeskBox.Models;
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

    [Fact]
    public async Task RustPreview_IsSingleResidentOwner_AndPersistsIncrementalMutations()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings-rust"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using (var migration = new SearchIndexService(settings, storePath))
        {
            migration.TryLoadPersistedIndex();
            migration.SaveIndex();
        }

        settings.Settings.SearchRustIndexerPreviewEnabled = true;
        string modulePath = Path.Combine(
            AppContext.BaseDirectory,
            SearchCoreNativeBackend.DllName);
        using var service = new SearchIndexService(settings, storePath, modulePath);
        service.TryLoadPersistedIndex();

        Assert.True(service.IsRustPreviewActive, service.RustPreviewFallbackReason);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Null(service.RustPreviewFallbackReason);
        Assert.Equal("alpha.txt", service.Search("alpha", 3)[0].Title);
        Assert.Equal("alpha.txt", service.GetRecentFiles(3)[0].Title);
        Assert.Single(service.GetFrequentFolders(3));

        SetIndexingEnabled(service, enabled: true);
        string addedPath = Path.Combine(_root, "rust-added.txt");
        File.WriteAllText(addedPath, "added");
        Assert.True(InvokeTryAddEntry(service, addedPath));

        string removedFolder = Path.Combine(_root, "removed-tree");
        Directory.CreateDirectory(removedFolder);
        string removedFile = Path.Combine(removedFolder, "rust-remove.txt");
        File.WriteAllText(removedFile, "remove");
        Assert.True(InvokeTryAddEntry(service, removedFile));
        Assert.Single(service.Search("rust-remove", 3));
        Assert.True(InvokeRemoveEntriesUnderPath(service, removedFolder));
        Assert.Empty(service.Search("rust-remove", 3));

        service.SaveIndex();
        Assert.Single(service.Search("rust-added", 3));
        Assert.True(await service.TryUnloadForIdleAsync());
        Assert.False(service.IsIndexResident);
        Assert.False(service.IsRustPreviewActive);

        Assert.True(await service.EnsureLoadedAsync());
        Assert.True(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Single(service.Search("rust-added", 3));
        Assert.Empty(service.Search("rust-remove", 3));
    }

    [Fact]
    public async Task RustPreview_MutationCompactionRenameTreeDeleteAndIdleSoak_PreservesLiveSet()
    {
        using SearchIndexService service = CreateRustPreviewService("mutation-soak");
        string firstDirectory = Path.Combine(_root, "stage6d-a");
        string secondDirectory = Path.Combine(_root, "stage6d-b");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();

        SetIndexingEnabled(service, enabled: true);
        try
        {
            for (int index = 0; index < 64; index++)
            {
                string directory = index % 2 == 0 ? firstDirectory : secondDirectory;
                string path = Path.Combine(directory, $"stage6d-{index:000}.txt");
                File.WriteAllText(path, index.ToString());
                paths.Add(path);
                expectedPaths.Add(path);
                Assert.True(InvokeTryAddEntry(service, path));
            }

            // More than 4,096 replacements crosses the resident compaction
            // threshold while retaining only 64 live entries.
            for (int round = 0; round < 65; round++)
            {
                foreach (string path in paths)
                {
                    Assert.True(InvokeTryAddEntry(service, path));
                }
            }

            foreach (int index in Enumerable.Range(0, 64).Where(value => value % 8 == 0))
            {
                string path = paths[index];
                File.Delete(path);
                Assert.True(InvokeRemoveEntry(service, path));
                expectedPaths.Remove(path);
            }

            foreach (int index in new[] { 2, 4 })
            {
                string oldPath = paths[index];
                string newPath = Path.Combine(firstDirectory, $"stage6d-renamed-{index:000}.txt");
                File.Move(oldPath, newPath);
                InvokeApplyFileSystemRenamed(service, newPath, oldPath);
                expectedPaths.Remove(oldPath);
                expectedPaths.Add(newPath);
            }

            Directory.Delete(secondDirectory, recursive: true);
            Assert.True(InvokeRemoveEntriesUnderPath(service, secondDirectory));
            expectedPaths.RemoveWhere(path =>
                SearchIndexService.IsSameOrDescendant(path, secondDirectory));
        }
        finally
        {
            SetIndexingEnabled(service, enabled: false);
        }

        Assert.True(GetNativeTombstoneCount(service) >= 4096);
        SearchCoreNativeBackend backendBeforeCompaction = GetNativeBackend(service);
        service.SaveIndex();

        Assert.Equal(0, GetNativeTombstoneCount(service));
        Assert.NotSame(backendBeforeCompaction, GetNativeBackend(service));
        Assert.Equal(
            expectedPaths.Order(StringComparer.OrdinalIgnoreCase),
            service.Search("stage6d", 100)
                .Select(item => item.DetailPath!)
                .Order(StringComparer.OrdinalIgnoreCase));

        Assert.True(await service.TryUnloadForIdleAsync());
        Assert.False(service.IsIndexResident);
        Assert.True(await service.EnsureLoadedAsync());
        Assert.True(service.IsRustPreviewActive, service.RustPreviewFallbackReason);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Equal(
            expectedPaths.Order(StringComparer.OrdinalIgnoreCase),
            service.Search("stage6d", 100)
                .Select(item => item.DetailPath!)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RustPreview_RealWatcherRenameDeleteAndOverflowRecovery_RemainsExact()
    {
        using SearchIndexService service = CreateRustPreviewService("watcher-soak");
        string watchedRoot = Path.Combine(_root, "stage6d-watcher");
        Directory.CreateDirectory(watchedRoot);
        SetSessionEpoch(service, 1);
        SetIndexingEnabled(service, enabled: true);
        InvokeSetupWatchers(service, [watchedRoot], epoch: 1);

        try
        {
            string originalPath = Path.Combine(watchedRoot, "stage6d-watch-original.txt");
            File.WriteAllText(originalPath, "original");
            await WaitUntilAsync(() =>
                service.Search("stage6d-watch-original", 10)
                    .Any(item => string.Equals(
                        item.DetailPath,
                        originalPath,
                        StringComparison.OrdinalIgnoreCase)));

            string renamedPath = Path.Combine(watchedRoot, "stage6d-watch-renamed.txt");
            File.Move(originalPath, renamedPath);
            await WaitUntilAsync(() =>
                service.Search("stage6d-watch-renamed", 10)
                    .Any(item => string.Equals(
                        item.DetailPath,
                        renamedPath,
                        StringComparison.OrdinalIgnoreCase)) &&
                service.Search("stage6d-watch-original", 10)
                    .All(item => !string.Equals(
                        item.DetailPath,
                        originalPath,
                        StringComparison.OrdinalIgnoreCase)));

            // Simulate a watcher buffer gap by removing a still-existing file
            // from the index, then route a real watcher Error recovery for the
            // owned root. The root scan must restore the missed entry.
            Assert.True(InvokeRemoveEntry(service, renamedPath));
            Assert.DoesNotContain(
                service.Search("stage6d-watch-renamed", 10),
                item => string.Equals(
                    item.DetailPath,
                    renamedPath,
                    StringComparison.OrdinalIgnoreCase));
            InvokeWatcherError(service, GetFirstWatcher(service));
            await WaitUntilAsync(() =>
                service.WatcherRecoveryCount >= 1 &&
                service.Search("stage6d-watch-renamed", 10)
                    .Any(item => string.Equals(
                        item.DetailPath,
                        renamedPath,
                        StringComparison.OrdinalIgnoreCase)),
                timeout: TimeSpan.FromSeconds(10));

            string treePath = Path.Combine(watchedRoot, "stage6d-watch-tree");
            Directory.CreateDirectory(treePath);
            string childPath = Path.Combine(treePath, "stage6d-watch-child.txt");
            File.WriteAllText(childPath, "child");
            await WaitUntilAsync(() =>
                service.Search("stage6d-watch-child", 10)
                    .Any(item => string.Equals(
                        item.DetailPath,
                        childPath,
                        StringComparison.OrdinalIgnoreCase)));

            Directory.Delete(treePath, recursive: true);
            await WaitUntilAsync(() =>
                service.Search("stage6d-watch-child", 10)
                    .All(item => string.Equals(
                        item.DetailPath,
                        childPath,
                        StringComparison.OrdinalIgnoreCase) is false));
        }
        finally
        {
            SetIndexingEnabled(service, enabled: false);
        }

        Assert.True(service.IsRustPreviewActive, service.RustPreviewFallbackReason);
        Assert.True(service.HasSingleResidentBackend);
    }

    [Fact]
    public void RustPreview_MissingModule_FallsBackToManagedWithoutEmptyResults()
    {
        string storePath = CreatePersistedIndex();
        var settings = new SettingsService(Path.Combine(_root, "settings-fallback"));
        settings.Settings.SearchCustomIndexerEnabled = true;

        using (var migration = new SearchIndexService(settings, storePath))
        {
            migration.TryLoadPersistedIndex();
            migration.SaveIndex();
        }

        settings.Settings.SearchRustIndexerPreviewEnabled = true;
        string missingModule = Path.Combine(_root, "missing", SearchCoreNativeBackend.DllName);
        using var service = new SearchIndexService(settings, storePath, missingModule);
        service.TryLoadPersistedIndex();

        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.False(string.IsNullOrWhiteSpace(service.RustPreviewFallbackReason));
        Assert.Equal(3, service.EntryCount);
        Assert.Equal(3, service.Search("alpha", 3).Count);
    }

    [Fact]
    public async Task RustPreview_RuntimeQueryFailure_RecoversManagedUntilExplicitRetry()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-query");
        GetNativeBackend(service).Dispose();

        IReadOnlyList<SearchResultItem> results = service.Search("alpha", 3);

        Assert.Equal(3, results.Count);
        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.True(service.IsRustPreviewSuppressedForSession);
        Assert.Equal(1, service.NativeRuntimeRecoveryCount);
        Assert.Contains("query", service.RustPreviewFallbackReason, StringComparison.OrdinalIgnoreCase);

        Assert.True(await service.TryUnloadForIdleAsync());
        Assert.True(await service.EnsureLoadedAsync());
        Assert.False(service.IsRustPreviewActive);
        Assert.Equal(3, service.Search("alpha", 3).Count);

        service.ResetRustPreviewRuntimeFallback();
        Assert.False(service.IsRustPreviewSuppressedForSession);
        Assert.True(await service.TryUnloadForIdleAsync());
        Assert.True(await service.EnsureLoadedAsync());
        Assert.True(service.IsRustPreviewActive, service.RustPreviewFallbackReason);
        Assert.True(service.HasSingleResidentBackend);
    }

    [Fact]
    public void RustPreview_RuntimeProjectionFailure_RecoversWithoutEmptyRecommendations()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-projection");
        GetNativeBackend(service).Dispose();

        IReadOnlyList<SearchResultItem> recent = service.GetRecentFiles(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal("alpha.txt", recent[0].Title);
        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Contains(
            "recent projection",
            service.RustPreviewFallbackReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.GetFrequentFolders(3));
    }

    [Fact]
    public void RustPreview_RuntimeSaveFailure_RecoversLastValidSnapshot()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-save");
        GetNativeBackend(service).Dispose();

        service.SaveIndex();

        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Equal(1, service.NativeRuntimeRecoveryCount);
        Assert.Contains("save", service.RustPreviewFallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, service.Search("alpha", 3).Count);
    }

    [Fact]
    public async Task RustPreview_RuntimeIdleUnloadFailure_RecoversManagedOwner()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-idle-unload");
        GetNativeBackend(service).Dispose();

        bool unloaded = await service.TryUnloadForIdleAsync();

        Assert.False(unloaded);
        Assert.True(service.IsIndexResident);
        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.True(service.IsRustPreviewSuppressedForSession);
        Assert.Equal(1, service.NativeRuntimeRecoveryCount);
        Assert.Contains(
            "idle unload",
            service.RustPreviewFallbackReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, service.Search("alpha", 3).Count);
    }

    [Fact]
    public void RustPreview_RuntimeUpsertFailure_RecoversAndRetriesManagedMutation()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-upsert");
        SetIndexingEnabled(service, enabled: true);
        string addedPath = Path.Combine(_root, "runtime-added.txt");
        File.WriteAllText(addedPath, "added");
        GetNativeBackend(service).Dispose();

        try
        {
            Assert.True(InvokeTryAddEntry(service, addedPath));
        }
        finally
        {
            SetIndexingEnabled(service, enabled: false);
        }

        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Equal(1, service.NativeRuntimeRecoveryCount);
        Assert.Contains(
            "upsert mutation",
            service.RustPreviewFallbackReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(addedPath, service.Search("runtime-added", 3).Single().DetailPath);
    }

    [Fact]
    public void RustPreview_RuntimeRemovalFailures_RecoversAndRetriesManagedMutations()
    {
        string exactPath = Path.Combine(_root, "alpha.txt");
        using (SearchIndexService exactService = CreateRustPreviewService("runtime-remove"))
        {
            SetIndexingEnabled(exactService, enabled: true);
            GetNativeBackend(exactService).Dispose();
            try
            {
                Assert.True(InvokeRemoveEntry(exactService, exactPath));
            }
            finally
            {
                SetIndexingEnabled(exactService, enabled: false);
            }

            Assert.DoesNotContain(
                exactService.Search("alpha", 3),
                item => string.Equals(item.DetailPath, exactPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                "remove mutation",
                exactService.RustPreviewFallbackReason,
                StringComparison.OrdinalIgnoreCase);
        }

        using SearchIndexService treeService = CreateRustPreviewService("runtime-tree-remove");
        SetIndexingEnabled(treeService, enabled: true);
        GetNativeBackend(treeService).Dispose();
        try
        {
            Assert.True(InvokeRemoveEntriesUnderPath(treeService, _root));
        }
        finally
        {
            SetIndexingEnabled(treeService, enabled: false);
        }

        Assert.Equal(0, treeService.EntryCount);
        Assert.Empty(treeService.Search("alpha", 3));
        Assert.Contains(
            "tree removal mutation",
            treeService.RustPreviewFallbackReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RustPreview_RuntimeReconciliationFailure_RetainsSnapshotAndDefersFreshScan()
    {
        using SearchIndexService service = CreateRustPreviewService("runtime-reconcile");
        SetIndexingEnabled(service, enabled: true);
        SetSessionEpoch(service, 1);
        GetNativeBackend(service).Dispose();

        try
        {
            InvokeReconcileIndex(service, [_root], scanGeneration: 7, epoch: 1);
        }
        finally
        {
            SetIndexingEnabled(service, enabled: false);
        }

        Assert.False(service.IsRustPreviewActive);
        Assert.True(service.HasSingleResidentBackend);
        Assert.Equal(3, service.EntryCount);
        Assert.Equal(3, service.Search("alpha", 3).Count);
        Assert.Contains(
            "scan reconciliation",
            service.RustPreviewFallbackReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RustPreviewSetting_DirectX64DefaultsOn_AndPersistsExplicitOptOut()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings-preview"));
        await settings.LoadAsync();
        Assert.True(AppSettings.SearchRustIndexerDefaultEnabled);
        Assert.True(settings.Settings.SearchRustIndexerPreviewEnabled);

        settings.Settings.SearchRustIndexerPreviewEnabled = false;
        await settings.SaveAsync();

        var reloaded = new SettingsService(Path.Combine(_root, "settings-preview"));
        await reloaded.LoadAsync();
        Assert.False(reloaded.Settings.SearchRustIndexerPreviewEnabled);
    }

    private static void SetIndexingEnabled(SearchIndexService service, bool enabled)
    {
        FieldInfo field = typeof(SearchIndexService).GetField(
            "_indexingEnabled",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(service, enabled ? 1 : 0);
    }

    private static void SetSessionEpoch(SearchIndexService service, long epoch)
    {
        FieldInfo field = typeof(SearchIndexService).GetField(
            "_sessionEpoch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(service, epoch);
    }

    private static SearchCoreNativeBackend GetNativeBackend(SearchIndexService service)
    {
        FieldInfo field = typeof(SearchIndexService).GetField(
            "_nativeIndex",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<SearchCoreNativeBackend>(field.GetValue(service));
    }

    private static int GetNativeTombstoneCount(SearchIndexService service)
    {
        FieldInfo field = typeof(SearchIndexService).GetField(
            "_nativeTombstoneCount",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<int>(field.GetValue(service));
    }

    private static bool InvokeTryAddEntry(SearchIndexService service, string path)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "TryAddEntry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(service, [path, false, null])!;
    }

    private static bool InvokeRemoveEntriesUnderPath(
        SearchIndexService service,
        string path)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "RemoveEntriesUnderPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(
            service,
            [path, null, CancellationToken.None])!;
    }

    private static bool InvokeRemoveEntry(SearchIndexService service, string path)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "RemoveEntry",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null)!;
        return (bool)method.Invoke(service, [path])!;
    }

    private static void InvokeApplyFileSystemRenamed(
        SearchIndexService service,
        string newPath,
        string oldPath)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "ApplyFileSystemRenamed",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(service, [newPath, oldPath]);
    }

    private static void InvokeSetupWatchers(
        SearchIndexService service,
        List<string> roots,
        long epoch)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "SetupWatchers",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(service, [roots, epoch, CancellationToken.None]);
    }

    private static FileSystemWatcher GetFirstWatcher(SearchIndexService service)
    {
        FieldInfo field = typeof(SearchIndexService).GetField(
            "_watchers",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var watchers = Assert.IsType<List<FileSystemWatcher>>(field.GetValue(service));
        return Assert.Single(watchers);
    }

    private static void InvokeWatcherError(
        SearchIndexService service,
        FileSystemWatcher watcher)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "OnWatcherError",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(
            service,
            [watcher, new ErrorEventArgs(new InternalBufferOverflowException())]);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out while waiting for the search index state to converge.");
            }

            await Task.Delay(25);
        }
    }

    private static void InvokeReconcileIndex(
        SearchIndexService service,
        List<string> roots,
        int scanGeneration,
        long epoch)
    {
        MethodInfo method = typeof(SearchIndexService).GetMethod(
            "ReconcileIndex",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(
            service,
            [roots, scanGeneration, epoch, CancellationToken.None]);
    }

    private SearchIndexService CreateRustPreviewService(string suffix)
    {
        string storePath = CreatePersistedIndex($"index-{suffix}.json");
        var settings = new SettingsService(Path.Combine(_root, $"settings-{suffix}"));
        settings.Settings.SearchCustomIndexerEnabled = true;
        using (var migration = new SearchIndexService(settings, storePath))
        {
            migration.TryLoadPersistedIndex();
            migration.SaveIndex();
        }

        settings.Settings.SearchRustIndexerPreviewEnabled = true;
        string modulePath = Path.Combine(
            AppContext.BaseDirectory,
            SearchCoreNativeBackend.DllName);
        var service = new SearchIndexService(settings, storePath, modulePath);
        service.TryLoadPersistedIndex();
        Assert.True(service.IsRustPreviewActive, service.RustPreviewFallbackReason);
        Assert.True(service.HasSingleResidentBackend);
        return service;
    }

    private string CreatePersistedIndex(string fileName = "index.json")
    {
        Directory.CreateDirectory(_root);
        string storePath = Path.Combine(_root, fileName);
        var payload = new
        {
            entries = new[]
            {
                new
                {
                    fullPath = Path.Combine(_root, "alpha.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 3),
                    futureEntryField = "ignored"
                },
                new
                {
                    fullPath = Path.Combine(_root, "alphabet.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 2),
                    futureEntryField = "ignored"
                },
                new
                {
                    fullPath = Path.Combine(_root, "my-alpha.txt"),
                    isDirectory = false,
                    lastModified = new DateTime(2026, 1, 1),
                    futureEntryField = "ignored"
                }
            },
            futureIndexField = new { enabled = true }
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

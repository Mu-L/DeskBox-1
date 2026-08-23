using DeskBox.Models;
using DeskBox.Services;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace DeskBox.Tests;

public sealed class SearchCoreNativeBackendTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox-SearchCore-" + Guid.NewGuid().ToString("N"));

    public SearchCoreNativeBackendTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string ModulePath => Path.Combine(
        AppContext.BaseDirectory,
        SearchCoreNativeBackend.DllName);

    [Fact]
    public void RustBackend_MatchesCurrentManagedOrderScoresAndTextProjection()
    {
        SearchCoreSourceEntry[] source =
        [
            Entry(@"C:\Data", "alpha.txt", ticks: 90),
            Entry(@"c:\data", "alphabet.md", ticks: 80),
            Entry(@"C:\Data", "my-alpha.txt", ticks: 70),
            Entry(@"D:\Unicode", "ÄBC-notes.txt", ticks: 60),
            Entry(@"d:\unicode", "项目ÄBC.md", ticks: 50),
            Entry(@"D:\Unicode", "项目计划.txt", ticks: 40),
            Entry(@"E:\Folders", "alpha-folder", ticks: 30, isDirectory: true),
            Entry(@"E:\Folders", "unrelated.bin", ticks: 20),
            Entry(@"D:\Unicode", "Σigma.txt", ticks: 19),
            Entry(@"D:\Unicode", "ςigma.md", ticks: 18),
            Entry(@"D:\Unicode", "𐐀-note.txt", ticks: 17),
            Entry(@"D:\Unicode", ".alpha", ticks: 16),
            Entry(@"D:\Unicode", "Straße.txt", ticks: 15)
        ];

        using SearchIndexService managed = CreateManagedIndex(source);
        using SearchCoreNativeBackend rust = CreateRustBackend(source);

        foreach (string query in new[]
                 {
                     "alpha",
                     "äbc",
                     "项目",
                     "txt",
                     "σigma",
                     "𐐨",
                     "strasse"
                 })
        {
            SearchResultItem[] expected = managed.Search(query, 6).ToArray();
            SearchCoreQuerySnapshot actual = rust.Query(query, 6);

            Assert.Equal((uint)source.Length, actual.ScannedEntryCount);
            Assert.True(
                expected.Length == actual.Items.Count,
                $"Query '{query}' count mismatch. Managed=[{string.Join(", ", expected.Select(item => item.Title))}], Rust=[{string.Join(", ", actual.Items.Select(item => item.FileName))}].");
            Assert.Equal(
                expected.Select(item => item.Title),
                actual.Items.Select(item => item.FileName));
            Assert.Equal(
                expected.Select(item => item.Subtitle),
                actual.Items.Select(item => item.DirectoryPath));
            Assert.Equal(
                expected.Select(item => item.Kind == SearchResultKind.Folder),
                actual.Items.Select(item => item.IsDirectory));
            Assert.Equal(
                expected.Select(item => (uint)item.RelevanceScore),
                actual.Items.Select(item => item.RelevanceScore));
            Assert.Equal(
                expected.Select(item => item.ModifiedAt!.Value.UtcDateTime),
                actual.Items.Select(item => item.LastModifiedUtc));
        }
    }

    [Fact]
    public void SealedIndex_DeduplicatesDirectoriesAndDropsBuildOnlyLookupMemory()
    {
        const int count = 20_000;
        const string directory = @"C:\Shared\A-Very-Long-Directory-Used-By-Every-Indexed-File";
        SearchCoreSourceEntry[] source = Enumerable.Range(0, count)
            .Select(index => Entry(
                index % 2 == 0 ? directory : directory.ToLowerInvariant(),
                $"document-{index:000000}-alpha.txt",
                index))
            .ToArray();
        ulong duplicatedFullPathUtf16Bytes = (ulong)source.Sum(
            entry => entry.DirectoryPath.Length + entry.FileName.Length) * sizeof(char);

        using SearchCoreNativeBackend rust = CreateRustBackend(source, seal: false);
        SearchCoreMemoryStats building = rust.GetMemoryStats();
        Assert.False(building.IsSealed);
        Assert.Equal((uint)count, building.EntryCount);
        Assert.Equal(1U, building.DirectoryCount);
        Assert.True(building.BuildLookupCapacityBytes > 0);

        rust.Seal();
        SearchCoreMemoryStats sealedStats = rust.GetMemoryStats();
        Assert.True(sealedStats.IsSealed);
        Assert.Equal(0UL, sealedStats.BuildLookupCapacityBytes);
        Assert.True(
            sealedStats.TotalTrackedCapacityBytes < duplicatedFullPathUtf16Bytes,
            $"Tracked native capacity {sealedStats.TotalTrackedCapacityBytes:N0} should be below even the duplicated full-path UTF-16 payload {duplicatedFullPathUtf16Bytes:N0}.");
        _output.WriteLine(
            "entries={0:N0}; directories={1:N0}; native-tracked={2:N0}; managed-full-path-utf16-floor={3:N0}; ratio={4:P2}",
            count,
            sealedStats.DirectoryCount,
            sealedStats.TotalTrackedCapacityBytes,
            duplicatedFullPathUtf16Bytes,
            (double)sealedStats.TotalTrackedCapacityBytes / duplicatedFullPathUtf16Bytes);
    }

    [Fact]
    public void Query_HonorsPreCancelledManagedTokenWithoutEnteringNativeScan()
    {
        using SearchCoreNativeBackend rust = CreateRustBackend(
            [Entry(@"C:\Data", "alpha.txt", 1)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => rust.Query("alpha", 10, cancellation.Token));
    }

    [Fact]
    public void Loader_RejectsMissingAbsoluteModuleWithoutChangingProductBackend()
    {
        string missing = Path.Combine(_root, "missing", SearchCoreNativeBackend.DllName);
        bool loaded = SearchCoreNativeBackend.TryCreate(
            missing,
            0,
            0,
            out SearchCoreNativeBackend? backend,
            out string error);

        Assert.False(loaded);
        Assert.Null(backend);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DbixDirectOpen_MatchesManagedResultsWithoutBuildLookup()
    {
        SearchCoreSourceEntry[] source =
        [
            Entry(@"C:\Bench\项目", "report_000001.pdf", 90),
            Entry(@"C:\Bench\项目", "report_000002.md", 80),
            Entry(@"D:\Bench\资料", "项目计划_000003.txt", 70),
            Entry(@"D:\Bench\资料", "Σigma_000004.txt", 60),
            Entry(@"D:\Bench\资料", "ςigma_000005.md", 50),
            Entry(@"E:\Bench", "report-folder", 40, isDirectory: true)
        ];
        string dbixPath = Path.Combine(_root, "direct.dbix");
        WriteDbix(dbixPath, source);

        bool loaded = SearchCoreNativeBackend.TryOpenDbix(
            ModulePath,
            dbixPath,
            300_000,
            out SearchCoreNativeBackend? backend,
            out SearchCoreDbixLoadInfo loadInfo,
            out string error);

        Assert.True(loaded, error);
        Assert.NotNull(backend);
        using (backend)
        using (SearchIndexService managed = CreateManagedIndex(source))
        {
            Assert.Equal(1U, loadInfo.DbixVersion);
            Assert.Equal((uint)source.Length, loadInfo.EntryCount);
            Assert.Equal((ulong)new FileInfo(dbixPath).Length, loadInfo.SourceFileBytes);
            SearchCoreMemoryStats stats = backend.GetMemoryStats();
            Assert.True(stats.IsSealed);
            Assert.Equal(0UL, stats.BuildLookupCapacityBytes);

            foreach (string query in new[] { "report", "项目", "σigma", "txt" })
            {
                SearchResultItem[] expected = managed.Search(query, 20).ToArray();
                SearchCoreQuerySnapshot actual = backend.Query(query, 20);
                Assert.Equal(
                    expected.Select(item => item.Title),
                    actual.Items.Select(item => item.FileName));
                Assert.Equal(
                    expected.Select(item => item.ModifiedAt!.Value.UtcDateTime),
                    actual.Items.Select(item => item.LastModifiedUtc));
            }
        }
    }

    [Fact]
    public void DbixFailure_NeverExposesPartialHandleOrInvalidatesOpenedIndex()
    {
        SearchCoreSourceEntry[] source =
        [
            Entry(@"C:\Bench", "report_000001.pdf", 2),
            Entry(@"C:\Bench", "notes_000002.txt", 1)
        ];
        string dbixPath = Path.Combine(_root, "replace.dbix");
        WriteDbix(dbixPath, source);
        Assert.True(
            SearchCoreNativeBackend.TryOpenDbix(
                ModulePath,
                dbixPath,
                300_000,
                out SearchCoreNativeBackend? opened,
                out _,
                out string openError),
            openError);
        Assert.NotNull(opened);
        using (opened)
        {
            File.WriteAllBytes(dbixPath, [0x44, 0x42, 0x49]);
            bool reopened = SearchCoreNativeBackend.TryOpenDbix(
                ModulePath,
                dbixPath,
                300_000,
                out SearchCoreNativeBackend? failed,
                out _,
                out string error);

            Assert.False(reopened);
            Assert.Null(failed);
            Assert.Contains("corrupt", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("report_000001.pdf", opened.Query("report", 10).Items.Single().FileName);
        }

        WriteDbix(dbixPath, source);
        byte[] unsupported = File.ReadAllBytes(dbixPath);
        BitConverter.GetBytes(2).CopyTo(unsupported, sizeof(int));
        File.WriteAllBytes(dbixPath, unsupported);
        Assert.False(
            SearchCoreNativeBackend.TryOpenDbix(
                ModulePath,
                dbixPath,
                300_000,
                out SearchCoreNativeBackend? versionBackend,
                out _,
                out string versionError));
        Assert.Null(versionBackend);
        Assert.Contains("version", versionError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback", versionError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DbixDirectOpen_HonorsPreCancelledToken()
    {
        string dbixPath = Path.Combine(_root, "cancel.dbix");
        WriteDbix(dbixPath, [Entry(@"C:\Bench", "report.txt", 1)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SearchCoreNativeBackend.TryOpenDbix(
                ModulePath,
                dbixPath,
                300_000,
                out _,
                out _,
                out _,
                cancellation.Token));
    }

    [Fact]
    public void AbiV3Mutation_IsAtomicAndReconcilesStaleTreeWithoutDroppingUpsert()
    {
        SearchCoreSourceEntry[] source =
        [
            Entry(@"C:\Root", "old.txt", 10),
            Entry(@"C:\Root", "stale.txt", 20),
            Entry(@"C:\Other", "keep.txt", 30)
        ];
        string dbixPath = Path.Combine(_root, "mutate.dbix");
        WriteDbix(dbixPath, source);
        using SearchCoreNativeBackend backend = OpenDbix(dbixPath);
        DateTime replacementTime = new(638_900_000_000_000_100L, DateTimeKind.Utc);

        SearchCoreMutationResult result = backend.ApplyMutations(
        [
            new SearchCoreMutation(
                SearchCoreMutationKind.Upsert,
                @"C:\Root\old.txt",
                LastModified: replacementTime,
                ScanGeneration: 7),
            new SearchCoreMutation(
                SearchCoreMutationKind.RemoveStaleTree,
                @"C:\Root",
                ScanGeneration: 7)
        ]);

        Assert.Equal(2U, result.AppliedMutationCount);
        Assert.Equal(2U, result.LiveEntryCount);
        Assert.Equal(2U, result.TombstoneCount);
        Assert.Equal(replacementTime, backend.Query("old", 10).Items.Single().LastModifiedUtc);
        Assert.Empty(backend.Query("stale", 10).Items);
        Assert.Single(backend.Query("keep", 10).Items);

        Assert.Throws<SearchCoreNativeOperationException>(() => backend.ApplyMutations(
        [
            new SearchCoreMutation(
                SearchCoreMutationKind.Upsert,
                @"C:\Root\duplicate.txt",
                LastModified: replacementTime),
            new SearchCoreMutation(
                SearchCoreMutationKind.Upsert,
                @"c:\root\DUPLICATE.txt",
                LastModified: replacementTime)
        ]));
        Assert.Equal(2U, backend.GetMemoryStats().EntryCount);
        Assert.Empty(backend.Query("duplicate", 10).Items);
    }

    [Fact]
    public void AbiV3RecentAndFrequentProjections_MatchManagedIndex()
    {
        SearchCoreSourceEntry[] source =
        [
            Entry(@"C:\A", "one.txt", 10),
            Entry(@"C:\A", "two.txt", 30),
            Entry(@"C:\B", "three.txt", 40),
            Entry(@"C:\B", "four.txt", 20),
            Entry(@"C:\B", "folder", 50, isDirectory: true)
        ];
        string dbixPath = Path.Combine(_root, "projection.dbix");
        WriteDbix(dbixPath, source);
        using SearchCoreNativeBackend backend = OpenDbix(dbixPath);
        using SearchIndexService managed = CreateManagedIndex(source);

        Assert.Equal(
            managed.GetRecentFiles(3).Select(item => item.DetailPath),
            backend.GetRecentFiles(3).Select(item => item.FullPath));
        Assert.Equal(
            managed.GetFrequentFolders(2).Select(item => item.DetailPath),
            backend.GetFrequentFolders(2).Select(item => item.FullPath));
        Assert.Equal(
            managed.GetFrequentFolders(2).Select(item => item.ModifiedAt!.Value.UtcDateTime),
            backend.GetFrequentFolders(2).Select(item => item.LastModifiedUtc));
    }

    [Fact]
    public void AbiV3SaveDbix_PersistsOnlyLiveTransactionStateAndReloads()
    {
        Assert.True(
            SearchCoreNativeBackend.TryCreate(
                ModulePath,
                0,
                0,
                out SearchCoreNativeBackend? created,
                out string createError),
            createError);
        Assert.NotNull(created);
        string dbixPath = Path.Combine(_root, "saved.dbix");
        DateTime modified = new(638_900_000_000_000_123L, DateTimeKind.Utc);
        using (created)
        {
            created.Seal();
            created.ApplyMutations(
            [
                new SearchCoreMutation(
                    SearchCoreMutationKind.Upsert,
                    @"C:\Saved\alpha.txt",
                    LastModified: modified,
                    ScanGeneration: 2),
                new SearchCoreMutation(
                    SearchCoreMutationKind.Upsert,
                    @"C:\Removed\removed.txt",
                    LastModified: modified,
                    ScanGeneration: 2)
            ]);
            created.ApplyMutations(
            [
                new SearchCoreMutation(
                    SearchCoreMutationKind.RemoveExact,
                    @"C:\Removed\removed.txt")
            ]);
            SearchCoreDbixSaveInfo save = created.SaveDbix(dbixPath);
            Assert.Equal(1U, save.EntryCount);
            Assert.Equal(1U, save.DirectoryCount);
            Assert.True(save.FileBytes > 0);
        }

        using SearchCoreNativeBackend reopened = OpenDbix(dbixPath);
        Assert.Equal("alpha.txt", reopened.Query("alpha", 10).Items.Single().FileName);
        Assert.Empty(reopened.Query("removed", 10).Items);
        Assert.Equal(modified, reopened.Query("alpha", 10).Items.Single().LastModifiedUtc);
    }

    private SearchIndexService CreateManagedIndex(IReadOnlyList<SearchCoreSourceEntry> entries)
    {
        Directory.CreateDirectory(_root);
        string storePath = Path.Combine(_root, "managed-index.json");
        var payload = new
        {
            entries = entries.Select(entry => new
            {
                fullPath = Path.Combine(entry.DirectoryPath, entry.FileName),
                isDirectory = entry.IsDirectory,
                lastModified = entry.LastModified
            }).ToArray()
        };
        File.WriteAllText(storePath, JsonSerializer.Serialize(payload));
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.SearchCustomIndexerEnabled = true;
        var service = new SearchIndexService(settings, storePath);
        service.TryLoadPersistedIndex();
        Assert.Equal(entries.Count, service.EntryCount);
        return service;
    }

    private static SearchCoreNativeBackend CreateRustBackend(
        IReadOnlyList<SearchCoreSourceEntry> entries,
        bool seal = true)
    {
        int initialChars = entries.Sum(
            entry => entry.DirectoryPath.Length + entry.FileName.Length);
        bool loaded = SearchCoreNativeBackend.TryCreate(
            ModulePath,
            entries.Count,
            initialChars,
            out SearchCoreNativeBackend? backend,
            out string error);
        Assert.True(loaded, error);
        Assert.NotNull(backend);
        backend.AddEntries(entries);
        if (seal)
        {
            backend.Seal();
        }
        return backend;
    }

    private static SearchCoreNativeBackend OpenDbix(string dbixPath)
    {
        Assert.True(
            SearchCoreNativeBackend.TryOpenDbix(
                ModulePath,
                dbixPath,
                300_000,
                out SearchCoreNativeBackend? backend,
                out _,
                out string error),
            error);
        Assert.NotNull(backend);
        return backend;
    }

    private static void WriteDbix(
        string path,
        IReadOnlyList<SearchCoreSourceEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var directories = new List<string>();
        var directoryIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SearchCoreSourceEntry entry in entries)
        {
            if (!directoryIds.ContainsKey(entry.DirectoryPath))
            {
                directoryIds.Add(entry.DirectoryPath, directories.Count);
                directories.Add(entry.DirectoryPath);
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(0x58494244); // DBIX
        writer.Write(1);
        writer.Write(DateTime.UtcNow.Ticks);
        writer.Write(directories.Count);
        foreach (string directory in directories)
        {
            writer.Write(directory);
        }
        writer.Write(entries.Count);
        foreach (SearchCoreSourceEntry entry in entries)
        {
            writer.Write(directoryIds[entry.DirectoryPath]);
            byte[] fileName = Encoding.UTF8.GetBytes(entry.FileName);
            writer.Write(fileName.Length);
            writer.Write(fileName);
            writer.Write(entry.IsDirectory);
            writer.Write(entry.LastModified.ToBinary());
        }
    }

    private static SearchCoreSourceEntry Entry(
        string directory,
        string fileName,
        long ticks,
        bool isDirectory = false)
    {
        return new SearchCoreSourceEntry(
            directory,
            fileName,
            isDirectory,
            new DateTime(638_900_000_000_000_000L + ticks, DateTimeKind.Utc));
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
            // Best-effort cleanup for isolated test data.
        }
    }
}

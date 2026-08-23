namespace DeskBox.Tests;

public sealed class SearchCoreStage6AContractTests
{
    [Fact]
    public void SearchCore_StartedIndependentFromFrozenProductNativeAbi()
    {
        string workspace = Read("native/Cargo.toml");
        string productNative = Read("native/deskbox-native/src/lib.rs");
        string stageReport = Read("docs/architecture/rust-stage-6a-search-core-report.md");

        Assert.Contains("\"deskbox-search-core\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_search_core", productNative, StringComparison.Ordinal);
        Assert.Contains("产品默认路径保持不变", stageReport, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITIES", productNative, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1", productNative, StringComparison.Ordinal);
    }

    [Fact]
    public void AbiV3_PreservesV1BuffersAndAtomicDirectDbixOpen()
    {
        string rust = Read("native/deskbox-search-core/src/lib.rs");
        string header = Read("native/include/deskbox_search_core.h");

        foreach (string export in new[]
                 {
                     "deskbox_search_core_abi_version",
                     "deskbox_search_core_open_dbix_v1",
                     "deskbox_search_core_create_v1",
                     "deskbox_search_core_add_batch_v1",
                     "deskbox_search_core_seal_v1",
                     "deskbox_search_core_reset_cancel_v1",
                     "deskbox_search_core_cancel_v1",
                     "deskbox_search_core_query_v1",
                     "deskbox_search_core_copy_entries_v1",
                     "deskbox_search_core_mutate_batch_v1",
                     "deskbox_search_core_project_v1",
                     "deskbox_search_core_save_dbix_v1",
                     "deskbox_search_core_stats_v1",
                     "deskbox_search_core_destroy_v1"
                 })
        {
            Assert.Contains(export, rust, StringComparison.Ordinal);
            Assert.Contains(export, header, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "DeskBoxSearchEntryInputV1",
                     "utf16_data",
                     "result_capacity",
                     "required_utf16_chars",
                     "AtomicBool",
                     "BinaryHeap",
                     "directory_descriptor_capacity_bytes",
                     "build_lookup_capacity_bytes",
                     "total_tracked_capacity_bytes",
                     "u_toupper",
                     "non-ASCII code",
                     "original_width"
                 })
        {
            Assert.Contains(token, rust, StringComparison.Ordinal);
        }
        Assert.Contains(
            "no Rust string, vector, or allocator-owned result crosses the boundary",
            rust,
            StringComparison.Ordinal);
        Assert.Contains(
            "sizeof(DeskBoxSearchStatsV1) == 104",
            header,
            StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SEARCH_CORE_ABI_VERSION 3u", header, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSearchOpenDbixRequestV1", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SEARCH_STATUS_CORRUPT_DATA", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedBridge_IsExplicitAbsolutePathOnlyAndHasNoAutomaticFallback()
    {
        string bridge = Read("src/DeskBox/Services/SearchCoreNativeBackend.cs");

        foreach (string token in new[]
                 {
                     "Path.GetFullPath(modulePath)",
                     "Path.IsPathFullyQualified(fullPath)",
                     "NativeLibrary.Load(fullPath)",
                     "NativeLibrary.TryGetExport",
                     "delegate* unmanaged[Cdecl]",
                     "AddEntries(",
                     "Seal()",
                     "CancellationToken",
                     "GetMemoryStats()"
                 })
        {
            Assert.Contains(token, bridge, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Environment.GetEnvironmentVariable", bridge, StringComparison.Ordinal);
        Assert.Contains("ManagedOrdinalCasingMatchesSearchCoreV3", bridge, StringComparison.Ordinal);
        Assert.Contains("TryOpenDbix(", bridge, StringComparison.Ordinal);
        Assert.Contains("rebuild/fallback is required", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImport", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("new SearchIndexService", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchIndexService.", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("_searchIndexService", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("static readonly Lazy", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void TestBuild_StillCompilesAndCopiesIndependentModule()
    {
        string project = Read("tests/DeskBox.Tests/DeskBox.Tests.csproj");
        string script = Read("scripts/build-rust-search-core.ps1");

        foreach (string token in new[]
                 {
                     "BuildDeskBoxSearchCoreTestModule",
                     "CopyDeskBoxSearchCoreTestModule",
                     "build-rust-search-core.ps1",
                     "deskbox_search_core.dll",
                     "deskbox_search_core.pdb"
                 })
        {
            Assert.Contains(token, project, StringComparison.Ordinal);
        }
        Assert.Contains("--package", script, StringComparison.Ordinal);
        Assert.Contains("deskbox-search-core", script, StringComparison.Ordinal);
        Assert.Contains("expected 3", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_search_core_open_dbix_v1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native.dll", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

namespace DeskBox.Tests;

public sealed class SearchCoreStage6CContractTests
{
    [Fact]
    public void AbiV3_AddsTransactionalMutationProjectionAndPersistence()
    {
        string rust = Read("native/deskbox-search-core/src/lib.rs");
        string header = Read("native/include/deskbox_search_core.h");
        string bridge = Read("src/DeskBox/Services/SearchCoreNativeBackend.cs");

        foreach (string token in new[]
                 {
                     "DESKBOX_SEARCH_CORE_ABI_VERSION 3u",
                     "deskbox_search_core_mutate_batch_v1",
                     "deskbox_search_core_project_v1",
                     "deskbox_search_core_save_dbix_v1",
                     "DESKBOX_SEARCH_MUTATION_REMOVE_STALE_TREE",
                     "DESKBOX_SEARCH_PROJECTION_RECENT_FILES",
                     "DESKBOX_SEARCH_PROJECTION_FREQUENT_FOLDERS"
                 })
        {
            Assert.Contains(token, header, StringComparison.Ordinal);
        }

        Assert.Contains("fn mutate_batch(", rust, StringComparison.Ordinal);
        Assert.Contains("resulting_live_count", rust, StringComparison.Ordinal);
        Assert.Contains("affected_entries", rust, StringComparison.Ordinal);
        Assert.Contains("live_directory_ids", Read("native/deskbox-search-core/src/dbix.rs"), StringComparison.Ordinal);
        Assert.Contains("SearchCoreNativeOperationException", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductPreview_HasOneResidentOwnerAndExplicitManagedFallback()
    {
        string service = Read("src/DeskBox/Services/SearchIndexService.cs");
        string settings = Read("src/DeskBox/Models/AppSettings.cs");
        string settingsUi = Read("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml");

        Assert.Contains("SearchRustIndexerPreviewEnabled", settings, StringComparison.Ordinal);
        Assert.Contains("SearchRustPreviewToggle", settingsUi, StringComparison.Ordinal);
        Assert.Contains("TryActivateNativeLoadedIndex", service, StringComparison.Ordinal);
        Assert.Contains("HasSingleResidentBackend", service, StringComparison.Ordinal);
        Assert.Contains("DisposeNativeIndexLocked", service, StringComparison.Ordinal);
        Assert.Contains("_nativeIndex is { } nativeBackend", service, StringComparison.Ordinal);
        Assert.Contains("LoadPersistedIndexCore(cancellationToken)", service, StringComparison.Ordinal);
        Assert.Contains("Rust preview fallback", service, StringComparison.Ordinal);
        Assert.Contains("NativeMutationBatchSize = 8192", service, StringComparison.Ordinal);
        Assert.Contains("FlushNativeScanMutationsLocked", service, StringComparison.Ordinal);
        Assert.Contains("Compacted the Rust resident index", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductPackaging_IsDirectX64OnlyAndStage6DOwnsTheDefaultPolicy()
    {
        string project = Read("src/DeskBox/DeskBox.csproj");
        string defaults = Read("src/DeskBox/Services/SettingsService.cs");
        string audit = Read("scripts/publish-aot-audit.ps1");

        Assert.Contains("DeskBoxSearchCorePreviewModule", project, StringComparison.Ordinal);
        Assert.Contains("BuildDeskBoxSearchCorePreviewModule", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxSearchCorePreviewToOutput", project, StringComparison.Ordinal);
        Assert.Contains("CopyDeskBoxSearchCorePreviewToPublish", project, StringComparison.Ordinal);
        Assert.Contains("'$(DeskBoxDistribution)' != 'Store'", project, StringComparison.Ordinal);
        Assert.Contains("'$(Platform)' != 'ARM64'", project, StringComparison.Ordinal);
        Assert.Contains("'$(RuntimeIdentifier)' != 'win-arm64'", project, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSearchCoreDefaultEnabled", project, StringComparison.Ordinal);
        Assert.Contains("AppSettings.SearchRustIndexerDefaultEnabled", defaults, StringComparison.Ordinal);
        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSearchCorePreviewModule=$($searchCorePreviewEnabled", audit, StringComparison.Ordinal);
        Assert.Contains("searchCorePreview = [ordered]@{", audit, StringComparison.Ordinal);
        Assert.Contains("exactly one root-level deskbox_search_core.dll", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductMemoryMeasurement_UsesIsolatedFullGridClonesAndExactBackendEvidence()
    {
        string script = Read("scripts/run-search-core-stage-6c-product-memory.ps1");

        Assert.Contains("ConfiguredEnabledWidgetCount", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_DEV_DATA_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("Rust SearchCore preview backend", script, StringComparison.Ordinal);
        Assert.Contains("SourceInputsUnchanged", script, StringComparison.Ordinal);
        Assert.Contains("searchRustIndexerPreviewEnabled", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("PrivateBytesMedian", script, StringComparison.Ordinal);
        Assert.Contains("WorkingSetMedian", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLocale_ContainsRustPreviewAndBackendStatusKeys()
    {
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] files = Directory.GetFiles(stringsDirectory, "*.json");
        Assert.Equal(12, files.Length);

        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            foreach (string key in new[]
                     {
                         "Settings.Search.Index.RustPreview.Title",
                         "Settings.Search.Index.RustPreview.Description",
                         "Settings.Search.Index.Backend.Managed",
                         "Settings.Search.Index.Backend.Rust",
                         "Settings.Search.Index.Backend.Fallback",
                         "Settings.Search.Index.Backend.Preparing"
                     })
            {
                Assert.Contains($"\"{key}\"", content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Stage6C_DocumentsMeasuredProductBoundaryAndKeepsDefaultDecisionIn6D()
    {
        string abi = Read("docs/architecture/search-core-native-abi-v3.md");
        string report = Read("docs/architecture/rust-stage-6c-search-core-report.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");
        string nativeReadme = Read("native/README.md");

        foreach (string token in new[]
                 {
                     "ABI：3",
                     "一次 DeskBox 会话只允许一个 resident owner",
                     "Store 与 ARM64",
                     "阶段 6D"
                 })
        {
            Assert.Contains(token, abi, StringComparison.Ordinal);
        }

        Assert.Contains("207,925", report, StringComparison.Ordinal);
        Assert.Contains("Private Bytes | 269.23 MiB | 236.86 MiB | **12.02%**", report, StringComparison.Ordinal);
        Assert.Contains("Working Set | 387.36 MiB | 355.76 MiB | **8.16%**", report, StringComparison.Ordinal);
        Assert.Contains("10k 的 resident 差值", report, StringComparison.Ordinal);
        Assert.Contains("6D：SearchCore 预览 soak、故障恢复与默认决策门禁", report, StringComparison.Ordinal);
        Assert.Contains("Todo 通知中心真实点击", report, StringComparison.Ordinal);
        Assert.Contains("阶段 6C 状态校正", roadmap, StringComparison.Ordinal);
        Assert.Contains("SearchCore ABI version: `3`", nativeReadme, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

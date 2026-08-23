namespace DeskBox.Tests;

public sealed class SearchCoreStage6DContractTests
{
    [Fact]
    public void RuntimeFallback_CoversEveryResidentNativeOperationAndExplicitRetry()
    {
        string service = Read("src/DeskBox/Services/SearchIndexService.cs");
        string engine = Read("src/DeskBox/Services/SearchEngineService.cs");

        foreach (string token in new[]
                 {
                     "TryRecoverManagedIndexFromNativeFailure",
                     "IsRecoverableNativeRuntimeFailure",
                     "_rustPreviewSuppressedForSession",
                     "_nativeRuntimeRecoveryCount",
                     "Rust SearchCore runtime {operation} failure",
                     "\"query\"",
                     "\"recent projection\"",
                     "\"frequent projection\"",
                     "\"save\"",
                     "\"idle unload\"",
                     "\"upsert mutation\"",
                     "\"remove mutation\"",
                     "\"tree removal mutation\"",
                     "\"scan reconciliation\""
                 })
        {
            Assert.Contains(token, service, StringComparison.Ordinal);
        }

        Assert.Contains("ResetRustPreviewRuntimeFallback", engine, StringComparison.Ordinal);
        Assert.Contains("ReconfigureCustomIndexBackendAsync", engine, StringComparison.Ordinal);
        Assert.Contains("LoadPersistedIndexCore(CancellationToken.None)", service, StringComparison.Ordinal);
        Assert.Contains("ScheduleWatcherRecovery(\"rust-runtime-fallback\")", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ReliabilityMatrix_InjectsFaultsAndExercisesWatcherCompactionAndIdleReload()
    {
        string tests = Read("tests/DeskBox.Tests/SearchIndexServiceTests.cs");

        foreach (string testName in new[]
                 {
                     "RuntimeQueryFailure_RecoversManagedUntilExplicitRetry",
                     "RuntimeProjectionFailure_RecoversWithoutEmptyRecommendations",
                     "RuntimeSaveFailure_RecoversLastValidSnapshot",
                     "RuntimeIdleUnloadFailure_RecoversManagedOwner",
                     "RuntimeUpsertFailure_RecoversAndRetriesManagedMutation",
                     "RuntimeRemovalFailures_RecoversAndRetriesManagedMutations",
                     "RuntimeReconciliationFailure_RetainsSnapshotAndDefersFreshScan",
                     "MutationCompactionRenameTreeDeleteAndIdleSoak_PreservesLiveSet",
                     "RealWatcherRenameDeleteAndOverflowRecovery_RemainsExact"
                 })
        {
            Assert.Contains(testName, tests, StringComparison.Ordinal);
        }

        Assert.Contains("GetNativeTombstoneCount(service) >= 4096", tests, StringComparison.Ordinal);
        Assert.Contains("InternalBufferOverflowException", tests, StringComparison.Ordinal);
        Assert.Contains("WatcherRecoveryCount >= 1", tests, StringComparison.Ordinal);
        Assert.Contains("TryUnloadForIdleAsync", tests, StringComparison.Ordinal);

        string memoryRunner = Read("scripts/run-search-core-stage-6c-product-memory.ps1");
        Assert.Contains("[Math]::Floor($ordered.Count / 2.0)", memoryRunner, StringComparison.Ordinal);
        Assert.Contains("[string]$StageLabel = \"6C\"", memoryRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotSearchSurface_RequiresOwnedRustResultAndFullControlMatrix()
    {
        string app = Read("src/DeskBox/App.AotManagedUiSmoke.cs");
        string window = Read("src/DeskBox/Views/SearchPopupWindow.AotSmoke.cs");
        string runner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        foreach (string token in new[]
                 {
                     "SearchCorePreviewReadOnly",
                     "RustSearchCorePreviewActive",
                     "RustSearchCoreOwnedResult",
                     "ExpectedRustFilePresent",
                     "NativeRuntimeRecoveryCount"
                 })
        {
            Assert.Contains(token, app, StringComparison.Ordinal);
        }

        Assert.Contains("HasAotResultPath", window, StringComparison.Ordinal);
        Assert.Contains("ExerciseAotReadOnlyControls", window, StringComparison.Ordinal);
        Assert.Contains("Write-SearchCoreDbixFixture", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$settings[\"searchRustIndexerPreviewEnabled\"]",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("expectedRustFilePresent", runner, StringComparison.Ordinal);
        Assert.Contains("singleResidentBackend", runner, StringComparison.Ordinal);
        Assert.Contains("nativeRuntimeRecoveryCount -ne 0", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_RecordsStage6DAndDirectX64DefaultWithUnsupportedBuildsExcluded()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string settings = Read("src/DeskBox/Models/AppSettings.cs");
        string defaults = Read("src/DeskBox/Services/SettingsService.cs");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("runtimeFallback = \"managed-session-quarantine\"", audit, StringComparison.Ordinal);
        Assert.Contains("aotSearchScenario = \"SearchCorePreviewReadOnly\"", audit, StringComparison.Ordinal);
        Assert.Contains("defaultEnabled = $searchCorePreviewEnabled", audit, StringComparison.Ordinal);
        Assert.Contains("defaultPolicy = \"Direct-x64-module-build\"", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSearchCoreDefaultEnabled", project, StringComparison.Ordinal);
        Assert.Contains("'$(DeskBoxDistribution)' == 'Direct'", project, StringComparison.Ordinal);
        Assert.Contains("'$(Platform)' != 'ARM64'", project, StringComparison.Ordinal);
        Assert.Contains("'$(RuntimeIdentifier)' != 'win-arm64'", project, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SEARCH_CORE_DEFAULT", project, StringComparison.Ordinal);
        Assert.Contains("SearchRustIndexerDefaultEnabled = true", settings, StringComparison.Ordinal);
        Assert.Contains("AppSettings.SearchRustIndexerDefaultEnabled", defaults, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage6D_ReportClosesReliabilityMemoryAndDefaultGatesBeforeStage7()
    {
        string report = Read("docs/architecture/rust-stage-6d-search-core-report.md");
        string abi = Read("docs/architecture/search-core-native-abi-v3.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");
        string nativeReadme = Read("native/README.md");

        foreach (string token in new[]
                 {
                     "208,021",
                     "269.73 → 235.08 MiB",
                     "12.85%",
                     "388.00 → 351.67 MiB",
                     "9.36%",
                     "Direct x64",
                     "Store/ARM64",
                     "阶段 7",
                     "Todo 通知中心真实点击"
                 })
        {
            Assert.Contains(token, report, StringComparison.Ordinal);
        }

        Assert.Contains("会话内隔离", abi, StringComparison.Ordinal);
        Assert.Contains("阶段 6D 状态校正", roadmap, StringComparison.Ordinal);
        Assert.Contains("Stage 6D report", nativeReadme, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

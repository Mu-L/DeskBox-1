namespace DeskBox.Tests;

public sealed class SearchCoreStage6BContractTests
{
    [Fact]
    public void DirectDbixLoader_IsAtomicBoundedCancelableAndExplicitlyRecoverable()
    {
        string rust = Read("native/deskbox-search-core/src/dbix.rs");
        string bridge = Read("src/DeskBox/Services/SearchCoreNativeBackend.cs");
        string header = Read("native/include/deskbox_search_core.h");

        foreach (string token in new[]
                 {
                     "MAX_DBIX_FILE_BYTES",
                     "MAX_DBIX_STRING_BYTES",
                     "max_entry_count",
                     "WaitForSingleObject",
                     "DESKBOX_SEARCH_STATUS_CANCELLED",
                     "DESKBOX_SEARCH_STATUS_UNSUPPORTED_FORMAT",
                     "DESKBOX_SEARCH_STATUS_CORRUPT_DATA",
                     "directory_utf16",
                     "file_name_utf16",
                     "directory_lookup: None",
                     "trailing"
                 })
        {
            Assert.Contains(token, rust, StringComparison.Ordinal);
        }

        Assert.Contains("deskbox_search_core_open_dbix_v1", header, StringComparison.Ordinal);
        Assert.Contains("TryOpenDbix(", bridge, StringComparison.Ordinal);
        Assert.Contains("partial DBIX handle", bridge, StringComparison.Ordinal);
        Assert.Contains("rebuild/fallback is required", bridge, StringComparison.Ordinal);
        Assert.Contains("EventWaitHandle", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatedBenchmark_UsesSeparateProcessesAndRequiredScaleMatrix()
    {
        string script = Read("scripts/run-search-core-stage-6b.ps1");
        string program = Read("tools/DeskBox.SearchCore.Benchmarks/Program.cs");
        string runner = Read("tools/DeskBox.SearchCore.Benchmarks/BenchmarkRunner.cs");

        Assert.Contains("@(10000, 100000, 300000)", script, StringComparison.Ordinal);
        Assert.Contains("ProcessStartInfo", program, StringComparison.Ordinal);
        Assert.Contains("measure", program, StringComparison.Ordinal);
        Assert.Contains("managed", program, StringComparison.Ordinal);
        Assert.Contains("rust", program, StringComparison.Ordinal);
        Assert.Contains("ValidateComparison", program, StringComparison.Ordinal);
        Assert.Contains("SequenceEqual", program, StringComparison.Ordinal);
        Assert.Contains("BaselinePrivateBytes", runner, StringComparison.Ordinal);
        Assert.Contains("ResidentPrivateBytes", runner, StringComparison.Ordinal);
        Assert.Contains("PeakPrivateBytes", runner, StringComparison.Ordinal);
        Assert.Contains("QueryP50Milliseconds", runner, StringComparison.Ordinal);
        Assert.Contains("QueryP95Milliseconds", runner, StringComparison.Ordinal);
        Assert.Contains("CancellationLatencyMilliseconds", runner, StringComparison.Ordinal);
        Assert.Contains("NativeBuildLookupCapacityBytes", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage6B_RecordedTheDormantSingleIndexBoundaryBefore6C()
    {
        string stageReport = Read("docs/architecture/rust-stage-6b-search-core-report.md");
        string benchmarkProject = Read(
            "tools/DeskBox.SearchCore.Benchmarks/DeskBox.SearchCore.Benchmarks.csproj");

        Assert.Contains("默认搜索后端保持 C#，没有双索引常驻", stageReport, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", benchmarkProject, StringComparison.Ordinal);
        Assert.Contains("PlatformTarget>x64", benchmarkProject, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

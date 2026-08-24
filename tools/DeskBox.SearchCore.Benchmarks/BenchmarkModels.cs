namespace DeskBox.SearchCore.Benchmarks;

internal sealed record SearchHit(
    string DirectoryPath,
    string FileName,
    bool IsDirectory,
    long ModifiedUtcTicks,
    uint Score)
{
    internal string FullPath => string.IsNullOrEmpty(DirectoryPath)
        ? FileName
        : Path.Combine(DirectoryPath, FileName);
}

internal interface ISearchBackend : IDisposable
{
    int EntryCount { get; }

    int DirectoryCount { get; }

    ulong NativeTrackedCapacityBytes { get; }

    ulong NativeBuildLookupCapacityBytes { get; }

    IReadOnlyList<SearchHit> Search(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);
}

internal sealed record QuerySignature(
    string Query,
    int ResultCount,
    string Sha256);

internal sealed record SearchCoreProcessResult
{
    public required string Backend { get; init; }

    public required int EntryCount { get; init; }

    public required int DirectoryCount { get; init; }

    public required long SourceFileBytes { get; init; }

    public required double LoadMilliseconds { get; init; }

    public required long BaselinePrivateBytes { get; init; }

    public required long BaselineWorkingSetBytes { get; init; }

    public required long ResidentPrivateBytes { get; init; }

    public required long ResidentWorkingSetBytes { get; init; }

    public required long PeakPrivateBytes { get; init; }

    public required long PeakWorkingSetBytes { get; init; }

    public required long ManagedHeapBytes { get; init; }

    public required ulong NativeTrackedCapacityBytes { get; init; }

    public required ulong NativeBuildLookupCapacityBytes { get; init; }

    public required double QueryP50Milliseconds { get; init; }

    public required double QueryP95Milliseconds { get; init; }

    public required bool CancellationObserved { get; init; }

    public required double CancellationLatencyMilliseconds { get; init; }

    public required IReadOnlyList<QuerySignature> Signatures { get; init; }
}

internal sealed record SearchCoreComparison(
    int EntryCount,
    SearchCoreProcessResult Managed,
    SearchCoreProcessResult Rust,
    double ResidentPrivateReductionPercent,
    double PeakPrivateReductionPercent);

internal sealed record SearchCoreSuiteResult(
    int SchemaVersion,
    DateTime GeneratedAtUtc,
    string StageLabel,
    string ModulePath,
    IReadOnlyList<SearchCoreComparison> Comparisons);

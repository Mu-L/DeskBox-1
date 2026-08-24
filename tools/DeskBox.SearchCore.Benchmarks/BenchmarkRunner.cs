using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskBox.SearchCore.Benchmarks;

internal static class BenchmarkRunner
{
    private static readonly string[] Queries =
    [
        "report",
        "project",
        "文档",
        "σigma",
        "2026",
        "a"
    ];

    internal static void Measure(
        string backendName,
        string fixturePath,
        string modulePath,
        int expectedEntryCount,
        string outputPath)
    {
        ForceFullCollection();
        ProcessSnapshot baseline = ProcessSnapshot.Capture();
        using var sampler = new ProcessMemorySampler(baseline);
        Stopwatch loadWatch = Stopwatch.StartNew();
        using ISearchBackend backend = backendName switch
        {
            "managed" => ManagedSearchBackend.Open(fixturePath, DbixFixture.MaximumEntries),
            "rust" => NativeSearchBackend.Open(
                modulePath,
                fixturePath,
                DbixFixture.MaximumEntries),
            _ => throw new ArgumentOutOfRangeException(nameof(backendName))
        };
        loadWatch.Stop();
        if (backend.EntryCount != expectedEntryCount)
        {
            throw new InvalidDataException(
                $"{backendName} loaded {backend.EntryCount} entries; expected {expectedEntryCount}.");
        }
        if (backendName == "rust" && backend.NativeBuildLookupCapacityBytes != 0)
        {
            throw new InvalidDataException("Direct DBIX load retained build-only lookup capacity.");
        }

        ForceFullCollection();
        ProcessSnapshot resident = ProcessSnapshot.Capture();
        long managedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes;

        var signatures = new List<QuerySignature>(Queries.Length);
        foreach (string query in Queries)
        {
            IReadOnlyList<SearchHit> hits = backend.Search(query, 200);
            signatures.Add(CreateSignature(query, hits));
        }

        var samples = new List<double>(Queries.Length * 5);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            foreach (string query in Queries)
            {
                Stopwatch queryWatch = Stopwatch.StartNew();
                _ = backend.Search(query, 200);
                queryWatch.Stop();
                samples.Add(queryWatch.Elapsed.TotalMilliseconds);
            }
        }
        (bool cancellationObserved, double cancellationLatency) =
            MeasureCancellation(backend);
        sampler.Stop();

        samples.Sort();
        var result = new SearchCoreProcessResult
        {
            Backend = backendName,
            EntryCount = backend.EntryCount,
            DirectoryCount = backend.DirectoryCount,
            SourceFileBytes = new FileInfo(fixturePath).Length,
            LoadMilliseconds = loadWatch.Elapsed.TotalMilliseconds,
            BaselinePrivateBytes = baseline.PrivateBytes,
            BaselineWorkingSetBytes = baseline.WorkingSetBytes,
            ResidentPrivateBytes = resident.PrivateBytes,
            ResidentWorkingSetBytes = resident.WorkingSetBytes,
            PeakPrivateBytes = sampler.PeakPrivateBytes,
            PeakWorkingSetBytes = sampler.PeakWorkingSetBytes,
            ManagedHeapBytes = managedHeapBytes,
            NativeTrackedCapacityBytes = backend.NativeTrackedCapacityBytes,
            NativeBuildLookupCapacityBytes = backend.NativeBuildLookupCapacityBytes,
            QueryP50Milliseconds = Percentile(samples, 0.50),
            QueryP95Milliseconds = Percentile(samples, 0.95),
            CancellationObserved = cancellationObserved,
            CancellationLatencyMilliseconds = cancellationLatency,
            Signatures = signatures
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(result, JsonOptions.Indented));
    }

    private static (bool Observed, double Milliseconds) MeasureCancellation(
        ISearchBackend backend)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim(false);
            Task task = Task.Run(
                () =>
                {
                    entered.Set();
                    _ = backend.Search("a", 200, cancellation.Token);
                });
            entered.Wait();
            Stopwatch watch = Stopwatch.StartNew();
            cancellation.Cancel();
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                return (true, watch.Elapsed.TotalMilliseconds);
            }
            watch.Stop();
        }
        return (false, 0);
    }

    private static QuerySignature CreateSignature(
        string query,
        IReadOnlyList<SearchHit> hits)
    {
        var payload = new StringBuilder();
        foreach (SearchHit hit in hits)
        {
            payload.Append(hit.FullPath)
                .Append('\u001F')
                .Append(hit.IsDirectory ? 'D' : 'F')
                .Append('\u001F')
                .Append(hit.ModifiedUtcTicks)
                .Append('\u001F')
                .Append(hit.Score)
                .Append('\n');
        }
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
        return new QuerySignature(query, hits.Count, hash);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(sorted.Count * percentile) - 1,
            0,
            sorted.Count - 1);
        return sorted[index];
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}

internal readonly record struct ProcessSnapshot(long PrivateBytes, long WorkingSetBytes)
{
    internal static ProcessSnapshot Capture()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessSnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64);
    }
}

internal sealed class ProcessMemorySampler : IDisposable
{
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _peakPrivateBytes;
    private long _peakWorkingSetBytes;
    private int _stopped;

    internal ProcessMemorySampler(ProcessSnapshot baseline)
    {
        _peakPrivateBytes = baseline.PrivateBytes;
        _peakWorkingSetBytes = baseline.WorkingSetBytes;
        _thread = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "SearchCore benchmark memory sampler"
        };
        _thread.Start();
    }

    internal long PeakPrivateBytes => Interlocked.Read(ref _peakPrivateBytes);

    internal long PeakWorkingSetBytes => Interlocked.Read(ref _peakWorkingSetBytes);

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _stop.Set();
        _thread.Join();
        SampleOnce();
    }

    private void SampleLoop()
    {
        while (!_stop.Wait(5))
        {
            SampleOnce();
        }
    }

    private void SampleOnce()
    {
        ProcessSnapshot snapshot = ProcessSnapshot.Capture();
        UpdateMaximum(ref _peakPrivateBytes, snapshot.PrivateBytes);
        UpdateMaximum(ref _peakWorkingSetBytes, snapshot.WorkingSetBytes);
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };
}

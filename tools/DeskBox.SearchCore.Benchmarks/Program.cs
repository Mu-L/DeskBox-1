using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.SearchCore.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Expected 'suite' or 'measure'.");
            }
            Dictionary<string, string> options = ParseOptions(args.Skip(1));
            switch (args[0])
            {
                case "measure":
                    BenchmarkRunner.Measure(
                        Required(options, "backend"),
                        Required(options, "fixture"),
                        Required(options, "module"),
                        ParseCount(Required(options, "count")),
                        Required(options, "output"));
                    break;
                case "suite":
                    RunSuite(
                        Required(options, "module"),
                        Required(options, "output"),
                        ParseCounts(Required(options, "counts")),
                        Optional(options, "stage", "6B"));
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunSuite(
        string modulePath,
        string outputDirectory,
        IReadOnlyList<int> counts,
        string stageLabel)
    {
        if (stageLabel.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException("Stage labels may contain only ASCII letters, digits, dots and hyphens.");
        }

        string fullModulePath = Path.GetFullPath(modulePath);
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        string fixtureDirectory = Path.Combine(fullOutputDirectory, "fixtures");
        string processDirectory = Path.Combine(fullOutputDirectory, "process-results");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(processDirectory);

        var comparisons = new List<SearchCoreComparison>(counts.Count);
        foreach (int count in counts)
        {
            string fixturePath = Path.Combine(fixtureDirectory, $"search-{count}.dbix");
            DbixFixture.Generate(fixturePath, count);
            SearchCoreProcessResult managed = RunChild(
                "managed",
                fixturePath,
                fullModulePath,
                count,
                Path.Combine(processDirectory, $"managed-{count}.json"));
            SearchCoreProcessResult rust = RunChild(
                "rust",
                fixturePath,
                fullModulePath,
                count,
                Path.Combine(processDirectory, $"rust-{count}.json"));
            ValidateComparison(managed, rust, count);
            long managedResidentDelta = managed.ResidentPrivateBytes - managed.BaselinePrivateBytes;
            long rustResidentDelta = rust.ResidentPrivateBytes - rust.BaselinePrivateBytes;
            long managedPeakDelta = managed.PeakPrivateBytes - managed.BaselinePrivateBytes;
            long rustPeakDelta = rust.PeakPrivateBytes - rust.BaselinePrivateBytes;
            comparisons.Add(new SearchCoreComparison(
                count,
                managed,
                rust,
                ReductionPercent(managedResidentDelta, rustResidentDelta),
                ReductionPercent(managedPeakDelta, rustPeakDelta)));
        }

        var suite = new SearchCoreSuiteResult(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTime.UtcNow,
            StageLabel: stageLabel,
            ModulePath: fullModulePath,
            Comparisons: comparisons);
        File.WriteAllText(
            Path.Combine(fullOutputDirectory, "summary.json"),
            JsonSerializer.Serialize(suite, JsonOptions.Indented));
        File.WriteAllText(
            Path.Combine(fullOutputDirectory, "summary.md"),
            BuildMarkdown(suite));
    }

    private static SearchCoreProcessResult RunChild(
        string backend,
        string fixturePath,
        string modulePath,
        int count,
        string outputPath)
    {
        string assemblyPath = typeof(Program).Assembly.Location;
        string hostPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to locate the dotnet host.");
        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("measure");
        AddOption(startInfo, "backend", backend);
        AddOption(startInfo, "fixture", fixturePath);
        AddOption(startInfo, "module", modulePath);
        AddOption(startInfo, "count", count.ToString(CultureInfo.InvariantCulture));
        AddOption(startInfo, "output", outputPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start isolated benchmark process.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{backend} benchmark for {count} entries failed with exit code {process.ExitCode}.\n{stdout}\n{stderr}");
        }
        return JsonSerializer.Deserialize<SearchCoreProcessResult>(
                   File.ReadAllText(outputPath),
                   JsonOptions.Indented)
               ?? throw new InvalidDataException("Benchmark child returned empty JSON.");
    }

    private static void ValidateComparison(
        SearchCoreProcessResult managed,
        SearchCoreProcessResult rust,
        int expectedCount)
    {
        if (managed.EntryCount != expectedCount || rust.EntryCount != expectedCount)
        {
            throw new InvalidDataException("Benchmark entry counts differ from the fixture.");
        }
        if (managed.DirectoryCount != rust.DirectoryCount)
        {
            throw new InvalidDataException("Managed and Rust directory counts differ.");
        }
        if (!managed.Signatures.SequenceEqual(rust.Signatures))
        {
            throw new InvalidDataException(
                $"Managed and Rust query signatures differ at {expectedCount} entries.");
        }
        if (rust.NativeBuildLookupCapacityBytes != 0)
        {
            throw new InvalidDataException("Rust DBIX direct load retained build lookup memory.");
        }
    }

    private static string BuildMarkdown(SearchCoreSuiteResult suite)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"# DeskBox SearchCore Stage {suite.StageLabel} isolated benchmark")
            .AppendLine()
            .AppendLine($"Generated UTC: {suite.GeneratedAtUtc:O}")
            .AppendLine()
            .AppendLine("Resident and peak private values below are deltas from each isolated process baseline.")
            .AppendLine()
            .AppendLine("| Entries | Managed resident MiB | Rust resident MiB | Resident reduction | Managed peak MiB | Rust peak MiB | Peak reduction | Managed load ms | Rust load ms | Managed P95 ms | Rust P95 ms | Rust tracked MiB |")
            .AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (SearchCoreComparison comparison in suite.Comparisons)
        {
            SearchCoreProcessResult managed = comparison.Managed;
            SearchCoreProcessResult rust = comparison.Rust;
            markdown.Append("| ").Append(comparison.EntryCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(ToMiB(managed.ResidentPrivateBytes - managed.BaselinePrivateBytes))
                .Append(" | ").Append(ToMiB(rust.ResidentPrivateBytes - rust.BaselinePrivateBytes))
                .Append(" | ").Append(comparison.ResidentPrivateReductionPercent.ToString("F1", CultureInfo.InvariantCulture)).Append('%')
                .Append(" | ").Append(ToMiB(managed.PeakPrivateBytes - managed.BaselinePrivateBytes))
                .Append(" | ").Append(ToMiB(rust.PeakPrivateBytes - rust.BaselinePrivateBytes))
                .Append(" | ").Append(comparison.PeakPrivateReductionPercent.ToString("F1", CultureInfo.InvariantCulture)).Append('%')
                .Append(" | ").Append(managed.LoadMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ").Append(rust.LoadMilliseconds.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ").Append(managed.QueryP95Milliseconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" | ").Append(rust.QueryP95Milliseconds.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" | ").Append(ToMiB((long)rust.NativeTrackedCapacityBytes))
                .AppendLine(" |");
        }
        markdown.AppendLine()
            .AppendLine("All query result signatures must match before this report is emitted. Cancellation and raw process values remain in summary.json.");
        return markdown.ToString();
    }

    private static double ReductionPercent(long managedBytes, long rustBytes) =>
        managedBytes <= 0
            ? 0
            : (managedBytes - rustBytes) * 100.0 / managedBytes;

    private static string ToMiB(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture);

    private static void AddOption(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add("--" + name);
        info.ArgumentList.Add(value);
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
    {
        string[] values = arguments.ToArray();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < values.Length; index += 2)
        {
            if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use '--name value' pairs.");
            }
            options.Add(values[index][2..], values[index + 1]);
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");

    private static string Optional(
        IReadOnlyDictionary<string, string> options,
        string name,
        string defaultValue) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private static IReadOnlyList<int> ParseCounts(string value)
    {
        int[] counts = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseCount)
            .Distinct()
            .Order()
            .ToArray();
        if (counts.Length == 0)
        {
            throw new ArgumentException("At least one entry count is required.");
        }
        return counts;
    }

    private static int ParseCount(string value)
    {
        int count = int.Parse(value, CultureInfo.InvariantCulture);
        return count is > 0 and <= DbixFixture.MaximumEntries
            ? count
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}

using System.Text;

namespace DeskBox.SearchCore.Benchmarks;

internal static class DbixFixture
{
    internal const int Magic = 0x58494244;
    internal const int Version = 1;
    internal const int MaximumEntries = 300_000;
    private const long BaseUtcTicks = 638_900_000_000_000_000;

    internal static void Generate(string path, int entryCount)
    {
        if (entryCount <= 0 || entryCount > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount));
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        int directoryCount = Math.Min(4_096, Math.Max(1, entryCount / 50));
        string[] directories = Enumerable.Range(0, directoryCount)
            .Select(index => $@"C:\DeskBoxStage6B\Volume{index % 8}\Projects\Project{index:D5}\Artifacts")
            .ToArray();

        using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(BaseUtcTicks);
        writer.Write(directories.Length);
        foreach (string directory in directories)
        {
            writer.Write(directory);
        }

        writer.Write(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            writer.Write(index % directories.Length);
            string fileName = GetFileName(index);
            byte[] utf8 = Encoding.UTF8.GetBytes(fileName);
            writer.Write(utf8.Length);
            writer.Write(utf8);
            writer.Write(index % 29 == 0);
            long ticks = BaseUtcTicks - (long)index * 10_000;
            writer.Write(new DateTime(ticks, DateTimeKind.Utc).ToBinary());
        }
    }

    internal static string GetFileName(int index) => (index % 8) switch
    {
        0 => $"report_{index:D6}.pdf",
        1 => $"project_notes_{index:D6}.md",
        2 => $"photo_{index:D6}.jpg",
        3 => $"文档_{index:D6}.docx",
        4 => $"Σigma_{index:D6}.txt",
        5 => $"archive_2026_{index:D6}.zip",
        6 => $"music_{index:D6}.mp3",
        _ => $"misc_{index:D6}.bin"
    };
}

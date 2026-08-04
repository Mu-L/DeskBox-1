namespace DeskBox.Services;

public sealed class DeskBoxDataPathService
{
    public static DeskBoxDataPathService Current { get; } = new();

    public DeskBoxDataPathService(string? rootPath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBox")
            : rootPath;
    }

    public string RootPath { get; }
    public string DataDirectory => Path.Combine(RootPath, "data");
    public string UpdatesDirectory => Path.Combine(RootPath, "updates");
    // Recovery snapshots intentionally live beside, rather than inside, the
    // app-data root. A normal uninstall can therefore never erase the only
    // automatic recovery copy together with settings and widget layouts.
    public string RecoveryDirectory => Path.Combine(
        Path.GetDirectoryName(RootPath) ?? RootPath,
        "DeskBox-Recovery");
    public string LogFilePath => Path.Combine(RootPath, "DeskBox.log");
}

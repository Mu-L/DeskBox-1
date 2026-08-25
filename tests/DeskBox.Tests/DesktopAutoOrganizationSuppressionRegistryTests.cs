using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopAutoOrganizationSuppressionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PendingRestore_IsConsumedOnlyForTheExactDestination()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.txt");
        string destination = Path.Combine(_root, "desktop", "source.txt");
        var registry = new DesktopAutoOrganizationSuppressionRegistry();
        registry.BeginOperation(
            "restore",
            [new FileService.FileTransferPlan(source, destination)]);

        Assert.False(registry.TryConsume(Path.Combine(_root, "desktop", "other.txt")));
        Assert.True(registry.TryConsume(destination));
        Assert.False(registry.TryConsume(destination));
    }

    [Fact]
    public void CompletedRestore_RequiresTheSameFileFingerprint()
    {
        Directory.CreateDirectory(_root);
        string destination = Path.Combine(_root, "restored.txt");
        File.WriteAllText(destination, "original");
        var registry = new DesktopAutoOrganizationSuppressionRegistry();
        var plan = new FileService.FileTransferPlan(
            Path.Combine(_root, "source.txt"),
            destination);
        registry.BeginOperation("restore", [plan]);
        registry.CompleteOperation("restore", [destination]);
        File.AppendAllText(destination, " replacement");

        Assert.False(registry.TryConsume(destination));
    }

    [Fact]
    public void ExpiredRestore_IsNotConsumed()
    {
        Directory.CreateDirectory(_root);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string destination = Path.Combine(_root, "expired.txt");
        var registry = new DesktopAutoOrganizationSuppressionRegistry(
            () => now,
            TimeSpan.FromSeconds(5));
        registry.BeginOperation(
            "restore",
            [new FileService.FileTransferPlan("source", destination)]);
        now += TimeSpan.FromSeconds(6);

        Assert.False(registry.TryConsume(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

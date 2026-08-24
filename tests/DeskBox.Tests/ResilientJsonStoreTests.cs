using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ResilientJsonStoreTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N")))
        .FullName;

    [Fact]
    public async Task SaveAsync_UnableToRemoveReplacedFile_UsesVerifiedInPlaceFallback()
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"updated\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        int replaceAttempts = 0;

        await ResilientJsonStore.SaveAsync(
            storePath,
            updatedJson,
            (_, _, _, _) =>
            {
                replaceAttempts++;
                throw CreateUnableToRemoveReplacedFileException();
            },
            _ => Task.CompletedTask);

        Assert.Equal(3, replaceAttempts);
        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_OtherReplaceFailure_PropagatesWithoutChangingPrimary()
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);
        var expected = new IOException("sharing violation", unchecked((int)0x80070020));

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            ResilientJsonStore.SaveAsync(
                storePath,
                "{\"value\":\"updated\"}",
                (_, _, _, _) => throw expected,
                _ => Task.CompletedTask));

        Assert.Same(expected, actual);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_UnableToRemoveAfterPartialReplace_DoesNotUseFallback()
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        await Assert.ThrowsAsync<IOException>(() =>
            ResilientJsonStore.SaveAsync(
                storePath,
                "{\"value\":\"updated\"}",
                (sourcePath, _, _, _) =>
                {
                    File.Delete(sourcePath);
                    throw CreateUnableToRemoveReplacedFileException();
                },
                _ => Task.CompletedTask));

        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(storePath)));
    }

    [Fact]
    public async Task SaveAsync_NormalReplace_PreservesPreviousVersionAsBackup()
    {
        string storePath = Path.Combine(_tempRoot, "settings.json");
        const string originalJson = "{\"value\":\"original\"}";
        const string updatedJson = "{\"value\":\"updated\"}";
        await File.WriteAllTextAsync(storePath, originalJson);

        await ResilientJsonStore.SaveAsync(storePath, updatedJson);

        Assert.Equal(updatedJson, await File.ReadAllTextAsync(storePath));
        Assert.Equal(
            originalJson,
            await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(storePath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static IOException CreateUnableToRemoveReplacedFileException() =>
        new(
            "unable to remove replaced file",
            ResilientJsonStore.UnableToRemoveReplacedFileHResult);
}

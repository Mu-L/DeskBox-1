namespace DeskBox.Services;

internal enum ResilientJsonLoadSource
{
    Primary,
    Backup,
    DefaultMissing,
    DefaultAfterFailure
}

internal sealed record ResilientJsonLoadResult<T>(
    T Value,
    ResilientJsonLoadSource Source);

internal static class ResilientJsonStore
{
    internal static string GetBackupPath(string storePath) => $"{storePath}.bak";

    public static async Task<T> LoadAsync<T>(
        string storePath,
        Func<string, T> deserialize,
        Func<T> createDefault,
        string logName)
    {
        ResilientJsonLoadResult<T> result = await LoadWithResultAsync(
            storePath,
            deserialize,
            createDefault,
            logName);
        return result.Value;
    }

    public static async Task<ResilientJsonLoadResult<T>> LoadWithResultAsync<T>(
        string storePath,
        Func<string, T> deserialize,
        Func<T> createDefault,
        string logName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(createDefault);

        bool primaryFailed = false;
        if (File.Exists(storePath))
        {
            try
            {
                return new ResilientJsonLoadResult<T>(
                    deserialize(await File.ReadAllTextAsync(storePath)),
                    ResilientJsonLoadSource.Primary);
            }
            catch (Exception ex)
            {
                primaryFailed = true;
                App.Log($"[{logName}] Primary store is invalid: {ex}");
                QuarantineCorruptFile(storePath, logName);
            }
        }

        string backupPath = GetBackupPath(storePath);
        if (!File.Exists(backupPath))
        {
            return new ResilientJsonLoadResult<T>(
                createDefault(),
                primaryFailed
                    ? ResilientJsonLoadSource.DefaultAfterFailure
                    : ResilientJsonLoadSource.DefaultMissing);
        }

        T recovered;
        try
        {
            string backupJson = await File.ReadAllTextAsync(backupPath);
            recovered = deserialize(backupJson);

            try
            {
                await RestorePrimaryAsync(storePath, backupJson);
                App.Log($"[{logName}] Restored store from '{backupPath}'.");
            }
            catch (Exception restoreException)
            {
                // A read-only or temporarily locked data directory must not
                // turn a valid backup into an apparent total settings loss.
                App.Log(
                    $"[{logName}] Backup loaded, but primary restore failed: " +
                    restoreException);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Backup store is invalid: {ex}");
            return new ResilientJsonLoadResult<T>(
                createDefault(),
                ResilientJsonLoadSource.DefaultAfterFailure);
        }

        return new ResilientJsonLoadResult<T>(
            recovered,
            ResilientJsonLoadSource.Backup);
    }

    public static async Task SaveAsync(string storePath, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(json);

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            if (File.Exists(storePath))
            {
                File.Replace(
                    tempPath,
                    storePath,
                    GetBackupPath(storePath),
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, storePath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void QuarantineCorruptFile(string storePath, string logName)
    {
        string corruptPath = $"{storePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(storePath, corruptPath);
            App.Log($"[{logName}] Preserved corrupt store as '{corruptPath}'.");
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Failed to quarantine corrupt store: {ex}");
        }
    }

    private static async Task RestorePrimaryAsync(string storePath, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

using DeskBox.Helpers;
using Microsoft.Win32;

namespace DeskBox.Services;

public sealed class DirectStartupService : IStartupService
{
    private const string AppName = "DeskBox";
    private readonly IDirectStartupTaskBackend _taskBackend;
    private readonly IDirectStartupRunEntryStore _runEntryStore;
    private readonly Func<string?> _executablePathProvider;
    private readonly string? _legacyShortcutPath;
    private readonly Func<string, string?> _shortcutTargetReader;
    private readonly Action<string> _shortcutDelete;
    private readonly Action<string> _log;
    private readonly Func<bool> _runEntryApprovedProvider;

    public DirectStartupService()
        : this(
            new DirectStartupTaskBackend(),
            new RegistryStartupRunEntryStore(),
            () => Environment.ProcessPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                AppName + ".lnk"),
            path => ShortcutHelper.ReadStoredMetadata(path)?.TargetPath,
            File.Delete,
            null,
            null)
    {
    }

    internal DirectStartupService(
        IDirectStartupTaskBackend taskBackend,
        IDirectStartupRunEntryStore runEntryStore,
        Func<string?> executablePathProvider,
        string? legacyShortcutPath = null,
        Func<string, string?>? shortcutTargetReader = null,
        Action<string>? shortcutDelete = null,
        Action<string>? logger = null,
        Func<bool>? runEntryApprovedProvider = null)
    {
        _taskBackend = taskBackend;
        _runEntryStore = runEntryStore;
        _executablePathProvider = executablePathProvider;
        _legacyShortcutPath = legacyShortcutPath;
        _shortcutTargetReader = shortcutTargetReader ?? (_ => null);
        _shortcutDelete = shortcutDelete ?? (_ => { });
        _log = logger ?? (message =>
            global::DeskBox.App.Log($"[DirectStartupService] {message}"));
        _runEntryApprovedProvider = runEntryApprovedProvider ?? IsRunEntryApproved;
    }

    public StartupRegistrationState GetState()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return StartupRegistrationState.BlockedOrFailed;
            }

            // The Run entry is the primary registration: visible in Windows'
            // Startup apps and user-toggleable there. When the user disables
            // DeskBox in that UI, the registry value survives but Windows marks
            // it disapproved. That choice is authoritative even if a legacy
            // scheduled task still exists.
            string? runValue = _runEntryStore.Read();
            if (IsCommandOwnedBy(runValue, executablePath))
            {
                return _runEntryApprovedProvider()
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.DisabledByUser;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null && task.Enabled && task.IsOwnedBy(executablePath))
            {
                return StartupRegistrationState.Enabled;
            }

            if (!string.IsNullOrWhiteSpace(runValue) || task is not null)
            {
                return StartupRegistrationState.PathMismatch;
            }

            return StartupRegistrationState.NotRegistered;
        }
        catch
        {
            return StartupRegistrationState.BlockedOrFailed;
        }
    }

    public bool IsEnabled() => GetState() == StartupRegistrationState.Enabled;

    public string? GetRunValue()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            string? runValue = _runEntryStore.Read();
            if (executablePath is not null &&
                IsCommandOwnedBy(runValue, executablePath))
            {
                return runValue;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (executablePath is not null &&
                task is not null &&
                task.Enabled &&
                task.IsOwnedBy(executablePath))
            {
                return task.CommandLine;
            }

            return runValue ?? task?.CommandLine;
        }
        catch
        {
            return null;
        }
    }

    public StartupOperationResult Enable()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                const string message =
                    "Cannot enable startup: the executable path is unavailable.";
                Log(message);
                return new StartupOperationResult(
                    StartupRegistrationState.BlockedOrFailed,
                    message);
            }

            StartupOperationResult runResult = TryEnableRunEntry(executablePath);
            if (runResult.State == StartupRegistrationState.Enabled)
            {
                string cleanupError = RemoveOwnedAlternativeRegistrations(executablePath);
                Log("Startup enabled through the per-user Run entry");
                return new StartupOperationResult(
                    StartupRegistrationState.Enabled,
                    cleanupError);
            }

            if (runResult.State == StartupRegistrationState.DisabledByUser)
            {
                // Do not silently bypass Windows' Startup apps choice with an
                // older task or Startup-folder shortcut.
                string cleanupError = RemoveOwnedAlternativeRegistrations(executablePath);
                string error = CombineErrors(runResult.ErrorMessage, cleanupError);
                Log(
                    "Startup remains disabled by Windows Startup apps; " +
                    "the user must re-enable DeskBox there.");
                return new StartupOperationResult(
                    StartupRegistrationState.DisabledByUser,
                    error);
            }

            if (TryEnableScheduledTask(executablePath))
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
                return new StartupOperationResult(StartupRegistrationState.Enabled);
            }

            string failure =
                "Startup could not be enabled: the Run entry was unavailable " +
                $"and task registration failed: {_taskBackend.LastError}";
            Log(failure);
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                CombineErrors(runResult.ErrorMessage, failure));
        }
        catch (Exception ex)
        {
            Log($"Failed to enable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public StartupOperationResult Disable()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                const string message =
                    "Cannot disable startup: the executable path is unavailable.";
                Log(message);
                return new StartupOperationResult(
                    StartupRegistrationState.BlockedOrFailed,
                    message);
            }

            List<string> failures = [];
            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null && task.IsOwnedBy(executablePath) && !_taskBackend.TryDelete())
            {
                string failure =
                    $"Failed to delete the owned startup task: {_taskBackend.LastError}";
                failures.Add(failure);
                Log(failure);
            }

            try
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
            }
            catch (Exception ex)
            {
                failures.Add($"Failed to delete the owned Run entry: {ex.Message}");
            }

            if (!DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out string shortcutError))
            {
                failures.Add(shortcutError);
            }

            Log("Startup disabled");
            StartupRegistrationState state = GetState();
            return new StartupOperationResult(state, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            Log($"Failed to disable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    /// <summary>
    /// Migrates an owned scheduled task or Startup-folder shortcut to the
    /// per-user Run entry after it has been written and read back successfully.
    /// A failed migration deliberately leaves the old registration untouched.
    /// </summary>
    internal void TryMigrateLegacyRegistration()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return;
            }

            DirectStartupTaskRegistration? existingTask = _taskBackend.Read();
            bool ownsTask = existingTask is not null &&
                            existingTask.IsOwnedBy(executablePath);
            string? existingRunValue = _runEntryStore.Read();
            bool ownsRunEntry = IsCommandOwnedBy(existingRunValue, executablePath);
            bool ownsShortcut = IsLegacyShortcutOwnedBy(executablePath);
            if (!ownsTask && !ownsRunEntry && !ownsShortcut)
            {
                return;
            }

            bool hasPreferredRunEntry =
                IsPreferredRunCommand(existingRunValue, executablePath);
            if (ownsRunEntry && hasPreferredRunEntry)
            {
                if (!ownsTask && !ownsShortcut)
                {
                    // The common steady state. Avoid rewriting the Run entry or
                    // emitting a migration log on every application launch.
                    return;
                }

                StartupRegistrationState currentState = _runEntryApprovedProvider()
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.DisabledByUser;
                string cleanupError = RemoveOwnedAlternativeRegistrations(executablePath);
                if (currentState == StartupRegistrationState.Enabled)
                {
                    Log("Migrated startup registration to the per-user Run entry");
                }
                else
                {
                    Log(
                        "Removed legacy startup registrations while preserving " +
                        "the Windows-disabled Startup apps state.");
                }

                if (!string.IsNullOrWhiteSpace(cleanupError))
                {
                    Log($"Startup migration cleanup was incomplete: {cleanupError}");
                }

                return;
            }

            StartupOperationResult runResult = TryEnableRunEntry(executablePath);
            if (runResult.State is not
                (StartupRegistrationState.Enabled or
                 StartupRegistrationState.DisabledByUser))
            {
                if (ownsTask)
                {
                    Log(
                        "Startup migration deferred: the Run entry is unavailable, " +
                        $"the scheduled task remains: {runResult.ErrorMessage}");
                }

                return;
            }

            string migrationCleanupError =
                RemoveOwnedAlternativeRegistrations(executablePath);
            if (runResult.State == StartupRegistrationState.Enabled)
            {
                Log("Migrated startup registration to the per-user Run entry");
            }
            else
            {
                Log(
                    "Startup migration preserved the Windows-disabled Startup " +
                    "apps state and removed legacy launch mechanisms.");
            }

            if (!string.IsNullOrWhiteSpace(migrationCleanupError))
            {
                Log($"Startup migration cleanup was incomplete: {migrationCleanupError}");
            }
        }
        catch (Exception ex)
        {
            Log($"Legacy startup migration failed and was preserved: {ex.Message}");
        }
    }

    private StartupOperationResult TryEnableRunEntry(string executablePath)
    {
        string? existing = _runEntryStore.Read();
        if (!string.IsNullOrWhiteSpace(existing) &&
            !IsCommandOwnedBy(existing, executablePath))
        {
            if (CommandTargetExists(existing))
            {
                Log($"Preserved Run entry owned by another installation: '{existing}'");
                return new StartupOperationResult(
                    StartupRegistrationState.PathMismatch,
                    "The Run entry belongs to another DeskBox installation.");
            }

            Log(
                $"Taking over the orphaned Run entry pointing at a missing target: '{existing}'");
        }

        try
        {
            string desiredCommand = BuildRunCommand(executablePath);
            if (!string.Equals(
                    existing?.Trim(),
                    desiredCommand,
                    StringComparison.OrdinalIgnoreCase))
            {
                _runEntryStore.Write(desiredCommand);
            }

            string? writtenValue = _runEntryStore.Read();
            if (!IsPreferredRunCommand(writtenValue, executablePath))
            {
                const string message =
                    "The per-user Run entry could not be verified after writing.";
                Log(message);
                return new StartupOperationResult(
                    StartupRegistrationState.BlockedOrFailed,
                    message);
            }

            if (!_runEntryApprovedProvider())
            {
                return new StartupOperationResult(
                    StartupRegistrationState.DisabledByUser,
                    "Windows Startup apps has disabled the DeskBox Run entry.");
            }

            return new StartupOperationResult(StartupRegistrationState.Enabled);
        }
        catch (Exception ex)
        {
            Log($"The per-user Run entry could not be written: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    private bool TryEnableScheduledTask(string executablePath)
    {
        DirectStartupTaskRegistration? existing = _taskBackend.Read();
        if (existing is not null && !existing.IsOwnedBy(executablePath))
        {
            if (File.Exists(existing.ExecutablePath))
            {
                Log(
                    $"Preserved startup task owned by another installation: " +
                    $"'{existing.ExecutablePath}'");
                return false;
            }

            Log(
                $"Taking over the orphaned startup task pointing at a missing " +
                $"target: '{existing.ExecutablePath}'");
        }

        if (existing is not null && _taskBackend.IsPreferred(existing, executablePath))
        {
            return true;
        }

        bool registered = _taskBackend.TryRegister(executablePath);
        if (!registered)
        {
            Log($"Failed to register the preferred startup task: {_taskBackend.LastError}");
        }
        return registered;
    }

    /// <summary>
    /// Windows' Startup apps page disables entries by flipping a bit under
    /// Explorer\StartupApproved instead of deleting the Run value; the entry
    /// counts as enabled unless that state explicitly disables it.
    /// </summary>
    private static bool IsRunEntryApproved()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                writable: false);
            if (key?.GetValue(AppName) is byte[] state && state.Length > 0)
            {
                return (state[0] & 1) == 0;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool CommandTargetExists(string commandLine)
    {
        string? target = ExtractExecutablePath(commandLine);
        return !string.IsNullOrWhiteSpace(target) && File.Exists(target);
    }

    private static string BuildRunCommand(string executablePath) =>
        $"\"{executablePath}\" --startup";

    private static bool IsPreferredRunCommand(
        string? commandLine,
        string executablePath) =>
        string.Equals(
            commandLine?.Trim(),
            BuildRunCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);

    private string RemoveOwnedAlternativeRegistrations(string executablePath)
    {
        List<string> failures = [];
        DirectStartupTaskRegistration? task = _taskBackend.Read();
        if (task is not null &&
            task.IsOwnedBy(executablePath) &&
            !_taskBackend.TryDelete())
        {
            string failure =
                $"Failed to remove the superseded startup task: {_taskBackend.LastError}";
            failures.Add(failure);
            Log(failure);
        }

        if (!DeleteLegacyStartupShortcutIfOwnedBy(
                executablePath,
                out string shortcutError))
        {
            failures.Add(shortcutError);
        }

        return string.Join("; ", failures);
    }

    private static string CombineErrors(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first}; {second}";
    }

    private void DeleteLegacyRunEntryIfOwnedBy(string executablePath)
    {
        if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath))
        {
            _runEntryStore.Delete();
        }
    }

    private bool IsLegacyShortcutOwnedBy(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(_legacyShortcutPath) ||
            !File.Exists(_legacyShortcutPath))
        {
            return false;
        }

        try
        {
            return DirectStartupTaskBackend.PathsEqual(
                _shortcutTargetReader(_legacyShortcutPath),
                executablePath);
        }
        catch
        {
            return false;
        }
    }

    private void DeleteLegacyStartupShortcutIfOwnedBy(string executablePath) =>
        _ = DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out _);

    private bool DeleteLegacyStartupShortcutIfOwnedBy(
        string executablePath,
        out string error)
    {
        error = string.Empty;
        if (!IsLegacyShortcutOwnedBy(executablePath) ||
            string.IsNullOrWhiteSpace(_legacyShortcutPath))
        {
            return true;
        }

        try
        {
            _shortcutDelete(_legacyShortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            error =
                $"Failed to delete the owned legacy startup shortcut: {ex.Message}";
            Log(error);
            return false;
        }
    }

    private string? GetExecutablePath()
    {
        string? executablePath = _executablePathProvider();
        return string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetFullPath(executablePath);
    }

    internal static bool IsCommandOwnedBy(
        string? commandLine,
        string executablePath)
    {
        string? commandExecutablePath = ExtractExecutablePath(commandLine);
        return DirectStartupTaskBackend.PathsEqual(
            commandExecutablePath,
            executablePath);
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        int separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private void Log(string message) => _log(message);

    public StartupOperationResult SetEnabled(bool enabled)
    {
        if (enabled)
        {
            return Enable();
        }

        return Disable();
    }
}

internal interface IDirectStartupRunEntryStore
{
    string? Read();

    void Write(string commandLine);

    void Delete();
}

internal sealed class RegistryStartupRunEntryStore : IDirectStartupRunEntryStore
{
    private const string RegistryKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DeskBox";

    public string? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeyPath,
            writable: false);
        return key?.GetValue(AppName) as string;
    }

    public void Write(string commandLine)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            RegistryKeyPath,
            writable: true);
        key.SetValue(AppName, commandLine, RegistryValueKind.String);
    }

    public void Delete()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeyPath,
            writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}

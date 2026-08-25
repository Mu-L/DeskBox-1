using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace DeskBox.Services;

internal sealed record DirectStartupTaskRegistration(
    string ExecutablePath,
    string Arguments,
    string PrincipalUserId,
    string TriggerUserId,
    string LogonType,
    string RunLevel,
    int Priority,
    bool Enabled,
    string ExecutionTimeLimit,
    string MultipleInstancesPolicy,
    bool StartWhenAvailable,
    bool DisallowStartIfOnBatteries,
    bool StopIfGoingOnBatteries,
    bool RunOnlyIfIdle,
    string TriggerDelay)
{
    public string CommandLine =>
        $"\"{ExecutablePath}\" {Arguments}".TrimEnd();

    public bool IsOwnedBy(string executablePath) =>
        DirectStartupTaskBackend.PathsEqual(ExecutablePath, executablePath);
}

internal interface IDirectStartupTaskBackend
{
    string LastError { get; }

    DirectStartupTaskRegistration? Read();

    bool IsPreferred(
        DirectStartupTaskRegistration registration,
        string executablePath);

    bool TryRegister(string executablePath);

    bool TryDelete();
}

/// <summary>
/// Registers the direct-distribution startup entry with Task Scheduler 2.0 by
/// using the inbox schtasks.exe client. This stays Native AOT friendly and runs
/// only when startup registration is queried or changed; it adds no resident
/// helper process or service to DeskBox.
/// </summary>
internal sealed class DirectStartupTaskBackend : IDirectStartupTaskBackend
{
    internal const string TaskName = "DeskBox User Startup";
    internal const string StartupArguments =
        "--startup --startup-source=scheduled-task";
    internal const int InteractiveTaskPriority = 4;
    private const int SchtasksTimeoutMilliseconds = 10_000;
    private static readonly XNamespace TaskNamespace =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public string LastError { get; private set; } = string.Empty;

    public DirectStartupTaskRegistration? Read()
    {
        LastError = string.Empty;
        SchtasksResult result = RunSchtasks("/Query", "/TN", TaskName, "/XML");
        if (result.ExitCode != 0)
        {
            LastError = FormatFailure("query", result);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            LastError = "Task query succeeded but returned no XML.";
            return null;
        }

        try
        {
            return ParseTaskXml(result.StandardOutput);
        }
        catch (Exception ex)
        {
            LastError = $"Task XML could not be parsed: {ex.Message}";
            return null;
        }
    }

    public bool IsPreferred(
        DirectStartupTaskRegistration registration,
        string executablePath)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string currentUserSid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        string currentUserName = identity.Name;
        return registration.IsOwnedBy(executablePath) &&
               registration.Enabled &&
               string.Equals(
                   registration.Arguments.Trim(),
                   StartupArguments,
                   StringComparison.OrdinalIgnoreCase) &&
               IsCurrentUserId(
                   registration.PrincipalUserId,
                   currentUserSid,
                   currentUserName) &&
               IsCurrentUserId(
                   registration.TriggerUserId,
                   currentUserSid,
                   currentUserName) &&
               string.Equals(
                   registration.LogonType,
                   "InteractiveToken",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   registration.RunLevel,
                   "LeastPrivilege",
                   StringComparison.OrdinalIgnoreCase) &&
               registration.Priority == InteractiveTaskPriority &&
               string.Equals(
                   registration.ExecutionTimeLimit,
                   "PT0S",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   registration.MultipleInstancesPolicy,
                   "IgnoreNew",
                   StringComparison.OrdinalIgnoreCase) &&
               registration.StartWhenAvailable &&
               !registration.DisallowStartIfOnBatteries &&
               !registration.StopIfGoingOnBatteries &&
               !registration.RunOnlyIfIdle &&
               (string.IsNullOrWhiteSpace(registration.TriggerDelay) ||
                string.Equals(
                    registration.TriggerDelay,
                    "PT0S",
                    StringComparison.OrdinalIgnoreCase));
    }

    public bool TryRegister(string executablePath)
    {
        LastError = string.Empty;
        string taskXml = BuildTaskXml(executablePath, GetCurrentUserSid());
        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-startup-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(temporaryPath, taskXml, Encoding.Unicode);
            SchtasksResult result = RunSchtasks(
                "/Create",
                "/TN",
                TaskName,
                "/XML",
                temporaryPath,
                "/F");
            if (result.ExitCode != 0)
            {
                LastError = FormatFailure("register", result);
                return false;
            }

            DirectStartupTaskRegistration? verified = Read();
            if (verified is null || !IsPreferred(verified, executablePath))
            {
                string verificationError = string.IsNullOrWhiteSpace(LastError)
                    ? verified is null
                        ? "The registered task could not be read back."
                        : DescribePreferenceMismatch(verified, executablePath)
                    : LastError;
                bool removedInvalidTask = TryDelete();
                LastError = removedInvalidTask
                    ? verificationError
                    : $"{verificationError} Cleanup also failed: {LastError}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Task registration failed: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort temporary-file cleanup.
            }
        }
    }

    public bool TryDelete()
    {
        LastError = string.Empty;
        SchtasksResult result = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (result.ExitCode == 0)
        {
            return true;
        }

        LastError = FormatFailure("delete", result);
        return false;
    }

    internal static string BuildTaskXml(
        string executablePath,
        string currentUserSid)
    {
        string fullExecutablePath = Path.GetFullPath(executablePath);
        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(
                TaskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(
                    TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Source", "DeskBox"),
                    new XElement(TaskNamespace + "Author", "DeskBox"),
                    new XElement(
                        TaskNamespace + "Description",
                        "Starts DeskBox promptly after this user signs in."),
                    new XElement(TaskNamespace + "URI", $"\\{TaskName}")),
                new XElement(
                    TaskNamespace + "Triggers",
                    new XElement(
                        TaskNamespace + "LogonTrigger",
                        new XElement(TaskNamespace + "Enabled", "true"),
                        new XElement(TaskNamespace + "UserId", currentUserSid))),
                new XElement(
                    TaskNamespace + "Principals",
                    new XElement(
                        TaskNamespace + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", currentUserSid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "false"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNamespace + "Priority", InteractiveTaskPriority)),
                new XElement(
                    TaskNamespace + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", fullExecutablePath),
                        new XElement(TaskNamespace + "Arguments", StartupArguments),
                        new XElement(
                            TaskNamespace + "WorkingDirectory",
                            Path.GetDirectoryName(fullExecutablePath) ?? string.Empty)))));

        return $"{document.Declaration}{Environment.NewLine}{document}";
    }

    internal static DirectStartupTaskRegistration ParseTaskXml(string taskXml)
    {
        XDocument document = XDocument.Parse(taskXml, LoadOptions.None);
        XElement root = document.Root ??
            throw new InvalidDataException("The task XML has no root element.");
        XNamespace ns = root.Name.Namespace;
        XElement? trigger = root.Descendants(ns + "LogonTrigger").FirstOrDefault();
        XElement? principal = root.Descendants(ns + "Principal").FirstOrDefault();
        XElement? settings = root.Element(ns + "Settings");
        XElement? action = root.Descendants(ns + "Exec").FirstOrDefault();

        static string Value(XElement? parent, XNamespace ns, string name) =>
            parent?.Element(ns + name)?.Value?.Trim() ?? string.Empty;
        static bool BooleanValue(
            XElement? parent,
            XNamespace ns,
            string name,
            bool defaultValue = false) =>
            bool.TryParse(Value(parent, ns, name), out bool value)
                ? value
                : defaultValue;

        string runLevel = Value(principal, ns, "RunLevel");
        if (string.IsNullOrWhiteSpace(runLevel))
        {
            // Task Scheduler omits LeastPrivilege when exporting XML because it
            // is the schema default. Normalize that omission before validation.
            runLevel = "LeastPrivilege";
        }

        _ = int.TryParse(
            Value(settings, ns, "Priority"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int priority);

        return new DirectStartupTaskRegistration(
            Value(action, ns, "Command"),
            Value(action, ns, "Arguments"),
            Value(principal, ns, "UserId"),
            Value(trigger, ns, "UserId"),
            Value(principal, ns, "LogonType"),
            runLevel,
            priority,
            // Enabled=true is also commonly omitted from exported XML.
            BooleanValue(settings, ns, "Enabled", defaultValue: true),
            Value(settings, ns, "ExecutionTimeLimit"),
            Value(settings, ns, "MultipleInstancesPolicy"),
            BooleanValue(settings, ns, "StartWhenAvailable"),
            BooleanValue(settings, ns, "DisallowStartIfOnBatteries"),
            BooleanValue(settings, ns, "StopIfGoingOnBatteries"),
            BooleanValue(settings, ns, "RunOnlyIfIdle"),
            Value(trigger, ns, "Delay"));
    }

    internal static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribePreferenceMismatch(
        DirectStartupTaskRegistration registration,
        string executablePath)
    {
        return
            "The registered task did not match the required least-privilege " +
            $"startup contract: expectedPath='{executablePath}' " +
            $"actualPath='{registration.ExecutablePath}' " +
            $"arguments='{registration.Arguments}' " +
            $"principal='{registration.PrincipalUserId}' " +
            $"trigger='{registration.TriggerUserId}' " +
            $"logonType='{registration.LogonType}' " +
            $"runLevel='{registration.RunLevel}' " +
            $"priority={registration.Priority} enabled={registration.Enabled} " +
            $"executionLimit='{registration.ExecutionTimeLimit}' " +
            $"instances='{registration.MultipleInstancesPolicy}' " +
            $"startWhenAvailable={registration.StartWhenAvailable} " +
            $"disallowBattery={registration.DisallowStartIfOnBatteries} " +
            $"stopOnBattery={registration.StopIfGoingOnBatteries} " +
            $"runOnlyIfIdle={registration.RunOnlyIfIdle} " +
            $"delay='{registration.TriggerDelay}'.";
    }

    private static bool IsCurrentUserId(
        string candidate,
        string currentUserSid,
        string currentUserName) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (string.Equals(
             candidate,
             currentUserSid,
             StringComparison.OrdinalIgnoreCase) ||
         (!string.IsNullOrWhiteSpace(currentUserName) &&
          string.Equals(
              candidate,
              currentUserName,
              StringComparison.OrdinalIgnoreCase)));

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }

    private static SchtasksResult RunSchtasks(params string[] arguments)
    {
        string schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        if (!File.Exists(schtasksPath))
        {
            return new SchtasksResult(-1, string.Empty, $"Missing inbox client: {schtasksPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = schtasksPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new SchtasksResult(-1, string.Empty, "schtasks.exe did not start.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort timeout cleanup.
                }

                return new SchtasksResult(-1, string.Empty, "schtasks.exe timed out.");
            }

            Task.WaitAll([outputTask, errorTask], TimeSpan.FromSeconds(2));
            return new SchtasksResult(
                process.ExitCode,
                outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty,
                errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty);
        }
        catch (Exception ex)
        {
            return new SchtasksResult(-1, string.Empty, ex.Message);
        }
    }

    private static string FormatFailure(string operation, SchtasksResult result)
    {
        string detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return $"Task {operation} failed with exit code {result.ExitCode}: {detail.Trim()}";
    }

    private readonly record struct SchtasksResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

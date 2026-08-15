using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Delegates shell launches to the running Explorer desktop process so child
/// applications receive the same user environment as desktop-launched apps.
/// </summary>
internal static class ExplorerShellLaunchService
{
    private const int ShellWindowClassDesktop = 8;
    private const int ShellWindowFindNeedDispatch = 1;
    private const int ShowNormal = 1;

    public static bool TryOpen(
        string path,
        string workingDirectory,
        string verb,
        out string? error)
    {
        error = null;
        object? localShell = null;
        object? shellWindows = null;
        object? desktopWindow = null;
        object? desktopDocument = null;
        object? explorerHostedShell = null;

        try
        {
            Type? shellApplicationType = Type.GetTypeFromProgID("Shell.Application");
            if (shellApplicationType is null)
            {
                error = "Shell.Application is unavailable.";
                return false;
            }

            localShell = Activator.CreateInstance(shellApplicationType);
            if (localShell is null)
            {
                error = "Could not create Shell.Application.";
                return false;
            }

            dynamic shell = localShell;
            shellWindows = shell.Windows();
            if (shellWindows is null)
            {
                error = "Could not access Explorer shell windows.";
                return false;
            }

            // Shell.Application created above is local to this process. Launching through
            // it would still leak DeskBox's environment to the child. FindWindowSW returns
            // the desktop window hosted by the existing Explorer process; its document's
            // Application object therefore executes ShellExecute inside Explorer.
            object desktopLocation = null!;
            object desktopRoot = 0;
            int desktopHwnd = 0;
            dynamic windows = shellWindows;
            desktopWindow = windows.FindWindowSW(
                ref desktopLocation,
                ref desktopRoot,
                ShellWindowClassDesktop,
                ref desktopHwnd,
                ShellWindowFindNeedDispatch);
            if (desktopWindow is null)
            {
                error = "Could not locate the Explorer desktop window.";
                return false;
            }

            dynamic desktop = desktopWindow;
            desktopDocument = desktop.Document;
            if (desktopDocument is null)
            {
                error = "Could not access the Explorer desktop document.";
                return false;
            }

            dynamic document = desktopDocument;
            explorerHostedShell = document.Application;
            if (explorerHostedShell is null)
            {
                error = "Could not access the Explorer-hosted shell.";
                return false;
            }

            dynamic explorerShell = explorerHostedShell;
            explorerShell.ShellExecute(
                path,
                string.Empty,
                workingDirectory,
                verb,
                ShowNormal);
            return true;
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ReleaseComObject(explorerHostedShell);
            ReleaseComObject(desktopDocument);
            ReleaseComObject(desktopWindow);
            ReleaseComObject(shellWindows);
            ReleaseComObject(localShell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            _ = Marshal.ReleaseComObject(value);
        }
        catch
        {
            // COM cleanup must never turn a successful launch into a user-visible failure.
        }
    }
}

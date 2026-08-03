using DeskBox.Helpers;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Listens for application-wide lifecycle signals that are otherwise easy to
/// miss when individual widgets are hidden or Explorer is restarted.
/// </summary>
internal sealed class AppLifecycleRecoveryWatcher : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcDestroy = 0x0082;
    private const uint PbtResumeAutomatic = 0x0012;
    private const uint PbtResumeSuspend = 0x0007;
    private const uint PbtResumeCritical = 0x0006;
    private const uint WtsSessionLock = 0x0007;
    private const uint WtsSessionUnlock = 0x0008;
    private const uint WtsSessionLogon = 0x0005;
    private const uint WtsSessionLogoff = 0x0006;
    private const uint WtsSessionRemoteConnect = 0x0009;
    private const uint WtsSessionRemoteDisconnect = 0x000A;
    private const uint NotifyForThisSession = 0;
    private static readonly uint s_taskbarCreatedMessage = Win32Helper.RegisterWindowMessage("TaskbarCreated");
    private static readonly UIntPtr SubclassId = new(0xDBA7);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(420);

    private readonly IntPtr _hWnd;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<string> _recoveryAction;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private bool _isDisposed;
    private bool _isSubclassInstalled;
    private bool _sessionNotificationRegistered;
    private string _pendingReasons = string.Empty;

    public AppLifecycleRecoveryWatcher(
        IntPtr hWnd,
        DispatcherQueue dispatcherQueue,
        Action<string> recoveryAction)
    {
        _hWnd = hWnd;
        _recoveryAction = recoveryAction;
        _subclassProc = WindowSubclassProc;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = RecoveryDelay;
        _timer.IsRepeating = false;
        _timer.Tick += RecoveryTimer_Tick;

        if (_hWnd == IntPtr.Zero)
        {
            return;
        }

        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _subclassProc,
            SubclassId,
            UIntPtr.Zero);

        try
        {
            _sessionNotificationRegistered = WTSRegisterSessionNotification(
                _hWnd,
                NotifyForThisSession);
        }
        catch (DllNotFoundException)
        {
            App.Log("[Lifecycle] Session notifications are unavailable on this system.");
        }
        catch (EntryPointNotFoundException)
        {
            App.Log("[Lifecycle] Session notification API is unavailable on this system.");
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] Session notification registration failed: {ex.Message}");
        }

        App.Log(
            $"[Lifecycle] Recovery watcher attached hwnd=0x{_hWnd.ToInt64():X} " +
            $"subclass={_isSubclassInstalled} session={_sessionNotificationRegistered}");
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmPowerBroadcast)
        {
            uint powerEvent = unchecked((uint)wParam.ToUInt64());
            if (powerEvent is PbtResumeAutomatic or PbtResumeSuspend or PbtResumeCritical)
            {
                QueueRecovery("resume");
            }
        }
        else if (message == WmWtsSessionChange)
        {
            uint sessionEvent = unchecked((uint)wParam.ToUInt64());
            if (sessionEvent == WtsSessionUnlock ||
                sessionEvent == WtsSessionLogon ||
                sessionEvent == WtsSessionRemoteConnect)
            {
                QueueRecovery(sessionEvent == WtsSessionUnlock
                    ? "session-unlock"
                    : "session-reconnect");
            }
            else if (sessionEvent == WtsSessionLock ||
                     sessionEvent == WtsSessionLogoff ||
                     sessionEvent == WtsSessionRemoteDisconnect)
            {
                App.Log("[Lifecycle] Session ended or locked; deferring external-state recovery.");
            }
        }
        else if (message == WmDisplayChange || message == WmDpiChanged)
        {
            QueueRecovery("display-message");
        }
        else if (message == s_taskbarCreatedMessage)
        {
            // Explorer restart recreates the taskbar/tray window and can also
            // invalidate shell icon and file-system watcher state.
            QueueRecovery("explorer-restart");
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void QueueRecovery(string reason)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingReasons))
        {
            _pendingReasons = reason;
        }
        else if (!_pendingReasons.Contains(reason, StringComparison.Ordinal))
        {
            _pendingReasons += "," + reason;
        }

        _timer.Stop();
        _timer.Interval = RecoveryDelay;
        _timer.Start();
    }

    private void RecoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _timer.Stop();
        if (_isDisposed || string.IsNullOrWhiteSpace(_pendingReasons))
        {
            return;
        }

        string reasons = _pendingReasons;
        _pendingReasons = string.Empty;
        try
        {
            _recoveryAction(reasons);
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] Recovery callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= RecoveryTimer_Tick;

        if (_sessionNotificationRegistered)
        {
            try
            {
                WTSUnRegisterSessionNotification(_hWnd);
            }
            catch
            {
                // Best effort during teardown; the window is going away.
            }

            _sessionNotificationRegistered = false;
        }

        if (_isSubclassInstalled)
        {
            Win32Helper.RemoveWindowSubclass(_hWnd, _subclassProc, SubclassId);
            _isSubclassInstalled = false;
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}

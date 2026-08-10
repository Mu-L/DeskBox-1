using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Registers the optional Todo quick-add shortcut against DeskBox's hidden tray window.
/// The implementation deliberately uses RegisterHotKey instead of a keyboard hook.
/// </summary>
public sealed class TodoHotkeyService : IDisposable
{
    private const int TodoHotkeyId = 0x544F;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x544F);

    private readonly SettingsService _settingsService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _isInvoking;

    public TodoHotkeyService(SettingsService settingsService, Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
    }

    public bool IsRegistered => _isRegistered;

    public GlobalHotkeyGesture CurrentGesture => GlobalHotkeyService.NormalizeGesture(
        _settingsService.Settings.Todo.QuickRecord.TodoHotkeyModifiers,
        _settingsService.Settings.Todo.QuickRecord.TodoHotkeyKey);

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        Detach();
        _windowHandle = windowHandle;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _windowHandle,
            _subclassProc,
            SubclassId,
            UIntPtr.Zero);
        RefreshRegistration();
    }

    public void Detach()
    {
        Unregister();
        if (_isSubclassInstalled && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        _isSubclassInstalled = false;
        _windowHandle = IntPtr.Zero;
    }

    public void RefreshRegistration()
    {
        Unregister();
        TodoQuickRecordSettings settings = _settingsService.Settings.Todo.QuickRecord;
        if (_windowHandle == IntPtr.Zero || !settings.TodoHotkeyEnabled)
        {
            return;
        }

        GlobalHotkeyGesture gesture = CurrentGesture;
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            return;
        }

        _isRegistered = Win32Helper.RegisterHotKey(
            _windowHandle,
            TodoHotkeyId,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
        App.Log(_isRegistered
            ? $"[TodoHotkey] Registered modifiers={(int)gesture.Modifiers} key=0x{gesture.VirtualKey:X2}"
            : $"[TodoHotkey] Registration failed modifiers={(int)gesture.Modifiers} key=0x{gesture.VirtualKey:X2}");
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture)
    {
        gesture = GlobalHotkeyService.NormalizeGesture((int)gesture.Modifiers, gesture.VirtualKey);
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            return false;
        }

        TodoQuickRecordSettings settings = _settingsService.Settings.Todo.QuickRecord;
        settings.TodoHotkeyModifiers = (int)gesture.Modifiers;
        settings.TodoHotkeyKey = gesture.VirtualKey;
        _settingsService.SaveDebounced();
        RefreshRegistration();
        return true;
    }

    public void SetEnabled(bool enabled)
    {
        TodoQuickRecordSettings settings = _settingsService.Settings.Todo.QuickRecord;
        if (settings.TodoHotkeyEnabled == enabled)
        {
            return;
        }

        settings.TodoHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == GlobalHotkeyService.WmHotkey && wParam.ToUInt32() == TodoHotkeyId)
        {
            Win32Helper.ReleaseAllModifiers();
            App.UiDispatcherQueue.TryEnqueue(async () => await InvokeAsync());
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task InvokeAsync()
    {
        if (_isInvoking)
        {
            return;
        }

        _isInvoking = true;
        try
        {
            await _invokeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[TodoHotkey] Invocation failed: {ex}");
        }
        finally
        {
            _isInvoking = false;
        }
    }

    private void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, TodoHotkeyId);
        }

        _isRegistered = false;
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt)) value |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifierKeys.Control)) value |= ModControl;
        if (modifiers.HasFlag(HotkeyModifierKeys.Shift)) value |= ModShift;
        return value;
    }

    public void Dispose() => Detach();
}

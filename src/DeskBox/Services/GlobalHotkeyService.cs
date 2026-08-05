using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using Windows.System;

namespace DeskBox.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const uint WmHotkey = 0x0312;
    private const int MainHotkeyId = 0x4442;
    private const int ProbeHotkeyId = 0x4443;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4442);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private long _invocationSequence;

    public GlobalHotkeyService(
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
    }

    public event Action? RegistrationChanged;

    public bool IsRegistered => _isRegistered;
    public long InvocationCount => Interlocked.Read(ref _invocationSequence);
    public string? LastError { get; private set; }

    public GlobalHotkeyGesture CurrentGesture => NormalizeGesture(
        _settingsService.Settings.GlobalHotkeyModifiers,
        _settingsService.Settings.GlobalHotkeyKey);

    public string CurrentGestureText => FormatGesture(CurrentGesture, _localizationService);

    public void Attach(IntPtr windowHandle)
    {
        App.Log($"[GlobalHotkey] Attach called hwnd=0x{windowHandle.ToInt64():X}");
        if (windowHandle == IntPtr.Zero)
        {
            App.Log("[GlobalHotkey] Attach skipped: windowHandle is Zero");
            return;
        }

        Detach();
        _windowHandle = windowHandle;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
        App.Log($"[GlobalHotkey] Subclass installed={_isSubclassInstalled} error={Marshal.GetLastWin32Error()}");
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
        LastError = null;

        App.Log($"[GlobalHotkey] RefreshRegistration hwnd=0x{_windowHandle.ToInt64():X} enabled={_settingsService.Settings.GlobalHotkeyEnabled} gesture={CurrentGestureText}");

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.GlobalHotkeyEnabled)
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: handle=0 or disabled");
            NotifyRegistrationChanged();
            return;
        }

        var gesture = CurrentGesture;
        if (!IsValidGesture(gesture))
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: invalid gesture");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            NotifyRegistrationChanged();
            return;
        }

        if (Register(_windowHandle, MainHotkeyId, gesture))
        {
            _isRegistered = true;
            App.Log($"[GlobalHotkey] Registered gesture={CurrentGestureText} hwnd=0x{_windowHandle.ToInt64():X}");
            NotifyRegistrationChanged();
            return;
        }

        App.Log($"[GlobalHotkey] RegisterHotKey failed gesture={CurrentGestureText} error={Marshal.GetLastWin32Error()}");
        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        NotifyRegistrationChanged();
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        error = null;
        gesture = NormalizeGesture((int)gesture.Modifiers, gesture.VirtualKey);
        if (!IsValidGesture(gesture))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return false;
        }

        bool isCurrentGesture = gesture.Equals(CurrentGesture);
        if (_windowHandle != IntPtr.Zero &&
            !(isCurrentGesture && _isRegistered) &&
            !CanRegister(_windowHandle, gesture))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
            return false;
        }

        var settings = _settingsService.Settings;
        settings.GlobalHotkeyModifiers = (int)gesture.Modifiers;
        settings.GlobalHotkeyKey = gesture.VirtualKey;
        _settingsService.SaveDebounced();
        RefreshRegistration();
        return true;
    }

    public void SetEnabled(bool enabled)
    {
        if (_settingsService.Settings.GlobalHotkeyEnabled == enabled)
        {
            return;
        }

        _settingsService.Settings.GlobalHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
    }

    public bool ResetToDefault(out string? error)
    {
        var gesture = new GlobalHotkeyGesture(
            (HotkeyModifierKeys)SettingsService.DefaultGlobalHotkeyModifiers,
            SettingsService.DefaultGlobalHotkeyKey);
        return TryApplyGesture(gesture, out error);
    }

    public void Dispose()
    {
        Detach();
    }

    public static GlobalHotkeyGesture NormalizeGesture(int modifiers, int virtualKey)
    {
        var normalizedModifiers = (HotkeyModifierKeys)modifiers &
            (HotkeyModifierKeys.Alt | HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift);
        return new GlobalHotkeyGesture(normalizedModifiers, virtualKey);
    }

    public static bool IsValidGesture(GlobalHotkeyGesture gesture)
    {
        if (gesture.VirtualKey <= 0)
        {
            return false;
        }

        if (gesture.Modifiers == HotkeyModifierKeys.None)
        {
            return IsFunctionKey(gesture.VirtualKey);
        }

        return IsAllowedPrimaryKey(gesture.VirtualKey);
    }

    public static bool IsRiskyGesture(GlobalHotkeyGesture gesture)
    {
        return gesture.Modifiers == HotkeyModifierKeys.None ||
               gesture.VirtualKey is
                   (int)VirtualKey.F1 or
                   (int)VirtualKey.F2 or
                   (int)VirtualKey.F5 or
                   (int)VirtualKey.F11 or
                   (int)VirtualKey.F12;
    }

    public static string FormatGesture(GlobalHotkeyGesture gesture, LocalizationService localization)
    {
        if (gesture.VirtualKey <= 0)
        {
            return localization.T("Settings.GlobalHotkey.NotSet");
        }

        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(FormatVirtualKey(gesture.VirtualKey));
        return string.Join(" + ", parts);
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == WmHotkey && wParam == (UIntPtr)MainHotkeyId)
        {
            // Immediately release all modifier keys to clear any stuck state.
            // See SearchHotkeyService for the full explanation.
            Win32Helper.ReleaseAllModifiers();

            App.UiDispatcherQueue.TryEnqueue(() =>
            {
                _ = InvokeHotkeyAsync("registered");
            });
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task InvokeHotkeyAsync(string source)
    {
        long invocationId = Interlocked.Increment(ref _invocationSequence);
        App.Log(
            $"[GlobalHotkey] Triggered id={invocationId} source={source} " +
            $"gesture={CurrentGestureText}");
        try
        {
            await _invokeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[GlobalHotkey] Invocation failed id={invocationId}: {ex}");
        }
    }

    private void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, MainHotkeyId);
            App.Log($"[GlobalHotkey] Unregistered gesture={CurrentGestureText}");
        }

        _isRegistered = false;
    }

    private static bool CanRegister(IntPtr windowHandle, GlobalHotkeyGesture gesture)
    {
        if (!Register(windowHandle, ProbeHotkeyId, gesture))
        {
            return false;
        }

        Win32Helper.UnregisterHotKey(windowHandle, ProbeHotkeyId);
        return true;
    }

    private static bool Register(IntPtr windowHandle, int id, GlobalHotkeyGesture gesture)
    {
        bool registered = Win32Helper.RegisterHotKey(
            windowHandle,
            id,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
        if (!registered)
        {
            _ = Marshal.GetLastWin32Error();
        }

        return registered;
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            value |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            value |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            value |= ModShift;
        }

        return value;
    }

    internal static bool AreCurrentModifiersPressed(HotkeyModifierKeys modifiers)
    {
        bool ctrl = Win32Helper.IsKeyDown((int)VirtualKey.Control) ||
                    Win32Helper.IsKeyDown((int)VirtualKey.LeftControl) ||
                    Win32Helper.IsKeyDown((int)VirtualKey.RightControl);
        bool alt = Win32Helper.IsKeyDown((int)VirtualKey.Menu) ||
                   Win32Helper.IsKeyDown((int)VirtualKey.LeftMenu) ||
                   Win32Helper.IsKeyDown((int)VirtualKey.RightMenu);
        bool shift = Win32Helper.IsKeyDown((int)VirtualKey.Shift) ||
                     Win32Helper.IsKeyDown((int)VirtualKey.LeftShift) ||
                     Win32Helper.IsKeyDown((int)VirtualKey.RightShift);

        return ctrl == modifiers.HasFlag(HotkeyModifierKeys.Control) &&
               alt == modifiers.HasFlag(HotkeyModifierKeys.Alt) &&
               shift == modifiers.HasFlag(HotkeyModifierKeys.Shift);
    }

    private static bool IsAllowedPrimaryKey(int virtualKey)
    {
        return IsLetterKey(virtualKey) ||
               IsDigitKey(virtualKey) ||
               IsFunctionKey(virtualKey) ||
               virtualKey is
                   (int)VirtualKey.Space or
                   (int)VirtualKey.Tab or
                   (int)VirtualKey.Insert or
                   (int)VirtualKey.Delete or
                   (int)VirtualKey.Home or
                   (int)VirtualKey.End or
                   (int)VirtualKey.PageUp or
                   (int)VirtualKey.PageDown;
    }

    private static bool IsLetterKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.A and <= (int)VirtualKey.Z;
    }

    private static bool IsDigitKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9 ||
               virtualKey is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9;
    }

    private static bool IsFunctionKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24;
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9)
        {
            return $"Num {virtualKey - (int)VirtualKey.NumberPad0}";
        }

        if (virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return $"F{virtualKey - (int)VirtualKey.F1 + 1}";
        }

        return ((VirtualKey)virtualKey) switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",
            _ => ((VirtualKey)virtualKey).ToString()
        };
    }

    private void NotifyRegistrationChanged()
    {
        RegistrationChanged?.Invoke();
    }
}

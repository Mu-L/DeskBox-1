// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Services;

/// <summary>
/// Recognizes an isolated left or right Windows-key tap while preserving every
/// Windows-key chord. Start-menu masking is performed by the hook service only
/// after this state machine reports a completed isolated tap.
/// </summary>
internal sealed class WindowsTapHotkeyStateMachine
{
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;

    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _chordObserved;

    public ReservedHotkeyEventDisposition Process(uint virtualKey, bool isKeyDown)
    {
        if (virtualKey is LeftWindows or RightWindows)
        {
            return ProcessWindowsKey(virtualKey, isKeyDown);
        }

        if (isKeyDown && HasWindowsKeyDown)
        {
            _chordObserved = true;
        }

        return ReservedHotkeyEventDisposition.PassThrough;
    }

    public void Reset()
    {
        _leftWindowsDown = false;
        _rightWindowsDown = false;
        _chordObserved = false;
    }

    private ReservedHotkeyEventDisposition ProcessWindowsKey(uint virtualKey, bool isKeyDown)
    {
        ref bool keyDown = ref (virtualKey == LeftWindows
            ? ref _leftWindowsDown
            : ref _rightWindowsDown);

        if (isKeyDown)
        {
            if (!keyDown && HasWindowsKeyDown)
            {
                _chordObserved = true;
            }

            keyDown = true;
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        if (!keyDown)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        keyDown = false;
        if (HasWindowsKeyDown)
        {
            return ReservedHotkeyEventDisposition.PassThrough;
        }

        bool shouldTrigger = !_chordObserved;
        _chordObserved = false;
        return shouldTrigger
            ? ReservedHotkeyEventDisposition.TriggerAndPassThrough
            : ReservedHotkeyEventDisposition.PassThrough;
    }

    private bool HasWindowsKeyDown => _leftWindowsDown || _rightWindowsDown;
}

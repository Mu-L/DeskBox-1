using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DeskBox.Helpers;

public static class TextBoxEditorShortcutHelper
{
    public static bool IsCtrlSaveShortcut(
        VirtualKey key,
        bool controlPressed,
        bool shiftPressed = false) =>
        controlPressed && !shiftPressed && key == VirtualKey.S;

    public static void InsertLineBreak(TextBox textBox)
    {
        string text = textBox.Text ?? string.Empty;
        int selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        int selectionLength = Math.Clamp(
            textBox.SelectionLength,
            0,
            text.Length - selectionStart);
        string lineBreak = Environment.NewLine;

        textBox.Text = text
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, lineBreak);
        textBox.Select(selectionStart + lineBreak.Length, 0);
    }
}

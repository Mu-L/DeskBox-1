using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DeskBox.Controls;

/// <summary>
/// Lossless Markdown source editor. Formatting commands mutate the selected
/// range instead of replacing the whole document, preserving native undo,
/// caret, selection, and viewport state.
/// </summary>
public sealed partial class MarkdownSourceEditor : UserControl
{
    private bool _isSynchronizingText;
    private EditorViewportSnapshot? _lastEditorViewport;
    private EditorViewportSnapshot? _pendingCommandViewport;
    private Func<string, string>? _textResolver;

    public MarkdownSourceEditor()
    {
        InitializeComponent();
        FormattingCommandBar.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(FormattingCommandBar_PointerPressed),
            handledEventsToo: true);
        EditorTextBox.AddHandler(
            PreviewKeyDownEvent,
            new KeyEventHandler(EditorTextBox_PreviewKeyDown),
            handledEventsToo: true);
        EditorTextBox.LostFocus += EditorTextBox_LostFocus;
        ApplyToolbarVisibility();
        ApplyLocalizedText();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata("Write with Markdown…"));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly),
        typeof(bool),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(false));

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize),
        typeof(double),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(14.0));

    public static readonly DependencyProperty ShowFormattingToolbarProperty = DependencyProperty.Register(
        nameof(ShowFormattingToolbar),
        typeof(bool),
        typeof(MarkdownSourceEditor),
        new PropertyMetadata(true, OnToolbarVisibilityChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public bool ShowFormattingToolbar
    {
        get => (bool)GetValue(ShowFormattingToolbarProperty);
        set => SetValue(ShowFormattingToolbarProperty, value);
    }

    public Func<string, string>? TextResolver
    {
        get => _textResolver;
        set
        {
            _textResolver = value;
            ApplyLocalizedText();
        }
    }

    public TextBox SourceTextBox => EditorTextBox;

    public event EventHandler? EditorTextChanged;

    public event EventHandler? CommitRequested;

    public event EventHandler? CancelRequested;

    public void FocusEditor(bool selectAll = false, bool moveCaretToEnd = false)
    {
        EditorTextBox.Focus(FocusState.Programmatic);
        if (selectAll)
        {
            EditorTextBox.SelectAll();
        }
        else if (moveCaretToEnd)
        {
            EditorTextBox.Select(EditorTextBox.Text.Length, 0);
        }
    }

    public bool FindNext(string? value, StringComparison comparison = StringComparison.CurrentCultureIgnoreCase)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int start = Math.Min(
            EditorTextBox.Text.Length,
            EditorTextBox.SelectionStart + EditorTextBox.SelectionLength);
        int index = EditorTextBox.Text.IndexOf(value, start, comparison);
        if (index < 0)
        {
            index = EditorTextBox.Text.IndexOf(value, comparison);
        }

        if (index < 0)
        {
            return false;
        }

        EditorTextBox.Focus(FocusState.Programmatic);
        EditorTextBox.Select(index, value.Length);
        return true;
    }

    public void ApplyFormat(string action)
    {
        if (IsReadOnly || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        EditorViewportSnapshot viewport = PrepareEditorCommandViewport();
        switch (action)
        {
            case "Bold":
                ToggleWrappedSelection("**", "**", "bold text");
                break;
            case "Italic":
                ToggleWrappedSelection("*", "*", "italic text");
                break;
            case "Strike":
                ToggleWrappedSelection("~~", "~~", "strikethrough text");
                break;
            case "Code":
                if (EditorTextBox.SelectedText.Contains('\n'))
                {
                    WrapSelection("```\n", "\n```", "code");
                }
                else
                {
                    ToggleWrappedSelection("`", "`", "code");
                }
                break;
            case "Link":
                WrapSelection("[", "](https://)", "link text");
                break;
            case "Heading":
                PrefixSelectedLines("## ");
                break;
            case "List":
                PrefixSelectedLines("- ");
                break;
            case "Task":
                PrefixSelectedLines("- [ ] ");
                break;
            case "Quote":
                PrefixSelectedLines("> ");
                break;
            case "Table":
                ReplaceSelection("| Column 1 | Column 2 |\n| --- | --- |\n| Content | Content |");
                break;
            default:
                return;
        }

        RestoreEditorViewport(viewport);
    }

    private static void OnTextPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var editor = (MarkdownSourceEditor)sender;
        string value = args.NewValue as string ?? string.Empty;
        if (editor.EditorTextBox.Text == value)
        {
            return;
        }

        editor._isSynchronizingText = true;
        try
        {
            editor.EditorTextBox.Text = value;
        }
        finally
        {
            editor._isSynchronizingText = false;
        }
    }

    private static void OnToolbarVisibilityChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownSourceEditor)sender).ApplyToolbarVisibility();

    private void ApplyToolbarVisibility() =>
        FormattingCommandBar.Visibility = ShowFormattingToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            ApplyFormat(action);
        }
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSynchronizingText)
        {
            return;
        }

        SetValue(TextProperty, EditorTextBox.Text);
        EditorTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditorTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (EditorTextBox.FocusState != FocusState.Unfocused)
        {
            _lastEditorViewport = CaptureEditorViewport();
        }
    }

    private void EditorTextBox_LostFocus(object sender, RoutedEventArgs e) =>
        _lastEditorViewport = CaptureEditorViewport();

    private void FormattingCommandBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsReadOnly)
        {
            _pendingCommandViewport = CaptureEditorViewport();
        }
    }

    private void EditorTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        if (control && e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            IndentSelection(outdent: shift);
            return;
        }

        if (!control && !shift && e.Key == VirtualKey.Enter && TryContinueMarkdownList())
        {
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private EditorViewportSnapshot PrepareEditorCommandViewport()
    {
        EditorViewportSnapshot snapshot =
            _pendingCommandViewport ??
            _lastEditorViewport ??
            CaptureEditorViewport();
        _pendingCommandViewport = null;

        int start = Math.Clamp(snapshot.SelectionStart, 0, EditorTextBox.Text.Length);
        int length = Math.Clamp(
            snapshot.SelectionLength,
            0,
            EditorTextBox.Text.Length - start);
        EditorTextBox.Select(start, length);
        return snapshot with { SelectionStart = start, SelectionLength = length };
    }

    private EditorViewportSnapshot CaptureEditorViewport()
    {
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(EditorTextBox);
        return new EditorViewportSnapshot(
            EditorTextBox.SelectionStart,
            EditorTextBox.SelectionLength,
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0);
    }

    private void RestoreEditorViewport(EditorViewportSnapshot viewport)
    {
        int start = Math.Clamp(EditorTextBox.SelectionStart, 0, EditorTextBox.Text.Length);
        int length = Math.Clamp(
            EditorTextBox.SelectionLength,
            0,
            EditorTextBox.Text.Length - start);

        EditorTextBox.Focus(FocusState.Programmatic);
        EditorTextBox.Select(start, length);
        RestoreScrollOffset();
        _lastEditorViewport = viewport with
        {
            SelectionStart = start,
            SelectionLength = length
        };

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            RestoreScrollOffset);

        void RestoreScrollOffset()
        {
            FindDescendant<ScrollViewer>(EditorTextBox)?.ChangeView(
                viewport.HorizontalOffset,
                viewport.VerticalOffset,
                null,
                disableAnimation: true);
        }
    }

    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        int start = EditorTextBox.SelectionStart;
        int length = EditorTextBox.SelectionLength;
        string selected = length > 0
            ? EditorTextBox.Text.Substring(start, length)
            : placeholder;
        ReplaceTextRange(start, length, prefix + selected + suffix);
        EditorTextBox.Select(start + prefix.Length, selected.Length);
    }

    private void ToggleWrappedSelection(string prefix, string suffix, string placeholder)
    {
        int start = EditorTextBox.SelectionStart;
        int length = EditorTextBox.SelectionLength;
        string text = EditorTextBox.Text;
        string selected = length > 0 ? text.Substring(start, length) : string.Empty;

        if (length > 0 &&
            selected.Length >= prefix.Length + suffix.Length &&
            selected.StartsWith(prefix, StringComparison.Ordinal) &&
            selected.EndsWith(suffix, StringComparison.Ordinal))
        {
            string unwrapped = selected[prefix.Length..^suffix.Length];
            ReplaceTextRange(start, length, unwrapped);
            EditorTextBox.Select(start, unwrapped.Length);
            return;
        }

        if (length > 0 &&
            start >= prefix.Length &&
            start + length + suffix.Length <= text.Length &&
            text.AsSpan(start - prefix.Length, prefix.Length).SequenceEqual(prefix) &&
            text.AsSpan(start + length, suffix.Length).SequenceEqual(suffix))
        {
            ReplaceTextRange(
                start - prefix.Length,
                prefix.Length + length + suffix.Length,
                selected);
            EditorTextBox.Select(start - prefix.Length, selected.Length);
            return;
        }

        WrapSelection(prefix, suffix, placeholder);
    }

    private void PrefixSelectedLines(string prefix)
    {
        int start = EditorTextBox.SelectionStart;
        int end = start + EditorTextBox.SelectionLength;
        string text = EditorTextBox.Text;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        int lookupEnd = end > start && end <= text.Length && text[end - 1] == '\n'
            ? end - 1
            : end;
        int lineEnd = text.IndexOf('\n', lookupEnd);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        string block = text[lineStart..lineEnd];
        string[] lines = block.Split('\n');
        bool removePrefix = lines.Any(line => line.TrimEnd('\r').Length > 0) &&
            lines.Where(line => line.TrimEnd('\r').Length > 0)
                .All(line => line.TrimStart('\r').StartsWith(prefix, StringComparison.Ordinal));
        string replacement = string.Join(
            "\n",
            lines.Select(line => removePrefix && line.StartsWith(prefix, StringComparison.Ordinal)
                ? line[prefix.Length..]
                : prefix + line));
        ReplaceTextRange(lineStart, lineEnd - lineStart, replacement);
        EditorTextBox.Select(lineStart, replacement.Length);
    }

    private void ReplaceSelection(string replacement)
    {
        int start = EditorTextBox.SelectionStart;
        ReplaceTextRange(start, EditorTextBox.SelectionLength, replacement);
        EditorTextBox.Select(start + replacement.Length, 0);
    }

    private void ReplaceTextRange(int start, int length, string replacement)
    {
        start = Math.Clamp(start, 0, EditorTextBox.Text.Length);
        length = Math.Clamp(length, 0, EditorTextBox.Text.Length - start);
        EditorTextBox.Select(start, length);
        EditorTextBox.SelectedText = replacement;
    }

    private void IndentSelection(bool outdent)
    {
        int start = EditorTextBox.SelectionStart;
        int length = EditorTextBox.SelectionLength;
        if (length == 0)
        {
            if (outdent)
            {
                int lineStart = EditorTextBox.Text.LastIndexOf('\n', Math.Max(0, start - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                int remove = EditorTextBox.Text.AsSpan(lineStart).StartsWith("    ") ? 4 :
                    EditorTextBox.Text.AsSpan(lineStart).StartsWith("\t") ? 1 : 0;
                if (remove > 0)
                {
                    ReplaceTextRange(lineStart, remove, string.Empty);
                    EditorTextBox.Select(Math.Max(lineStart, start - remove), 0);
                }
            }
            else
            {
                ReplaceTextRange(start, 0, "    ");
                EditorTextBox.Select(start + 4, 0);
            }
            return;
        }

        string selected = EditorTextBox.Text.Substring(start, length);
        string replacement = string.Join("\n", selected.Split('\n').Select(line =>
            outdent
                ? line.StartsWith("    ", StringComparison.Ordinal) ? line[4..]
                    : line.StartsWith('\t') ? line[1..] : line
                : "    " + line));
        ReplaceTextRange(start, length, replacement);
        EditorTextBox.Select(start, replacement.Length);
    }

    private bool TryContinueMarkdownList()
    {
        int selectionStart = EditorTextBox.SelectionStart;
        int selectionLength = EditorTextBox.SelectionLength;
        string text = EditorTextBox.Text;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, selectionStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        string line = text[lineStart..selectionStart].TrimEnd('\r');
        Match match = MarkdownListLineRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(match.Groups["content"].Value))
        {
            ReplaceTextRange(lineStart, selectionStart + selectionLength - lineStart, Environment.NewLine);
            EditorTextBox.Select(lineStart + Environment.NewLine.Length, 0);
            return true;
        }

        string marker = match.Groups["marker"].Value;
        if (int.TryParse(match.Groups["number"].Value, out int number))
        {
            marker = $"{number + 1}{match.Groups["punctuation"].Value}";
        }

        string continuation = Environment.NewLine +
                              match.Groups["indent"].Value +
                              marker + " " +
                              match.Groups["task"].Value;
        ReplaceTextRange(selectionStart, selectionLength, continuation);
        EditorTextBox.Select(selectionStart + continuation.Length, 0);
        return true;
    }

    private void ApplyLocalizedText()
    {
        BoldButton.Label = T("Markdown.Editor.Bold", "Bold");
        ItalicButton.Label = T("Markdown.Editor.Italic", "Italic");
        HeadingButton.Label = T("Markdown.Editor.Heading", "Heading");
        ListButton.Label = T("Markdown.Editor.List", "List");
        TaskButton.Label = T("Markdown.Editor.Task", "Task list");
        LinkButton.Label = T("Markdown.Editor.Link", "Link");
        StrikeButton.Label = T("Markdown.Editor.Strikethrough", "Strikethrough");
        QuoteButton.Label = T("Markdown.Editor.Quote", "Quote");
        CodeButton.Label = T("Markdown.Editor.Code", "Code");
        TableButton.Label = T("Markdown.Editor.Table", "Table");
    }

    private string T(string key, string fallback)
    {
        if (_textResolver is null)
        {
            return fallback;
        }

        string value = _textResolver(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?:(?<number>\d+)(?<punctuation>[.)])|(?<marker>[-+*]))\s+(?<task>\[[ xX]\]\s+)?(?<content>.*)$")]
    private static partial Regex MarkdownListLineRegex();

    private readonly record struct EditorViewportSnapshot(
        int SelectionStart,
        int SelectionLength,
        double HorizontalOffset,
        double VerticalOffset);
}

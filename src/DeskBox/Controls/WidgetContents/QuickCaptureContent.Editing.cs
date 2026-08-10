using System.Text.RegularExpressions;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private string _previousEditorBodyText = string.Empty;
    private EditorViewportSnapshot? _pendingEditorCommandViewport;
    private EditorViewportSnapshot? _lastEditorViewport;

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        await EnterEditModeAsync();
    }

    private async Task EnterEditModeAsync()
    {
        if (_selectedItem is not { IsRecent: false } item || _isEditing)
        {
            return;
        }

        _isEditing = true;
        _isCreating = false;
        _editorPreviewOnly = false;
        EditorPreviewModeButton.IsChecked = false;
        _editingFormat = item.ContentFormat;
        _editingAppearance = item.AppearancePreset;
        ApplyNoteAppearance(_editingAppearance);
        _hasUnsavedChanges = false;
        _revisionCaptured = false;
        _pendingAttachments.Clear();
        _suppressEditorChanges = true;
        EditorTitleTextBox.Text = item.Title ?? string.Empty;
        EditorBodyTextBox.Text = item.Body;
        _previousEditorBodyText = item.Body;

        QuickCaptureDraft? draft = await ViewModel.GetDraftAsync(item.Id);
        if (draft is not null &&
            (!string.Equals(draft.Title, item.Title, StringComparison.Ordinal) ||
             !string.Equals(draft.Body, item.Body, StringComparison.Ordinal)))
        {
            EditorTitleTextBox.Text = draft.Title ?? string.Empty;
            EditorBodyTextBox.Text = draft.Body;
            _previousEditorBodyText = draft.Body;
            _editingFormat = draft.ContentFormat;
            _hasUnsavedChanges = true;
            RaiseFeedback(
                "已恢复未完成内容",
                WidgetFeedbackSeverity.Info,
                "quick-capture-draft-restored");
            RestartPersistenceTimers();
        }

        _suppressEditorChanges = false;
        if (_hasUnsavedChanges)
        {
            await CaptureRevisionOnceAsync();
        }
        ReadingSurface.Visibility = Visibility.Collapsed;
        NoSelectionPanel.Visibility = Visibility.Collapsed;
        EditorSurface.Visibility = Visibility.Visible;
        EditButton.Visibility = Visibility.Collapsed;
        DoneEditingButton.Visibility = Visibility.Visible;
        FormattingToolbarHost.Visibility = Visibility.Visible;
        UpdateNoteCommandAvailability();
        ApplyResponsiveLayout();
        PlaySurfaceFadeIn(EditorSurface);
        EditorBodyTextBox.Focus(FocusState.Programmatic);
        EditorBodyTextBox.Select(EditorBodyTextBox.Text.Length, 0);
        _lastEditorViewport = CaptureEditorViewport();
        RefreshMarkdownPreview();
    }

    private async Task CaptureRevisionOnceAsync()
    {
        if (_revisionCaptured || _selectedItem is null ||
            !_settingsService.Settings.QuickCaptureRevisionHistoryEnabled)
        {
            return;
        }

        _revisionCaptured = true;
        await ViewModel.CreateRevisionAsync(_selectedItem.Id);
    }

    private async void DoneEditingButton_Click(object sender, RoutedEventArgs e)
    {
        await ForceCommitAsync(returnToReading: true);
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorChanges || !_isEditing)
        {
            return;
        }

        if (ReferenceEquals(sender, EditorBodyTextBox))
        {
            TryContinueMarkdownList();
            _previousEditorBodyText = EditorBodyTextBox.Text;
        }

        MarkEditorChanged();
    }

    private void MarkEditorChanged()
    {
        bool captureRevision = !_hasUnsavedChanges;
        _hasUnsavedChanges = true;
        if (captureRevision)
        {
            _ = CaptureRevisionOnceAsync();
        }
        RestartPersistenceTimers();
    }

    private void RestartPersistenceTimers()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
        if (!_draftTimer.IsRunning)
        {
            _draftTimer.Start();
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async void AutoSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await SaveEditorAsync();
    }

    private async void DraftTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isEditing || !_hasUnsavedChanges)
        {
            sender.Stop();
            return;
        }

        try
        {
            if (_selectedItem is null)
            {
                // Establish a stable note id before recovery snapshots begin.
                await SaveEditorAsync();
            }

            if (_selectedItem is not null && _hasUnsavedChanges)
            {
                await ViewModel.SaveDraftAsync(
                    _selectedItem.Id,
                    EditorTitleTextBox.Text,
                    EditorBodyTextBox.Text,
                    _editingFormat);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureContent] Draft save failed: {ex}");
        }
    }

    private void PreviewTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        RefreshMarkdownPreview();
    }

    private void RefreshMarkdownPreview()
    {
        if (!_isEditing || EditorPreviewPane.Visibility != Visibility.Visible)
        {
            return;
        }

        EditorMarkdownPreview.SetContent(
            EditorBodyTextBox.Text,
            _editingFormat,
            GetCurrentAttachmentModels(),
            allowRemoteImages: _settingsService.Settings.QuickCaptureAllowRemoteImages);
    }

    private async Task<bool> SaveEditorAsync()
    {
        if (!_isEditing || !_hasUnsavedChanges || _isSaving)
        {
            return true;
        }

        if (!_isCreating && _selectedItem is { IsRecent: true })
        {
            // Defensive recovery for transient state written by an older build.
            // A clipboard entry is never a writable note.
            _hasUnsavedChanges = false;
            ShowReadingSurface();
            return true;
        }

        string title = EditorTitleTextBox.Text;
        string body = EditorBodyTextBox.Text;
        if (string.IsNullOrWhiteSpace(title) &&
            string.IsNullOrWhiteSpace(body) &&
            _pendingAttachments.Count == 0)
        {
            return false;
        }

        if (!_isCreating &&
            _selectedItem is { } existingItem &&
            _pendingAttachments.Count == 0 &&
            string.Equals(
                string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim(),
                existingItem.Title?.Trim() ?? string.Empty,
                StringComparison.Ordinal) &&
            string.Equals(body, existingItem.Body, StringComparison.Ordinal) &&
            _editingAppearance == existingItem.AppearancePreset &&
            _editingFormat == existingItem.ContentFormat)
        {
            // A user may toggle a format on and off while inspecting the
            // result. Treat the restored document as a true no-op so its
            // modified time, revision count, and list order do not churn.
            _hasUnsavedChanges = false;
            await ViewModel.DiscardDraftAsync(existingItem.Id);
            return true;
        }

        _isSaving = true;
        try
        {
            if (_isCreating || _selectedItem is null)
            {
                QuickCaptureItem? created = await ViewModel.AddDetailedItemAsync(
                    title,
                    body,
                    _editingAppearance,
                    _editingFormat);
                if (created is null)
                {
                    return false;
                }

                await ViewModel.RefreshItemsAsync();
                _selectedItem = ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, created.Id, StringComparison.Ordinal));
                _isCreating = false;
                ItemsList.SelectedItem = _selectedItem;
                if (_selectedItem is not null && _pendingAttachments.Count > 0)
                {
                    _selectedItem = await ViewModel.AddAttachmentsAsync(
                        _selectedItem,
                        _pendingAttachments) ?? _selectedItem;
                    _pendingAttachments.Clear();
                    EditorBodyTextBox.Text = _selectedItem.Body;
                    body = EditorBodyTextBox.Text;
                }
            }
            else
            {
                bool updated = await ViewModel.EditItemDetailsAsync(
                    _selectedItem,
                    title,
                    body,
                    _editingAppearance,
                    _editingFormat);
                if (!updated)
                {
                    throw new InvalidOperationException("随记未能保存，请重试。");
                }
            }

            if (_selectedItem is not null)
            {
                await ViewModel.DiscardDraftAsync(_selectedItem.Id);
                await ViewModel.RefreshItemsAsync();
                _selectedItem = ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, _selectedItem.Id, StringComparison.Ordinal)) ?? _selectedItem;
                ItemsList.SelectedItem = _selectedItem;
            }

            bool unchanged = string.Equals(title, EditorTitleTextBox.Text, StringComparison.Ordinal) &&
                string.Equals(body, EditorBodyTextBox.Text, StringComparison.Ordinal);
            _hasUnsavedChanges = !unchanged;
            if (!unchanged)
            {
                RestartPersistenceTimers();
            }

            RenderReadingSurface();
            return true;
        }
        catch (Exception ex)
        {
            if (_selectedItem is not null)
            {
                try
                {
                    await ViewModel.SaveDraftAsync(
                        _selectedItem.Id,
                        title,
                        body,
                        _editingFormat);
                }
                catch
                {
                }
            }

            RaiseFeedback(
                "保存失败，内容已保留。",
                WidgetFeedbackSeverity.Error,
                "quick-capture-save-failed",
                "重试",
                async () => { await SaveEditorAsync(); });
            App.Log($"[QuickCaptureContent] Save failed widget={WidgetId}: {ex}");
            return false;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task ForceCommitAsync(bool returnToReading)
    {
        if (!_isEditing)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _draftTimer.Stop();
        _previewTimer.Stop();
        bool saved = await SaveEditorAsync();
        if (!returnToReading)
        {
            return;
        }

        if (!saved && _isCreating &&
            string.IsNullOrWhiteSpace(EditorTitleTextBox.Text) &&
            string.IsNullOrWhiteSpace(EditorBodyTextBox.Text) &&
            _pendingAttachments.Count == 0)
        {
            _isCreating = false;
            _isEditing = false;
            _hasUnsavedChanges = false;
            _pendingAttachments.Clear();
            ShowReadingSurface();
            ApplyResponsiveLayout();
            return;
        }

        if (!saved && _hasUnsavedChanges)
        {
            return;
        }

        _revisionCaptured = false;
        RenderReadingSurface();
        ShowReadingSurface();
        ApplyResponsiveLayout();
    }

    private async void EditorBodyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsControlPressed() && e.Key == Windows.System.VirtualKey.V &&
            ClipboardContainsImportableAttachment())
        {
            e.Handled = true;
            await ImportClipboardAttachmentsAsync();
            return;
        }

        if (IsControlPressed() && e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ForceCommitAsync(returnToReading: true);
            return;
        }

        if (IsControlPressed())
        {
            string? action = e.Key switch
            {
                Windows.System.VirtualKey.B => "Bold",
                Windows.System.VirtualKey.I => "Italic",
                Windows.System.VirtualKey.K => "Link",
                _ => null
            };
            if (action is not null)
            {
                e.Handled = true;
                ApplyMarkdownFormat(action);
                return;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            e.Handled = true;
            IndentSelection(outdent: IsShiftPressed());
        }
    }

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            ApplyMarkdownFormat(action);
        }
    }

    private void FormattingMoreButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)Resources["QuickCaptureFormattingMenuPresenterStyle"],
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        };

        MenuFlyoutItem CreateCommand(string text, string glyph)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = Math.Max(12, ViewModel.ActionIconSize)
                },
                Style = (Style)Resources["QuickCaptureCompactMenuItemStyle"]
            };
        }

        MenuFlyoutItem attachment = CreateCommand("附件", "\uE723");
        attachment.Click += AddAttachmentButton_Click;
        flyout.Items.Add(attachment);
        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach ((string text, string glyph, string action) in new[]
        {
            ("删除线", "\uEDE0", "Strike"),
            ("引用", "\uE8B2", "Quote"),
            ("代码", "\uE943", "Code"),
            ("表格", "\uE80A", "Table")
        })
        {
            MenuFlyoutItem command = CreateCommand(text, glyph);
            command.Click += (_, _) => ApplyMarkdownFormat(action);
            flyout.Items.Add(command);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem find = CreateCommand("在随记中查找", "\uE721");
        find.Click += FindInEditorButton_Click;
        flyout.Items.Add(find);
        MenuFlyoutItem convert = CreateCommand("转换为 Markdown", "\uE8AB");
        convert.Click += ConvertFormatButton_Click;
        flyout.Items.Add(convert);
        flyout.ShowAt(FormattingMoreButton);
    }

    private void ApplyMarkdownFormat(string action)
    {
        EditorViewportSnapshot viewport = PrepareEditorCommandViewport();
        EnsureMarkdownEditing();
        switch (action)
        {
            case "Bold":
                ToggleWrappedSelection("**", "**", "加粗文本");
                break;
            case "Italic":
                ToggleWrappedSelection("*", "*", "斜体文本");
                break;
            case "Strike":
                ToggleWrappedSelection("~~", "~~", "删除线文本");
                break;
            case "Code":
                if (EditorBodyTextBox.SelectedText.Contains('\n'))
                {
                    WrapSelection("```\n", "\n```", "代码");
                }
                else
                {
                    ToggleWrappedSelection("`", "`", "代码");
                }
                break;
            case "Link":
                WrapSelection("[", "](https://)", "链接文字");
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
                ReplaceSelection("| 列 1 | 列 2 |\n| --- | --- |\n| 内容 | 内容 |");
                break;
        }

        RestoreEditorViewport(viewport);
    }

    private void EditorBodyTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isEditing && EditorBodyTextBox.FocusState != FocusState.Unfocused)
        {
            _lastEditorViewport = CaptureEditorViewport();
        }
    }

    private void EditorBodyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        EditorField_LostFocus(sender, e);
        if (_isEditing)
        {
            _lastEditorViewport = CaptureEditorViewport();
        }
    }

    private void FormattingCommandBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditing)
        {
            _pendingEditorCommandViewport = CaptureEditorViewport();
        }
    }

    private EditorViewportSnapshot PrepareEditorCommandViewport()
    {
        EditorViewportSnapshot snapshot =
            _pendingEditorCommandViewport ??
            _lastEditorViewport ??
            CaptureEditorViewport();
        _pendingEditorCommandViewport = null;

        int start = Math.Clamp(snapshot.SelectionStart, 0, EditorBodyTextBox.Text.Length);
        int length = Math.Clamp(
            snapshot.SelectionLength,
            0,
            EditorBodyTextBox.Text.Length - start);
        EditorBodyTextBox.Select(start, length);
        return snapshot with { SelectionStart = start, SelectionLength = length };
    }

    private EditorViewportSnapshot CaptureEditorViewport()
    {
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(EditorBodyTextBox);
        return new EditorViewportSnapshot(
            EditorBodyTextBox.SelectionStart,
            EditorBodyTextBox.SelectionLength,
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0);
    }

    private void RestoreEditorViewport(EditorViewportSnapshot viewport)
    {
        int start = Math.Clamp(EditorBodyTextBox.SelectionStart, 0, EditorBodyTextBox.Text.Length);
        int length = Math.Clamp(
            EditorBodyTextBox.SelectionLength,
            0,
            EditorBodyTextBox.Text.Length - start);

        EditorBodyTextBox.Focus(FocusState.Programmatic);
        EditorBodyTextBox.Select(start, length);
        RestoreScrollOffset();
        _lastEditorViewport = viewport with
        {
            SelectionStart = start,
            SelectionLength = length
        };

        // TextBox can perform a second automatic ScrollToCaret during its next
        // layout pass. Reapply only the viewport offset after that pass; never
        // reselect text asynchronously, which could interfere with fast typing.
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            RestoreScrollOffset);

        void RestoreScrollOffset()
        {
            if (!_isEditing || EditorBodyTextBox.Visibility != Visibility.Visible)
            {
                return;
            }

            FindDescendant<ScrollViewer>(EditorBodyTextBox)?.ChangeView(
                viewport.HorizontalOffset,
                viewport.VerticalOffset,
                null,
                disableAnimation: true);
        }
    }

    private void EnsureMarkdownEditing()
    {
        if (_editingFormat == QuickCaptureContentFormat.Markdown)
        {
            return;
        }

        _editingFormat = QuickCaptureContentFormat.Markdown;
        MarkEditorChanged();
        RaiseFeedback(
            "已转换为 Markdown",
            WidgetFeedbackSeverity.Info,
            "quick-capture-format-converted");
    }

    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        int start = EditorBodyTextBox.SelectionStart;
        int length = EditorBodyTextBox.SelectionLength;
        string selected = length > 0
            ? EditorBodyTextBox.Text.Substring(start, length)
            : placeholder;
        string replacement = prefix + selected + suffix;
        ReplaceTextRange(start, length, replacement);
        EditorBodyTextBox.Select(start + prefix.Length, selected.Length);
    }

    private void ToggleWrappedSelection(string prefix, string suffix, string placeholder)
    {
        int start = EditorBodyTextBox.SelectionStart;
        int length = EditorBodyTextBox.SelectionLength;
        string text = EditorBodyTextBox.Text;
        string selected = length > 0
            ? text.Substring(start, length)
            : string.Empty;

        if (length > 0 &&
            selected.Length >= prefix.Length + suffix.Length &&
            selected.StartsWith(prefix, StringComparison.Ordinal) &&
            selected.EndsWith(suffix, StringComparison.Ordinal))
        {
            string unwrapped = selected[prefix.Length..^suffix.Length];
            ReplaceTextRange(start, length, unwrapped);
            EditorBodyTextBox.Select(start, unwrapped.Length);
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
            EditorBodyTextBox.Select(start - prefix.Length, selected.Length);
            return;
        }

        WrapSelection(prefix, suffix, placeholder);
    }

    private void PrefixSelectedLines(string prefix)
    {
        int start = EditorBodyTextBox.SelectionStart;
        int end = start + EditorBodyTextBox.SelectionLength;
        string text = EditorBodyTextBox.Text;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        int lookupEnd = end > start && end <= text.Length && text[end - 1] == '\n'
            ? end - 1
            : end;
        int lineEnd = text.IndexOf('\n', lookupEnd);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        string block = text[lineStart..lineEnd];
        string[] lines = block.Split('\n');
        bool removePrefix = lines.Any(line => line.Length > 0) &&
            lines.Where(line => line.Length > 0)
                .All(line => line.StartsWith(prefix, StringComparison.Ordinal));
        string replacement = string.Join(
            "\n",
            lines.Select(line => removePrefix && line.StartsWith(prefix, StringComparison.Ordinal)
                ? line[prefix.Length..]
                : prefix + line));
        ReplaceTextRange(lineStart, lineEnd - lineStart, replacement);
        EditorBodyTextBox.Select(lineStart, replacement.Length);
    }

    private void ReplaceSelection(string replacement)
    {
        int start = EditorBodyTextBox.SelectionStart;
        ReplaceTextRange(start, EditorBodyTextBox.SelectionLength, replacement);
        EditorBodyTextBox.Select(start + replacement.Length, 0);
    }

    private void ReplaceTextRange(int start, int length, string replacement)
    {
        start = Math.Clamp(start, 0, EditorBodyTextBox.Text.Length);
        length = Math.Clamp(length, 0, EditorBodyTextBox.Text.Length - start);
        _suppressEditorChanges = true;
        try
        {
            // Replacing SelectedText keeps the native TextBox viewport and undo
            // history intact. Assigning the entire Text property made every
            // formatting command look like a document reload.
            EditorBodyTextBox.Select(start, length);
            EditorBodyTextBox.SelectedText = replacement;
            _previousEditorBodyText = EditorBodyTextBox.Text;
        }
        finally
        {
            _suppressEditorChanges = false;
        }
        MarkEditorChanged();
    }

    private void IndentSelection(bool outdent)
    {
        int start = EditorBodyTextBox.SelectionStart;
        int length = EditorBodyTextBox.SelectionLength;
        if (length == 0)
        {
            if (outdent)
            {
                int lineStart = EditorBodyTextBox.Text.LastIndexOf('\n', Math.Max(0, start - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                int remove = EditorBodyTextBox.Text.AsSpan(lineStart).StartsWith("    ") ? 4 :
                    EditorBodyTextBox.Text.AsSpan(lineStart).StartsWith("\t") ? 1 : 0;
                if (remove > 0)
                {
                    ReplaceTextRange(lineStart, remove, string.Empty);
                    EditorBodyTextBox.Select(Math.Max(lineStart, start - remove), 0);
                }
            }
            else
            {
                ReplaceTextRange(start, 0, "    ");
                EditorBodyTextBox.Select(start + 4, 0);
            }
            return;
        }

        string selected = EditorBodyTextBox.Text.Substring(start, length);
        string replacement = string.Join("\n", selected.Split('\n').Select(line =>
            outdent
                ? line.StartsWith("    ", StringComparison.Ordinal) ? line[4..]
                    : line.StartsWith('\t') ? line[1..] : line
                : "    " + line));
        ReplaceTextRange(start, length, replacement);
        EditorBodyTextBox.Select(start, replacement.Length);
    }

    private void TryContinueMarkdownList()
    {
        if (_suppressEditorChanges || _editingFormat != QuickCaptureContentFormat.Markdown)
        {
            return;
        }

        string current = EditorBodyTextBox.Text;
        int caret = EditorBodyTextBox.SelectionStart;
        if (current.Length != _previousEditorBodyText.Length + 1 ||
            caret <= 0 || current[caret - 1] != '\n')
        {
            return;
        }

        int previousLineEnd = caret - 1;
        int previousLineStart = current.LastIndexOf('\n', Math.Max(0, previousLineEnd - 1));
        previousLineStart = previousLineStart < 0 ? 0 : previousLineStart + 1;
        string previousLine = current[previousLineStart..previousLineEnd];
        Match match = MarkdownListLineRegex().Match(previousLine);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["content"].Value))
        {
            return;
        }

        string marker = match.Groups["marker"].Value;
        if (int.TryParse(match.Groups["number"].Value, out int number))
        {
            marker = $"{number + 1}{match.Groups["punctuation"].Value}";
        }

        string continuation = match.Groups["indent"].Value + marker + " " + match.Groups["task"].Value;
        ReplaceTextRange(caret, 0, continuation);
        EditorBodyTextBox.Select(caret + continuation.Length, 0);
    }

    private async void MarkdownView_TaskToggleRequested(
        object? sender,
        QuickCaptureTaskToggleRequestedEventArgs e)
    {
        if (_isEditing)
        {
            if (_markdownService.TryToggleTask(EditorBodyTextBox.Text, e.TaskIndex, out string updated))
            {
                _suppressEditorChanges = true;
                EditorBodyTextBox.Text = updated;
                _previousEditorBodyText = updated;
                _suppressEditorChanges = false;
                MarkEditorChanged();
                RefreshMarkdownPreview();
            }
            return;
        }

        if (_selectedItem is not
            { IsRecent: false, ContentFormat: QuickCaptureContentFormat.Markdown } item ||
            !_markdownService.TryToggleTask(item.Body, e.TaskIndex, out string toggled))
        {
            return;
        }

        await ViewModel.CreateRevisionAsync(item.Id);
        await ViewModel.EditItemDetailsAsync(
            item,
            item.Title,
            toggled,
            item.AppearancePreset,
            QuickCaptureContentFormat.Markdown);
        await ViewModel.RefreshItemsAsync();
    }

    private async void ConvertFormatButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureMarkdownEditing();
        await SaveEditorAsync();
    }

    private async void FindInEditorButton_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "查找内容"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "在随记中查找",
            Content = input,
            PrimaryButtonText = "查找下一处",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        int start = Math.Min(
            EditorBodyTextBox.Text.Length,
            EditorBodyTextBox.SelectionStart + EditorBodyTextBox.SelectionLength);
        int index = EditorBodyTextBox.Text.IndexOf(
            input.Text,
            start,
            StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            index = EditorBodyTextBox.Text.IndexOf(
                input.Text,
                StringComparison.CurrentCultureIgnoreCase);
        }

        if (index >= 0)
        {
            EditorBodyTextBox.Focus(FocusState.Programmatic);
            EditorBodyTextBox.SelectionStart = index;
            EditorBodyTextBox.SelectionLength = input.Text.Length;
        }
        else
        {
            RaiseFeedback("未找到匹配内容。", deduplicationKey: "quick-capture-find-empty");
        }
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?:(?<number>\d+)(?<punctuation>[.)])|(?<marker>[-+*]))\s+(?<task>\[[ xX]\]\s+)?(?<content>.*)$")]
    private static partial Regex MarkdownListLineRegex();

    private readonly record struct EditorViewportSnapshot(
        int SelectionStart,
        int SelectionLength,
        double HorizontalOffset,
        double VerticalOffset);
}

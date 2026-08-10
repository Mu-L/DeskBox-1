using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// Quick Capture boundary around DeskBox's native safe Markdown presenter.
/// Consumers keep raw Markdown and task toggles are reported by source order.
/// </summary>
public sealed partial class QuickCaptureMarkdownView : UserControl
{
    private string _source = string.Empty;
    private QuickCaptureContentFormat _format;
    private IReadOnlyList<TodoAttachment> _attachments = [];
    private bool _allowRemoteImages;

    public QuickCaptureMarkdownView()
    {
        InitializeComponent();
        MarkdownPresenter.TaskToggleRequested += MarkdownPresenter_TaskToggleRequested;
    }

    public event EventHandler<QuickCaptureTaskToggleRequestedEventArgs>? TaskToggleRequested;

    public bool AreTaskListsInteractive
    {
        get => MarkdownPresenter.AreTaskListsInteractive;
        set => MarkdownPresenter.AreTaskListsInteractive = value;
    }

    public void SetContent(
        string? source,
        QuickCaptureContentFormat format,
        IReadOnlyList<TodoAttachment>? attachments = null,
        bool allowRemoteImages = false)
    {
        _source = source ?? string.Empty;
        _format = format;
        _attachments = attachments ?? [];
        _allowRemoteImages = allowRemoteImages;
        Render();
    }

    private void Render()
    {
        bool markdown = _format == QuickCaptureContentFormat.Markdown;
        MarkdownPresenter.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        PlainTextPresenter.Visibility = markdown ? Visibility.Collapsed : Visibility.Visible;
        if (!markdown)
        {
            PlainTextPresenter.Text = _source;
            MarkdownPresenter.Markdown = string.Empty;
            return;
        }

        PlainTextPresenter.Text = string.Empty;
        MarkdownPresenter.AllowRemoteImages = _allowRemoteImages;
        MarkdownPresenter.AttachmentResolver = attachmentId => _attachments
            .FirstOrDefault(attachment => string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))
            ?.FilePath;
        MarkdownPresenter.Markdown = _source;
    }

    private void MarkdownPresenter_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        TaskToggleRequested?.Invoke(
            this,
            new QuickCaptureTaskToggleRequestedEventArgs(e.TaskIndex));
    }
}

public sealed class QuickCaptureTaskToggleRequestedEventArgs(int taskIndex) : EventArgs
{
    public int TaskIndex { get; } = taskIndex;
}

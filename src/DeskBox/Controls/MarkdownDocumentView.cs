using DeskBox.Models;
using DeskBox.Services;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace DeskBox.Controls;

/// <summary>
/// Safe native Markdown reader. All textual blocks share one RichTextBlock so
/// users can select and copy across paragraphs, headings, lists, and tables.
/// </summary>
public sealed class MarkdownDocumentView : UserControl
{
    private const double BodyLineHeightRatio = 1.72;
    private const double TaskLineHeightRatio = 2.16;
    private const double HeadingLineHeightRatio = 1.42;
    private const double CodeLineHeightRatio = 1.60;
    private const double ParagraphSpacing = 5;
    private const double ListItemSpacing = 3;

    private readonly RichTextBlock _documentText = new()
    {
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ScrollViewer _scrollViewer = new();
    private readonly MarkdownDocumentService _markdownService;
    private Func<string, string?>? _attachmentResolver;
    private int _taskListIndex;
    private bool _isLoaded;
    private bool _renderQueued;

    public MarkdownDocumentView()
        : this(new MarkdownDocumentService())
    {
    }

    internal MarkdownDocumentView(MarkdownDocumentService markdownService)
    {
        _markdownService = markdownService;
        _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ApplyContentHost();
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => QueueRender());
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueRender());
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            QueueRender();
        };
        Unloaded += (_, _) => _isLoaded = false;
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(string.Empty, OnDocumentPropertyChanged));

    public static readonly DependencyProperty ContentFormatProperty = DependencyProperty.Register(
        nameof(ContentFormat),
        typeof(TextContentFormat),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(TextContentFormat.Markdown, OnDocumentPropertyChanged));

    public static readonly DependencyProperty AllowRemoteImagesProperty = DependencyProperty.Register(
        nameof(AllowRemoteImages),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(false, OnDocumentPropertyChanged));

    public static readonly DependencyProperty AreTaskListsInteractiveProperty = DependencyProperty.Register(
        nameof(AreTaskListsInteractive),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(false, OnDocumentPropertyChanged));

    public static readonly DependencyProperty UseInternalScrollViewerProperty = DependencyProperty.Register(
        nameof(UseInternalScrollViewer),
        typeof(bool),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(true, OnContentHostPropertyChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public TextContentFormat ContentFormat
    {
        get => (TextContentFormat)GetValue(ContentFormatProperty);
        set => SetValue(ContentFormatProperty, value);
    }

    public bool AllowRemoteImages
    {
        get => (bool)GetValue(AllowRemoteImagesProperty);
        set => SetValue(AllowRemoteImagesProperty, value);
    }

    public bool AreTaskListsInteractive
    {
        get => (bool)GetValue(AreTaskListsInteractiveProperty);
        set => SetValue(AreTaskListsInteractiveProperty, value);
    }

    /// <summary>
    /// Controls whether the reader owns its vertical scrolling. Turn this off
    /// when the reader is hosted inside a page-level ScrollViewer.
    /// </summary>
    public bool UseInternalScrollViewer
    {
        get => (bool)GetValue(UseInternalScrollViewerProperty);
        set => SetValue(UseInternalScrollViewerProperty, value);
    }

    public Func<string, string?>? AttachmentResolver
    {
        get => _attachmentResolver;
        set
        {
            _attachmentResolver = value;
            QueueRender();
        }
    }

    public bool WasTruncated { get; private set; }

    public event EventHandler<MarkdownTaskToggleRequestedEventArgs>? TaskToggleRequested;

    public event EventHandler<MarkdownAttachmentRequestedEventArgs>? AttachmentOpenRequested;

    public void Refresh() => QueueRender();

    private static void OnDocumentPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).QueueRender();

    private void QueueRender()
    {
        if (!_isLoaded || _renderQueued)
        {
            return;
        }

        _renderQueued = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _renderQueued = false;
                if (_isLoaded && Visibility == Visibility.Visible)
                {
                    Render();
                }
            });
    }

    private static void OnContentHostPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).ApplyContentHost();

    private void ApplyContentHost()
    {
        if (UseInternalScrollViewer)
        {
            if (!ReferenceEquals(_scrollViewer.Content, _documentText))
            {
                _scrollViewer.Content = _documentText;
            }

            Content = _scrollViewer;
            return;
        }

        if (ReferenceEquals(_scrollViewer.Content, _documentText))
        {
            _scrollViewer.Content = null;
        }

        Content = _documentText;
    }

    private void Render()
    {
        double verticalOffset = _scrollViewer.VerticalOffset;
        string source = Markdown ?? string.Empty;
        _documentText.Blocks.Clear();
        _taskListIndex = 0;
        WasTruncated = false;
        if (source.Length == 0)
        {
            return;
        }

        if (ContentFormat == TextContentFormat.PlainText)
        {
            var paragraph = CreateParagraph(BaseFontSize, FontWeights.Normal);
            AppendText(paragraph.Inlines, source);
            _documentText.Blocks.Add(paragraph);
            RestoreScrollOffset(source, verticalOffset);
            return;
        }

        MarkdownParseResult document = _markdownService.Parse(source);
        WasTruncated = document.WasTruncated;
        foreach (MdBlock block in document.Document)
        {
            AppendBlock(block, quoteDepth: 0, listDepth: 0);
        }

        RestoreScrollOffset(source, verticalOffset);
    }

    private void RestoreScrollOffset(string renderedSource, double verticalOffset)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (string.Equals(renderedSource, Markdown, StringComparison.Ordinal))
                {
                    _scrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: true);
                }
            });
    }

    private double BaseFontSize => double.IsFinite(FontSize) && FontSize > 0
        ? FontSize
        : 14;

    private void AppendBlock(MdBlock block, int quoteDepth, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendLeafParagraph(
                    heading,
                    heading.Level switch
                    {
                        1 => BaseFontSize * 1.62,
                        2 => BaseFontSize * 1.38,
                        3 => BaseFontSize * 1.20,
                        _ => BaseFontSize * 1.08
                    },
                    FontWeights.SemiBold,
                    quoteDepth,
                    listDepth);
                break;
            case ParagraphBlock paragraph:
                AppendLeafParagraph(
                    paragraph,
                    BaseFontSize,
                    FontWeights.Normal,
                    quoteDepth,
                    listDepth);
                break;
            case QuoteBlock quote:
                foreach (MdBlock child in quote)
                {
                    AppendBlock(child, quoteDepth + 1, listDepth);
                }
                break;
            case ListBlock list:
                AppendList(list, quoteDepth, listDepth);
                break;
            case FencedCodeBlock code:
                AppendCode(code, quoteDepth, listDepth);
                break;
            case CodeBlock code:
                AppendCode(code, quoteDepth, listDepth);
                break;
            case ThematicBreakBlock:
                var separator = CreateParagraph(BaseFontSize, FontWeights.Normal);
                separator.Margin = new Thickness(Indent(quoteDepth, listDepth), 5, 0, 5);
                separator.Inlines.Add(new Run
                {
                    Text = "────────────────",
                    Foreground = BrushResource("DividerStrokeColorDefaultBrush")
                });
                _documentText.Blocks.Add(separator);
                break;
            case Table table:
                AppendTable(table, quoteDepth, listDepth);
                break;
            case HtmlBlock:
                break;
            case ContainerBlock container:
                foreach (MdBlock child in container)
                {
                    AppendBlock(child, quoteDepth, listDepth);
                }
                break;
            case LeafBlock leaf:
                AppendLeafParagraph(
                    leaf,
                    BaseFontSize,
                    FontWeights.Normal,
                    quoteDepth,
                    listDepth);
                break;
        }
    }

    private void AppendLeafParagraph(
        LeafBlock leaf,
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight,
        int quoteDepth,
        int listDepth)
    {
        Paragraph paragraph = CreateParagraph(fontSize, fontWeight);
        ApplyParagraphIndent(paragraph, quoteDepth, listDepth);
        if (fontSize > BaseFontSize * 1.05)
        {
            paragraph.LineHeight = Math.Ceiling(fontSize * HeadingLineHeightRatio);
            paragraph.Margin = new Thickness(paragraph.Margin.Left, 9, 0, 5);
        }
        AppendQuoteMarker(paragraph, quoteDepth);
        if (leaf.Inline is { } container)
        {
            AppendContainer(paragraph.Inlines, container);
        }
        else if (leaf is CodeBlock code)
        {
            AppendText(paragraph.Inlines, code.Lines.ToString());
        }

        _documentText.Blocks.Add(paragraph);
    }

    private void AppendList(ListBlock list, int quoteDepth, int listDepth)
    {
        int number = int.TryParse(list.OrderedStart, out int orderedStart) ? orderedStart : 1;
        foreach (ListItemBlock item in list.OfType<ListItemBlock>())
        {
            bool? taskState = FindTaskState(item);
            MdBlock? firstContent = item.FirstOrDefault(child => child is not ListBlock);
            var paragraph = CreateParagraph(BaseFontSize, FontWeights.Normal);
            ApplyParagraphIndent(paragraph, quoteDepth, listDepth);
            paragraph.Margin = new Thickness(
                paragraph.Margin.Left,
                ListItemSpacing,
                0,
                ListItemSpacing);
            AppendQuoteMarker(paragraph, quoteDepth);
            if (taskState is { } isChecked)
            {
                paragraph.LineHeight = Math.Max(
                    paragraph.LineHeight,
                    Math.Ceiling(BaseFontSize * TaskLineHeightRatio));
                paragraph.Inlines.Add(new InlineUIContainer { Child = CreateTaskMarker(isChecked) });
                paragraph.Inlines.Add(new Run { Text = " " });
            }
            else
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = list.IsOrdered ? $"{number++}. " : "• ",
                    Foreground = BrushResource("TextFillColorSecondaryBrush")
                });
            }

            if (firstContent is LeafBlock leaf && leaf.Inline is { } inline)
            {
                AppendContainer(paragraph.Inlines, inline);
            }
            else if (firstContent is ContainerBlock container)
            {
                AppendBlockText(paragraph.Inlines, container);
            }
            _documentText.Blocks.Add(paragraph);

            foreach (MdBlock child in item)
            {
                if (ReferenceEquals(child, firstContent))
                {
                    continue;
                }

                AppendBlock(child, quoteDepth, listDepth + 1);
            }
        }
    }

    private void AppendCode(CodeBlock code, int quoteDepth, int listDepth)
    {
        var paragraph = CreateParagraph(Math.Max(11, BaseFontSize - 1), FontWeights.Normal);
        paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        paragraph.LineHeight = Math.Ceiling(paragraph.FontSize * CodeLineHeightRatio);
        paragraph.Margin = new Thickness(Indent(quoteDepth, listDepth), 8, 0, 8);
        AppendQuoteMarker(paragraph, quoteDepth);
        AppendText(paragraph.Inlines, code.Lines.ToString());
        _documentText.Blocks.Add(paragraph);
    }

    private void AppendTable(Table table, int quoteDepth, int listDepth)
    {
        foreach (TableRow row in table.OfType<TableRow>())
        {
            var paragraph = CreateParagraph(
                Math.Max(11, BaseFontSize - 0.5),
                row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal);
            paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            ApplyParagraphIndent(paragraph, quoteDepth, listDepth);
            paragraph.LineHeight = Math.Ceiling(paragraph.FontSize * CodeLineHeightRatio);
            paragraph.Margin = new Thickness(paragraph.Margin.Left, 2, 0, 2);
            AppendQuoteMarker(paragraph, quoteDepth);
            foreach (TableCell cell in row.OfType<TableCell>())
            {
                paragraph.Inlines.Add(new Run { Text = "| " });
                AppendBlockText(paragraph.Inlines, cell);
                paragraph.Inlines.Add(new Run { Text = " " });
            }
            paragraph.Inlines.Add(new Run { Text = "|" });
            _documentText.Blocks.Add(paragraph);
        }
    }

    private static Paragraph CreateParagraph(
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight) => new()
    {
        FontSize = fontSize,
        FontWeight = fontWeight,
        LineHeight = Math.Ceiling(fontSize * BodyLineHeightRatio),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Margin = new Thickness(0, ParagraphSpacing, 0, ParagraphSpacing)
    };

    private static double Indent(int quoteDepth, int listDepth) =>
        quoteDepth * 10 + listDepth * 18;

    private static void ApplyParagraphIndent(Paragraph paragraph, int quoteDepth, int listDepth) =>
        paragraph.Margin = new Thickness(
            Indent(quoteDepth, listDepth),
            paragraph.Margin.Top,
            0,
            paragraph.Margin.Bottom);

    private static void AppendQuoteMarker(Paragraph paragraph, int quoteDepth)
    {
        if (quoteDepth > 0)
        {
            paragraph.Inlines.Add(new Run
            {
                Text = "▎ ",
                Foreground = BrushResource("AccentFillColorDefaultBrush")
            });
        }
    }

    private static void AppendText(InlineCollection destination, string? text)
    {
        string value = text ?? string.Empty;
        string[] parts = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (int index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                destination.Add(new LineBreak());
            }
            if (parts[index].Length > 0)
            {
                destination.Add(new Run { Text = parts[index] });
            }
        }
    }

    private void AppendBlockText(InlineCollection destination, ContainerBlock container)
    {
        bool needsSeparator = false;
        foreach (MdBlock block in container)
        {
            if (needsSeparator)
            {
                destination.Add(new LineBreak());
            }

            if (block is LeafBlock leaf && leaf.Inline is { } inline)
            {
                AppendContainer(destination, inline);
                needsSeparator = true;
            }
            else if (block is ContainerBlock nested)
            {
                AppendBlockText(destination, nested);
                needsSeparator = true;
            }
        }
    }

    private CheckBox CreateTaskMarker(bool isChecked)
    {
        var marker = new CheckBox
        {
            IsChecked = isChecked,
            IsEnabled = AreTaskListsInteractive,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform { Y = 6 },
            Tag = _taskListIndex++
        };
        if (AreTaskListsInteractive)
        {
            marker.Click += TaskMarker_Click;
        }
        return marker;
    }

    private void TaskMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int taskIndex })
        {
            TaskToggleRequested?.Invoke(
                this,
                new MarkdownTaskToggleRequestedEventArgs(taskIndex));
        }
    }

    private void AppendContainer(InlineCollection destination, ContainerInline container)
    {
        for (MdInline? current = container.FirstChild; current is not null; current = current.NextSibling)
        {
            AppendInline(destination, current);
        }
    }

    private void AppendInline(InlineCollection destination, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                destination.Add(new Run { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                destination.Add(new Run
                {
                    Text = code.Content,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = BrushResource("SystemFillColorCriticalBrush")
                });
                break;
            case LineBreakInline:
                destination.Add(new LineBreak());
                break;
            case HtmlInline:
                break;
            case LinkInline link when link.IsImage:
                AppendImage(destination, link);
                break;
            case LinkInline link:
                AppendLink(destination, link);
                break;
            case EmphasisInline emphasis:
                var span = new Span();
                if (emphasis.DelimiterChar == '~')
                {
                    span.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                }
                else if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                AppendContainer(span.Inlines, emphasis);
                destination.Add(span);
                break;
            case ContainerInline nested:
                AppendContainer(destination, nested);
                break;
        }
    }

    private void AppendImage(InlineCollection destination, LinkInline link)
    {
        string? source = link.Url;
        if (MarkdownDocumentService.TryGetAttachmentId(source, out string? attachmentId))
        {
            source = AttachmentResolver?.Invoke(attachmentId!);
        }
        else if (!AllowRemoteImages || !MarkdownDocumentService.IsRemoteImage(source))
        {
            AppendBlockedImageLabel(destination, link);
            return;
        }

        if (!TryCreateImageUri(source, out Uri? uri))
        {
            AppendBlockedImageLabel(destination, link);
            return;
        }

        var image = new Image
        {
            Source = new BitmapImage(uri),
            MaxWidth = 480,
            MaxHeight = 320,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetName(
            image,
            string.IsNullOrWhiteSpace(link.Title) ? "Markdown image" : link.Title);
        destination.Add(new InlineUIContainer { Child = image });
    }

    private static void AppendBlockedImageLabel(InlineCollection destination, LinkInline link) =>
        destination.Add(new Run
        {
            Text = string.IsNullOrWhiteSpace(link.Title) ? "[image]" : $"[{link.Title}]"
        });

    private void AppendLink(InlineCollection destination, LinkInline link)
    {
        if (!MarkdownDocumentService.IsAllowedLink(link.Url))
        {
            AppendContainer(destination, link);
            return;
        }

        if (MarkdownDocumentService.TryGetAttachmentId(link.Url, out string? attachmentId))
        {
            var attachmentLink = new Hyperlink();
            AppendContainer(attachmentLink.Inlines, link);
            attachmentLink.Click += (_, _) => AttachmentOpenRequested?.Invoke(
                this,
                new MarkdownAttachmentRequestedEventArgs(attachmentId!));
            destination.Add(attachmentLink);
            return;
        }

        var hyperlink = new Hyperlink { NavigateUri = new Uri(link.Url!) };
        AppendContainer(hyperlink.Inlines, link);
        destination.Add(hyperlink);
    }

    private static bool? FindTaskState(ListItemBlock item)
    {
        foreach (LeafBlock leaf in item.Descendants<LeafBlock>())
        {
            if (leaf.Inline?.FirstChild is { } first &&
                string.Equals(first.GetType().Name, "TaskList", StringComparison.Ordinal))
            {
                return first.GetType().GetProperty("Checked")?.GetValue(first) as bool?;
            }
        }
        return null;
    }

    private static bool TryCreateImageUri(string? source, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }
        if (File.Exists(source))
        {
            uri = new Uri(Path.GetFullPath(source));
            return true;
        }
        return Uri.TryCreate(source, UriKind.Absolute, out uri);
    }

    private static Brush BrushResource(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object? resource) == true &&
            resource is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

public sealed class MarkdownTaskToggleRequestedEventArgs(int taskIndex) : EventArgs
{
    public int TaskIndex { get; } = taskIndex;
}

public sealed class MarkdownAttachmentRequestedEventArgs(string attachmentId) : EventArgs
{
    public string AttachmentId { get; } = attachmentId;
}

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
using Microsoft.UI.Xaml.Shapes;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace DeskBox.Controls;

/// <summary>
/// Safe, DeskBox-owned Markdown reader built from native WinUI elements. It
/// never renders raw HTML, blocks unsafe navigation schemes, and resolves local
/// attachments only through the host-provided resolver.
/// </summary>
public sealed class MarkdownDocumentView : UserControl
{
    private readonly StackPanel _documentPanel = new() { Spacing = 8 };
    private readonly ScrollViewer _scrollViewer = new();
    private readonly MarkdownDocumentService _markdownService;
    private Func<string, string?>? _attachmentResolver;
    private int _taskListIndex;

    public MarkdownDocumentView()
        : this(new MarkdownDocumentService())
    {
    }

    internal MarkdownDocumentView(MarkdownDocumentService markdownService)
    {
        _markdownService = markdownService;
        _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scrollViewer.Content = _documentPanel;
        ApplyContentHost();
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => Render());
        Loaded += (_, _) => Render();
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
            Render();
        }
    }

    public bool WasTruncated { get; private set; }

    public event EventHandler<MarkdownTaskToggleRequestedEventArgs>? TaskToggleRequested;

    public event EventHandler<MarkdownAttachmentRequestedEventArgs>? AttachmentOpenRequested;

    public void Refresh() => Render();

    private static void OnDocumentPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).Render();

    private static void OnContentHostPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((MarkdownDocumentView)sender).ApplyContentHost();

    private void ApplyContentHost()
    {
        if (UseInternalScrollViewer)
        {
            if (!ReferenceEquals(_scrollViewer.Content, _documentPanel))
            {
                _scrollViewer.Content = _documentPanel;
            }

            Content = _scrollViewer;
            return;
        }

        if (ReferenceEquals(_scrollViewer.Content, _documentPanel))
        {
            _scrollViewer.Content = null;
        }

        Content = _documentPanel;
    }

    private void Render()
    {
        _documentPanel.Children.Clear();
        _taskListIndex = 0;
        WasTruncated = false;
        string source = Markdown ?? string.Empty;
        if (source.Length == 0)
        {
            return;
        }

        if (ContentFormat == TextContentFormat.PlainText)
        {
            _documentPanel.Children.Add(new TextBlock
            {
                FontSize = BaseFontSize,
                IsTextSelectionEnabled = true,
                Text = source,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        MarkdownParseResult document = _markdownService.Parse(source);
        WasTruncated = document.WasTruncated;
        foreach (MdBlock block in document.Document)
        {
            UIElement? element = RenderBlock(block);
            if (element is not null)
            {
                _documentPanel.Children.Add(element);
            }
        }
    }

    private double BaseFontSize => double.IsFinite(FontSize) && FontSize > 0
        ? FontSize
        : 14;

    private UIElement? RenderBlock(MdBlock block) => block switch
    {
        HeadingBlock heading => RenderLeaf(
            heading,
            heading.Level switch
            {
                1 => BaseFontSize * 1.62,
                2 => BaseFontSize * 1.38,
                3 => BaseFontSize * 1.20,
                _ => BaseFontSize * 1.08
            },
            FontWeights.SemiBold),
        ParagraphBlock paragraph => RenderLeaf(paragraph, BaseFontSize, FontWeights.Normal),
        QuoteBlock quote => RenderQuote(quote),
        ListBlock list => RenderList(list),
        FencedCodeBlock code => RenderCode(code),
        CodeBlock code => RenderCode(code),
        ThematicBreakBlock => new Rectangle
        {
            Height = 1,
            Margin = new Thickness(0, 5, 0, 5),
            Fill = BrushResource("DividerStrokeColorDefaultBrush")
        },
        Table table => RenderTable(table),
        HtmlBlock => null,
        ContainerBlock container => RenderContainer(container),
        LeafBlock leaf => RenderLeaf(leaf, BaseFontSize, FontWeights.Normal),
        _ => null
    };

    private UIElement RenderContainer(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (MdBlock child in container)
        {
            UIElement? element = RenderBlock(child);
            if (element is not null)
            {
                panel.Children.Add(element);
            }
        }

        return panel;
    }

    private RichTextBlock RenderLeaf(
        LeafBlock leaf,
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight)
    {
        var text = new RichTextBlock
        {
            FontSize = fontSize,
            FontWeight = fontWeight,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };
        var paragraph = new Paragraph();
        if (leaf.Inline is { } container)
        {
            AppendContainer(paragraph.Inlines, container);
        }
        else if (leaf is CodeBlock code)
        {
            paragraph.Inlines.Add(new Run { Text = code.Lines.ToString() });
        }

        text.Blocks.Add(paragraph);
        return text;
    }

    private UIElement RenderQuote(QuoteBlock quote) => new Border
    {
        BorderBrush = BrushResource("AccentFillColorDefaultBrush"),
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(10, 2, 0, 2),
        Child = RenderContainer(quote)
    };

    private UIElement RenderCode(CodeBlock code) => new Border
    {
        Padding = new Thickness(10, 8, 10, 8),
        CornerRadius = new CornerRadius(6),
        Background = BrushResource("CardBackgroundFillColorSecondaryBrush"),
        Child = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = Math.Max(11, BaseFontSize - 1),
            IsTextSelectionEnabled = true,
            Text = code.Lines.ToString(),
            TextWrapping = TextWrapping.Wrap
        }
    };

    private UIElement RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        int number = int.TryParse(list.OrderedStart, out int orderedStart) ? orderedStart : 1;
        foreach (ListItemBlock item in list.OfType<ListItemBlock>())
        {
            bool? taskState = FindTaskState(item);
            var row = new Grid { ColumnSpacing = 7 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement marker = taskState is { } isChecked
                ? CreateTaskMarker(isChecked)
                : new TextBlock
                {
                    FontSize = BaseFontSize,
                    Text = list.IsOrdered ? $"{number++}." : "•",
                    Foreground = BrushResource("TextFillColorSecondaryBrush")
                };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);
            FrameworkElement body = (FrameworkElement)RenderContainer(item);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private CheckBox CreateTaskMarker(bool isChecked)
    {
        var marker = new CheckBox
        {
            IsChecked = isChecked,
            IsEnabled = AreTaskListsInteractive,
            MinWidth = 0,
            Margin = new Thickness(0, -2, 0, 0),
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

    private UIElement RenderTable(Table table)
    {
        var grid = new Grid
        {
            BorderBrush = BrushResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1)
        };
        int columnCount = table.OfType<TableRow>()
            .Select(row => row.Count)
            .DefaultIfEmpty(1)
            .Max();
        for (int column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 72
            });
        }

        int rowIndex = 0;
        foreach (TableRow row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int columnIndex = 0;
            foreach (TableCell cell in row.OfType<TableCell>())
            {
                var border = new Border
                {
                    Padding = new Thickness(7, 5, 7, 5),
                    BorderBrush = BrushResource("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = row.IsHeader
                        ? BrushResource("CardBackgroundFillColorSecondaryBrush")
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Child = RenderContainer(cell)
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, Math.Min(columnIndex, columnCount - 1));
                grid.Children.Add(border);
                columnIndex++;
            }

            rowIndex++;
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = grid
        };
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
            uri = new Uri(System.IO.Path.GetFullPath(source));
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

using DeskBox.Services;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdBlock = Markdig.Syntax.Block;

namespace DeskBox.Controls;

/// <summary>
/// DeskBox-owned, read-only Markdown presenter. It intentionally does not use
/// Toolkit Labs and only turns the safe Markdig AST into native XAML elements.
/// </summary>
public sealed class TodoMarkdownPresenter : UserControl
{
    private readonly StackPanel _documentPanel = new() { Spacing = 8 };
    private readonly ScrollViewer _scrollViewer = new();
    private readonly TodoMarkdownService _markdownService;
    private int _taskListIndex;

    public TodoMarkdownPresenter()
        : this(new TodoMarkdownService())
    {
    }

    public TodoMarkdownPresenter(TodoMarkdownService markdownService)
    {
        _markdownService = markdownService;
        _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scrollViewer.Content = _documentPanel;
        Content = _scrollViewer;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(TodoMarkdownPresenter),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public bool AllowRemoteImages
    {
        get => (bool)GetValue(AllowRemoteImagesProperty);
        set => SetValue(AllowRemoteImagesProperty, value);
    }

    public static readonly DependencyProperty AllowRemoteImagesProperty = DependencyProperty.Register(
        nameof(AllowRemoteImages),
        typeof(bool),
        typeof(TodoMarkdownPresenter),
        new PropertyMetadata(false, OnRenderingOptionChanged));

    public bool AreTaskListsInteractive
    {
        get => (bool)GetValue(AreTaskListsInteractiveProperty);
        set => SetValue(AreTaskListsInteractiveProperty, value);
    }

    public static readonly DependencyProperty AreTaskListsInteractiveProperty = DependencyProperty.Register(
        nameof(AreTaskListsInteractive),
        typeof(bool),
        typeof(TodoMarkdownPresenter),
        new PropertyMetadata(false, OnRenderingOptionChanged));

    public Func<string, string?>? AttachmentResolver { get; set; }

    public event EventHandler<MarkdownTaskToggleRequestedEventArgs>? TaskToggleRequested;

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((TodoMarkdownPresenter)sender).Render(args.NewValue as string);
    }

    private static void OnRenderingOptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var presenter = (TodoMarkdownPresenter)sender;
        presenter.Render(presenter.Markdown);
    }

    private void Render(string? markdown)
    {
        _documentPanel.Children.Clear();
        _taskListIndex = 0;
        TodoMarkdownDocument document = _markdownService.Parse(markdown);
        foreach (MdBlock block in document.Document)
        {
            UIElement? element = RenderBlock(block);
            if (element is not null)
            {
                _documentPanel.Children.Add(element);
            }
        }
    }

    private UIElement? RenderBlock(MdBlock block)
    {
        return block switch
        {
            HeadingBlock heading => RenderLeaf(heading, heading.Level switch
            {
                1 => 24,
                2 => 20,
                3 => 17,
                _ => 15
            }, FontWeights.SemiBold),
            ParagraphBlock paragraph => RenderLeaf(paragraph, 14, FontWeights.Normal),
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
            LeafBlock leaf => RenderLeaf(leaf, 14, FontWeights.Normal),
            _ => null
        };
    }

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

    private RichTextBlock RenderLeaf(LeafBlock leaf, double fontSize, Windows.UI.Text.FontWeight fontWeight)
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

    private UIElement RenderQuote(QuoteBlock quote)
    {
        return new Border
        {
            BorderBrush = BrushResource("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Child = RenderContainer(quote)
        };
    }

    private UIElement RenderCode(CodeBlock code)
    {
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = BrushResource("CardBackgroundFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                IsTextSelectionEnabled = true,
                Text = code.Lines.ToString(),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

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
            TaskToggleRequested?.Invoke(this, new MarkdownTaskToggleRequestedEventArgs(taskIndex));
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
                Width = new GridLength(1, GridUnitType.Star)
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

        return grid;
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
                    FontFamily = new FontFamily("Consolas"),
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
        if (TryGetAttachmentId(source, out string? attachmentId))
        {
            source = AttachmentResolver?.Invoke(attachmentId!);
        }
        else if (!AllowRemoteImages || !TodoMarkdownService.IsRemoteImage(source))
        {
            destination.Add(new Run
            {
                Text = string.IsNullOrWhiteSpace(link.Title) ? "[image]" : $"[{link.Title}]"
            });
            return;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            destination.Add(new Run { Text = "[image]" });
            return;
        }

        destination.Add(new InlineUIContainer
        {
            Child = new Image
            {
                Source = new BitmapImage(uri),
                MaxWidth = 480,
                MaxHeight = 320,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 0, 4)
            }
        });
    }

    private void AppendLink(InlineCollection destination, LinkInline link)
    {
        if (!TodoMarkdownService.IsAllowedLink(link.Url))
        {
            AppendContainer(destination, link);
            return;
        }

        if (TryGetAttachmentId(link.Url, out _))
        {
            var span = new Span { Foreground = BrushResource("AccentTextFillColorPrimaryBrush") };
            AppendContainer(span.Inlines, link);
            destination.Add(span);
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

    private static bool TryGetAttachmentId(string? source, out string? attachmentId)
    {
        attachmentId = null;
        if (source?.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase) == true)
        {
            attachmentId = source["attachment:".Length..];
            return !string.IsNullOrWhiteSpace(attachmentId);
        }
        const string deskBoxPrefix = "deskbox-attachment://";
        if (source?.StartsWith(deskBoxPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            attachmentId = source[deskBoxPrefix.Length..];
            return !string.IsNullOrWhiteSpace(attachmentId);
        }
        return false;
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

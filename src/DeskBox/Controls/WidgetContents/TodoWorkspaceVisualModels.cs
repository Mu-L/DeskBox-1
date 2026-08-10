using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

internal sealed record TodoWorkspaceNavigationItem(
    string Id,
    string Name,
    string Glyph,
    TodoSmartView? SmartView = null,
    string? ListId = null,
    bool IsTrash = false,
    string? SectionId = null,
    string? TagId = null,
    bool IsChild = false);

public sealed class TodoWorkspaceTaskRow : ObservableObject
{
    private readonly Func<TodoTask, Task> _toggleCompletion;
    private readonly Func<TodoTask, DateOnly?, string?, Task> _setColorMarker;
    private readonly DateOnly? _occurrenceDate;
    private readonly IReadOnlyDictionary<string, TodoList> _lists;
    private readonly IReadOnlyDictionary<string, TodoTag> _tags;
    private readonly LocalizationService _localization;
    private readonly TodoWidgetPresentationSettings _presentation;
    private bool _isBusy;
    private bool _isSelectionMode;

    internal TodoWorkspaceTaskRow(
        TodoTask task,
        TodoWorkspaceSnapshot snapshot,
        LocalizationService localization,
        TodoWidgetPresentationSettings presentation,
        Func<TodoTask, Task> toggleCompletion,
        Func<TodoTask, DateOnly?, string?, Task> setColorMarker,
        DateOnly? occurrenceDate = null)
    {
        Task = task;
        _localization = localization;
        _presentation = presentation;
        _toggleCompletion = toggleCompletion;
        _setColorMarker = setColorMarker;
        _occurrenceDate = occurrenceDate;
        _lists = snapshot.Lists.ToDictionary(list => list.Id, StringComparer.Ordinal);
        _tags = snapshot.Tags.ToDictionary(tag => tag.Id, StringComparer.Ordinal);
    }

    public TodoTask Task { get; }

    public string Id => Task.Id;

    internal DateOnly? OccurrenceDate => _occurrenceDate;

    public string Title => Task.Title;

    public bool IsCompleted => Task.Status == TodoTaskStatus.Completed;

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        internal set => SetProperty(ref _isSelectionMode, value);
    }

    public string CompletionGlyph => IsCompleted ? "\uE73E" : string.Empty;

    public string CompletionAutomationName => _localization.T(IsCompleted
        ? "Todo.Menu.MarkActive"
        : "Todo.Menu.MarkCompleted");

    public double ContentOpacity => IsCompleted ? 0.60 : 1.0;

    public string ListName => _lists.GetValueOrDefault(Task.ListId)?.Name ?? string.Empty;

    public string TagText => string.Join(
        "  ",
        Task.TagIds.Select(tagId => _tags.GetValueOrDefault(tagId)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"#{name}"));

    public string ScheduleText => Task.Schedule is not { } schedule
        ? string.Empty
        : schedule.Time is { } time
            ? $"{schedule.Date:M/d} {time:HH\\:mm}"
            : $"{schedule.Date:M/d}";

    public string DeadlineText => Task.DeadlineAt is not { } deadline
        ? string.Empty
        : deadline.ToLocalTime().ToString("M/d HH:mm");

    public bool IsOverdue => Task.DeadlineAt is { } deadline &&
                             deadline < DateTimeOffset.Now &&
                             !IsCompleted;

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            if (_presentation.ShowSchedule && !string.IsNullOrWhiteSpace(ScheduleText))
            {
                parts.Add($"{_localization.T("Todo.Workspace.Planned")}: {ScheduleText}");
            }

            if (_presentation.ShowDeadline && !string.IsNullOrWhiteSpace(DeadlineText))
            {
                parts.Add($"{_localization.T("Todo.Workspace.Deadline")}: {DeadlineText}");
            }

            if (_presentation.ShowStepProgress && Task.Steps.Count > 0)
            {
                parts.Add($"{Task.Steps.Count(step => step.IsCompleted)}/{Task.Steps.Count}");
            }

            if (_presentation.ShowTags && !string.IsNullOrWhiteSpace(TagText))
            {
                parts.Add(TagText);
            }

            if (_presentation.ShowAttachments && Task.Attachments.Count > 0)
            {
                parts.Add($"\uE723 {Task.Attachments.Count}");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string PriorityGlyph => Task.Priority switch
    {
        TodoPriority.High => "\uE7BA",
        TodoPriority.Medium => "\uE814",
        TodoPriority.Low => "\uE74B",
        _ => string.Empty
    };

    public Visibility PriorityVisibility => Task.Priority == TodoPriority.None
        ? Visibility.Collapsed
        : Visibility.Visible;

    internal async Task ToggleCompletionAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            await _toggleCompletion(Task);
        }
        finally
        {
            _isBusy = false;
        }
    }

    internal Task SetColorMarkerAsync(string? colorMarker) =>
        _setColorMarker(Task, _occurrenceDate, colorMarker);
}

/// <summary>A virtualizable native task row used by the shared workspace ListView.</summary>
public sealed class TodoWorkspaceTaskCard : Grid
{
    internal const double ColorMarkerWidth = 4;
    internal const double ColorMarkerHeight = 24;
    private readonly Border _colorMarker;
    private readonly CheckBox _completionCheckBox;
    private readonly TextBlock _title;
    private readonly TextBlock _metadata;
    private readonly FontIcon _priority;
    private TodoWorkspaceTaskRow? _boundRow;

    public TodoWorkspaceTaskCard()
    {
        MinHeight = 50;
        AllowDrop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ColumnSpacing = 4;
        Padding = new Thickness(5, 5, 7, 5);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

        _colorMarker = new Border
        {
            Width = ColorMarkerWidth,
            Height = ColorMarkerHeight,
            Margin = new Thickness(1, 0, 2, 0),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        SetColumn(_colorMarker, 1);
        Children.Add(_colorMarker);

        _completionCheckBox = new CheckBox
        {
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = null,
            IsThreeState = false
        };
        _completionCheckBox.Click += CompletionCheckBox_Click;
        Children.Add(_completionCheckBox);

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title = new TextBlock
        {
            FontSize = 14,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        };
        _metadata = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ResourceBrush("TextFillColorTertiaryBrush"),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textPanel.Children.Add(_title);
        textPanel.Children.Add(_metadata);
        SetColumn(textPanel, 2);
        Children.Add(textPanel);

        _priority = new FontIcon
        {
            FontSize = 13,
            Foreground = ResourceBrush("AccentTextFillColorPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(_priority, 3);
        Children.Add(_priority);
        DragOver += TaskCard_DragOver;
        DragLeave += TaskCard_DragLeave;
        Drop += TaskCard_Drop;
        DataContextChanged += (_, args) => Bind(args.NewValue as TodoWorkspaceTaskRow);
    }

    private async void CompletionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TodoWorkspaceTaskRow row)
        {
            await row.ToggleCompletionAsync();
        }
    }

    private void Bind(TodoWorkspaceTaskRow? row)
    {
        if (_boundRow is not null)
        {
            _boundRow.PropertyChanged -= BoundRow_PropertyChanged;
        }
        _boundRow = row;
        if (row is null)
        {
            return;
        }
        row.PropertyChanged += BoundRow_PropertyChanged;

        _title.Text = row.Title;
        _title.Opacity = row.ContentOpacity;
        _title.TextDecorations = row.IsCompleted
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;
        _metadata.Text = row.MetadataText;
        _metadata.Visibility = string.IsNullOrWhiteSpace(row.MetadataText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _metadata.Foreground = row.IsOverdue
            ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
            : ResourceBrush("TextFillColorTertiaryBrush");
        _completionCheckBox.IsChecked = row.IsCompleted;
        AutomationProperties.SetName(_completionCheckBox, row.CompletionAutomationName);
        _priority.Glyph = row.PriorityGlyph;
        _priority.Visibility = row.PriorityVisibility;
        string? colorMarker = TodoItem.NormalizeColorMarker(row.Task.ColorMarker);
        _colorMarker.Visibility = colorMarker is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        _colorMarker.Background = new SolidColorBrush(
            AccentColorHelper.FromHex(TodoItem.GetColorMarkerHex(colorMarker)));
        UpdateSelectionMode(row);
    }

    private void BoundRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TodoWorkspaceTaskRow row &&
            e.PropertyName == nameof(TodoWorkspaceTaskRow.IsSelectionMode))
        {
            UpdateSelectionMode(row);
        }
    }

    private void UpdateSelectionMode(TodoWorkspaceTaskRow row)
    {
        _completionCheckBox.IsEnabled = !row.IsSelectionMode;
        _completionCheckBox.Opacity = row.IsSelectionMode ? 0.28 : 1;
    }

    private void TaskCard_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.IsGlyphVisible = true;
        Background = ResourceBrush("SubtleFillColorSecondaryBrush");
    }

    private void TaskCard_DragLeave(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            e.Handled = true;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private async void TaskCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat) ||
            DataContext is not TodoWorkspaceTaskRow row)
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Link;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        try
        {
            string? colorMarker = TodoItem.NormalizeColorMarker(
                await DeskBoxDragData.TryGetTodoColorMarkerAsync(e.DataView));
            if (colorMarker is not null)
            {
                await row.SetColorMarkerAsync(colorMarker);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Task color drop failed: {ex.Message}");
        }
    }

    internal static Brush ResourceBrush(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
            value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    internal static void ApplyStyle(Control control, string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
            value is Style style)
        {
            control.Style = style;
        }
    }
}

/// <summary>
/// Lightweight wrapping panel for compact task metadata. WinUI's horizontal
/// StackPanel measures with infinite width, which made chips disappear past
/// the right edge on narrow Todo widgets.
/// </summary>
internal sealed class TodoWorkspaceWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 5;

    public double VerticalSpacing { get; set; } = 5;

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsInfinity(availableSize.Width)
            ? double.MaxValue
            : Math.Max(0, availableSize.Width);
        double lineWidth = 0;
        double lineHeight = 0;
        double desiredWidth = 0;
        double desiredHeight = 0;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableWidth, availableSize.Height));
            Size size = child.DesiredSize;
            double nextWidth = lineWidth == 0
                ? size.Width
                : lineWidth + HorizontalSpacing + size.Width;
            if (lineWidth > 0 && nextWidth > availableWidth)
            {
                desiredWidth = Math.Max(desiredWidth, lineWidth);
                desiredHeight += lineHeight + VerticalSpacing;
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth = nextWidth;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
        }

        desiredWidth = Math.Max(desiredWidth, lineWidth);
        desiredHeight += lineHeight;
        return new Size(
            double.IsInfinity(availableSize.Width)
                ? desiredWidth
                : Math.Min(availableSize.Width, desiredWidth),
            desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (UIElement child in Children)
        {
            Size size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(new Point(x, y), size));
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}

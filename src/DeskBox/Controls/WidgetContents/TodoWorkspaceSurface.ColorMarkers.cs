using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

internal sealed partial class TodoWorkspaceSurface
{
    private readonly Border _colorMarkerBar = new();
    private readonly StackPanel _colorMarkerButtons = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 2
    };
    private readonly Dictionary<string, Button> _colorMarkerButtonByValue =
        new(StringComparer.Ordinal);
    private string? _activeColorMarkerFilter;
    private Button? _pressedColorMarkerButton;
    private Point _colorMarkerDragStartPoint;
    private bool _isStartingColorMarkerDrag;
    private DateTimeOffset _suppressColorMarkerClickUntil;

    private void BuildColorMarkerBar()
    {
        _colorMarkerBar.Margin = new Thickness(1, -2, 1, 5);
        _colorMarkerBar.MinHeight = 24;
        _colorMarkerBar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        var label = new FontIcon
        {
            Glyph = "\uE790",
            FontSize = 11,
            Width = 20,
            Foreground = TodoWorkspaceTaskCard.ResourceBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(label, _localization.T("Todo.Menu.ColorMarker"));
        AutomationProperties.SetName(label, _localization.T("Todo.Menu.ColorMarker"));
        content.Children.Add(label);

        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            Button button = CreateColorMarkerButton(colorMarker);
            _colorMarkerButtonByValue[colorMarker] = button;
            _colorMarkerButtons.Children.Add(button);
        }
        content.Children.Add(_colorMarkerButtons);

        _colorMarkerBar.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        UpdateColorMarkerButtons();
    }

    private Button CreateColorMarkerButton(string colorMarker)
    {
        string name = _localization.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker));
        var button = new Button
        {
            Tag = colorMarker,
            CanDrag = true,
            Width = 24,
            Height = 22,
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new Border
            {
                Width = 15,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = CreateColorMarkerBrush(colorMarker)
            }
        };
        TodoWorkspaceTaskCard.ApplyStyle(button, "SubtleButtonStyle");
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += ColorMarkerButton_Click;
        button.DragStarting += ColorMarkerButton_DragStarting;
        button.PointerReleased += ColorMarkerButton_PointerReleased;
        button.PointerCaptureLost += ColorMarkerButton_PointerCaptureLost;
        button.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ColorMarkerButton_PointerPressed),
            handledEventsToo: true);
        button.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(ColorMarkerButton_PointerMoved),
            handledEventsToo: true);
        return button;
    }

    private void ColorMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow < _suppressColorMarkerClickUntil ||
            sender is not FrameworkElement { Tag: string value })
        {
            return;
        }

        string? marker = TodoItem.NormalizeColorMarker(value);
        _activeColorMarkerFilter = string.Equals(
            _activeColorMarkerFilter,
            marker,
            StringComparison.Ordinal)
            ? null
            : marker;
        UpdateColorMarkerButtons();
        RefreshRows();
        RenderCurrentView();
    }

    private void ColorMarkerButton_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string colorMarker } ||
            TodoItem.NormalizeColorMarker(colorMarker) is null)
        {
            e.Cancel = true;
            return;
        }

        DeskBoxDragData.SetTodoColorMarker(e.Data, colorMarker);
        e.Data.RequestedOperation = DataPackageOperation.Link;
        e.Data.Properties.Title = _localization.T(
            TodoItem.GetColorMarkerLocalizationKey(colorMarker));
    }

    private void ColorMarkerButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button &&
            e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _pressedColorMarkerButton = button;
            _colorMarkerDragStartPoint = e.GetCurrentPoint(this).Position;
        }
    }

    private async void ColorMarkerButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isStartingColorMarkerDrag ||
            sender is not Button button ||
            !ReferenceEquals(button, _pressedColorMarkerButton))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedColorMarkerButton = null;
            return;
        }

        double deltaX = point.Position.X - _colorMarkerDragStartPoint.X;
        double deltaY = point.Position.Y - _colorMarkerDragStartPoint.Y;
        if ((deltaX * deltaX) + (deltaY * deltaY) < 25)
        {
            return;
        }

        _isStartingColorMarkerDrag = true;
        _suppressColorMarkerClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
        e.Handled = true;
        try
        {
            await button.StartDragAsync(e.GetCurrentPoint(button));
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Failed to start color marker drag: {ex.Message}");
        }
        finally
        {
            _suppressColorMarkerClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(350);
            _pressedColorMarkerButton = null;
            _isStartingColorMarkerDrag = false;
        }
    }

    private void ColorMarkerButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedColorMarkerButton = null;
    }

    private void ColorMarkerButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStartingColorMarkerDrag)
        {
            _pressedColorMarkerButton = null;
        }
    }

    private void UpdateColorMarkerButtons()
    {
        foreach ((string marker, Button button) in _colorMarkerButtonByValue)
        {
            bool selected = string.Equals(marker, _activeColorMarkerFilter, StringComparison.Ordinal);
            button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            button.BorderBrush = selected
                ? CreateColorMarkerBrush(marker)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Background = selected
                ? TodoWorkspaceTaskCard.ResourceBrush("SubtleFillColorSecondaryBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private bool TryFindColorDropTarget(
        DependencyObject? source,
        out TodoTask task,
        out DateOnly? occurrenceDate)
    {
        if (FindDataContext<TodoWorkspaceTaskRow>(source) is { } row)
        {
            task = row.Task;
            occurrenceDate = row.OccurrenceDate;
            return true;
        }

        if (FindTaggedReference<TodoCalendarDragContext>(source) is { } context)
        {
            task = context.Task;
            occurrenceDate = context.RecurrenceOccurrenceDate;
            return true;
        }

        if (IsWithin(source, _detailPane) && SelectedTask is { } selected)
        {
            task = selected;
            occurrenceDate = _selectedOccurrenceDate;
            return true;
        }

        task = null!;
        occurrenceDate = null;
        return false;
    }

    private void AttachColorMarkerDropTarget(FrameworkElement target)
    {
        target.AllowDrop = true;
        target.DragOver += TaskColorTarget_DragOver;
        target.Drop += TaskColorTarget_Drop;
    }

    private void TaskColorTarget_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void TaskColorTarget_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat) ||
            sender is not DependencyObject source ||
            !TryFindColorDropTarget(source, out TodoTask task, out DateOnly? occurrenceDate))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Link;
        try
        {
            string? colorMarker = TodoItem.NormalizeColorMarker(
                await DeskBoxDragData.TryGetTodoColorMarkerAsync(e.DataView));
            if (colorMarker is not null)
            {
                await ApplyTaskColorMarkerAsync(task, occurrenceDate, colorMarker);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWorkspace] Calendar color drop failed: {ex.Message}");
        }
    }

    private async Task ApplyTaskColorMarkerAsync(
        TodoTask task,
        DateOnly? occurrenceDate,
        string? colorMarker)
    {
        string? normalized = TodoItem.NormalizeColorMarker(colorMarker);
        await RunMutationAsync(async () =>
        {
            if (occurrenceDate is { } date &&
                task.RecurrenceRule?.GenerationMode == TodoRecurrenceGenerationMode.FixedSchedule)
            {
                await _workspace.ApplyRecurrenceEditAsync(
                    task.Id,
                    date,
                    TodoRecurrenceEditScope.Occurrence,
                    editable => editable.ColorMarker = normalized);
                return;
            }

            TodoTask? editable = await _workspace.GetTaskAsync(task.Id);
            if (editable is null ||
                string.Equals(editable.ColorMarker, normalized, StringComparison.Ordinal))
            {
                return;
            }

            editable.ColorMarker = normalized;
            await _workspace.SaveTaskAsync(editable);
        });
    }

    private MenuFlyoutSubItem CreateTaskColorMarkerMenu(
        TodoTask task,
        DateOnly? occurrenceDate)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = _localization.T("Todo.Menu.ColorMarker"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        submenu.Items.Add(CreateTaskColorMarkerMenuItem(task, occurrenceDate, null));
        submenu.Items.Add(new MenuFlyoutSeparator());
        foreach (string marker in TodoItem.SupportedColorMarkers)
        {
            submenu.Items.Add(CreateTaskColorMarkerMenuItem(task, occurrenceDate, marker));
        }
        return submenu;
    }

    private ToggleMenuFlyoutItem CreateTaskColorMarkerMenuItem(
        TodoTask task,
        DateOnly? occurrenceDate,
        string? colorMarker)
    {
        string? normalized = TodoItem.NormalizeColorMarker(colorMarker);
        var item = new ToggleMenuFlyoutItem
        {
            Text = _localization.T(TodoItem.GetColorMarkerLocalizationKey(normalized)),
            IsChecked = string.Equals(
                TodoItem.NormalizeColorMarker(task.ColorMarker),
                normalized,
                StringComparison.Ordinal),
            Icon = normalized is null
                ? new FontIcon { Glyph = "\uE711" }
                : new FontIcon
                {
                    Glyph = "\uE790",
                    Foreground = CreateColorMarkerBrush(normalized)
                }
        };
        item.Click += async (_, _) =>
            await ApplyTaskColorMarkerAsync(task, occurrenceDate, normalized);
        return item;
    }

    private static Brush CreateColorMarkerBrush(string? colorMarker) =>
        new SolidColorBrush(AccentColorHelper.FromHex(TodoItem.GetColorMarkerHex(colorMarker)));
}

using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class TodoManualReorderTests
{
    [Fact]
    public void ResolveManualDropTargetIndex_AccountsForRemovalBeforeInsertion()
    {
        TodoItemViewModel first = CreateItem("first");
        TodoItemViewModel second = CreateItem("second");
        TodoItemViewModel third = CreateItem("third");
        TodoItemViewModel fourth = CreateItem("fourth");
        TodoItemViewModel[] items = [first, second, third, fourth];

        Assert.Equal(
            2,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                second.Id,
                fourth.Id,
                insertAfter: false));
        Assert.Equal(
            3,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                second.Id,
                fourth.Id,
                insertAfter: true));
        Assert.Equal(
            1,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                fourth.Id,
                second.Id,
                insertAfter: false));
        Assert.Equal(
            1,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                second.Id,
                second.Id,
                insertAfter: true));
        Assert.Equal(
            -1,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                "missing",
                second.Id,
                insertAfter: false));
        Assert.Equal(
            -1,
            TodoDragPackage.ResolveManualDropTargetIndex(
                items,
                second.Id,
                null,
                insertAfter: false));
    }

    [Fact]
    public void TodoSurface_UsesManualRowDropWithoutMutatingTheAotProjection()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string dragDrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs"));
        string listInteraction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ListInteraction.cs"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/TodoWidgetViewModel.cs"));

        Assert.Contains("CanReorderItems=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "TodoDragPackage.ResolveManualDropTargetIndex",
            dragDrop,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ViewModel.MoveItemAsync(",
            dragDrop,
            StringComparison.Ordinal);
        Assert.Contains(
            "TodoListView.CanReorderItems = false",
            dragDrop,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CanReorderItems = true",
            dragDrop + listInteraction,
            StringComparison.Ordinal);
        Assert.Contains(
            "public object[] VisibleItemsSource",
            viewModel,
            StringComparison.Ordinal);
    }

    private static TodoItemViewModel CreateItem(string id)
    {
        return new TodoItemViewModel(new TodoItem
        {
            Id = id,
            Text = id
        });
    }
}

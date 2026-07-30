using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    public object? CaptureTransientState()
    {
        string focusTarget = FocusManager.GetFocusedElement(RootGrid.XamlRoot) switch
        {
            object focused when ReferenceEquals(focused, InputTextBox) => "Input",
            object focused when ReferenceEquals(focused, SearchTextBox) => "Search",
            object focused when ReferenceEquals(focused, ItemsListView) => "Items",
            _ => "Root"
        };
        return new QuickCaptureWidgetTransientState(
            ViewModel.InputText,
            ViewModel.SearchText,
            ViewModel.SelectedView,
            focusTarget);
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not QuickCaptureWidgetTransientState quickState)
        {
            return;
        }

        ViewModel.InputText = quickState.InputText;
        ViewModel.SearchText = quickState.SearchText;
        ViewModel.SelectedView = quickState.SelectedView;
        if (!string.IsNullOrWhiteSpace(quickState.SearchText))
        {
            ViewModel.ExpandSearch();
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            FrameworkElement target = quickState.FocusTarget switch
            {
                "Input" => InputTextBox,
                "Search" => SearchTextBox,
                "Items" => ItemsListView,
                _ => RootGrid
            };
            target.Focus(FocusState.Programmatic);
        });
    }
}

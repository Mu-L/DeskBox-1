using DeskBox.ViewModels;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void AttachViewModelSubscriptions(WidgetViewModel viewModel)
    {
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Items.CollectionChanged += ViewModel_ItemsCollectionChanged;
    }

    private void DetachViewModelSubscriptions(WidgetViewModel viewModel)
    {
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.Items.CollectionChanged -= ViewModel_ItemsCollectionChanged;
    }
}

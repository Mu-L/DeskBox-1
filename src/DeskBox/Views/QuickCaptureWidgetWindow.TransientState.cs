using DeskBox.Contracts;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    public object? CaptureTransientState()
    {
        return ((IWidgetTransientStateContent)_sharedContent).CaptureTransientState();
    }

    public void RestoreTransientState(object? state)
    {
        ((IWidgetTransientStateContent)_sharedContent).RestoreTransientState(state);
    }
}

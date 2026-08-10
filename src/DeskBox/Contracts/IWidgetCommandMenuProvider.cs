using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Contracts;

/// <summary>
/// Lets hosted content place its high-value commands in the shared widget
/// title-bar menu instead of reserving permanent space inside the surface.
/// </summary>
public interface IWidgetCommandMenuProvider
{
    void AppendWidgetCommands(MenuFlyout menu);
}

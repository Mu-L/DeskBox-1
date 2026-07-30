using DeskBox.Models;

namespace DeskBox.Controls;

public sealed record WidgetGroupMemberPresentation(
    string WidgetId,
    string Name,
    WidgetKind WidgetKind,
    string Glyph,
    string IconKind,
    bool IsActive);

public sealed record WidgetGroupPresentation(
    string GroupId,
    string SurfaceId,
    string ActiveMemberId,
    string NavigationStyle,
    string TitleDisplayMode,
    bool WheelSwitchEnabled,
    bool HoverSwitchEnabled,
    IReadOnlyList<WidgetGroupMemberPresentation> Members);

public enum WidgetGroupSwitchOrigin
{
    Programmatic,
    Picker,
    Wheel,
    Keyboard
}

public sealed class WidgetGroupMemberEventArgs(
    string widgetId,
    WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Picker) : EventArgs
{
    public string WidgetId { get; } = widgetId;

    public WidgetGroupSwitchOrigin Origin { get; } = origin;
}

public sealed class WidgetGroupReorderEventArgs(string sourceWidgetId, string targetWidgetId) : EventArgs
{
    public string SourceWidgetId { get; } = sourceWidgetId;

    public string TargetWidgetId { get; } = targetWidgetId;
}

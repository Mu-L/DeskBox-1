using DeskBox.Controls;
using DeskBox.Helpers;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private bool _groupingInitialized;
    private bool _groupPickerInteractionOpen;

    protected void InitializeWidgetGrouping()
    {
        if (_groupingInitialized)
        {
            return;
        }

        _groupingInitialized = true;
        WidgetShellControl.GroupMemberInvoked += WidgetShellControl_GroupMemberInvoked;
        WidgetShellControl.GroupMemberRemoveRequested += WidgetShellControl_GroupMemberRemoveRequested;
        WidgetShellControl.GroupMemberDetachRequested += WidgetShellControl_GroupMemberDetachRequested;
        WidgetShellControl.GroupMemberReorderRequested += WidgetShellControl_GroupMemberReorderRequested;
        WidgetShellControl.GroupDissolveRequested += WidgetShellControl_GroupDissolveRequested;
        WidgetShellControl.GroupPickerOpened += WidgetShellControl_GroupPickerOpened;
        WidgetShellControl.GroupPickerClosed += WidgetShellControl_GroupPickerClosed;

        if (App.Current.WidgetManager is { } manager)
        {
            manager.WidgetGroupsChanged += WidgetManager_WidgetGroupsChanged;
        }

        RefreshWidgetGroupPresentation();
    }

    public void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null)
    {
        WidgetShellControl.SetGroupDropPreview(visible, ready, messageKey);
    }

    protected void CleanupWidgetGrouping()
    {
        if (!_groupingInitialized)
        {
            return;
        }

        _groupingInitialized = false;
        WidgetShellControl.GroupMemberInvoked -= WidgetShellControl_GroupMemberInvoked;
        WidgetShellControl.GroupMemberRemoveRequested -= WidgetShellControl_GroupMemberRemoveRequested;
        WidgetShellControl.GroupMemberDetachRequested -= WidgetShellControl_GroupMemberDetachRequested;
        WidgetShellControl.GroupMemberReorderRequested -= WidgetShellControl_GroupMemberReorderRequested;
        WidgetShellControl.GroupDissolveRequested -= WidgetShellControl_GroupDissolveRequested;
        WidgetShellControl.GroupPickerOpened -= WidgetShellControl_GroupPickerOpened;
        WidgetShellControl.GroupPickerClosed -= WidgetShellControl_GroupPickerClosed;

        if (App.Current?.WidgetManager is { } manager)
        {
            manager.WidgetGroupsChanged -= WidgetManager_WidgetGroupsChanged;
        }

        if (_groupPickerInteractionOpen)
        {
            _groupPickerInteractionOpen = false;
            ReleaseInteractionLayer("widget-group-picker-cleanup");
        }
    }

    protected void RefreshWidgetGroupPresentation(
        bool animateIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        WidgetGroupPresentation? presentation =
            App.Current?.WidgetManager?.GetWidgetGroupPresentation(Config.Id);
        Diagnostics.SetSurfaceId(presentation?.SurfaceId);
        WidgetShellControl.SetGroupPresentation(
            presentation,
            animateIdentity,
            origin,
            forward);
        OnWidgetGroupPresentationChanged(presentation);
    }

    protected virtual void OnWidgetGroupPresentationChanged(
        WidgetGroupPresentation? presentation)
    {
    }

    protected void SynchronizeWidgetGroupLayout()
    {
        App.Current?.WidgetManager?.SynchronizeGroupLayoutFromMember(Config);
    }

    private void WidgetManager_WidgetGroupsChanged()
    {
        if (IsClosing)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshWidgetGroupPresentation();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => RefreshWidgetGroupPresentation());
        }
    }

    private async void WidgetShellControl_GroupMemberInvoked(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (!string.Equals(e.WidgetId, Config.Id, StringComparison.Ordinal) &&
            App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.SwitchWidgetGroupMemberAsync(e.WidgetId, e.Origin);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Switch failed target={e.WidgetId}: {ex}");
            }
        }
    }

    private async void WidgetShellControl_GroupMemberRemoveRequested(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.RemoveWidgetFromGroupAsync(e.WidgetId, revealStandalone: true);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Remove member failed id={e.WidgetId}: {ex}");
            }
        }
    }

    private async void WidgetShellControl_GroupMemberDetachRequested(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (!IsCursorOutsideGroupWindow() ||
            App.Current?.WidgetManager is not { } manager)
        {
            return;
        }

        try
        {
            await manager.RemoveWidgetFromGroupAsync(
                e.WidgetId,
                revealStandalone: true);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Drag detach failed id={e.WidgetId}: {ex}");
        }
    }

    private bool IsCursorOutsideGroupWindow()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return false;
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        const int releaseMargin = 8;
        return cursor.X < position.X - releaseMargin ||
               cursor.Y < position.Y - releaseMargin ||
               cursor.X > position.X + size.Width + releaseMargin ||
               cursor.Y > position.Y + size.Height + releaseMargin;
    }

    private async void WidgetShellControl_GroupMemberReorderRequested(
        object? sender,
        WidgetGroupReorderEventArgs e)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.ReorderWidgetGroupMemberAsync(
                    e.SourceWidgetId,
                    e.TargetWidgetId);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetGroup] Reorder failed source={e.SourceWidgetId} " +
                    $"target={e.TargetWidgetId}: {ex}");
            }
        }
    }

    private async void WidgetShellControl_GroupDissolveRequested(object? sender, EventArgs e)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.DissolveWidgetGroupContainingAsync(Config.Id);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Dissolve failed member={Config.Id}: {ex}");
            }
        }
    }

    private void WidgetShellControl_GroupPickerOpened(object? sender, EventArgs e)
    {
        if (_groupPickerInteractionOpen)
        {
            return;
        }

        _groupPickerInteractionOpen = true;
        BeginInteractionLayer("widget-group-picker");
    }

    private void WidgetShellControl_GroupPickerClosed(object? sender, EventArgs e)
    {
        if (!_groupPickerInteractionOpen)
        {
            return;
        }

        _groupPickerInteractionOpen = false;
        ReleaseInteractionLayer("widget-group-picker");
    }
}

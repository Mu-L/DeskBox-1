using DeskBox.Helpers;
using DeskBox.Models;
using System.Text.Json;

namespace DeskBox.Tests;

public sealed class FileInteractionPolicyTests
{
    [Fact]
    public void DropIntent_ReferenceGridNeverTurnsIntoAFileTransfer()
    {
        Assert.Equal(
            FileDropIntent.Reference,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: false,
                forceCopy: false,
                controlDown: false,
                shiftDown: true,
                defaultMove: true));
    }

    [Fact]
    public void DropIntent_VirtualPayloadAlwaysCopies()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: true,
                controlDown: false,
                shiftDown: true,
                defaultMove: true));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void DropIntent_AltOrCtrlShiftCreatesShortcutForMappedFiles(
        bool altDown,
        bool controlDown,
        bool shiftDown)
    {
        Assert.Equal(
            FileDropIntent.Shortcut,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown,
                shiftDown,
                defaultMove: true,
                altDown: altDown,
                canLink: true));
    }

    [Fact]
    public void DropIntent_FollowWindowsUsesCopyAcrossVolumes()
    {
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                followWindows: true,
                sameVolume: false));
        Assert.Equal(
            FileDropIntent.Move,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: false,
                followWindows: true,
                sameVolume: true));
    }

    [Theory]
    [InlineData(24, 1, 28)]
    [InlineData(32, 1, 36)]
    [InlineData(40, -1, 36)]
    [InlineData(56, 1, 56)]
    [InlineData(24, -1, 24)]
    public void IconSizePolicy_UsesDiscreteBoundedSteps(
        double current,
        int direction,
        double expected)
    {
        Assert.Equal(expected, FileWidgetIconSizePolicy.GetNext(current, direction));
    }

    [Fact]
    public void WidgetIconSizeOverride_RoundTripsWithoutChangingTheGlobalSetting()
    {
        var config = new WidgetConfig { IconSizeOverride = 48 };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.Equal(48, restored?.IconSizeOverride);
    }
}

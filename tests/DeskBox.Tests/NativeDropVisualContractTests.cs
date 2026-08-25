using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class NativeDropVisualContractTests
{
    [Theory]
    [InlineData("复制到“{0}”", "复制到%1")]
    [InlineData("Move to \"{0}\"", "Move to %1")]
    [InlineData("Nach „{0}“ verschieben", "Nach %1 verschieben")]
    [InlineData("「{0}」に移動", "%1に移動")]
    public void LocalizedFolderCaption_BecomesAShellInsertTemplate(
        string localized,
        string expected)
    {
        Assert.Equal(
            expected,
            ContentWidgetWindow.ToShellDropDescriptionMessage(localized));
    }

    [Fact]
    public void ExternalFileDrag_UsesTheShellImageAndDropDescriptionPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string target = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTarget.cs");
        string imageManager = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropImageManager.cs");
        string description = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropDescriptionWriter.cs");

        Assert.Contains(
            "e.DragUIOverride.IsContentVisible = false;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNativeFileDropDescription",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.UI.Content.DesktopChildSiteBridge",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.EnumChildWindows(",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.Register();",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nativeFileDropTargets[targetWindow] = target;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "SuppressesNativeShellDragVisual: false",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropEffectPolicy.Copy => \"Widget.CopyToFolder\"",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropEffectPolicy.Move => \"Widget.MoveToFolder\"",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "return new NativeDropDescriptionText(message, targetName);",
            window,
            StringComparison.Ordinal);

        Assert.Contains(
            "NativeDropImageManager.TryCreate()",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragEnter(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragOver(point, effect);",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragLeave();",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.Drop(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropDescriptionWriter.TryApply(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropDescriptionWriter.TryClear(dataObject)",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateShellVisual(point, effect);",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScheduleNativeFileDropFallback(",
            window,
            StringComparison.Ordinal);

        Assert.Contains(
            "4657278A-411B-11D2-839A-00C04FD918D0",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "4657278B-411B-11D2-839A-00C04FD918D0",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoCreateInstance(",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "delegate* unmanaged[Stdcall]",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetVtableEntry(ShowVtableSlot)",
            imageManager,
            StringComparison.Ordinal);

        Assert.Contains(
            "RegisterClipboardFormatW(\"DropDescription\")",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "RegisterClipboardFormatW(\"DragWindow\")",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "DdwmUpdateWindow = WmUser + 3",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "release: true",
            description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeShellVisual_DoesNotIntroduceBuiltInComRcws()
    {
        string combined = string.Join(
            Environment.NewLine,
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropImageManager.cs"),
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropDescriptionWriter.cs"),
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropComDataReader.cs"));

        Assert.DoesNotContain(
            "Marshal.GetObjectForIUnknown",
            combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.ReleaseComObject",
            combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[ComImport",
            combined,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}

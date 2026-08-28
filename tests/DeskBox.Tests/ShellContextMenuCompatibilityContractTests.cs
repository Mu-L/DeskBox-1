namespace DeskBox.Tests;

public sealed class ShellContextMenuCompatibilityContractTests
{
    [Fact]
    public void NativeContextMenu_UsesRawShellComAbiWithoutRuntimeCallableWrappers()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuHelper.cs");

        Assert.Contains(
            "public static unsafe class ShellContextMenuHelper",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellFolderGetUiObjectOfSlot = 10",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenuQuerySlot = 3",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenuInvokeSlot = 4",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu2HandleMenuMessageSlot = 6",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu3HandleMenuMessage2Slot = 7",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "delegate* unmanaged[Stdcall]",
            helper,
            StringComparison.Ordinal);

        Assert.DoesNotContain("[ComImport]", helper, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.GetObjectForIUnknown(",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.ReleaseComObject",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.QueryInterface",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeCommand_UsesCompleteNativeStructureAndChecksFailure()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuHelper.cs");

        Assert.Contains(
            "public uint dwHotKey;",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "public IntPtr hIcon;",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "cbSize = (uint)sizeof(CMINVOKECOMMANDINFO)",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "int invokeResult = InvokeCommand(",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (invokeResult < 0)",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "InvokeCommand failed: hr=0x",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductPath_PreservesOwnerPathAndPointerCoordinates()
    {
        string menu = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");

        Assert.Contains(
            "ShellContextMenuHelper.ShowContextMenu(",
            menu,
            StringComparison.Ordinal);
        Assert.Contains("_hostWindowHandle,", menu, StringComparison.Ordinal);
        Assert.Contains("item.Path,", menu, StringComparison.Ordinal);
        Assert.Contains("screenX,", menu, StringComparison.Ordinal);
        Assert.Contains("screenY);", menu, StringComparison.Ordinal);
        Assert.Contains(
            "if (result == ShellContextMenuHelper.NativeMenuResult.Failed)",
            menu,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}

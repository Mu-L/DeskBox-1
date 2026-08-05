using DeskBox.Controls;

namespace DeskBox.Tests;

public sealed class VirtualShortcutDragProviderTests
{
    [Fact]
    public void CanProvide_AcceptsOneOrMoreExistingShortcuts()
    {
        Assert.True(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\One.lnk", @"E:\DeskBox\my\Two.LNK"],
            _ => true));
    }

    [Fact]
    public void CanProvide_RejectsMixedFileTypes()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\One.lnk", @"E:\DeskBox\my\Readme.txt"],
            _ => true));
    }

    [Fact]
    public void CanProvide_RejectsMissingShortcut()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\Missing.lnk"],
            _ => false));
    }

    [Fact]
    public void CanProvide_RejectsEmptySelection()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [],
            _ => true));
    }

    [Fact]
    public void Provider_AdvertisesOnDemandStorageItems()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/VirtualShortcutDragProvider.cs"));

        Assert.Contains(
            "SetDataProvider(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StandardDataFormats.StorageItems",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StorageFile.CreateStreamedFileAsync",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}

using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class DpiAdaptiveLayoutContractTests
{
    [Fact]
    public void FileIconTiles_UseMeasuredHeightAsTheAuthority()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XElement itemSlot = document.Descendants()
            .Single(element => (string?)element.Attribute("Tag") == "ItemSlot");
        XElement stackSurface = document.Descendants()
            .Single(element =>
                (string?)element.Attribute("Tag") == "StackSurface" &&
                (string?)element.Attribute("MinHeight") == "{Binding TileHeight}");

        Assert.Null(itemSlot.Attribute("Height"));
        Assert.Equal(
            "{Binding DataContext.IconTileHeight, ElementName=ItemsGrid}",
            (string?)itemSlot.Attribute("MinHeight"));
        Assert.Null(stackSurface.Attribute("Height"));
        Assert.Equal("{Binding TileHeight}", (string?)stackSurface.Attribute("MinHeight"));
    }

    [Fact]
    public void TitleChrome_UsesAutoRowsAndDesiredTextHeight()
    {
        XDocument shell = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        XElement titleRow = shell.Descendants()
            .First(element => element.Name.LocalName == "RowDefinition");
        string shellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string switcher = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetGroupTitleSwitcher.xaml"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));
        string quickCaptureCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Appearance.cs"));

        Assert.Equal("Auto", (string?)titleRow.Attribute("Height"));
        Assert.Equal("46", (string?)titleRow.Attribute("MinHeight"));
        Assert.Contains("TitleBarGrid.DesiredSize.Height", shellCode, StringComparison.Ordinal);
        Assert.Contains("ShellRoot.RowDefinitions[0].MinHeight", shellCode, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"32\"", switcher, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"Root\"\r\n        Height=\"32\"", switcher, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"Auto\" MinHeight=\"46\" />", quickCapture, StringComparison.Ordinal);
        Assert.Contains("RootGrid.RowDefinitions[0].MinHeight", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("RootGrid.RowDefinitions[0].Height = GridLength.Auto", quickCaptureCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TopologySwitch_GatesPersistenceUntilEveryWindowHasBeenRestored()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));

        int begin = manager.IndexOf(
            "window.BeginDisplayTopologyTransition(generation)",
            StringComparison.Ordinal);
        int activate = manager.IndexOf(
            "_topologyLayoutService.ActivateCurrentTopology",
            begin,
            StringComparison.Ordinal);
        int end = manager.IndexOf(
            "window.EndDisplayTopologyTransition(generation)",
            activate,
            StringComparison.Ordinal);

        Assert.True(begin >= 0 && activate > begin && end > activate);
        Assert.Contains("finally", manager[activate..end], StringComparison.Ordinal);
        Assert.Contains("persist = false;", bounds, StringComparison.Ordinal);
        Assert.Contains("updateConfig = false;", bounds, StringComparison.Ordinal);
        Assert.Contains("xamlRoot.Changed += ObservedXamlRoot_Changed", bounds, StringComparison.Ordinal);
        Assert.Contains("RequestDisplayTopologyRestore(\"xaml-root-scale\")", bounds, StringComparison.Ordinal);
    }
}

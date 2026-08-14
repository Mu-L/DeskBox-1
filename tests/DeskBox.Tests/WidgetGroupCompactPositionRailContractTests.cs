namespace DeskBox.Tests;

public sealed class WidgetGroupCompactPositionRailContractTests
{
    [Fact]
    public void CollapsedGroup_UsesPositionRailWithoutChangingExpandedLayout()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string shellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        Assert.Contains(
            "x:Name=\"CompactGroupPositionRail\"",
            shellXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"CompactGroupBadge\"",
            shellXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"CompactGroupBadgeText\"",
            shellXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateCompactGroupPositionRail(presentation);",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetGroupNavigationInteractionPolicy.ResolvePositionRailSlots(",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "GroupTitleSwitcher.NavigationStyle =",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "presentation?.NavigationStyle ??",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "show = show && _groupPresentation is null;",
            shellCode,
            StringComparison.Ordinal);
    }
}

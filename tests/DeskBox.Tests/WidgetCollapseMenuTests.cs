namespace DeskBox.Tests;

public sealed class WidgetCollapseMenuTests
{
    [Fact]
    public void ExpansionMenu_ProvidesPerWidgetDirectionChoices()
    {
        string builder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCollapseMenuBuilder.cs"));
        string contentMenu = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string quickCaptureMenu = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"));

        Assert.Contains("CreateExpansionDirectionSubItem", builder, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetCompactExpansionDirectionSettings.GetOverride(config)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionAuto", builder, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionDown", builder, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionUp", builder, StringComparison.Ordinal);
        Assert.Contains("SetCompactExpansionDirectionOverride", contentMenu, StringComparison.Ordinal);
        Assert.Contains("SetCompactExpansionDirectionOverride", quickCaptureMenu, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreAutomaticWidthItem_HasNoIcon()
    {
        string builder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCollapseMenuBuilder.cs"));
        int itemStart = builder.IndexOf("var resetWidthItem", StringComparison.Ordinal);
        int itemEnd = builder.IndexOf("resetWidthItem.Click", itemStart, StringComparison.Ordinal);

        Assert.True(itemStart >= 0 && itemEnd > itemStart);
        Assert.DoesNotContain(
            "Icon =",
            builder[itemStart..itemEnd],
            StringComparison.Ordinal);
    }
}

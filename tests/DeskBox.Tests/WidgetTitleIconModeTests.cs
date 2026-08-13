using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTitleIconModeTests
{
    [Fact]
    public void SearchWidget_UsesDedicatedSearchIconFamily()
    {
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Search));
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromLegacyGlyph("\uE721"));
        Assert.Equal("search", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Search));
        Assert.Equal("WidgetTitleIcon.Label.Search", WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Search));
    }

    [Fact]
    public void TodoAndQuickCaptureColorIcons_UseTheSameVisualPaletteAndCanvas()
    {
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/todo.svg"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/quick-capture.svg"));

        foreach (string icon in new[] { todo, quickCapture })
        {
            Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#38BDF8\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#2563EB\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#60A5FA\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#1D4ED8\"", icon, StringComparison.Ordinal);
            Assert.Contains("#FFFFFF", icon, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("#B3E0FF", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("#8CD0FF", quickCapture, StringComparison.Ordinal);
    }
}

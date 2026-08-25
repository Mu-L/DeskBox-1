using DeskBox.Models;
using DeskBox.Services;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class WidgetForegroundSettingsTests
{
    [Fact]
    public void Resolve_UsesGlobalDefaultsWithoutOverrides()
    {
        var settings = new AppSettings
        {
            WidgetForegroundMode = WidgetForegroundSettings.ModeDark,
            WidgetForegroundColor = "#123456",
            WidgetTextEdgeMode = WidgetForegroundSettings.EdgeSoft
        };
        var config = new WidgetConfig();

        Assert.Equal(
            WidgetForegroundSettings.ModeDark,
            WidgetForegroundSettings.ResolveMode(config, settings));
        Assert.Equal(
            WidgetForegroundSettings.EdgeSoft,
            WidgetForegroundSettings.ResolveEdgeMode(config, settings));
        Assert.Equal(
            Color.FromArgb(0xFF, 0x12, 0x34, 0x56),
            WidgetForegroundSettings.ResolveCustomColor(config, settings));
    }

    [Fact]
    public void Resolve_PrefersIndependentWidgetOverrides()
    {
        var settings = new AppSettings
        {
            WidgetForegroundMode = WidgetForegroundSettings.ModeDark,
            WidgetForegroundColor = "#123456",
            WidgetTextEdgeMode = WidgetForegroundSettings.EdgeOff
        };
        var config = new WidgetConfig();
        WidgetForegroundSettings.SetModeOverride(
            config,
            WidgetForegroundSettings.ModeLight);
        WidgetForegroundSettings.SetEdgeModeOverride(
            config,
            WidgetForegroundSettings.EdgeStrong);
        WidgetForegroundSettings.SetCustomColorOverride(
            config,
            Color.FromArgb(0x40, 0xAA, 0xBB, 0xCC));

        Assert.Equal(
            WidgetForegroundSettings.ModeLight,
            WidgetForegroundSettings.ResolveMode(config, settings));
        Assert.Equal(
            WidgetForegroundSettings.EdgeStrong,
            WidgetForegroundSettings.ResolveEdgeMode(config, settings));
        Assert.Equal(
            Color.FromArgb(0xFF, 0xAA, 0xBB, 0xCC),
            WidgetForegroundSettings.ResolveCustomColor(config, settings));
    }

    [Fact]
    public void ClearingOverrides_RestoresGlobalBehaviorButKeepsChosenColor()
    {
        var config = new WidgetConfig();
        WidgetForegroundSettings.SetModeOverride(
            config,
            WidgetForegroundSettings.ModeCustom);
        WidgetForegroundSettings.SetEdgeModeOverride(
            config,
            WidgetForegroundSettings.EdgeSoft);
        WidgetForegroundSettings.SetCustomColorOverride(
            config,
            Color.FromArgb(0xFF, 0x10, 0x20, 0x30));

        WidgetForegroundSettings.SetModeOverride(config, null);
        WidgetForegroundSettings.SetEdgeModeOverride(config, null);

        Assert.Null(WidgetForegroundSettings.GetModeOverride(config));
        Assert.Null(WidgetForegroundSettings.GetEdgeModeOverride(config));
        Assert.Equal(
            "#102030",
            config.Metadata[WidgetForegroundSettings.ColorOverrideMetadataKey]);
    }

    [Fact]
    public void NormalizeGlobal_CanonicalizesModesAndOpaqueColor()
    {
        var settings = new AppSettings
        {
            WidgetForegroundMode = "custom",
            WidgetForegroundColor = "80123456",
            WidgetTextEdgeMode = "strong"
        };

        Assert.True(WidgetForegroundSettings.NormalizeGlobal(settings));
        Assert.Equal(WidgetForegroundSettings.ModeCustom, settings.WidgetForegroundMode);
        Assert.Equal("#123456", settings.WidgetForegroundColor);
        Assert.Equal(WidgetForegroundSettings.EdgeStrong, settings.WidgetTextEdgeMode);
        Assert.False(WidgetForegroundSettings.NormalizeGlobal(settings));
    }

    [Fact]
    public void NormalizeOverrides_RemovesUnsupportedValuesAndCanonicalizesColor()
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                [WidgetForegroundSettings.ModeOverrideMetadataKey] = "automatic",
                [WidgetForegroundSettings.EdgeOverrideMetadataKey] = "soft",
                [WidgetForegroundSettings.ColorOverrideMetadataKey] = "#80112233"
            }
        };

        Assert.True(WidgetForegroundSettings.NormalizeOverrides(config));
        Assert.False(config.Metadata.ContainsKey(
            WidgetForegroundSettings.ModeOverrideMetadataKey));
        Assert.Equal(
            WidgetForegroundSettings.EdgeSoft,
            config.Metadata[WidgetForegroundSettings.EdgeOverrideMetadataKey]);
        Assert.Equal(
            "#112233",
            config.Metadata[WidgetForegroundSettings.ColorOverrideMetadataKey]);
        Assert.False(WidgetForegroundSettings.NormalizeOverrides(config));
    }
}

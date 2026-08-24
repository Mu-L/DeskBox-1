using DeskBox.Models;
using DeskBox.Services;
using System.Text.Json;

namespace DeskBox.Tests;

public sealed class WidgetFileStackSettingsTests
{
    [Fact]
    public void Resolve_UsesGlobalDefaultsWithoutOverrides()
    {
        var config = new WidgetConfig();

        Assert.True(WidgetFileStackSettings.ResolveEnabled(config, globalDefault: true));
        Assert.Equal(
            SettingsService.FileStackGroupByDateModified,
            WidgetFileStackSettings.ResolveGroupBy(
                config,
                SettingsService.FileStackGroupByDateModified));
        Assert.Equal(5, WidgetFileStackSettings.ResolveThreshold(config, 5));
        Assert.Equal(
            SettingsService.FileStackOrderByName,
            WidgetFileStackSettings.ResolveOrderBy(
                config,
                SettingsService.FileStackOrderByName));
        Assert.True(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Fact]
    public void Resolve_PrefersWidgetOverrides()
    {
        var config = new WidgetConfig();
        WidgetFileStackSettings.SetEnabledOverride(config, false);
        WidgetFileStackSettings.SetGroupByOverride(
            config,
            SettingsService.FileStackGroupByDateModified);
        WidgetFileStackSettings.SetThresholdOverride(config, 2);
        WidgetFileStackSettings.SetOrderByOverride(
            config,
            SettingsService.FileStackOrderByDateModified);

        Assert.False(WidgetFileStackSettings.ResolveEnabled(config, globalDefault: true));
        Assert.Equal(
            SettingsService.FileStackGroupByDateModified,
            WidgetFileStackSettings.ResolveGroupBy(
                config,
                SettingsService.FileStackGroupByKind));
        Assert.Equal(2, WidgetFileStackSettings.ResolveThreshold(config, 5));
        Assert.Equal(
            SettingsService.FileStackOrderByDateModified,
            WidgetFileStackSettings.ResolveOrderBy(
                config,
                SettingsService.FileStackOrderByWidget));
        Assert.False(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Fact]
    public void ClearOverrides_RestoresGlobalDefaults()
    {
        var config = new WidgetConfig();
        WidgetFileStackSettings.SetEnabledOverride(config, true);
        WidgetFileStackSettings.SetGroupByOverride(
            config,
            SettingsService.FileStackGroupByDateModified);
        WidgetFileStackSettings.SetThresholdOverride(config, 5);
        WidgetFileStackSettings.SetOrderByOverride(
            config,
            SettingsService.FileStackOrderByName);

        WidgetFileStackSettings.ClearOverrides(config);

        Assert.Null(WidgetFileStackSettings.GetEnabledOverride(config));
        Assert.Null(WidgetFileStackSettings.GetGroupByOverride(config));
        Assert.Null(WidgetFileStackSettings.GetThresholdOverride(config));
        Assert.Null(WidgetFileStackSettings.GetOrderByOverride(config));
        Assert.True(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    public void NormalizeThreshold_AllowsOnlySupportedOptions(int value, int expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeFileStackThreshold(value));
    }

    [Theory]
    [InlineData(null, SettingsService.FileStackOrderByWidget)]
    [InlineData("name", SettingsService.FileStackOrderByName)]
    [InlineData("dateadded", SettingsService.FileStackOrderByDateAdded)]
    [InlineData("datemodified", SettingsService.FileStackOrderByDateModified)]
    [InlineData("unexpected", SettingsService.FileStackOrderByWidget)]
    public void NormalizeOrder_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeFileStackOrderBy(value));
    }

    [Fact]
    public void NormalizeOverrides_RemovesInvalidMetadataAndCanonicalizesValues()
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                [WidgetFileStackSettings.EnabledOverrideMetadataKey] = "true",
                [WidgetFileStackSettings.GroupByOverrideMetadataKey] = "datecreated",
                [WidgetFileStackSettings.ThresholdOverrideMetadataKey] = "4",
                [WidgetFileStackSettings.OrderByOverrideMetadataKey] = "unexpected"
            }
        };

        Assert.True(WidgetFileStackSettings.NormalizeOverrides(config));
        Assert.Equal("True", config.Metadata[WidgetFileStackSettings.EnabledOverrideMetadataKey]);
        Assert.Equal(
            SettingsService.FileStackGroupByKind,
            config.Metadata[WidgetFileStackSettings.GroupByOverrideMetadataKey]);
        Assert.False(config.Metadata.ContainsKey(WidgetFileStackSettings.ThresholdOverrideMetadataKey));
        Assert.False(config.Metadata.ContainsKey(WidgetFileStackSettings.OrderByOverrideMetadataKey));
    }

    [Fact]
    public void WidgetConfig_FileAddedTimesRoundTripThroughJson()
    {
        var addedAt = new DateTimeOffset(2026, 7, 17, 1, 45, 0, TimeSpan.FromHours(8));
        var config = new WidgetConfig
        {
            FileAddedAtTrackingInitialized = true,
            FileAddedAtByPath = new Dictionary<string, DateTimeOffset>
            {
                [@"C:\Work\report.docx"] = addedAt
            }
        };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.NotNull(restored);
        Assert.True(restored.FileAddedAtTrackingInitialized);
        Assert.Equal(addedAt, restored.FileAddedAtByPath[@"C:\Work\report.docx"]);
    }

    [Fact]
    public void CustomGroupingOverride_IsAcceptedAndCanonicalized()
    {
        var config = new WidgetConfig();

        WidgetFileStackSettings.SetGroupByOverride(config, "custom");

        Assert.Equal(
            SettingsService.FileStackGroupByCustom,
            WidgetFileStackSettings.GetGroupByOverride(config));
        Assert.Equal(
            SettingsService.FileStackGroupByCustom,
            config.Metadata[WidgetFileStackSettings.GroupByOverrideMetadataKey]);
    }

    [Fact]
    public void NormalizeExtensions_AcceptsFriendlyInputAndRemovesDuplicates()
    {
        var extensions = SettingsService.NormalizeFileStackExtensions(
            ["PSD", "*.AI", ".psd", "  .FIG  ", @"bad\path"]);

        Assert.Equal([".psd", ".ai", ".fig"], extensions);
    }

    [Fact]
    public void StackOrder_RoundTripPreservesMixedDisplayUnitOrder()
    {
        var config = new WidgetConfig();
        string[] order =
        [
            "Documents",
            @"Item:C:\B",
            "Images",
            @"Item:C:\A"
        ];

        WidgetFileStackSettings.SetStackOrder(config, order);

        Assert.Equal(order, WidgetFileStackSettings.GetStackOrder(config));
    }

    [Fact]
    public void StackMemberOverrides_RoundTripPreservesManualMemberships()
    {
        var config = new WidgetConfig();
        var memberships =
            new Dictionary<string, List<string>>
            {
                ["Manual:alpha"] =
                [
                    @"C:\Work\one.txt",
                    @"C:\Work\two.png"
                ],
                ["Documents"] =
                [
                    @"C:\Work\forced.pdf"
                ]
            };

        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            memberships);

        Dictionary<string, List<string>> restored =
            WidgetFileStackSettings.GetStackMemberOverrides(config);
        Assert.Equal(
            memberships.Keys,
            restored.Keys);
        Assert.Equal(
            memberships["Manual:alpha"],
            restored["Manual:alpha"]);
        Assert.Equal(
            memberships["Documents"],
            restored["Documents"]);
    }

    [Fact]
    public void WidgetMetadataJson_PreservesDictionaryKeysAndCompactFormat()
    {
        var config = new WidgetConfig();
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MiXeD:Group"] = "Display name"
        };

        WidgetFileStackSettings.SetStackNameOverrides(config, names);

        string json = config.Metadata[WidgetFileStackSettings.StackNameOverridesMetadataKey];
        Assert.Equal("{\"MiXeD:Group\":\"Display name\"}", json);
        Assert.Equal(
            "Display name",
            WidgetFileStackSettings.GetStackNameOverrides(config)["MiXeD:Group"]);
    }

    [Fact]
    public void AppSettings_CustomRulesRoundTripThroughJson()
    {
        var settings = new AppSettings
        {
            FileStackGroupBy = SettingsService.FileStackGroupByCustom,
            FileStackUnmatchedBehavior = SettingsService.FileStackUnmatchedOther,
            FileStackCustomRules =
            [
                new FileStackCustomRule
                {
                    Id = "design",
                    Name = "Design",
                    Extensions = [".psd", ".fig"]
                }
            ]
        };

        string json = JsonSerializer.Serialize(settings);
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(SettingsService.FileStackGroupByCustom, restored.FileStackGroupBy);
        Assert.Equal(SettingsService.FileStackUnmatchedOther, restored.FileStackUnmatchedBehavior);
        FileStackCustomRule rule = Assert.Single(restored.FileStackCustomRules);
        Assert.Equal("design", rule.Id);
        Assert.Equal([".psd", ".fig"], rule.Extensions);
    }
}

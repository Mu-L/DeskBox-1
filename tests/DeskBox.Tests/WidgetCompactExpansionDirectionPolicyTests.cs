using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactExpansionDirectionPolicyTests
{
    [Fact]
    public void Auto_PreservesAutomaticAnchorOrder()
    {
        WidgetCompactExpansionAnchor[] automatic =
        [
            WidgetCompactExpansionAnchor.RightBottom,
            WidgetCompactExpansionAnchor.RightTop,
            WidgetCompactExpansionAnchor.LeftBottom
        ];

        IReadOnlyList<WidgetCompactExpansionAnchor> result =
            WidgetCompactExpansionDirectionPolicy.Apply(
                SettingsService.WidgetCompactExpansionDirectionAuto,
                automatic);

        Assert.Equal(automatic, result);
    }

    [Theory]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionDown,
        WidgetCompactExpansionAnchor.RightTop,
        WidgetCompactExpansionAnchor.LeftTop)]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionUp,
        WidgetCompactExpansionAnchor.RightBottom,
        WidgetCompactExpansionAnchor.LeftBottom)]
    public void FixedDirection_ChangesOnlyVerticalAnchorAndKeepsHorizontalPreference(
        string direction,
        WidgetCompactExpansionAnchor expectedFirst,
        WidgetCompactExpansionAnchor expectedSecond)
    {
        IReadOnlyList<WidgetCompactExpansionAnchor> result =
            WidgetCompactExpansionDirectionPolicy.Apply(
                direction,
                [
                    WidgetCompactExpansionAnchor.RightBottom,
                    WidgetCompactExpansionAnchor.RightTop,
                    WidgetCompactExpansionAnchor.LeftBottom,
                    WidgetCompactExpansionAnchor.LeftTop
                ]);

        Assert.Equal(new[] { expectedFirst, expectedSecond }, result);
    }

    [Theory]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionDown,
        WidgetCompactExpansionAnchor.LeftTop,
        WidgetCompactExpansionAnchor.RightTop)]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionUp,
        WidgetCompactExpansionAnchor.LeftBottom,
        WidgetCompactExpansionAnchor.RightBottom)]
    public void FixedDirection_UsesBothHorizontalFallbacksWhenAutomaticOrderIsEmpty(
        string direction,
        WidgetCompactExpansionAnchor expectedFirst,
        WidgetCompactExpansionAnchor expectedSecond)
    {
        IReadOnlyList<WidgetCompactExpansionAnchor> result =
            WidgetCompactExpansionDirectionPolicy.Apply(direction, []);

        Assert.Equal(new[] { expectedFirst, expectedSecond }, result);
    }

    [Theory]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionAuto,
        WidgetPositionAnchors.RightBottom,
        WidgetPositionAnchors.RightBottom)]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionDown,
        WidgetPositionAnchors.RightBottom,
        WidgetPositionAnchors.RightTop)]
    [InlineData(
        SettingsService.WidgetCompactExpansionDirectionUp,
        WidgetPositionAnchors.LeftTop,
        WidgetPositionAnchors.LeftBottom)]
    public void PositionAnchor_UsesTheSameVerticalEdgeAsExpansion(
        string direction,
        string currentAnchor,
        string expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactExpansionDirectionPolicy.ApplyToPositionAnchor(
                direction,
                currentAnchor));
    }

    [Fact]
    public void SettingsPage_ExposesThreeDirectionOptionsAndBindsTheSelection()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SettingsViewModel.CapsuleOptions.cs"));

        Assert.Contains("Settings.Capsule.ExpansionDirection.Title", xaml, StringComparison.Ordinal);
        Assert.Contains("AvailableWidgetCompactExpansionDirectionOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedWidgetCompactExpansionDirection, Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionAuto", viewModel, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionDown", viewModel, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactExpansionDirectionUp", viewModel, StringComparison.Ordinal);
    }
}

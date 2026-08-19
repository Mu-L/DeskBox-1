namespace DeskBox.Tests;

public sealed class WidgetCornerSettingsContractTests
{
    [Fact]
    public void CornerSelector_OffersRoundSmallAndSquareWithRoundFirst()
    {
        string options = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs"));
        string displayNames = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SettingsViewModel.DisplayNames.cs"));

        Assert.Contains(
            "[CornerRound, CornerSmall, CornerSquare]",
            options,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CornerDefault", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Corner.Default", displayNames, StringComparison.Ordinal);
    }

    [Fact]
    public void CornerLocalization_NoLongerContainsSystemDefaultOption()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string file in Directory.EnumerateFiles(stringsRoot, "*.json"))
        {
            string json = File.ReadAllText(file);
            Assert.DoesNotContain("\"Settings.Corner.Default\"", json, StringComparison.Ordinal);
        }
    }
}

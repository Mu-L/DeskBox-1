using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CitySearchServiceTests
{
    [Theory]
    [InlineData("New York", "newyork")]
    [InlineData("São Paulo", "saopaulo")]
    [InlineData("  New-York  ", "newyork")]
    public void NormalizeSearchText_IgnoresSpacingPunctuationAndDiacritics(
        string input,
        string expected)
    {
        Assert.Equal(expected, CitySearchService.NormalizeSearchText(input));
    }

    [Fact]
    public void SearchLocal_NewYorkWithoutSpace_ReturnsNewYorkCityFirst()
    {
        var results = CitySearchService.SearchLocal("newyork", isEn: true);

        var result = Assert.IsType<DeskBox.Models.WeatherCitySearchResult>(results.First());
        Assert.Equal("New York", result.Name);
        Assert.InRange(result.Latitude, 40.70, 40.73);
        Assert.InRange(result.Longitude, -74.02, -73.99);
    }
}

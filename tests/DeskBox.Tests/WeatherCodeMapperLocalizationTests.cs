using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class WeatherCodeMapperLocalizationTests
{
    [Theory]
    [InlineData("zh-TW", "晴")]
    [InlineData("hi-IN", "साफ आसमान")]
    [InlineData("es-ES", "Cielo despejado")]
    [InlineData("fr-FR", "Ciel dégagé")]
    [InlineData("ar-SA", "سماء صافية")]
    [InlineData("bn-BD", "পরিষ্কার আকাশ")]
    [InlineData("ru-RU", "Ясное небо")]
    public void NewLocales_LocalizeWeatherDescription(string locale, string expected)
    {
        Assert.Equal(expected, WeatherCodeMapper.GetDescription(0, locale));
    }
}

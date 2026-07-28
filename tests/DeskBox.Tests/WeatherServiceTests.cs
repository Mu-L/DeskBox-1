using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WeatherServiceTests
{
    [Fact]
    public void ConvertMsnToWeatherData_UsesFollowingDayWhenCurrentDayHasNoHourlyRows()
    {
        var response = new MsnWeatherBody
        {
            Forecast = new MsnWeatherForecast
            {
                Days =
                [
                    new MsnForecastDay { Hourly = [] },
                    new MsnForecastDay
                    {
                        Hourly =
                        [
                            new MsnForecastHour
                            {
                                Valid = "2026-07-29T00:00:00+08:00",
                                Temp = 27,
                                Precip = 20,
                                Cap = "Partly cloudy",
                                Icon = 29
                            },
                            new MsnForecastHour
                            {
                                Valid = "2026-07-29T01:00:00+08:00",
                                Temp = 26,
                                Precip = 30,
                                Cap = "Cloudy",
                                Icon = 26
                            }
                        ]
                    }
                ]
            }
        };

        WeatherData data = WeatherService.ConvertMsnToWeatherData(response, 30.5, 114.3);

        Assert.NotNull(data.Hourly);
        Assert.Equal(2, data.Hourly.Time.Count);
        Assert.Equal([27d, 26d], data.Hourly.Temperature);
        Assert.Equal([20d, 30d], data.Hourly.PrecipitationProbability);
    }
}

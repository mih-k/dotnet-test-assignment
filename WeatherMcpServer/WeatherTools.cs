using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool, Description("Get current weather for a city.")]
    public static Task<string> GetCurrentWeather(
        WeatherService service,
        [Description("City name (e.g., London)")] string city,
        [Description("Optional ISO country code (e.g., GB)")] string? countryCode = null)
        => service.GetCurrentWeatherAsync(city, countryCode);

    [McpServerTool, Description("Get a summarized 1–5 day forecast.")]
    public static Task<string> GetWeatherForecast(
        WeatherService service,
        [Description("City name (e.g., Berlin)")] string city,
        [Description("Optional ISO country code (e.g., DE)")] string? countryCode = null,
        [Description("Days ahead (1–5)")] int days = 3)
        => service.GetForecastAsync(city, countryCode, Math.Clamp(days, 1, 5));

    [McpServerTool, Description("Active government weather alerts via One Call 3.0.")]
    public static Task<string> GetWeatherAlerts(
        WeatherService service,
        [Description("City name (e.g., Miami)")] string city,
        [Description("Optional ISO country code (e.g., US)")] string? countryCode = null)
        => service.GetAlertsAsync(city, countryCode);
}

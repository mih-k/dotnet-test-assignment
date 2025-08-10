using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class WeatherService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WeatherService> _log;

    public WeatherService(HttpClient http, IConfiguration cfg, ILogger<WeatherService> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    // ---------- Public API ----------

    public async Task<string> GetCurrentWeatherAsync(string city, string? countryCode)
    {
        if (!TryValidateCity(city, out var reason)) return reason;

        var api = RequireApiKeyOrThrow();
        var baseUrl = _cfg["WeatherApi:BaseUrl"] ?? "https://api.openweathermap.org/data/2.5";
        var units = _cfg["WeatherApi:Units"] ?? "metric";
        var lang = _cfg["WeatherApi:Lang"] ?? "en";

        var location = BuildLocation(city, countryCode);
        var url = $"{baseUrl}/weather?q={Uri.EscapeDataString(location)}&appid={api}&units={units}&lang={lang}";

        var json = await SendAsync(url);
        if (json.Error != null) return json.Error;

        try
        {
            var root = json.Document!.RootElement;
            var temp = root.GetProperty("main").GetProperty("temp").GetDecimal();
            var feels = root.GetProperty("main").GetProperty("feels_like").GetDecimal();
            var hum = root.GetProperty("main").GetProperty("humidity").GetInt32();
            var desc = root.GetProperty("weather")[0].GetProperty("description").GetString();
            return $"Current weather in {location}: {temp}°C (feels {feels}°C), {desc}, humidity {hum}%.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse current weather response for {Location}", location);
            return "Unable to parse weather data from provider.";
        }
    }

    public async Task<string> GetForecastAsync(string city, string? countryCode, int days = 3)
    {
        if (!TryValidateCity(city, out var reason)) return reason;

        var api = RequireApiKeyOrThrow();
        var baseUrl = _cfg["WeatherApi:BaseUrl"] ?? "https://api.openweathermap.org/data/2.5";
        var units = _cfg["WeatherApi:Units"] ?? "metric";
        var lang = _cfg["WeatherApi:Lang"] ?? "en";
        days = Math.Clamp(days, 1, 5);

        var location = BuildLocation(city, countryCode);
        var url = $"{baseUrl}/forecast?q={Uri.EscapeDataString(location)}&appid={api}&units={units}&lang={lang}";

        var json = await SendAsync(url);
        if (json.Error != null) return json.Error;

        try
        {
            var list = json.Document!.RootElement.GetProperty("list");

            var groups = new Dictionary<string, (decimal temp, string desc, string hour)>();
            foreach (var item in list.EnumerateArray())
            {
                var dtTxt = item.GetProperty("dt_txt").GetString() ?? "";
                var date = dtTxt.Length >= 10 ? dtTxt[..10] : dtTxt;
                var hour = dtTxt.Length >= 13 ? dtTxt.Substring(11, 2) : "00";
                var temp = item.GetProperty("main").GetProperty("temp").GetDecimal();
                var desc = item.GetProperty("weather")[0].GetProperty("description").GetString() ?? "";
                if (!groups.ContainsKey(date) || hour == "12")
                    groups[date] = (temp, desc, hour);
            }

            var lines = groups.Keys
                .OrderBy(k => k)
                .Take(days)
                .Select(d => $"{d}: {groups[d].temp}°C, {groups[d].desc}");

            return $"Forecast for {location} (next {days} day(s)):\n- " + string.Join("\n- ", lines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse forecast response for {Location}", location);
            return "Unable to parse forecast data from provider.";
        }
    }

    public async Task<string> GetAlertsAsync(string city, string? countryCode)
    {
        if (!TryValidateCity(city, out var reason)) return reason;

        var api = RequireApiKeyOrThrow();
        var units = _cfg["WeatherApi:Units"] ?? "metric";
        var lang = _cfg["WeatherApi:Lang"] ?? "en";

        (double lat, double lon, string resolved) geo;
        try
        {
            geo = await GeocodeAsync(city, countryCode);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Geocoding failed for {City}/{Country}", city, countryCode);
            return ex.Message.Contains("Location not found", StringComparison.OrdinalIgnoreCase)
                ? $"City not found: {BuildLocation(city, countryCode)}"
                : "Unable to resolve location. Please verify city and country code.";
        }

        var url = $"https://api.openweathermap.org/data/3.0/onecall?lat={geo.lat}&lon={geo.lon}&appid={api}&units={units}&lang={lang}";
        var json = await SendAsync(url, endpointHint: "onecall");

        if (json.Error != null)
        {
            // Make subscription errors clearer
            if (json.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return "OpenWeather One Call 3.0 access denied. Your account likely lacks a One Call subscription (or the API key is invalid).";
            return json.Error;
        }

        try
        {
            var root = json.Document!.RootElement;
            if (!root.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array || alerts.GetArrayLength() == 0)
                return $"No active alerts for {geo.resolved}.";

            var lines = new List<string>();
            foreach (var a in alerts.EnumerateArray())
            {
                string GetStr(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long GetLong(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

                var sender = GetStr("sender_name");
                var evt = GetStr("event");
                var start = GetLong("start");
                var end = GetLong("end");
                var desc = GetStr("description");

                var startIso = start > 0 ? DateTimeOffset.FromUnixTimeSeconds(start).ToString("u") : "unknown";
                var endIso = end > 0 ? DateTimeOffset.FromUnixTimeSeconds(end).ToString("u") : "unknown";

                lines.Add($"• {evt} ({sender})\n  from {startIso} to {endIso}\n  {desc}".Trim());
            }
            return $"Alerts for {geo.resolved}:\n" + string.Join("\n\n", lines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse alerts response for {Resolved}", geo.resolved);
            return "Unable to parse alert data from provider.";
        }
    }

    // ---------- Internals ----------

    private string RequireApiKeyOrThrow()
    {
        var api = _cfg["WeatherApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(api))
        {
            _log.LogWarning("Weather API key not configured (WeatherApi:ApiKey).");
            throw new InvalidOperationException("Weather API key is not set (WeatherApi:ApiKey).");
        }
        return api;
    }

    private static bool TryValidateCity(string city, out string error)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            error = "City must not be empty.";
            return false;
        }
        error = "";
        return true;
    }

    private static string BuildLocation(string city, string? countryCode) =>
        string.IsNullOrWhiteSpace(countryCode) ? city.Trim() : $"{city.Trim()},{countryCode.Trim()}";

    private async Task<(double lat, double lon, string resolved)> GeocodeAsync(string city, string? countryCode)
    {
        var api = RequireApiKeyOrThrow();
        var q = BuildLocation(city, countryCode);
        var url = $"https://api.openweathermap.org/geo/1.0/direct?q={Uri.EscapeDataString(q)}&limit=1&appid={api}";

        var json = await SendAsync(url, endpointHint: "geocoding");
        if (json.Error != null) throw new InvalidOperationException(json.Error);

        var arr = json.Document!.RootElement.EnumerateArray();
        if (!arr.MoveNext()) throw new InvalidOperationException($"Location not found: {q}");

        var el = arr.Current;
        var resolved = $"{el.GetProperty("name").GetString()},{el.GetProperty("country").GetString()}";
        return (el.GetProperty("lat").GetDouble(), el.GetProperty("lon").GetDouble(), resolved);
    }

    private sealed record JsonResult(JsonDocument? Document, string? Error, HttpStatusCode? StatusCode);

    private async Task<JsonResult> SendAsync(string url, string? endpointHint = null)
    {
        try
        {
            _log.LogDebug("HTTP GET {Url}", Redact(url));
            using var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                // Map common provider codes to clearer messages
                var mapped = MapProviderError(resp.StatusCode, body, endpointHint);
                _log.LogInformation("Provider returned {Status} for {Hint}: {Message}", (int)resp.StatusCode, endpointHint ?? "request", mapped);
                return new JsonResult(null, mapped, resp.StatusCode);
            }

            return new JsonResult(JsonDocument.Parse(body), null, null);
        }
        catch (TaskCanceledException ex)
        {
            _log.LogWarning(ex, "Timeout calling provider for {Hint}", endpointHint ?? "request");
            return new JsonResult(null, "Weather provider timeout. Please try again.", null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Network error calling provider for {Hint}", endpointHint ?? "request");
            return new JsonResult(null, "Network error reaching weather provider.", null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error calling provider for {Hint}", endpointHint ?? "request");
            return new JsonResult(null, "Unexpected error contacting weather provider.", null);
        }
    }

    private static string MapProviderError(HttpStatusCode status, string body, string? hint)
    {
        var lower = body.ToLowerInvariant();

        // City not found (OpenWeather often returns 404 with message)
        if (status == HttpStatusCode.NotFound || lower.Contains("city not found"))
            return "City not found. Please check the city and country code.";

        // Unauthorized/Forbidden
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
        {
            if (hint == "onecall") return "OpenWeather One Call 3.0 access denied. Your account likely lacks a One Call subscription (or the API key is invalid).";
            return "Weather API key invalid or missing.";
        }

        // Too Many Requests
        if (status == (HttpStatusCode)429) return "Rate limit exceeded. Please wait and try again.";

        // Default
        return $"Error {(int)status}: {body}";
    }

    // Avoid logging secrets
    private static string Redact(string url)
    {
        var i = url.IndexOf("appid=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url;
        var j = url.IndexOf('&', i);
        return j < 0 ? url[..(i + 6)] + "****" : url[..(i + 6)] + "****" + url[j..];
    }
}

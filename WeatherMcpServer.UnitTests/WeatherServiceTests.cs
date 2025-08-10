using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class WeatherServiceTests
{
    [Fact]
    public async Task CurrentWeather_MissingApiKey_Throws()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var svc = new WeatherService(new HttpClient(), cfg, NullLogger<WeatherService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetCurrentWeatherAsync("London", "GB"));
    }

    [Fact]
    public async Task CurrentWeather_HttpError_MapsToFriendlyMessage()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WeatherApi:ApiKey"] = "test",
                ["WeatherApi:BaseUrl"] = "https://api.test"
            })
            .Build();

        var http = new HttpClient(new FakeHandler(HttpStatusCode.NotFound, "{\"message\":\"city not found\"}"));
        var svc = new WeatherService(http, cfg, NullLogger<WeatherService>.Instance);

        var res = await svc.GetCurrentWeatherAsync("NopeCity", "ZZ");
        Assert.Contains("City not found", res);
    }

    [Fact]
    public async Task Forecast_DaysAreClamped_1to5()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WeatherApi:ApiKey"] = "test",
                ["WeatherApi:BaseUrl"] = "https://api.test"
            })
            .Build();

        // Minimal valid /forecast response: list array with one item
        var body = """
        { "list": [ { "dt_txt":"2025-08-09 12:00:00", "main": { "temp": 20 }, "weather":[{"description":"clear"}] } ] }
        """;

        var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, body));
        var svc = new WeatherService(http, cfg, NullLogger<WeatherService>.Instance);

        var resLow = await svc.GetForecastAsync("X", null, -10);
        var resHigh = await svc.GetForecastAsync("X", null, 999);
        Assert.Contains("next 1 day", resLow);
        Assert.Contains("next 5 day", resHigh);
    }

    [Fact]
    public async Task Alerts_Forbidden_ShowsSubscriptionHint()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WeatherApi:ApiKey"] = "test"
            })
            .Build();

        // Geocode OK
        var geoBody = """[{ "name": "City", "country":"CC", "lat": 1.1, "lon": 2.2 }]""";
        // One Call forbidden
        var chain = new ChainedHandler(new[]
        {
            ("/geo/1.0/direct", HttpStatusCode.OK,  geoBody),
            ("/data/3.0/onecall", HttpStatusCode.Forbidden, "forbidden")
        });

        var http = new HttpClient(chain) { BaseAddress = new Uri("https://api.test") };
        var svc = new WeatherService(http, cfg, NullLogger<WeatherService>.Instance);

        var res = await svc.GetAlertsAsync("City", "CC");
        Assert.Contains("One Call 3.0 access denied", res);
    }

    // Helpers

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public FakeHandler(HttpStatusCode code, string body)
        { _code = code; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    private sealed class ChainedHandler : HttpMessageHandler
    {
        private readonly Queue<(string pathContains, HttpStatusCode code, string body)> _responses;
        public ChainedHandler(IEnumerable<(string, HttpStatusCode, string)> responses)
            => _responses = new Queue<(string, HttpStatusCode, string)>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("no more responses") });

            var (path, code, body) = _responses.Dequeue();
            // crude path check to line up calls
            if (!req.RequestUri!.ToString().Contains(path, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unexpected request: " + req.RequestUri) });

            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>Cached Open-Meteo weather forecast proxy and city geocoding service for StandBy &amp; Settings.</summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, string Json)> _cache = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneDeskAgent/1.0");
        return client;
    }

    internal static string CacheKey(double lat, double lon, string model, string unit, string city) =>
        string.Create(CultureInfo.InvariantCulture, $"{lat:F4},{lon:F4},{model},{unit},{city}");

    public async Task<(int StatusCode, string Json)> GetForecastAsync(WeatherConfig cfg, bool force, CancellationToken ct)
    {
        var lat = double.IsFinite(cfg.Lat) ? cfg.Lat : -21.7642;
        var lon = double.IsFinite(cfg.Lon) ? cfg.Lon : -43.3496;
        var intervalMin = Math.Clamp(cfg.IntervalMin <= 0 ? 15 : cfg.IntervalMin, 5, 180);
        var model = WeatherModelJsonConverter.Normalize(cfg.Model);
        var unit = WeatherUnitJsonConverter.Normalize(cfg.Unit);
        var city = string.IsNullOrWhiteSpace(cfg.City) ? "Juiz de Fora" : cfg.City.Trim();
        var key = CacheKey(lat, lon, model, unit, city);

        if (!force && _cache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(intervalMin))
        {
            return (200, cached.Json);
        }

        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,apparent_temperature,weather_code,is_day&hourly=temperature_2m,weather_code,precipitation_probability,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,daylight_duration,uv_index_max&timezone=auto&forecast_days=7");
        if (!string.IsNullOrEmpty(model))
            url += "&models=" + Uri.EscapeDataString(model);
        if (unit == "fahrenheit")
            url += "&temperature_unit=fahrenheit";

        try
        {
            var raw = await Http.GetStringAsync(url, ct);
            var node = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("Invalid weather payload");
            node["city"] = city;
            node["lat"] = lat;
            node["lon"] = lon;
            node["unit"] = unit;
            node["model"] = model;
            node["intervalMin"] = intervalMin;
            node["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            node["stale"] = false;
            var json = node.ToJsonString();
            _cache[key] = (DateTimeOffset.UtcNow, json);
            return (200, json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_cache.TryGetValue(key, out var stale))
            {
                try
                {
                    var node = JsonNode.Parse(stale.Json)?.AsObject();
                    if (node is not null)
                    {
                        node["stale"] = true;
                        node["error"] = ex.Message;
                        return (200, node.ToJsonString());
                    }
                }
                catch { }
                return (200, stale.Json);
            }
            return (502, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    public async Task<(int StatusCode, string Json)> SearchAsync(string? query, string? lang, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (200, """{"results":[]}""");
        var language = string.IsNullOrWhiteSpace(lang) ? "pt" : lang.Trim();
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=6&language={Uri.EscapeDataString(language)}&format=json";
        try
        {
            var raw = await Http.GetStringAsync(url, ct);
            return (200, raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (502, JsonSerializer.Serialize(new { ok = false, error = ex.Message, results = Array.Empty<object>() }));
        }
    }

    public async Task<(double Lat, double Lon)?> TryGeocodeAsync(string city, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&format=json";
        try
        {
            var raw = await Http.GetStringAsync(url, cts.Token);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("latitude", out var latEl) && latEl.TryGetDouble(out var lat)
                    && item.TryGetProperty("longitude", out var lonEl) && lonEl.TryGetDouble(out var lon))
                    return (lat, lon);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }
}

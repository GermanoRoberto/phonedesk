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
        var city = string.IsNullOrWhiteSpace(cfg.City) ? "Juiz de Fora" : cfg.City.Trim();
        var lat = cfg.Lat;
        var lon = cfg.Lon;

        // Auto-geocode exact coordinates if not set or if city was customized
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || (lat == 0 && lon == 0))
        {
            lat = -21.7642;
            lon = -43.3496;
        }

        if (!string.IsNullOrWhiteSpace(city) && !string.Equals(city, "JF", StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(city, "Juiz de Fora", StringComparison.OrdinalIgnoreCase) || lat != -21.7642))
        {
            var geo = await TryGeocodeAsync(city, ct);
            if (geo.HasValue)
            {
                lat = geo.Value.Lat;
                lon = geo.Value.Lon;
            }
        }

        var intervalMin = Math.Clamp(cfg.IntervalMin <= 0 ? 15 : cfg.IntervalMin, 5, 180);
        var model = WeatherModelJsonConverter.Normalize(cfg.Model);
        var unit = WeatherUnitJsonConverter.Normalize(cfg.Unit);
        var key = CacheKey(lat, lon, model, unit, city);

        if (!force && _cache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(intervalMin))
        {
            return (200, cached.Json);
        }

        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,rain,weather_code,wind_speed_10m,wind_gusts_10m,is_day&hourly=temperature_2m,weather_code,precipitation_probability,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,uv_index_max&timezone=auto&forecast_days=7");
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

            // Intelligent Meteorological Alert Detection (El Niño, Severe Storms, Heatwaves)
            var cur = node["current"]?.AsObject();
            var temp = cur?["temperature_2m"]?.GetValue<double>() ?? 25.0;
            var feels = cur?["apparent_temperature"]?.GetValue<double>() ?? temp;
            var code = cur?["weather_code"]?.GetValue<int>() ?? 0;
            var windGusts = cur?["wind_gusts_10m"]?.GetValue<double>() ?? 0.0;
            var precip = cur?["precipitation"]?.GetValue<double>() ?? 0.0;
            var humidity = cur?["relative_humidity_2m"]?.GetValue<int>() ?? 60;

            JsonObject? alertObj = null;
            if (code is 95 or 96 or 99 || windGusts >= 55)
            {
                alertObj = new JsonObject
                {
                    ["level"] = "danger",
                    ["icon"] = "⛈️",
                    ["title"] = "Alerta de Tempestade & Rajadas",
                    ["desc"] = $"Ventos de até {Math.Round(windGusts)} km/h e temporais previstos."
                };
            }
            else if (precip >= 10 || code is 65 or 82)
            {
                alertObj = new JsonObject
                {
                    ["level"] = "warning",
                    ["icon"] = "🌧️",
                    ["title"] = "Alerta de Chuva Torrencial",
                    ["desc"] = "Chuva forte acumulada com risco de alagamentos."
                };
            }
            else if (feels >= 33 || temp >= 32)
            {
                alertObj = new JsonObject
                {
                    ["level"] = "warning",
                    ["icon"] = "🔥",
                    ["title"] = "Alerta de Calor Extremo (El Niño)",
                    ["desc"] = $"Sensação de {Math.Round(feels)}°C. Hidrate-se e evite exposição solar."
                };
            }
            else if (humidity <= 25)
            {
                alertObj = new JsonObject
                {
                    ["level"] = "warning",
                    ["icon"] = "💧",
                    ["title"] = "Alerta de Baixa Umidade do Ar",
                    ["desc"] = $"Umidade em nível crítico de {humidity}%. Beba bastante água."
                };
            }
            else if (temp <= 8)
            {
                alertObj = new JsonObject
                {
                    ["level"] = "info",
                    ["icon"] = "❄️",
                    ["title"] = "Alerta de Frio Intenso",
                    ["desc"] = $"Temperatura de {Math.Round(temp)}°C registrada na região."
                };
            }

            node["alert"] = alertObj;

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

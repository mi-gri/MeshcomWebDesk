using System.Text.Json;

namespace MeshcomWebDesk.Services.Weather;

/// <summary>
/// Fetches current weather data from the AWEKAS network.
/// API endpoint: https://api.awekas.at/current.php?key=API_KEY
/// Authentication: API-Key (from AWEKAS account settings)
/// Response: JSON with current weather fields under "current" object
/// </summary>
public class AwekasProvider : IWeatherProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AwekasProvider> _logger;

    private const string ApiUrl = "https://api.awekas.at/current.php";

    public string Name => "AWEKAS";

    public AwekasProvider(IHttpClientFactory httpClientFactory, ILogger<AwekasProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WeatherData?> FetchAsync(string apiKey, string stationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("AWEKAS: API-Key muss eingetragen sein.");

        var client = _httpClientFactory.CreateClient("WeatherApi");
        var url = $"{ApiUrl}?key={Uri.EscapeDataString(apiKey)}";

        _logger.LogDebug("AWEKAS fetch: {Url}", ApiUrl + "?key=***");

        using var response = await client.GetAsync(url, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AWEKAS HTTP {(int)response.StatusCode}: {json.Trim()}");

        _logger.LogInformation("AWEKAS Antwort: {Json}", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Fehler-Check
        if (root.TryGetProperty("error", out var errProp) &&
            errProp.ValueKind != JsonValueKind.Null &&
            !string.IsNullOrWhiteSpace(errProp.GetString()))
            throw new InvalidOperationException($"AWEKAS Fehler: {errProp.GetString()}");

        if (!root.TryGetProperty("current", out var current))
            throw new InvalidOperationException($"AWEKAS: Kein 'current'-Objekt in der Antwort. Antwort: {json.Trim()}");

        return ParseCurrentResponse(current, root);
    }

    /// <summary>Parst das 'current'-Objekt der AWEKAS current.php API.</summary>
    private WeatherData ParseCurrentResponse(JsonElement current, JsonElement root)
    {
        var data = new WeatherData { ProviderName = Name };

        // Zeitstempel
        if (root.TryGetProperty("fetchdate", out var fetchdate) &&
            fetchdate.ValueKind == JsonValueKind.Number)
        {
            data.ObservedUtc = DateTimeOffset
                .FromUnixTimeSeconds(fetchdate.GetInt64()).UtcDateTime;
        }

        TryAdd(current, "temperature",      data.Fields, WeatherFields.TempOutdoor);
        TryAdd(current, "indoortemperature",data.Fields, WeatherFields.TempIndoor);
        TryAdd(current, "humidity",         data.Fields, WeatherFields.HumidityOutdoor);
        TryAdd(current, "indoorhumidity",   data.Fields, WeatherFields.HumidityIndoor);
        TryAdd(current, "airpress_rel",     data.Fields, WeatherFields.PressureRelative);
        TryAdd(current, "windspeed",        data.Fields, WeatherFields.WindSpeed);
        TryAdd(current, "gustspeed",        data.Fields, WeatherFields.WindGust);
        TryAdd(current, "winddirection",    data.Fields, WeatherFields.WindDirection);
        TryAdd(current, "rainrate",         data.Fields, WeatherFields.RainRate);
        TryAdd(current, "precipitation",    data.Fields, WeatherFields.RainDaily);
        TryAdd(current, "uv",               data.Fields, WeatherFields.UvIndex);
        TryAdd(current, "solar",            data.Fields, WeatherFields.SolarRadiation);
        TryAdd(current, "dewpoint",         data.Fields, WeatherFields.DewPoint);

        return data;
    }

    private static void TryAdd(JsonElement el, string prop, Dictionary<string, double> target, string key)
    {
        if (!el.TryGetProperty(prop, out var val)) return;
        if (val.ValueKind == JsonValueKind.Number)
            target[key] = val.GetDouble();
        else if (val.ValueKind == JsonValueKind.String &&
                 double.TryParse(val.GetString(),
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            target[key] = parsed;
    }
}

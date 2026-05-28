using System.Net.Http.Json;
using System.Text.Json;
using MeshcomWebDesk.Services.Weather;

namespace MeshcomWebDesk.Services.Weather;

/// <summary>
/// Fetches current weather data from the AWEKAS network.
/// API endpoint: https://api.awekas.at/getData.php
/// Authentication: username (stationId) + password (apiKey)
/// Documentation: https://www.awekas.at (registered users)
/// </summary>
public class AwekasProvider : IWeatherProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AwekasProvider> _logger;

    // AWEKAS API endpoint for current station data
    private const string BaseUrl = "https://api.awekas.at/getData.php";

    public string Name => "AWEKAS";

    public AwekasProvider(IHttpClientFactory httpClientFactory, ILogger<AwekasProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WeatherData?> FetchAsync(string apiKey, string stationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(stationId))
        {
            _logger.LogWarning("AWEKAS: apiKey or stationId is empty");
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("WeatherApi");
            // AWEKAS uses basic auth or token query params depending on API version.
            // Format: ?id=<stationId>&pw=<apiKey>&lang=de&output=json
            var url = $"{BaseUrl}?id={Uri.EscapeDataString(stationId)}&pw={Uri.EscapeDataString(apiKey)}&lang=de&output=json";

            _logger.LogDebug("AWEKAS fetch: {Url}", url.Replace(apiKey, "***"));

            using var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AWEKAS returned HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseResponse(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWEKAS fetch failed");
            return null;
        }
    }

    private WeatherData? ParseResponse(string json)
    {
        try
        {
            // AWEKAS returns a semicolon-delimited string or JSON depending on the endpoint version.
            // The getData.php endpoint returns a semicolon-separated line:
            // station_id;temp;hum;baro;wind;gust;windir;rain;rain_rate;uvindex;solar;temp_in;hum_in;baro_abs;dew
            // Try JSON first, fall back to semicolon format.
            if (json.TrimStart().StartsWith("{") || json.TrimStart().StartsWith("["))
                return ParseJsonResponse(json);

            return ParseSemicolonResponse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AWEKAS: failed to parse response");
            return null;
        }
    }

    /// <summary>Parses AWEKAS JSON response format.</summary>
    private WeatherData? ParseJsonResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // AWEKAS JSON may be wrapped in array
        var obs = root.ValueKind == JsonValueKind.Array ? root[0] : root;

        var data = new WeatherData { ProviderName = Name };
        TryAddDouble(obs, "temperature",      data.Fields, WeatherFields.TempOutdoor);
        TryAddDouble(obs, "temperature_in",   data.Fields, WeatherFields.TempIndoor);
        TryAddDouble(obs, "humidity",         data.Fields, WeatherFields.HumidityOutdoor);
        TryAddDouble(obs, "humidity_in",      data.Fields, WeatherFields.HumidityIndoor);
        TryAddDouble(obs, "baromrel",         data.Fields, WeatherFields.PressureRelative);
        TryAddDouble(obs, "baromabs",         data.Fields, WeatherFields.PressureAbsolute);
        TryAddDouble(obs, "windspeed",        data.Fields, WeatherFields.WindSpeed);
        TryAddDouble(obs, "windgust",         data.Fields, WeatherFields.WindGust);
        TryAddDouble(obs, "winddir",          data.Fields, WeatherFields.WindDirection);
        TryAddDouble(obs, "rainrate",         data.Fields, WeatherFields.RainRate);
        TryAddDouble(obs, "dailyrain",        data.Fields, WeatherFields.RainDaily);
        TryAddDouble(obs, "uv",               data.Fields, WeatherFields.UvIndex);
        TryAddDouble(obs, "solarradiation",   data.Fields, WeatherFields.SolarRadiation);
        TryAddDouble(obs, "dewpoint",         data.Fields, WeatherFields.DewPoint);

        if (obs.TryGetProperty("observationtime", out var ts) &&
            DateTime.TryParse(ts.GetString(), out var dt))
            data.ObservedUtc = dt.ToUniversalTime();

        return data.Fields.Count > 0 ? data : null;
    }

    /// <summary>
    /// Parses the AWEKAS semicolon-delimited legacy format.
    /// Fields: stationId;temp;humidity;barom_rel;windspeed;windgust;winddir;rain_day;rain_rate;uv;solar;temp_in;humidity_in;barom_abs;dew
    /// </summary>
    private WeatherData? ParseSemicolonResponse(string raw)
    {
        var parts = raw.Trim().Split(';');
        if (parts.Length < 10)
        {
            _logger.LogWarning("AWEKAS semicolon response has too few fields: {Count}", parts.Length);
            return null;
        }

        var data = new WeatherData { ProviderName = Name };

        void Set(int index, string key)
        {
            if (index < parts.Length && double.TryParse(parts[index],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                data.Fields[key] = v;
        }

        Set(1,  WeatherFields.TempOutdoor);
        Set(2,  WeatherFields.HumidityOutdoor);
        Set(3,  WeatherFields.PressureRelative);
        Set(4,  WeatherFields.WindSpeed);
        Set(5,  WeatherFields.WindGust);
        Set(6,  WeatherFields.WindDirection);
        Set(7,  WeatherFields.RainDaily);
        Set(8,  WeatherFields.RainRate);
        Set(9,  WeatherFields.UvIndex);
        Set(10, WeatherFields.SolarRadiation);
        Set(11, WeatherFields.TempIndoor);
        Set(12, WeatherFields.HumidityIndoor);
        Set(13, WeatherFields.PressureAbsolute);
        Set(14, WeatherFields.DewPoint);

        return data.Fields.Count > 0 ? data : null;
    }

    private static void TryAddDouble(JsonElement el, string jsonProp, Dictionary<string, double> target, string key)
    {
        if (!el.TryGetProperty(jsonProp, out var prop)) return;
        double val;
        if (prop.ValueKind == JsonValueKind.Number)
            val = prop.GetDouble();
        else if (prop.ValueKind == JsonValueKind.String &&
                 double.TryParse(prop.GetString(),
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            val = parsed;
        else
            return;
        target[key] = val;
    }
}

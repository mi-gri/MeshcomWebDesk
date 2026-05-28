using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshcomWebDesk.Services.Weather;

namespace MeshcomWebDesk.Services.Weather;

/// <summary>
/// Fetches current weather data from the AWEKAS network.
/// API endpoint: https://api.awekas.at/station.php
/// Authentication: id (AWEKAS User-ID) + pw (AWEKAS password/API key)
/// Response: JSON with flat weather fields
/// </summary>
public class AwekasProvider : IWeatherProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AwekasProvider> _logger;

    // Confirmed working AWEKAS API endpoint
    private const string BaseUrl = "https://api.awekas.at/station.php";

    public string Name => "AWEKAS";

    public AwekasProvider(IHttpClientFactory httpClientFactory, ILogger<AwekasProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WeatherData?> FetchAsync(string apiKey, string stationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(stationId))
            throw new InvalidOperationException(
                "AWEKAS: User-ID (Stations-ID) und Passwort (API Key) müssen eingetragen sein.");

        // station.php: erst Klartext versuchen, dann MD5 – AWEKAS-Doku unterscheidet
        // zwischen Upload-API (MD5) und Lese-API (unklar), daher beide probieren.
        var md5Hash  = ComputeMd5(apiKey);
        var attempts = new[] { ("Klartext", apiKey), ("MD5", md5Hash) };

        Exception? lastEx = null;
        foreach (var (mode, pw) in attempts)
        {
            try
            {
                var result = await TryFetchAsync(stationId, pw, mode, ct);
                if (result != null)
                    return result;
            }
            catch (InvalidOperationException ex)
            {
                lastEx = ex;
                _logger.LogDebug("AWEKAS {Mode} fehlgeschlagen: {Msg}", mode, ex.Message);
            }
        }

        throw lastEx ?? new InvalidOperationException("AWEKAS: Keine Daten empfangen.");
    }

    private async Task<WeatherData?> TryFetchAsync(string stationId, string pw, string mode, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("WeatherApi");
        var url = $"{BaseUrl}?id={Uri.EscapeDataString(stationId)}&pw={Uri.EscapeDataString(pw)}";

        _logger.LogDebug("AWEKAS [{Mode}] fetch: {Url}", mode, url.Replace(pw, "***"));

        using var response = await client.GetAsync(url, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AWEKAS HTTP {(int)response.StatusCode}: {json.Trim()}");

        _logger.LogInformation("AWEKAS [{Mode}] Antwort: {Json}", mode, json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Fehler-Check
        if (root.TryGetProperty("error", out var errProp) &&
            errProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(errProp.GetString()))
            throw new InvalidOperationException($"AWEKAS [{mode}]: {errProp.GetString()}");

        var result = ParseJsonResponse(root);
        if (result == null)
            throw new InvalidOperationException(
                $"AWEKAS [{mode}]: Antwort empfangen, aber keine bekannten Felder. Antwort: {json.Trim()}");

        return result;
    }


    /// <summary>Parses AWEKAS JSON response format.</summary>
    private WeatherData? ParseJsonResponse(JsonElement root)
    {
        // AWEKAS JSON may be wrapped in array
        var obs = root.ValueKind == JsonValueKind.Array ? root[0] : root;

        var data = new WeatherData { ProviderName = Name };

        // Bekannte AWEKAS JSON-Feldnamen (station.php Erfolgsantwort)
        TryAddDouble(obs, "temperature",      data.Fields, WeatherFields.TempOutdoor);
        TryAddDouble(obs, "temperature_in",   data.Fields, WeatherFields.TempIndoor);
        TryAddDouble(obs, "temp",             data.Fields, WeatherFields.TempOutdoor);
        TryAddDouble(obs, "outtemp",          data.Fields, WeatherFields.TempOutdoor);
        TryAddDouble(obs, "outTemp",          data.Fields, WeatherFields.TempOutdoor);
        TryAddDouble(obs, "humidity",         data.Fields, WeatherFields.HumidityOutdoor);
        TryAddDouble(obs, "humidity_in",      data.Fields, WeatherFields.HumidityIndoor);
        TryAddDouble(obs, "outHumidity",      data.Fields, WeatherFields.HumidityOutdoor);
        TryAddDouble(obs, "baromrel",         data.Fields, WeatherFields.PressureRelative);
        TryAddDouble(obs, "baromabs",         data.Fields, WeatherFields.PressureAbsolute);
        TryAddDouble(obs, "barometer",        data.Fields, WeatherFields.PressureRelative);
        TryAddDouble(obs, "pressure",         data.Fields, WeatherFields.PressureRelative);
        TryAddDouble(obs, "windspeed",        data.Fields, WeatherFields.WindSpeed);
        TryAddDouble(obs, "windSpeed",        data.Fields, WeatherFields.WindSpeed);
        TryAddDouble(obs, "wind_speed",       data.Fields, WeatherFields.WindSpeed);
        TryAddDouble(obs, "windgust",         data.Fields, WeatherFields.WindGust);
        TryAddDouble(obs, "windGust",         data.Fields, WeatherFields.WindGust);
        TryAddDouble(obs, "wind_gust",        data.Fields, WeatherFields.WindGust);
        TryAddDouble(obs, "winddir",          data.Fields, WeatherFields.WindDirection);
        TryAddDouble(obs, "windDir",          data.Fields, WeatherFields.WindDirection);
        TryAddDouble(obs, "wind_dir",         data.Fields, WeatherFields.WindDirection);
        TryAddDouble(obs, "rainrate",         data.Fields, WeatherFields.RainRate);
        TryAddDouble(obs, "rainRate",         data.Fields, WeatherFields.RainRate);
        TryAddDouble(obs, "rain_rate",        data.Fields, WeatherFields.RainRate);
        TryAddDouble(obs, "dailyrain",        data.Fields, WeatherFields.RainDaily);
        TryAddDouble(obs, "dayRain",          data.Fields, WeatherFields.RainDaily);
        TryAddDouble(obs, "day_rain",         data.Fields, WeatherFields.RainDaily);
        TryAddDouble(obs, "uv",               data.Fields, WeatherFields.UvIndex);
        TryAddDouble(obs, "UV",               data.Fields, WeatherFields.UvIndex);
        TryAddDouble(obs, "uvindex",          data.Fields, WeatherFields.UvIndex);
        TryAddDouble(obs, "solarradiation",   data.Fields, WeatherFields.SolarRadiation);
        TryAddDouble(obs, "radiation",        data.Fields, WeatherFields.SolarRadiation);
        TryAddDouble(obs, "solar",            data.Fields, WeatherFields.SolarRadiation);
        TryAddDouble(obs, "dewpoint",         data.Fields, WeatherFields.DewPoint);
        TryAddDouble(obs, "dewPoint",         data.Fields, WeatherFields.DewPoint);
        TryAddDouble(obs, "dew",              data.Fields, WeatherFields.DewPoint);

        // Zeitstempel
        foreach (var tsName in new[] { "observationtime", "time", "datetime", "date", "timestamp" })
        {
            if (obs.TryGetProperty(tsName, out var ts) &&
                DateTime.TryParse(ts.GetString(), out var dt))
            {
                data.ObservedUtc = dt.ToUniversalTime();
                break;
            }
        }

        // Wenn keine bekannten Felder gefunden: alle numerischen Felder loggen
        if (data.Fields.Count == 0)
        {
            var allKeys = new List<string>();
            foreach (var prop in obs.EnumerateObject())
                allKeys.Add($"{prop.Name}={prop.Value}");
            _logger.LogWarning(
                "AWEKAS: Keine bekannten Felder in der Antwort. Verfügbare Felder: {Fields}",
                string.Join(", ", allKeys));
            return null;
        }

        return data;
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

    /// <summary>Berechnet den MD5-Hash des Passworts (Kleinbuchstaben-Hex) wie von AWEKAS erwartet.</summary>
    private static string ComputeMd5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

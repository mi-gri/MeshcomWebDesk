using Microsoft.Extensions.Options;
using MeshcomWebDesk.Models;
using MeshcomWebDesk.Services.Weather;

namespace MeshcomWebDesk.Services.Bot;

/// <summary>
/// Bot command --weather: returns the current weather provider status and license state.
/// Example reply: "Weather: AWEKAS | Temp=18.4°C Hum=72% | lizenziert OE1KBC-1"
/// </summary>
public class WeatherStatusCommand : IBotCommand
{
    private readonly WeatherApiPollingService _pollingService;
    private readonly IOptions<MeshcomSettings> _settings;

    public string Name        => "weather";
    public string Description => "Wetter-API Status (Provider, Messwerte, Lizenz)";

    public WeatherStatusCommand(WeatherApiPollingService pollingService, IOptions<MeshcomSettings> settings)
    {
        _pollingService = pollingService;
        _settings       = settings;
    }

    public Task<string> ExecuteAsync(string[] args, string senderCallsign)
    {
        var ws = _settings.Value.WeatherApi;

        if (ws.Provider == WeatherProvider.None)
            return Task.FromResult("Wetter-API deaktiviert.");

        var sb = new System.Text.StringBuilder();

        // Provider name
        var providerName = ws.Provider switch
        {
            WeatherProvider.Awekas       => "AWEKAS",
            WeatherProvider.WUnderground => "WUnderground",
            WeatherProvider.Simulation   => "Simulation",
            _                            => "Unbekannt"
        };
        sb.Append($"Weather: {providerName}");

        // Last data
        if (_pollingService.LastData?.Fields is { Count: > 0 } fields)
        {
            sb.Append(" |");
            if (fields.TryGetValue(WeatherFields.TempOutdoor, out var temp))
                sb.Append($" T={temp:F1}°C");
            if (fields.TryGetValue(WeatherFields.HumidityOutdoor, out var hum))
                sb.Append($" H={hum:F0}%");
            if (fields.TryGetValue(WeatherFields.PressureRelative, out var pres))
                sb.Append($" P={pres:F0}hPa");
            if (fields.TryGetValue(WeatherFields.WindSpeed, out var wind))
                sb.Append($" W={wind:F1}km/h");

            if (_pollingService.LastFetchUtc.HasValue)
                sb.Append($" ({_pollingService.LastFetchUtc.Value:HH:mm}z)");
        }
        else if (_pollingService.LastError is not null)
        {
            sb.Append($" | Fehler: {_pollingService.LastError}");
        }
        else
        {
            sb.Append(" | Keine Daten");
        }

        return Task.FromResult(sb.ToString());
    }
}

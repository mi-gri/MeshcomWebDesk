namespace MeshcomWebDesk.Models;

/// <summary>
/// Configuration for external weather data providers (AWEKAS, Weather Underground).
/// </summary>
public class WeatherApiSettings
{
    /// <summary>Selected weather provider. Default is None (disabled).</summary>
    public WeatherProvider Provider { get; set; } = WeatherProvider.None;

    /// <summary>API key for the selected provider.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Station ID or location identifier for the provider.</summary>
    public string StationId { get; set; } = string.Empty;

    /// <summary>Polling interval in minutes. Minimum 5, default 15.</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// License token (base64.signature format).
    /// Leave empty to run in unlicensed test mode.
    /// </summary>
    public string LicenseKey { get; set; } = string.Empty;
}

/// <summary>Supported weather data providers.</summary>
public enum WeatherProvider
{
    /// <summary>No external weather provider (feature disabled).</summary>
    None = 0,

    /// <summary>AWEKAS weather network (api.awekas.at).</summary>
    Awekas = 1,

    /// <summary>Weather Underground (api.weather.com).</summary>
    WUnderground = 2,

    /// <summary>Simulation mode – returns realistic fake data for testing (no API key required).</summary>
    Simulation = 99
}

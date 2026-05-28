using System.Text.Json;
using Microsoft.Extensions.Options;
using MeshcomWebDesk.Models;
using MeshcomWebDesk.Services.Weather;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Periodically fetches weather data from the configured provider (AWEKAS or Weather Underground),
/// applies field mapping and writes the result as JSON to <see cref="MeshcomSettings.TelemetryFilePath"/>.
/// The existing telemetry pipeline then picks up the file and sends it via MeshCom.
/// </summary>
public sealed class WeatherApiPollingService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<MeshcomSettings> _settings;
    private readonly WeatherLicenseService            _licenseService;
    private readonly AwekasProvider                   _awekasProvider;
    private readonly WUndergroundProvider             _wuProvider;
    private readonly SimulationProvider               _simProvider;
    private readonly ILogger<WeatherApiPollingService> _logger;

    private Timer? _timer;

    /// <summary>Last successfully fetched data (for status display).</summary>
    public WeatherData? LastData { get; private set; }

    /// <summary>UTC timestamp of the last successful fetch.</summary>
    public DateTime? LastFetchUtc { get; private set; }

    /// <summary>Last error message, or null if the last fetch was successful.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether the current license is valid.</summary>
    public bool IsLicensed { get; private set; }

    public WeatherApiPollingService(
        IOptionsMonitor<MeshcomSettings> settings,
        WeatherLicenseService licenseService,
        AwekasProvider awekasProvider,
        WUndergroundProvider wuProvider,
        SimulationProvider simProvider,
        ILogger<WeatherApiPollingService> logger)
    {
        _settings       = settings;
        _licenseService = licenseService;
        _awekasProvider = awekasProvider;
        _wuProvider     = wuProvider;
        _simProvider    = simProvider;
        _logger         = logger;
    }

    // ── IHostedService ───────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("WeatherApiPollingService starting");
        _timer = new Timer(OnTimer, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
            await _timer.DisposeAsync();
    }

    // ── Timer callback ───────────────────────────────────────────────────

    private async void OnTimer(object? state)
    {
        var s = _settings.CurrentValue.WeatherApi;

        if (s.Provider == WeatherProvider.None)
        {
            ScheduleNext(TimeSpan.FromMinutes(5));
            return;
        }

        await PollAsync(s, CancellationToken.None);

        var interval = TimeSpan.FromMinutes(Math.Max(MinInterval.TotalMinutes, s.PollIntervalMinutes));
        ScheduleNext(interval);
    }

    private void ScheduleNext(TimeSpan delay)
        => _timer?.Change(delay, Timeout.InfiniteTimeSpan);

    // ── Core polling logic ───────────────────────────────────────────────

    internal async Task PollAsync(WeatherApiSettings s, CancellationToken ct)
    {
        // Validate license (non-blocking – unlicensed is allowed but flagged)
        IsLicensed = _licenseService.IsLicensed(s.LicenseKey);

        IWeatherProvider provider = s.Provider switch
        {
            WeatherProvider.Awekas       => _awekasProvider,
            WeatherProvider.WUnderground => _wuProvider,
            WeatherProvider.Simulation   => _simProvider,
            _                            => throw new InvalidOperationException($"Unknown provider: {s.Provider}")
        };

        _logger.LogDebug("WeatherApi polling {Provider} (licensed={Licensed})", provider.Name, IsLicensed);

        var data = await provider.FetchAsync(s.ApiKey, s.StationId, ct);

        if (data == null)
        {
            LastError = $"{provider.Name}: No data received";
            _logger.LogWarning("WeatherApi: no data from {Provider}", provider.Name);
            return;
        }

        LastError = null;
        LastData  = data;
        LastFetchUtc = DateTime.UtcNow;

        await WriteJsonFileAsync(s, data, ct);
    }

    /// <summary>
    /// Applies FieldMapping and writes the result as a flat JSON object to TelemetryFilePath.
    /// The keys in the JSON are either the mapped TelemetryMapping-keys or the raw WeatherFields constants.
    /// </summary>
    private async Task WriteJsonFileAsync(WeatherApiSettings s, WeatherData data, CancellationToken ct)
    {
        var mainSettings = _settings.CurrentValue;

        if (string.IsNullOrWhiteSpace(mainSettings.TelemetryFilePath))
        {
            _logger.LogWarning("WeatherApi: TelemetryFilePath is not configured – data not written");
            return;
        }

        // Build output dictionary: mapped key → value
        var output = new Dictionary<string, object>();

        foreach (var (fieldKey, value) in data.Fields)
        {
            // Resolve the output key: either from FieldMapping or use the field key directly
            var outputKey = s.FieldMapping.TryGetValue(fieldKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                ? mapped
                : fieldKey;

            output[outputKey] = value;
        }

        // Add metadata (not picked up by TelemetryMapping unless explicitly mapped)
        output["_provider"]    = data.ProviderName;
        output["_observed"]    = data.ObservedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
        output["_licensed"]    = IsLicensed;

        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });

        var dir = Path.GetDirectoryName(mainSettings.TelemetryFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(mainSettings.TelemetryFilePath, json, System.Text.Encoding.UTF8, ct);

        _logger.LogInformation("WeatherApi: wrote {Count} fields to {Path} (licensed={Licensed})",
            data.Fields.Count, mainSettings.TelemetryFilePath, IsLicensed);
    }
}

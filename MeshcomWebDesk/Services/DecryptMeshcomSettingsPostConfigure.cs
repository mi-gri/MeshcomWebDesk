using Microsoft.Extensions.Options;
using MeshcomWebDesk.Models;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Decrypts sensitive fields in <see cref="MeshcomSettings"/> after they are loaded
/// from <c>appsettings.override.json</c>. Values encrypted by <see cref="SettingsService"/>
/// carry an <c>"aes:"</c> prefix; unprotected plain-text values pass through unchanged
/// for backward compatibility and manual edits.
/// Legacy <c>"dp:"</c> values (old ASP.NET Core Data Protection) return an empty string
/// so the user is prompted to re-enter them once.
/// </summary>
public sealed class DecryptMeshcomSettingsPostConfigure : IPostConfigureOptions<MeshcomSettings>
{
    private readonly ISettingsProtector _protector;

    public DecryptMeshcomSettingsPostConfigure(ISettingsProtector protector)
    {
        _protector = protector;
    }

    public void PostConfigure(string? name, MeshcomSettings options)
    {
        options.Database.MySqlConnectionString = _protector.TryDecrypt(options.Database.MySqlConnectionString);
        options.Database.InfluxToken           = _protector.TryDecrypt(options.Database.InfluxToken);
        options.Qrz.Password                   = _protector.TryDecrypt(options.Qrz.Password);
        options.TelemetryApiKey                = _protector.TryDecrypt(options.TelemetryApiKey);
        options.Mqtt.Password                  = _protector.TryDecrypt(options.Mqtt.Password);
        options.Ai.ApiKey                      = _protector.TryDecrypt(options.Ai.ApiKey);
        options.TelnetPassword                 = _protector.TryDecrypt(options.TelnetPassword);
        options.WeatherApi.ApiKey              = _protector.TryDecrypt(options.WeatherApi.ApiKey);

        // Decrypt per-node TLS passwords
        foreach (var node in options.Nodes)
            node.TelnetPassword = _protector.TryDecrypt(node.TelnetPassword);

        // Migration: MhMaxAgeDays (days) → MhMaxAgeHours (hours).
        // When the old key is present and the new one is still at its default (0), convert.
        if (options.MhMaxAgeDays > 0 && options.MhMaxAgeHours == 0)
            options.MhMaxAgeHours = options.MhMaxAgeDays * 24;
    }
}

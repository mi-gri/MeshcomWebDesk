namespace MeshcomWebDesk.Models;

/// <summary>
/// Represents the payload of a weather feature license token.
/// The token is RSA-signed and validated offline.
/// </summary>
public class WeatherLicense
{
    /// <summary>Licensed callsign including SSID (e.g. "OE1KBC-1").</summary>
    public string Callsign { get; set; } = string.Empty;

    /// <summary>Expiration date (UTC). Token is invalid after this date.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Optional: limit to specific provider(s). Empty = all providers allowed.</summary>
    public string AllowedProviders { get; set; } = string.Empty;

    /// <summary>
    /// Validates the license for the given callsign and checks expiration.
    /// Does NOT verify the signature – use <see cref="WeatherLicenseService"/> for that.
    /// </summary>
    public bool IsValidFor(string callsign) =>
        string.Equals(Callsign?.Trim(), callsign?.Trim(), StringComparison.OrdinalIgnoreCase)
        && ExpiresUtc > DateTime.UtcNow;
}

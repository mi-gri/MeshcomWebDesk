namespace MeshcomWebDesk.Models;

/// <summary>
/// Represents the payload of a program license token.
/// The token is RSA-SHA256-signed and validated offline.
/// Format: base64(json) + "." + base64(signature)
/// </summary>
public class AppLicense
{
    /// <summary>
    /// Licensed callsign WITHOUT SSID (e.g. "DH1FR").
    /// DH1FR-1, DH1FR-7, … are all covered by a single license for "DH1FR".
    /// </summary>
    public string Callsign { get; set; } = string.Empty;

    /// <summary>UTC date when this license was issued.</summary>
    public DateTime IssuedUtc { get; set; }

    /// <summary>
    /// Optional expiry (UTC). Token never expires when this is <see cref="DateTime.MaxValue"/>
    /// or the default <see cref="DateTime.MinValue"/>.
    /// </summary>
    public DateTime ExpiresUtc { get; set; } = DateTime.MaxValue;

    /// <summary>
    /// Optional list of feature flags unlocked by this license.
    /// An empty array means ALL features are allowed (current default).
    /// Future premium features can be gated with a non-empty list.
    /// Example: ["WeatherApi", "InfluxDb"]
    /// </summary>
    public string[] Features { get; set; } = [];

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the license covers the supplied callsign.
    /// Comparison is case-insensitive and strips any SSID from <paramref name="callsign"/>.
    /// </summary>
    public bool IsValidFor(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return false;
        var baseCallsign = StripSsid(callsign);
        return string.Equals(Callsign?.Trim(), baseCallsign, StringComparison.OrdinalIgnoreCase)
               && (ExpiresUtc == DateTime.MinValue || ExpiresUtc == DateTime.MaxValue || ExpiresUtc > DateTime.UtcNow);
    }

    /// <summary>
    /// Returns true when this license grants access to <paramref name="feature"/>.
    /// An empty <see cref="Features"/> array means all features are allowed.
    /// </summary>
    public bool HasFeature(string feature) =>
        Features.Length == 0
        || Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>Strips the SSID part from a callsign (e.g. "DH1FR-7" → "DH1FR").</summary>
    public static string StripSsid(string callsign)
    {
        var idx = callsign.IndexOf('-');
        return idx > 0 ? callsign[..idx].Trim() : callsign.Trim();
    }
}

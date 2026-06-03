using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MeshcomWebDesk.Models;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Validates the program-wide application license token.
/// Tokens are Base64-encoded JSON signed with RSA-SHA256.
/// Format: base64(json) + "." + base64(signature)
///
/// The Callsign in the token is WITHOUT SSID (e.g. "DH1FR").
/// Validation strips the SSID from the configured callsign automatically,
/// so one license covers DH1FR, DH1FR-1, DH1FR-7, etc.
///
/// Feature gating: call <see cref="HasFeature"/> to check if a specific
/// feature is unlocked. An empty Features array means all features are allowed.
/// </summary>
public class AppLicenseService
{
    // RSA Public Key (PEM) – matching private key is in tools/private/weather-license.key (never committed).
    private const string PublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqXUWV2EUUGzPZGCcUnBC
gR69PvBzBA1HQ6LHxjVqF5zIA7oQGps9CM/Uims3ZZFkoJsxZcFj/sINvBFlThUl
DD5JbZiZ1rbx2OJiIkPxlXa4LOqje/X+aRLYi4fBP4UyZebxbYE2F6IL86fW6jg3
3LJ7av25g8sId40UuJ4y/qUz2bP+u4XMBa/tvDRIFB2so6tbsvbVhqYOXburf/PO
L/Qmv8jd0OXDOYme1Ztmec/IZzD/SzkGx/SGivZLtC1NYfV2//pK6mV4Fw7cFbwn
6JLxyLfKA5Ivyn0a5xQgp/95fxW8b4JyMwS0VXT3VeZhUbYOHIRww7+nUyGqrr9C
8wIDAQAB
-----END PUBLIC KEY-----";

    private readonly ILogger<AppLicenseService> _logger;
    private readonly IOptionsMonitor<MeshcomSettings> _settings;

    private AppLicense? _cachedLicense;
    private string?     _cachedToken;

    public AppLicenseService(
        ILogger<AppLicenseService> logger,
        IOptionsMonitor<MeshcomSettings> settings)
    {
        _logger   = logger;
        _settings = settings;

        // Re-validate whenever settings change (token or callsign may have changed).
        _settings.OnChange(_ => InvalidateCache());
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>Returns the validated license, or null when unlicensed / token invalid.</summary>
    public AppLicense? License => GetLicense();

    /// <summary>True when a valid license exists for the configured callsign.</summary>
    public bool IsLicensed => License != null;

    /// <summary>
    /// Licensed callsign (without SSID) or null when unlicensed.
    /// Use this for display in the title bar.
    /// </summary>
    public string? LicensedCallsign => License?.Callsign;

    /// <summary>
    /// Returns true when the current license grants access to <paramref name="feature"/>.
    /// Always returns true when no feature restrictions are set (Features array is empty).
    /// Always returns true when ALL features are free (current policy – no licensed features yet).
    /// </summary>
    public bool HasFeature(string feature)
    {
        var lic = License;
        // Current policy: all features free for everyone.
        // When a feature is added to a license token, only licensed users get it.
        if (lic == null) return true;          // unlicensed → still all features allowed
        return lic.HasFeature(feature);
    }

    // ── Validation ───────────────────────────────────────────────────────

    private AppLicense? GetLicense()
    {
        var token = _settings.CurrentValue.LicenseToken;

        // Return cached result when token has not changed.
        if (token == _cachedToken) return _cachedLicense;

        _cachedToken   = token;
        _cachedLicense = Validate(token);
        return _cachedLicense;
    }

    private void InvalidateCache() => _cachedToken = null;

    private AppLicense? Validate(string? licenseToken)
    {
        if (string.IsNullOrWhiteSpace(licenseToken))
            return null;

        try
        {
            var parts = licenseToken.Trim().Split('.');
            if (parts.Length != 2)
            {
                _logger.LogWarning("App license token has invalid format (expected base64.base64)");
                return null;
            }

            var jsonBytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);

            if (!rsa.VerifyData(jsonBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                _logger.LogWarning("App license token signature verification failed");
                return null;
            }

            var json    = Encoding.UTF8.GetString(jsonBytes);
            var license = JsonSerializer.Deserialize<AppLicense>(json);

            if (license == null)
            {
                _logger.LogWarning("App license token JSON is invalid");
                return null;
            }

            var myCallsign = _settings.CurrentValue.MyCallsign?.Trim();
            if (!license.IsValidFor(myCallsign))
            {
                _logger.LogWarning(
                    "App license token is not valid for callsign {Callsign} (licensed for {Licensed})",
                    myCallsign, license.Callsign);
                return null;
            }

            _logger.LogInformation(
                "App license validated: {Callsign}, issued {Issued:yyyy-MM-dd}, expires {Expiry}",
                license.Callsign,
                license.IssuedUtc,
                license.ExpiresUtc == DateTime.MaxValue ? "never" : license.ExpiresUtc.ToString("yyyy-MM-dd"));

            return license;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate app license token");
            return null;
        }
    }
}

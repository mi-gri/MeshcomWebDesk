using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MeshcomWebDesk.Models;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Validates weather feature license tokens.
/// Tokens are Base64-encoded JSON signed with RSA-SHA256.
/// Format: base64(json) + "." + base64(signature)
/// </summary>
public class WeatherLicenseService
{
    // RSA Public Key (PEM format) for signature verification.
    // The corresponding private key is kept offline in tools/private/ (never committed).
    private const string PublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqXUWV2EUUGzPZGCcUnBC
gR69PvBzBA1HQ6LHxjVqF5zIA7oQGps9CM/Uims3ZZFkoJsxZcFj/sINvBFlThUl
DD5JbZiZ1rbx2OJiIkPxlXa4LOqje/X+aRLYi4fBP4UyZebxbYE2F6IL86fW6jg3
3LJ7av25g8sId40UuJ4y/qUz2bP+u4XMBa/tvDRIFB2so6tbsvbVhqYOXburf/PO
L/Qmv8jd0OXDOYme1Ztmec/IZzD/SzkGx/SGivZLtC1NYfV2//pK6mV4Fw7cFbwn
6JLxyLfKA5Ivyn0a5xQgp/95fxW8b4JyMwS0VXT3VeZhUbYOHIRww7+nUyGqrr9C
8wIDAQAB
-----END PUBLIC KEY-----";

    private readonly ILogger<WeatherLicenseService> _logger;
    private readonly IOptions<MeshcomSettings> _settings;

    public WeatherLicenseService(ILogger<WeatherLicenseService> logger, IOptions<MeshcomSettings> settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Validates the license token for the configured callsign.
    /// Returns the license if valid, otherwise null.
    /// </summary>
    public WeatherLicense? ValidateLicense(string? licenseToken)
    {
        if (string.IsNullOrWhiteSpace(licenseToken))
            return null;

        try
        {
            var parts = licenseToken.Split('.');
            if (parts.Length != 2)
            {
                _logger.LogWarning("Weather license token has invalid format (expected base64.base64)");
                return null;
            }

            var jsonBytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);

            // Verify RSA signature
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);

            var isValid = rsa.VerifyData(jsonBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!isValid)
            {
                _logger.LogWarning("Weather license token signature verification failed");
                return null;
            }

            var json = Encoding.UTF8.GetString(jsonBytes);
            var license = JsonSerializer.Deserialize<WeatherLicense>(json);

            if (license == null)
            {
                _logger.LogWarning("Weather license token JSON is invalid");
                return null;
            }

            var myCallsign = _settings.Value.MyCallsign?.Trim();
            if (!license.IsValidFor(myCallsign))
            {
                _logger.LogWarning("Weather license token is not valid for callsign {Callsign} or expired", myCallsign);
                return null;
            }

            _logger.LogInformation("Weather license validated: {Callsign}, expires {Expiry:yyyy-MM-dd}",
                license.Callsign, license.ExpiresUtc);

            return license;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate weather license token");
            return null;
        }
    }

    /// <summary>
    /// Returns true if the weather API feature is licensed (signature valid, not expired, callsign matches).
    /// </summary>
    public bool IsLicensed(string? licenseToken) => ValidateLicense(licenseToken) != null;
}

using System.Security.Cryptography;
using System.Text;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Platform-independent AES-256-GCM encryption for sensitive settings fields.
/// Uses a 32-byte key stored in <c>settings.key</c> (generated on first run).
/// The key file can be shared between Windows and Linux instances so that
/// settings saved on one platform can be decrypted on any other.
///
/// Encrypted values carry the prefix <c>"aes:"</c>.
/// Legacy values with prefix <c>"dp:"</c> (old ASP.NET Core Data Protection) are
/// returned as-is so existing plain-text fallbacks continue to work.
/// </summary>
public interface ISettingsProtector
{
    string Encrypt(string plaintext);
    string TryDecrypt(string value);
}

public sealed class SettingsProtector : ISettingsProtector
{
    private readonly byte[] _key;
    private readonly ILogger<SettingsProtector> _logger;

    public SettingsProtector(string keyPath, ILogger<SettingsProtector> logger)
    {
        _logger = logger;
        var keyFile = Path.Combine(keyPath, "settings.key");

        if (File.Exists(keyFile))
        {
            _key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            if (_key.Length != 32)
                throw new InvalidOperationException(
                    $"settings.key hat falsche Länge ({_key.Length} bytes, erwartet 32). Bitte löschen und neu starten.");
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(keyPath);
            File.WriteAllText(keyFile, Convert.ToBase64String(_key));
            // Restrict file permissions on Unix
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogInformation("Neuer Settings-Key generiert: {Path}", keyFile);
        }
    }

    /// <summary>Encrypts a non-empty plaintext and returns "aes:&lt;base64&gt;".</summary>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];                         // 16 bytes
        var data       = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Layout: nonce(12) | tag(16) | ciphertext
        var blob = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        ciphertext.CopyTo(blob, nonce.Length + tag.Length);

        return "aes:" + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a value with "aes:" prefix.
    /// Plain-text values (no prefix) are returned unchanged.
    /// Legacy "dp:" values are returned unchanged (fallback to plain-text display).
    /// </summary>
    public string TryDecrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        if (value.StartsWith("dp:", StringComparison.Ordinal))
        {
            // Legacy Data Protection value – cannot decrypt cross-platform.
            // Return empty string so the user re-enters the value once.
            _logger.LogWarning(
                "Veralteter dp:-Wert gefunden. Bitte Einstellung neu speichern (plattformübergreifend nicht entschlüsselbar).");
            return string.Empty;
        }

        if (!value.StartsWith("aes:", StringComparison.Ordinal)) return value;

        try
        {
            var blob       = Convert.FromBase64String(value[4..]);
            var nonce      = blob[..12];
            var tag        = blob[12..28];
            var ciphertext = blob[28..];
            var plaintext  = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AES-Entschlüsselung fehlgeschlagen.");
            return string.Empty;
        }
    }
}

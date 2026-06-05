using System.Text.Json;

namespace MeshcomWebDesk.Helpers;

/// <summary>
/// Validates and splits MeshCom messages according to firmware packet constraints.
///
/// Firmware buffer: incomingExtPacket[255], read(buf, 255) → safe max 254 bytes total.
/// Writing the null terminator at len=255 would overflow index 255 (latent firmware bug).
/// JSON overhead (type/dst fields) is ~35–50 bytes, leaving ~200 bytes for the msg value.
///
/// Emojis with variation selectors (e.g. 🌡️ = U+1F321 + U+FE0F) become two \uXXXX
/// escapes (12 + 6 = 18 bytes) in System.Text.Json output, so character count alone
/// is not sufficient to estimate packet size.
/// </summary>
public static class MessageValidator
{
    /// <summary>Safe maximum total JSON packet size (firmware buffer minus null terminator).</summary>
    public const int MaxPacketBytes = 254;

    /// <summary>
    /// Maximum JSON-encoded byte count for the msg value alone.
    /// 254 bytes total minus ~50 bytes conservative JSON overhead (type/dst fields).
    /// </summary>
    public const int MaxJsonMsgBytes = 200;

    /// <summary>Maximum decoded character count accepted by the firmware.</summary>
    public const int MaxCharLen = 149;

    /// <summary>
    /// Splits <paramref name="text"/> into chunks that each satisfy both firmware constraints:
    /// at most <see cref="MaxCharLen"/> decoded characters AND at most <see cref="MaxJsonMsgBytes"/>
    /// bytes when JSON-encoded. Splits preferentially at the last space or comma; falls back to
    /// a hard split only when no word boundary exists.
    /// </summary>
    public static IReadOnlyList<string> SplitMessage(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= MaxCharLen && JsonEncodedLength(text) <= MaxJsonMsgBytes)
            return [text];

        var parts = new List<string>();
        var span  = text.AsSpan();

        while (!span.IsEmpty)
        {
            // Find the largest prefix satisfying both the char limit and the JSON byte budget
            var limit = Math.Min(span.Length, MaxCharLen);
            while (limit > 1 && JsonEncodedLength(span[..limit].ToString()) > MaxJsonMsgBytes)
                limit--;

            if (span.Length <= limit)
            {
                parts.Add(span.ToString());
                break;
            }

            var slice   = span[..limit];
            var splitAt = slice.LastIndexOf(' ');
            if (splitAt <= 0) splitAt = slice.LastIndexOf(',');
            if (splitAt <= 0) splitAt = limit;   // hard split as last resort

            parts.Add(span[..splitAt].TrimEnd().ToString());
            span = splitAt < limit
                ? span[(splitAt + 1)..].TrimStart(' ')
                : span[splitAt..];
        }

        return parts;
    }

    /// <summary>Returns how many LoRa packets <paramref name="text"/> would be split into.</summary>
    public static int CountParts(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : SplitMessage(text).Count;

    /// <summary>
    /// Returns the byte count of the JSON-escaped string value (without surrounding quotes).
    /// Matches System.Text.Json default behaviour: non-ASCII → \uXXXX (6 bytes),
    /// non-BMP / emoji surrogate pair → \uXXXX\uXXXX (12 bytes).
    /// </summary>
    public static int JsonEncodedLength(string text) =>
        JsonSerializer.Serialize(text).Length - 2;
}

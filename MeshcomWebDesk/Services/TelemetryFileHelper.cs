using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Shared helper for writing telemetry data to the JSON file.
/// Instead of overwriting the entire file, existing keys are preserved and
/// only the supplied keys are added or updated (merge semantics).
/// This allows both the Weather API and the HTTP POST endpoint to write to
/// the same file without erasing each other's data.
/// </summary>
public static class TelemetryFileHelper
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions _writeOptions =
        new() { WriteIndented = true };

    /// <summary>
    /// Merges <paramref name="newFields"/> into the existing JSON file at
    /// <paramref name="filePath"/> and writes the result back.
    /// If the file does not yet exist it is created with only the new fields.
    /// Thread-safe via an async semaphore.
    /// </summary>
    public static async Task MergeAndWriteAsync(
        string filePath,
        IReadOnlyDictionary<string, object> newFields,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Load existing content (if any)
            var merged = new Dictionary<string, JsonNode?>();

            if (File.Exists(filePath))
            {
                try
                {
                    var existing = await File.ReadAllTextAsync(filePath, ct);
                    var doc = JsonNode.Parse(existing);
                    if (doc is JsonObject obj)
                    {
                        foreach (var kv in obj)
                            merged[kv.Key] = kv.Value?.DeepClone();
                    }
                }
                catch
                {
                    // Corrupted file – start fresh
                    merged.Clear();
                }
            }

            // Merge new fields (overwrite existing keys with same name)
            foreach (var (key, value) in newFields)
            {
                merged[key] = value switch
                {
                    bool   b => JsonValue.Create(b),
                    int    i => JsonValue.Create(i),
                    long   l => JsonValue.Create(l),
                    double d => JsonValue.Create(d),
                    float  f => JsonValue.Create(f),
                    string s => JsonValue.Create(s),
                    _        => JsonValue.Create(value.ToString())
                };
            }

            var result = new JsonObject();
            foreach (var kv in merged)
                result[kv.Key] = kv.Value;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = result.ToJsonString(_writeOptions);
            await File.WriteAllTextAsync(filePath, json + Environment.NewLine, Encoding.UTF8, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Merges a raw JSON string into the existing file.
    /// Only top-level keys from <paramref name="jsonBody"/> are merged.
    /// </summary>
    public static async Task MergeAndWriteAsync(
        string filePath,
        string jsonBody,
        CancellationToken ct = default)
    {
        var doc = JsonNode.Parse(jsonBody);
        if (doc is not JsonObject obj)
            throw new ArgumentException("jsonBody must be a JSON object.", nameof(jsonBody));

        var fields = new Dictionary<string, object>();
        foreach (var kv in obj)
        {
            if (kv.Value is not null)
                fields[kv.Key] = kv.Value.ToString()!;
        }

        await MergeAndWriteAsync(filePath, fields, ct);
    }
}

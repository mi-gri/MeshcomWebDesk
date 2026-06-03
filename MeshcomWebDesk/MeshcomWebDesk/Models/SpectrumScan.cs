namespace MeshcomWebDesk.Models;

/// <summary>
/// Ein einzelner Messpunkt aus einem SX1262-Spektrum-Scan.
/// </summary>
public sealed class SpectrumPoint
{
    /// <summary>Mittenfrequenz in MHz (z.B. 868.60)</summary>
    public double FreqMhz { get; init; }

    /// <summary>Die 33 Energie-Bins des SX1262-Spektrum-Scans.</summary>
    public int[] Bins { get; init; } = [];

    /// <summary>Maximaler Bin-Wert (Signalstärke-Indikator).</summary>
    public int Peak => Bins.Length > 0 ? Bins.Max() : 0;
}

/// <summary>
/// Sammlung aller Messpunkte eines vollständigen --spectrum-Laufs.
/// Parst die Rohleitungen aus der Console-Ausgabe.
/// </summary>
public sealed class SpectrumScan
{
    private readonly List<SpectrumPoint> _points = [];
    private double _pendingFreq = 0;

    public IReadOnlyList<SpectrumPoint> Points => _points;

    /// <summary>Gibt true zurück, wenn der Scan abgeschlossen ist (SCAN END empfangen).</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Verarbeitet eine einzelne Konsolenzeile.
    /// Gibt true zurück, wenn die Zeile zum Spektrum-Scan gehört.
    /// </summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // FREQ 868.60
        if (line.StartsWith("FREQ ") && double.TryParse(
                line[5..].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var freq))
        {
            _pendingFreq = freq;
            return true;
        }

        // SCAN END  (abschließende Zeile des gesamten Scans – VOR dem Daten-Branch prüfen!)
        if (line.Trim() == "SCAN END")
        {
            IsComplete = true;
            return true;
        }

        // SCAN 0,0,...,END
        if (line.StartsWith("SCAN ") && line.Contains(" END"))
        {
            var data = line[5..line.LastIndexOf(" END", StringComparison.Ordinal)].Trim();
            // trailing comma tolerant
            var parts = data.TrimEnd(',').Split(',', StringSplitOptions.RemoveEmptyEntries);
            var bins = parts
                .Select(p => int.TryParse(p.Trim(), out var v) ? v : 0)
                .ToArray();

            if (_pendingFreq > 0 && bins.Length > 0)
                _points.Add(new SpectrumPoint { FreqMhz = _pendingFreq, Bins = bins });

            _pendingFreq = 0;
            return true;
        }

        // --spectrum  (Startbefehl – gehört zum Scan-Kontext)
        if (line.Trim() == "--spectrum")
            return true;

        // [SX1262] Starting spectral scan ...
        if (line.Contains("spectral scan"))
            return true;

        return false;
    }

    /// <summary>Setzt den Scan zurück (für neuen Lauf).</summary>
    public void Reset()
    {
        _points.Clear();
        _pendingFreq = 0;
        IsComplete = false;
    }

    /// <summary>Fügt einen bereits geparsten Punkt direkt hinzu (für Snapshot-basiertes Parsen).</summary>
    public void AddPoint(SpectrumPoint pt) => _points.Add(pt);

    /// <summary>Markiert den Scan als abgeschlossen (SCAN END empfangen).</summary>
    public void MarkComplete() => IsComplete = true;
}

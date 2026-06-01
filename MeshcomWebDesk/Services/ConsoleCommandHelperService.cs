using System.Text.RegularExpressions;
using MeshcomWebDesk.Models;
using Microsoft.Extensions.Options;

namespace MeshcomWebDesk.Services;

/// <summary>
/// Verwaltet den Console Command Helper.
/// Horcht auf den aktiven IConsoleService, parst eingehende Zeilen
/// und hält den zuletzt bekannten Wert jedes Befehls vor.
///
/// Status-Update-Strategie:
///   Jeder gesendete Befehl wird als PendingCommand registriert.
///   Nach dem Senden werden NUR die neu eintreffenden Zeilen ausgewertet.
///   Enthält eine neue Zeile eine Fehlerantwort der Firmware → Status wird
///   NICHT geändert und OnCommandFailed wird ausgelöst.
///   Enthält eine neue Zeile eine erfolgreiche Bestätigung → Status wird
///   übernommen und OnChange ausgelöst.
///   Ohne Antwort läuft ein Timeout → PendingCommand wird verworfen.
///
/// Für --info / RefreshAsync gilt weiterhin das reine Snapshot-Parsing,
/// jedoch ebenfalls mit Error-Filter.
/// </summary>
public sealed class ConsoleCommandHelperService : IDisposable
{
    private readonly IOptionsMonitor<MeshcomSettings> _settings;
    private readonly SerialConsoleService _serial;
    private readonly TelnetService _telnet;
    private readonly HmacConsoleService _hmac;
    private readonly ILogger<ConsoleCommandHelperService> _logger;

    /// <summary>Letzte bekannte Werte: Key = Befehlsname (ohne --), Value = aktueller Wert.</summary>
    public Dictionary<string, string> CurrentValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wird ausgelöst wenn sich ein Wert erfolgreich geändert hat.</summary>
    public event Action? OnChange;

    /// <summary>
    /// Wird ausgelöst wenn ein gesendeter Befehl mit einer Fehlermeldung beantwortet wurde.
    /// Parameter: Befehlsname, Fehlertext der Firmware.
    /// </summary>
    public event Action<string, string>? OnCommandFailed;

    /// <summary>Letzter Refresh-Zeitstempel.</summary>
    public DateTime? LastRefresh { get; private set; }

    /// <summary>Gibt an ob der aktive Console-Service verbunden ist.</summary>
    public bool IsConnected => ActiveConsole.IsConnected;

    /// <summary>Liefert den aktiven Console-Service für direkte Anzeige (z.B. Console-Popup).</summary>
    public IConsoleService ActiveConsolePublic => ActiveConsole;

    // ── Pending-Command-Tracking ─────────────────────────────────────────

    private sealed record PendingCommand(
        string Name,            // Befehlsname ohne --
        string? SentValue,      // gesendeter Wert (null bei Action-Befehlen)
        int LineCountAtSend,    // Lines.Count zum Sendezeitpunkt
        CancellationTokenSource Cts);

    private readonly object _pendingLock = new();
    private readonly List<PendingCommand> _pending = [];
    private IDisposable? _settingsChangeToken;

    // Timeout: nach dieser Zeit wird ein PendingCommand ohne Antwort verworfen.
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(4);

    // Firmware-Fehlermuster – Zeile gilt als Fehler wenn eines davon matcht.
    private static readonly Regex ErrorLineRx = new(
        @"wrong\s+command|unknown\s+command|not\s+supported|not\s+between|invalid|failed|no\s+hardware",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ConsoleCommandHelperService(
        IOptionsMonitor<MeshcomSettings> settings,
        SerialConsoleService serial,
        TelnetService telnet,
        HmacConsoleService hmac,
        ILogger<ConsoleCommandHelperService> logger)
    {
        _settings = settings;
        _serial   = serial;
        _telnet   = telnet;
        _hmac     = hmac;
        _logger   = logger;

        AttachToActiveConsole();
        _settingsChangeToken = _settings.OnChange((_, _) =>
        {
            DetachFromAll();
            AttachToActiveConsole();
        });
    }

    // ── Aktiver Console-Service ──────────────────────────────────────────

    private IConsoleService ActiveConsole => _settings.CurrentValue.ConsoleMode switch
    {
        "serial" => _serial,
        "hmac"   => _hmac,
        _        => _telnet,
    };

    private void AttachToActiveConsole()
    {
        ActiveConsole.OnChange += HandleConsoleChange;
    }

    private void DetachFromAll()
    {
        _serial.OnChange -= HandleConsoleChange;
        _telnet.OnChange -= HandleConsoleChange;
        _hmac.OnChange   -= HandleConsoleChange;
    }

    // ── Befehle senden ───────────────────────────────────────────────────

    /// <summary>
    /// Sendet einen Befehl und registriert ihn als PendingCommand.
    /// Status wird erst übernommen wenn die Firmware eine Erfolgsantwort liefert.
    /// </summary>
    public async Task SendCommandAsync(string commandName, string? value = null)
    {
        if (!IsConnected) return;

        var line = string.IsNullOrWhiteSpace(value)
            ? $"--{commandName}"
            : $"--{commandName} {value}";
        _logger.LogDebug("CCH send: {Line}", line);

        int lineCountNow;
        lock (ActiveConsole.Lines)
            lineCountNow = ActiveConsole.Lines.Count;

        var cts = new CancellationTokenSource();
        var pending = new PendingCommand(commandName, value, lineCountNow, cts);
        lock (_pendingLock)
            _pending.Add(pending);

        await ActiveConsole.SendLineAsync(line);

        // Timeout: PendingCommand nach ResponseTimeout verwerfen.
        // Bei OptimisticUpdate und gesendeten Wert: Wert übernehmen wenn kein Fehler kam.
        _ = Task.Delay(ResponseTimeout, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_pendingLock) _pending.Remove(pending);

            var def = ConsoleCommandDefinitions.All
                .FirstOrDefault(d => d.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));

            if (def is { OptimisticUpdate: true } && value != null)
            {
                _logger.LogDebug("CCH optimistic update: {Cmd} = {Val}", commandName, value);
                UpdateValue(commandName, value);
            }
            else
            {
                _logger.LogDebug("CCH timeout – no response for {Cmd}", line);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Sendet eine rohe Zeile (ohne -- Präfix) an den aktiven Console-Service.</summary>
    public Task SendRawAsync(string line)
    {
        if (!IsConnected) return Task.CompletedTask;
        return ActiveConsole.SendLineAsync(line);
    }

    /// <summary>Sendet --info um alle bekannten Werte zu befüllen.
    /// Zusätzlich werden Befehle mit StatusCommand einzeln abgefragt.</summary>
    public async Task RefreshAsync()
    {
        if (!IsConnected) return;
        LastRefresh = DateTime.Now;
        await ActiveConsole.SendLineAsync("--info");

        // Befehle mit eigenem StatusCommand separat abfragen (z.B. --netconsole)
        var statusCmds = ConsoleCommandDefinitions.All
            .Where(d => d.StatusCommand != null && d.ParsePattern != null)
            .Select(d => d.StatusCommand!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in statusCmds)
        {
            await Task.Delay(200); // kurze Pause zwischen den Abfragen
            await ActiveConsole.SendLineAsync($"--{cmd}");
        }
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    private void HandleConsoleChange()
    {
        List<string> snapshot;
        lock (ActiveConsole.Lines)
            snapshot = [.. ActiveConsole.Lines];

        // ── 1. PendingCommands auswerten (nur neue Zeilen seit dem Senden) ──
        List<PendingCommand> pendingCopy;
        lock (_pendingLock)
            pendingCopy = [.. _pending];

        foreach (var pc in pendingCopy)
        {
            // Nur Zeilen, die nach dem Senden eingetroffen sind
            var newLines = snapshot.Skip(pc.LineCountAtSend).ToList();
            if (newLines.Count == 0) continue;

            // Fehlercheck: Enthält irgendeine neue Zeile eine Firmware-Fehlermeldung
            // die den gesendeten Befehlsnamen nennt?
            var errorLine = newLines.FirstOrDefault(l =>
                ErrorLineRx.IsMatch(l) &&
                l.Contains(pc.Name, StringComparison.OrdinalIgnoreCase));

            if (errorLine != null)
            {
                // Fehler – gesendeten Wert NICHT übernehmen.
                // Wert auf "?" setzen damit die UI signalisiert: unbekannt/Fehler.
                pc.Cts.Cancel();
                lock (_pendingLock) _pending.Remove(pc);
                _logger.LogWarning("CCH command failed: {Cmd} → {Error}", $"--{pc.Name}", errorLine);
                CurrentValues[pc.Name] = "?";
                OnChange?.Invoke();
                OnCommandFailed?.Invoke(pc.Name, errorLine.Trim());
                continue;
            }

            // Erfolgs-Bestätigung suchen: eine neue Zeile muss entweder
            //   a) den Befehlsnamen + Wert bestätigen  z.B.  "txpower 20 dBm"
            //   b) per ParsePattern matchen             z.B.  aus --info
            var def = ConsoleCommandDefinitions.All
                .FirstOrDefault(d => d.Name.Equals(pc.Name, StringComparison.OrdinalIgnoreCase));

            bool confirmed = false;

            if (def is not null && !string.IsNullOrEmpty(def.ParsePattern))
            {
                // ParsePattern-Match in neuen Zeilen
                foreach (var l in newLines)
                {
                    if (ErrorLineRx.IsMatch(l)) continue;
                    var m = Regex.Match(l, def.ParsePattern, RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    var val = m.Groups[1].Value.Trim();
                    UpdateValue(def.Name, val);
                    confirmed = true;
                    break;
                }
            }

            if (!confirmed && pc.SentValue != null)
            {
                // Einfache Bestätigung: neue Zeile enthält Befehlsname + gesendeten Wert.
                // Echo-Zeilen (beginnen mit "--") werden ausgeschlossen – sie sind der
                // gesendete Befehl, keine Firmware-Antwort.
                confirmed = newLines.Any(l =>
                    !ErrorLineRx.IsMatch(l) &&
                    !l.TrimStart().StartsWith("--", StringComparison.Ordinal) &&
                    l.Contains(pc.Name, StringComparison.OrdinalIgnoreCase) &&
                    l.Contains(pc.SentValue, StringComparison.OrdinalIgnoreCase));

                if (confirmed)
                    UpdateValue(pc.Name, pc.SentValue);
            }

            if (confirmed)
            {
                pc.Cts.Cancel();
                lock (_pendingLock) _pending.Remove(pc);
                OnChange?.Invoke();
            }
        }

        // ── 2. Globaler --info Snapshot-Parse (kein PendingCommand) ──────
        // Nur Zeilen die nicht als Fehler markiert sind
        var changed = false;
        foreach (var def in ConsoleCommandDefinitions.All)
        {
            if (string.IsNullOrEmpty(def.ParsePattern)) continue;
            foreach (var line in snapshot)
            {
                if (ErrorLineRx.IsMatch(line)) continue;
                var m = Regex.Match(line, def.ParsePattern, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var val = m.Groups[1].Value.Trim();
                if (UpdateValue(def.Name, val)) changed = true;
            }
        }

        // Toggle-Erkennung (--name on/off) nur aus Firmware-Antwortzeilen.
        // Echo-Zeilen beginnen mit "--" und repräsentieren den gesendeten Befehl,
        // nicht die Firmware-Antwort – sie werden daher explizit ausgeschlossen.
        foreach (var line in snapshot)
        {
            if (ErrorLineRx.IsMatch(line)) continue;
            if (line.TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
            var tm = Regex.Match(line, @"--(\w+)\s+(on|off)", RegexOptions.IgnoreCase);
            if (!tm.Success) continue;
            var state = tm.Groups[2].Value.ToLowerInvariant();
            if (UpdateValue(tm.Groups[1].Value, state)) changed = true;
        }

        if (changed)
            OnChange?.Invoke();
    }

    /// <summary>Setzt CurrentValues[name] = value und gibt true zurück wenn sich etwas geändert hat.</summary>
    private bool UpdateValue(string name, string value)
    {
        if (CurrentValues.TryGetValue(name, out var existing) && existing == value)
            return false;
        CurrentValues[name] = value;
        return true;
    }

    public void Dispose()
    {
        lock (_pendingLock)
        {
            foreach (var p in _pending) p.Cts.Cancel();
            _pending.Clear();
        }
        _settingsChangeToken?.Dispose();
        DetachFromAll();
    }
}

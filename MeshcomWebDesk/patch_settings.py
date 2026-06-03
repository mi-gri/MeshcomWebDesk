"""
Fügt den CalendarBeaconSection-Aufruf in Settings.razor ein.
Sucht den BeaconIntervalHours-Block und fügt danach die Komponente ein.
Fügt außerdem _model.CalendarBeacons beim Speichern ein.

Ausführen: python patch_settings.py
"""
import re, sys, pathlib

razor = pathlib.Path(__file__).parent / "Components/Pages/Settings.razor"
text  = razor.read_text(encoding="utf-8")

# ── 1. UI-Einbindung ─────────────────────────────────────────────────────────
# Suche nach dem Baken-Abschnitt (BeaconIntervalHours) und füge
# <CalendarBeaconSection> direkt danach ein.
ANCHOR_UI = "BeaconIntervalHours"
INSERT_UI = """
	@* ── Termin-Baken ──────────────────────────────────────────── *@
	<CalendarBeaconSection Entries="_model.CalendarBeacons" OnChanged="StateHasChanged" />
"""

if "<CalendarBeaconSection" in text:
	print("UI-Einbindung bereits vorhanden – überspringe.")
else:
	# Finde die Zeile, die BeaconIntervalHours enthält, und den nächsten </div> dahinter
	idx = text.find(ANCHOR_UI)
	if idx == -1:
		print(f"FEHLER: Ankerpunkt '{ANCHOR_UI}' nicht gefunden.")
		sys.exit(1)
	# Gehe ans Ende der Zeile
	eol = text.find("\n", idx)
	# Suche nächstes schließendes </div> (Abschnitt-Ende)
	close = text.find("</div>", eol)
	if close == -1:
		print("FEHLER: kein </div> nach Ankerpunkt gefunden.")
		sys.exit(1)
	insert_pos = close + len("</div>")
	text = text[:insert_pos] + "\n" + INSERT_UI + text[insert_pos:]
	print(f"UI-Einbindung eingefügt nach Position {insert_pos}.")

# ── 2. CalendarBeacons beim Speichern übergeben ───────────────────────────────
# SettingsService.SaveMeshcomSettingsAsync bekommt bereits CalendarBeacons –
# wir müssen sicherstellen dass _model.CalendarBeacons im Aufruf vorhanden ist.
# Da SaveMeshcomSettingsAsync das ganze MeshcomSettings-Objekt nimmt, ist das
# automatisch enthalten, sofern _model.CalendarBeacons korrekt befüllt ist.
# Nichts weiter zu tun.
print("Speicher-Logik: CalendarBeacons ist Teil von _model → automatisch gespeichert.")

razor.write_text(text, encoding="utf-8")
print("Settings.razor erfolgreich gepatcht.")

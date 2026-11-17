# GlassBar

Ein Ersatz für die Windows-11-Taskleiste: ein schwebendes, gläsernes Dock im Stil von macOS – mit eigenem
Hintergrund „Still“, nativ gezeichnet ohne Browser.

## Dock
- Angeheftete und laufende Programme, Fenstervorschau, Desktop-Ordner mit Gruppen und Windows-11-Kontextmenü
- Infobereich, Schnelleinstellungen, Uhr, Musik und Wetter
- Blendet sich aus, wenn ein Fenster darüberliegt (fährt nach unten weg); Windows-Taste: erst Dock, dann Startmenü
- Auf Wunsch ein Dock pro Bildschirm (Nebenbildschirme nur mit den Programmen ihres Bildschirms)
- Rechtsklick auf Programme zeigt die Sprungliste wie bei Windows (z. B. Chrome: meistbesucht, Inkognitofenster)
- Im Spiel: Erster Druck auf die Windows-Taste holt das Dock und gibt die Maus frei
- Start-Symbol wählbar (blaue Kachel, Glas, nur Logo oder eigenes Bild)
- Startet mit Windows über die Aufgabenplanung, mit erhöhter Priorität
- Knopf „Desktop anzeigen“ ganz rechts (wie bei Windows)
- Musik und Wetter getrennt: im Dock, im Hintergrund oder beides
- Einstellungsfenster mit Suche; Werte neben Reglern lassen sich direkt eintippen

## Hintergrund „Still“ (nativ)
- Lichtfeld (Direct3D), Uhr mit rollenden Ziffern, Datum und Wetter (Direct2D)
- Pillen und Karten: Musik mit Liedtexten, Wasser, Pomodoro, Wecker/Timer, Aufgaben, Training, Gebetszeiten,
  Klanglandschaften, Wetterkarte, Anordnen-Modus, Tipps
- Regen auf der Scheibe, Strömung und Leuchten unter der Maus – auf allen Bildschirmen
- Schläft, sobald er verdeckt ist

## Original und Danksagung
Der Hintergrund in GlassBar ist ein inoffizieller Nachbau von
**[„Still – Minimal Wallpaper for Your Focus“](https://steamcommunity.com/sharedfiles/filedetails/?id=3829782620)**
von **[MrShadow](https://steamcommunity.com/id/gj_emon)** aus dem Steam Workshop für Wallpaper Engine.
Idee, Gestaltung und Funktionen stammen vom Original – alle Rechte daran liegen bei seinem Autor.
GlassBar steht in keiner Verbindung zu MrShadow oder Wallpaper Engine. Wenn dir der Hintergrund gefällt,
unterstütze bitte das Original im Workshop.

Sollte der Autor mit dem Nachbau nicht einverstanden sein, wird er auf Wunsch entfernt.

## Bauen
Voraussetzungen: Windows 11, .NET 10 SDK.

```
dotnet build GlassBar/GlassBar.csproj -c Release
```

Einstellungen liegen in `%AppData%\GlassBar`.

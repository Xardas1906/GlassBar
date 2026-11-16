# GlassBar

Ein Ersatz für die Windows-11-Taskleiste: ein schwebendes, gläsernes Dock im Stil von macOS – mit eigenem
Hintergrund „Still“, nativ gezeichnet ohne Browser.

## Dock
- Angeheftete und laufende Programme, Fenstervorschau, Desktop-Ordner mit Gruppen und Windows-11-Kontextmenü
- Infobereich, Schnelleinstellungen, Uhr, Musik und Wetter
- Blendet sich aus, wenn ein Fenster darüberliegt (fährt nach unten weg); Windows-Taste: erst Dock, dann Startmenü
- Auf Wunsch ein Dock pro Bildschirm (Nebenbildschirme nur mit den Programmen ihres Bildschirms)
- Einstellungsfenster mit Suche

## Hintergrund „Still“ (nativ)
- Lichtfeld (Direct3D), Uhr mit rollenden Ziffern, Datum und Wetter (Direct2D)
- Pillen und Karten: Musik mit Liedtexten, Wasser, Pomodoro, Wecker/Timer, Aufgaben, Training, Gebetszeiten,
  Klanglandschaften, Wetterkarte, Anordnen-Modus, Tipps
- Regen auf der Scheibe, Strömung und Leuchten unter der Maus – auf allen Bildschirmen
- Schläft, sobald er verdeckt ist
- Alternativ: der Original-Hintergrund im Browser (WebView2)

„Still“ ist ein Hintergrund aus dem Steam Workshop von einem anderen Autor. Der native Teil ist ein Nachbau für den
privaten Gebrauch, `Still-Deutsch` enthält die deutsche Übersetzung des Originals.

## Bauen
Voraussetzungen: Windows 11, .NET 10 SDK.

```
dotnet build GlassBar/GlassBar.csproj -c Release
```

Einstellungen liegen in `%AppData%\GlassBar`.

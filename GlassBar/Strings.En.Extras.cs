namespace GlassBar;

public static partial class Strings
{
    /// <summary>Wächter, Updater, Dock-Erweiterungen (Fortschritt, Tastenkürzel, Mausrad) und helles Design.</summary>
    internal static readonly Dictionary<string, string> EnExtras = new()
    {
        // ---------- Wächter ----------
        ["GlassBar wurde wegen eines Fehlers beendet. Die Windows-Taskleiste ist wieder da."] = "GlassBar was closed because of an error. The Windows taskbar is back.",
        ["Einzelheiten stehen in {0}"] = "Details are in {0}",

        // ---------- Updates ----------
        ["Automatisch nach Updates suchen"] = "Check for updates automatically",
        ["Einmal am Tag. Ein neues Update erscheint im Rechtsklick-Menü des Docks."] = "Once a day. A new update shows up in the dock's right-click menu.",
        ["Update auf Version {0} installieren …"] = "Install update to version {0} …",
        ["Installiere …"] = "Installing …",
        ["Die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht)."] = "The downloaded file is damaged (checksum mismatch).",

        // ---------- Design ----------
        ["Design von Dock, Aufklapp-Fenstern und Menüs"] = "Theme of the dock, pop-up windows and menus",
        ["Hell: helles Glas mit dunkler Schrift, die Glasfarbe wird dafür aufgehellt. Über dunklem Hintergrund die Deckkraft erhöhen, damit die Schrift lesbar bleibt. Das Einstellungsfenster bleibt dunkel."] =
            "Light: light glass with dark text; the glass color is lightened for it. Over a dark wallpaper, raise the opacity so the text stays readable. The settings window stays dark.",
        ["Dunkel"] = "Dark",
        ["Hell"] = "Light",
        ["Wie Windows"] = "Like Windows",

        // ---------- Hintergrund-Effekte ----------
        ["Uhr, Datum, Pillen und Karten bleiben bei allen Varianten. Die Effekte nutzen die Farben der Palette und reagieren auf Musik und Maus."] =
            "Clock, date, pills and cards stay with every option. The effects use the palette colors and react to music and the mouse.",
        ["Polarlicht"] = "Aurora",
        ["Tempo des Effekts"] = "Effect speed",
        ["Stärke des Effekts"] = "Effect strength",
        ["Was ist neu?"] = "What’s new?",
        ["Neu in GlassBar"] = "New in GlassBar",
        ["Neu in GlassBar {0}"] = "New in GlassBar {0}",
        ["Alle Änderungen"] = "All changes",
        ["Stumm"] = "Muted",
        ["Die Windows-Taskleiste ist auf „automatisch ausblenden“ eingestellt."] = "The Windows taskbar is set to “automatically hide”.",
        ["Hast du das selbst so gewollt? Ältere GlassBar-Versionen konnten diese Einstellung nach einem Absturz versehentlich hinterlassen."] = "Did you set this yourself? Older GlassBar versions could accidentally leave this setting behind after a crash.",
        ["Ja: so lassen.  Nein: beim Beenden von GlassBar wieder normal anzeigen."] = "Yes: keep it.  No: show it normally again when GlassBar exits.",
        ["Pillen auch auf Nebenbildschirmen"] = "Pills on secondary screens too",
        ["Musik, Gebetszeiten und Wetter. Wasser, Pomodoro, Workout, Klang und Wecker bleiben auf dem Hauptbildschirm. Nicht bei „Nur Licht“."] = "Music, prayer times and weather. Water, Pomodoro, workout, sound and alarms stay on the main screen. Not with “Light only”.",
        ["Lautstärke {0} %"] = "Volume {0} %",
        ["Protokoll öffnen"] = "Open log",
        ["Es gibt noch kein Protokoll – bisher ist kein Fehler aufgetreten."] = "There is no log yet – no error has occurred so far.",
        ["Einstellungen exportieren …"] = "Export settings …",
        ["Einstellungen importieren …"] = "Import settings …",
        ["GlassBar-Einstellungen (*.glassbar)|*.glassbar|Zip-Datei (*.zip)|*.zip"] = "GlassBar settings (*.glassbar)|*.glassbar|Zip file (*.zip)|*.zip",
        ["Einstellungen gespeichert in {0}"] = "Settings saved to {0}",
        ["Die Einstellungen ließen sich nicht exportieren."] = "The settings could not be exported.",
        ["Alle Einstellungen werden durch die aus der Datei ersetzt. GlassBar startet dafür neu. Fortfahren?"] = "All settings will be replaced by the ones in the file. GlassBar restarts to do this. Continue?",
        ["Die Datei enthält keine GlassBar-Einstellungen."] = "The file does not contain GlassBar settings.",
        ["Die Einstellungen in der Datei sind beschädigt."] = "The settings in the file are damaged.",
        ["Windows-Akzentfarbe nutzen"] = "Use the Windows accent color",
        ["Für die Punkte unter laufenden Programmen und die Fortschrittsbalken."] = "For the dots under running apps and the progress bars.",
        ["Alle drei Werte merkt sich GlassBar getrennt für helles und dunkles Design."] = "GlassBar keeps all three values separately for the light and dark design.",
        ["Effekt auf Nebenbildschirmen"] = "Effect on secondary screens",
        ["Wie Hauptbildschirm"] = "Same as main screen",
        ["Effekte automatisch wechseln"] = "Change effects automatically",
        ["Nach Tageszeit: morgens Nebel, vormittags Lichtfeld, mittags Kaustik, abends Bokeh-Lichter, nachts Polarlicht."] =
            "By time of day: mist in the early morning, light field in the morning, caustics at midday, bokeh lights in the evening, aurora at night.",
        ["Nach Zeit"] = "By interval",
        ["Nach Tageszeit"] = "By time of day",
        ["Wechsel alle"] = "Change every",
        ["Kaustik"] = "Caustics",
        ["Bokeh-Lichter"] = "Bokeh lights",
        ["Nebel"] = "Mist",

        // ---------- Store-Apps ----------
        ["Diese App ist nicht mehr installiert."] = "This app is no longer installed.",

        // ---------- Tastatur und Maus ----------
        ["Tastatur und Maus"] = "Keyboard and mouse",
        ["Win+1 … 9 öffnet das Programm an dieser Stelle im Dock"] = "Win+1 … 9 opens the app at that position in the dock",
        ["Wie ein Klick aufs Symbol. Mit Umschalt öffnet sich ein neues Fenster."] = "Like clicking the icon. With Shift, a new window opens.",
        ["Mausrad im Dock"] = "Mouse wheel in the dock",
        ["Über Musik und Schnelleinstellungen: Lautstärke. Über einem Programm mit mehreren Fenstern: zwischen ihnen wechseln."] =
            "Over music and quick settings: volume. Over an app with several windows: switch between them.",
    };
}

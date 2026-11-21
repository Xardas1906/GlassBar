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

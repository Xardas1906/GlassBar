using System.Text.Json;

namespace GlassBar;

public class Config
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassBar");
    private static string FilePath => Path.Combine(Folder, "config.json");

    // ---------- Inhalte (werden nicht im Einstellungsfenster geändert) ----------
    public List<string> Pinned { get; set; } = new();
    public string[]? PinnedTrayIcons { get; set; }

    /// <summary>Gruppen je Ordner ("&lt;desktop&gt;" oder Ordnerpfad).</summary>
    public Dictionary<string, List<FolderGroup>> FolderGroups { get; set; } = new();

    /// <summary>Ordner, in denen der Abschnitt „Weitere“ zugeklappt ist.</summary>
    public List<string> CollapsedRest { get; set; } = new();

    // ---------- Aussehen ----------
    /// <summary>Tönung des Dock-Glases (#RRGGBB).</summary>
    public string TintColor { get; set; } = "#181818";
    /// <summary>Deckkraft der Tönung im Dock, 0–100 %.</summary>
    public double DockOpacity { get; set; } = 19;
    /// <summary>Deckkraft der Aufklapp-Fenster (Desktop, Vorschau), 0–100 %.</summary>
    public double FlyoutOpacity { get; set; } = 19;
    /// <summary>Deckkraft der Kontextmenüs, 0–100 %.</summary>
    public double MenuOpacity { get; set; } = 50;
    /// <summary>Icon-Größe im Dock in Pixeln.</summary>
    public double IconSize { get; set; } = 30;
    /// <summary>Abstand des Docks zum unteren Bildschirmrand.</summary>
    public double BottomMargin { get; set; } = 8;

    // ---------- Ausblenden ----------
    /// <summary>"Covered" = wenn ein Fenster darüber liegt, "FullScreen" = nur bei Vollbild, "Never" = nie.</summary>
    public string AutoHideMode { get; set; } = "Covered";
    public bool RevealAtEdge { get; set; } = true;
    /// <summary>Sprache der Oberfläche: "Auto" (wie Windows), "de" oder "en".</summary>
    public string Language { get; set; } = "Auto";
    /// <summary>Auch über Vollbild-Programmen (Spiele, Videos) am unteren Rand einblenden.</summary>
    public bool RevealOverFullScreen { get; set; }
    /// <summary>Bildschirme (Gerätename), auf denen das Dock fixiert ist und sich nicht ausblendet.</summary>
    public List<string> FixedScreens { get; set; } = new();
    /// <summary>Glaseffekt (Unschärfe) im Einstellungsfenster; aus = schlichter, leichter für schwache Grafikkarten.</summary>
    public bool SettingsGlass { get; set; } = true;
    /// <summary>Dock ausgeblendet: 1. Druck auf die Windows-Taste zeigt nur das Dock, 2. Druck das Startmenü.</summary>
    public bool WinKeyShowsDockFirst { get; set; } = true;
    /// <summary>Windows-Taste: zuerst Docks zeigen, wenn irgendein Dock ausgeblendet ist (sonst nur, wenn das Hauptdock ausgeblendet ist).</summary>
    public bool WinKeyAllDocks { get; set; } = true;
    /// <summary>Startmenü, Suche usw. über das Dock schieben.</summary>
    public bool LiftStartMenu { get; set; } = true;
    /// <summary>Dock fährt beim Aus- und Einblenden nach unten aus dem Bild bzw. wieder herein.</summary>
    public bool SlideAnimation { get; set; } = true;
    /// <summary>Auf jedem Bildschirm ein Dock (sonst nur auf dem Hauptbildschirm).</summary>
    public bool DockOnAllScreens { get; set; } = true;
    /// <summary>Laufende Programme nur im Dock des Bildschirms, auf dem ihr Fenster offen ist.</summary>
    public bool AppsOnOwnScreen { get; set; } = true;
    /// <summary>Dauer der Animation beim Hereinfahren (ms); Rausfahren ist etwas schneller.</summary>
    public double SlideDurationMs { get; set; } = 280;
    /// <summary>Abstand von Startmenü, Suche, Infocenter zum Dock (px).</summary>
    public double ShellFlyoutGap { get; set; } = 10;
    /// <summary>Abstand von Desktop-Fenster, Vorschau usw. zum Dock (px).</summary>
    public double FlyoutGap { get; set; } = 10;

    // ---------- Elemente im Dock ----------
    public bool ShowStartButton { get; set; } = true;
    public bool ShowSearchButton { get; set; } = true;
    public bool ShowTaskViewButton { get; set; } = true;
    public bool ShowDesktopButton { get; set; } = true;
    /// <summary>Schmaler Knopf ganz rechts: alle Fenster minimieren / Desktop anzeigen (Win+D).</summary>
    public bool ShowPeekButton { get; set; } = true;
    /// <summary>Symbol des Desktop-Stapels: "Folder" (Windows-Ordner), ein Symbolname (Monitor, Folder2, Stack, Home, Star, Heart, Grid) oder "Custom".</summary>
    public string DesktopIcon { get; set; } = "Folder";
    /// <summary>Kachelfarbe für die Symbole des Desktop-Stapels (#RRGGBB, "Accent" oder "Glass").</summary>
    public string DesktopTileColor { get; set; } = "#3B82F6";
    /// <summary>Eigenes Bild für den Desktop-Stapel (Kopie in %AppData%\GlassBar).</summary>
    public string DesktopIconPath { get; set; } = "";
    /// <summary>Windows-Symbol für den Desktop-Stapel ("Datei,Index"), wenn DesktopIcon = "Sys".</summary>
    public string DesktopIconSource { get; set; } = "";
    public bool ShowTray { get; set; } = true;
    public bool ShowQuickSettings { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public bool ShowRunningDots { get; set; } = true;
    /// <summary>Was gerade läuft (Cover, Titel, Tasten) im Dock.</summary>
    public bool ShowMusic { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    /// <summary>Stadt fürs Wetter; leer = aus dem Hintergrund „Still“ (Wallpaper Engine).</summary>
    public string WeatherCity { get; set; } = "";

    // ---------- Hintergrund ----------
    /// <summary>GlassBar zeigt den Hintergrund „Still“ selbst an (statt Wallpaper Engine).</summary>
    public bool ShowWallpaper { get; set; }
    /// <summary>Heutige Aufgaben im Hintergrund (mehrere mit ; trennen).</summary>
    public string WallpaperTasks { get; set; } = "";
    /// <summary>Wo die Musik erscheint: "Dock", "Wallpaper" oder "Both" (leer = wie früher InfoPlacement).</summary>
    public string MusicPlacement { get; set; } = "";
    /// <summary>Wo das Wetter erscheint: "Dock", "Wallpaper" oder "Both" (leer = wie früher InfoPlacement).</summary>
    public string WeatherPlacement { get; set; } = "";
    /// <summary>Alte gemeinsame Einstellung für Musik und Wetter; nur noch zum Übernehmen gelesen.</summary>
    public string InfoPlacement { get; set; } = "Dock";
    /// <summary>Bilder pro Sekunde des Hintergrunds (weniger = weniger Last).</summary>
    public double WallpaperFps { get; set; } = 30;
    /// <summary>Start-Knopf: "Tile" (blaue Kachel), "Glass" (Glas-Kachel), "Logo" (nur das Logo) oder "Custom" (eigenes Bild).</summary>
    public string StartIcon { get; set; } = "Tile";
    /// <summary>Eigenes Bild für den Start-Knopf (Kopie in %AppData%\GlassBar).</summary>
    public string StartIconPath { get; set; } = "";
    /// <summary>Farbe der Kachel: "Blue" (Verlauf wie Windows), "Accent" (Windows-Akzentfarbe) oder #RRGGBB.</summary>
    public string StartTileColor { get; set; } = "Blue";
    /// <summary>Logo: "Windows" (vier Quadrate), "Colorful" (bunt), "Outline" (Umriss), "Rounded" (abgerundet).</summary>
    public string StartLogoStyle { get; set; } = "Windows";
    /// <summary>Farbe des Logos: "White", "Black", "Accent" oder #RRGGBB.</summary>
    public string StartLogoColor { get; set; } = "White";
    /// <summary>Nativer Hintergrund auf Nebenbildschirmen: "Off", "Light" (nur Licht) oder "Clock" (Licht mit Uhr und Datum).</summary>
    public string WallpaperSecondary { get; set; } = "Clock";
    /// <summary>Was hinter Uhr und Pillen liegt: "Light" (Lichtfeld), "Image" (Windows-Hintergrundbild) oder "Solid" (einfarbig).</summary>
    public string WallpaperField { get; set; } = "Light";

    // ---------- Startmenü-Glas ----------
    /// <summary>Glas-Effekt auf das Windows-Startmenü und die Suche anwenden.</summary>
    public bool StartMenuGlass { get; set; }
    /// <summary>Deckkraft des Startmenü-Glases, 0–100 %.</summary>
    public double StartMenuGlassOpacity { get; set; } = 19;

    // ---------- Aufklapp-Fenster ----------
    public bool HoverOpensFolders { get; set; } = true;
    public bool WindowPreviews { get; set; } = true;
    public double HoverDelayMs { get; set; } = 400;

    /// <summary>ARGB-Wert aus Farbe und Deckkraft.</summary>
    public uint Tint(double opacityPercent)
    {
        uint rgb = 0x181818;
        try { rgb = Convert.ToUInt32(TintColor.TrimStart('#'), 16) & 0xFFFFFF; } catch { }
        uint a = (uint)Math.Clamp(Math.Round(opacityPercent / 100 * 255), 0, 255);
        return (a << 24) | rgb;
    }

    /// <summary>Alle Einstellungen auf Standard, Inhalte (angeheftet, Gruppen) bleiben.</summary>
    public void ResetAppearanceAndBehavior()
    {
        var d = new Config();
        foreach (var p in typeof(Config).GetProperties())
            if (p.CanWrite && p.Name is not (nameof(Pinned) or nameof(PinnedTrayIcons) or nameof(FolderGroups) or nameof(CollapsedRest) or nameof(WallpaperTasks) or nameof(WeatherCity)))
                p.SetValue(this, p.GetValue(d));
        Normalize();
    }

    /// <summary>Fehlende neue Werte aus alten Einstellungen übernehmen.</summary>
    private void Normalize()
    {
        if (string.IsNullOrEmpty(MusicPlacement)) MusicPlacement = string.IsNullOrEmpty(InfoPlacement) ? "Dock" : InfoPlacement;
        if (string.IsNullOrEmpty(WeatherPlacement)) WeatherPlacement = string.IsNullOrEmpty(InfoPlacement) ? "Dock" : InfoPlacement;
    }

    public static Config Load()
    {
        // kaputte Datei -> Sicherung (.bak); fehlt beides -> Standardwerte
        var c = SafeFile.Read(FilePath, json => JsonSerializer.Deserialize<Config>(json)) ?? new Config();
        c.Normalize();
        return c;
    }

    public void Save()
    {
        try { SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public class FolderGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<string> Items { get; set; } = new();
}

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
    /// <summary>Wo Musik und Wetter erscheinen: "Dock", "Wallpaper" oder "Both".</summary>
    public string InfoPlacement { get; set; } = "Dock";
    /// <summary>Bilder pro Sekunde des Hintergrunds (weniger = weniger Last).</summary>
    public double WallpaperFps { get; set; } = 30;
    /// <summary>
    /// Wie „Still“ dargestellt wird: "Browser" = das Original im eingebetteten Browser,
    /// "Native" = eigene Darstellung ohne Browser (viel leichter, noch nicht alle Funktionen).
    /// </summary>
    public string StillEngine { get; set; } = "Browser";
    /// <summary>Nativer Hintergrund auf Nebenbildschirmen: "Off", "Light" (nur Licht) oder "Clock" (Licht mit Uhr und Datum).</summary>
    public string WallpaperSecondary { get; set; } = "Clock";

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
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public class FolderGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<string> Items { get; set; } = new();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar;

/// <summary>
/// „Was ist neu“: Nach einem Update zeigt GlassBar beim ersten Start einmal die Neuerungen seit der zuletzt
/// gesehenen Version (Config.LastSeenVersion). Bei einer Neuinstallation nicht. Jederzeit erneut über
/// Einstellungen › System › „Was ist neu?“.
/// </summary>
internal static class WhatsNew
{
    private sealed record Note(string Version, string[] De, string[] En);

    /// <summary>Neueste zuerst. Kurz halten: das, was man sonst nicht entdeckt.</summary>
    private static readonly Note[] Notes =
    {
        new("1.5.2",
            new[]
            {
                "Alles zum Hintergrund an einer Stelle: Palette, Albumcover-Färbung, Körnung, Qualität und die Maus-Einstellungen stehen jetzt unter Einstellungen › Hintergrund.",
                "Natürliche Farben: Wetter, Teich und Sternenhimmel zeigen echte Himmels-, Wasser- und Nachtfarben (umschaltbar auf die Palette).",
                "Nur noch ein Tempo-Regler je Effekt (0–300 %); die Vorschaubilder zeigen immer die aktuellen Farben. Lavalampe scharf und ruhig.",
            },
            new[]
            {
                "Everything about the background in one place: palette, album-cover tint, grain, quality and the mouse settings are now under Settings › Background.",
                "Natural colors: weather, pond and starry sky show real sky, water and night colors (switchable to the palette).",
                "Just one speed slider per effect (0–300 %); previews always show the current colors. Lava lamp sharp and calm.",
            }),
        new("1.5.1",
            new[]
            {
                "Regen neu: dünne Tropfen in mehreren Tiefen, so dicht wie es draußen gerade regnet – und Tropfen auf der Scheibe.",
                "Wetter zum Ausprobieren: Einstellungen › Hintergrund › Wetter › „Wetter zeigen“ (Regen, Schnee, Nebel, Gewitter, Sternennacht).",
                "Natürlicher Nachthimmel mit Milchstraße und Mond, ruhigeres Funkeln, weniger Schnee, Lavalampe ohne Ruckeln.",
            },
            new[]
            {
                "New rain: thin drops in several layers, as heavy as it is raining outside – plus drops on the glass.",
                "Weather to try out: Settings › Background › Weather › “Show weather” (rain, snow, fog, thunderstorm, starry night).",
                "Natural night sky with the Milky Way and moon, calmer twinkling, less snow, lava lamp without stutter.",
            }),
        new("1.5.0",
            new[]
            {
                "Acht neue Hintergründe: Sternenhimmel, Seide, Lavalampe, Teich, Glühwürmchen, Höhenlinien, Musik-Wellen und Wetter.",
                "„Wetter“ zeigt das echte Wetter der Wetter-Pille: Wolken, Regen, Schnee, Nebel, Gewitter, nachts Sterne.",
                "„Musik-Wellen“ tanzen mit Bass, Mitten und Höhen der Musik, die gerade läuft.",
            },
            new[]
            {
                "Eight new backgrounds: starry sky, silk, lava lamp, pond, fireflies, contour lines, music waves and weather.",
                "“Weather” shows the real weather from the weather pill: clouds, rain, snow, fog, thunderstorms, stars at night.",
                "“Music waves” move with the bass, mids and highs of whatever is playing.",
            }),
        new("1.4.5",
            new[]
            {
                "Diese Übersicht erscheint jetzt nach jedem Update (wieder öffnen: Einstellungen › System).",
                "Mittelklick auf die Musik-Pille im Hintergrund schaltet das Musikprogramm stumm – wie schon im Dock.",
                "Die Einstellungssuche findet auch die neuen Hintergründe, Effekt-Regler und Knöpfe.",
            },
            new[]
            {
                "This overview now appears after every update (open again: Settings › System).",
                "Middle-click on the music pill on the wallpaper mutes the music app – like in the dock.",
                "Settings search now also finds the new backgrounds, effect sliders and buttons.",
            }),
        new("1.4.4",
            new[] { "Mittelklick auf die Musik-Pille im Dock: Musikprogramm stumm / wieder laut.", "Zugeklappte Musik-Pille im Hintergrund: der Ring zeigt kurz die Lautstärke." },
            new[] { "Middle-click on the music pill in the dock: mute / unmute the music app.", "Collapsed music pill on the wallpaper: its ring briefly shows the volume." }),
        new("1.4.1",
            new[] { "Mausrad über der Musik regelt die Lautstärke des Musikprogramms (z. B. Spotify) statt der Systemlautstärke.", "Pillen auch auf Nebenbildschirmen (Einstellungen › Mehrere Bildschirme)." },
            new[] { "Mouse wheel over the music changes the music app's volume (e.g. Spotify) instead of the system volume.", "Pills on secondary screens too (Settings › Multiple monitors)." }),
        new("1.4.0",
            new[]
            {
                "Hintergrund: Tempo und Stärke je Effekt, automatischer Wechsel nach Zeit oder Tageszeit, Vorschaubilder.",
                "Deckkraft getrennt für helles und dunkles Design, Windows-Akzentfarbe für Laufpunkte.",
                "Einstellungen exportieren/importieren, Infobereich-Symbole per Ziehen sortieren, schnellerer Start.",
            },
            new[]
            {
                "Wallpaper: speed and strength per effect, automatic change by interval or time of day, preview images.",
                "Separate opacity for light and dark design, Windows accent color for running dots.",
                "Export/import settings, drag to sort notification icons, faster startup.",
            }),
    };

    private static Version Parse(string? v) => Version.TryParse(v, out var r) ? r : new Version(0, 0);

    /// <summary>Beim Start: nach einem Update einmal zeigen. <paramref name="configExisted"/> = gab es schon Einstellungen?</summary>
    public static void ShowIfUpdated(bool configExisted)
    {
        string current = Updater.Current.ToString(3);
        string? last = App.Config.LastSeenVersion;
        if (last == current) return;
        App.Config.LastSeenVersion = current;
        App.Config.Save();
        if (!configExisted) return;                       // Neuinstallation: nichts zeigen
        // Update von einer Version ohne diese Übersicht: nur die Neuerungen der aktuellen Version

        var notes = last == null
            ? Notes.Where(n => n.Version == current).ToList()
            : Notes.Where(n => Parse(n.Version) > Parse(last) && Parse(n.Version) <= Parse(current)).ToList();
        if (notes.Count > 0) Show(notes);
    }

    /// <summary>Neuerungen der letzten Versionen zeigen (Einstellungen › System).</summary>
    public static void ShowRecent() => Show(Notes.Take(3).ToList());

    private static void Show(List<Note> notes)
    {
        var w = new Window
        {
            Title = L.T("Neu in GlassBar"), Width = 480, SizeToContent = SizeToContent.Height, MaxHeight = 640,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = true, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13,
        };
        Native.KeepRounded(w, () => 8);
        var sub = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(new TextBlock
        {
            Text = L.F("Neu in GlassBar {0}", notes[0].Version), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
        });
        foreach (var n in notes)
        {
            if (notes.Count > 1)
                body.Children.Add(new TextBlock { Text = n.Version, Foreground = sub, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var line in L.En ? n.En : n.De)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Foreground = sub, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
                body.Children.Add(row);
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(MakeButton(L.T("Alle Änderungen"), false, () =>
        {
            Native.Launch("https://github.com/Xardas1906/GlassBar/releases");
            w.Close();
        }));
        buttons.Children.Add(MakeButton("OK", true, w.Close));
        body.Children.Add(buttons);

        w.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) w.Close(); };
        w.Show();
        w.Activate();
    }

    private static Border MakeButton(string text, bool primary, Action click)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(primary ? Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(primary ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, Foreground = Brushes.White },
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}

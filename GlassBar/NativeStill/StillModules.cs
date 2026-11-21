using System.Numerics;
using System.Windows.Threading;
using GlassBar.NativeStill.Audio;
using GlassBar.NativeStill.Ui;
using Vortice.Direct2D1;

namespace GlassBar.NativeStill;

/// <summary>Meldet alle Teile der Oberfläche beim Renderer an (Pillen, Karten, Ebenen) und verbindet sie.</summary>
public static class StillModules
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StillRenderer, Parts> Registered = new();

    private sealed class Parts
    {
        public EditOverlay Edit = null!;
        public AlarmsOverlay Alarms = null!;
        public LyricsOverlay Lyrics = null!;
    }

    public static void Register(StillRenderer r, Dispatcher ui)
    {
        var u = r.Ui;
        var music = new MusicPill();
        var workout = new WorkoutPill();
        var pomodoro = new PomodoroPill();
        u.Add(music);
        u.Add(new WaterPill());
        u.Add(pomodoro);
        u.Add(workout);
        u.Add(new PrayerPill());
        u.Add(new SoundPill());

        var lyrics = new LyricsOverlay();
        music.Lyrics = lyrics;   // Einstellung „Liedtext: Karte“ – die Musikkarte zeigt ihn
        var alarms = new AlarmsOverlay();
        var edit = new EditOverlay();
        u.Add(new Glue(u, lyrics));
        u.Add(lyrics);
        u.Add(new TasksOverlay());
        u.Add(alarms);
        u.Add(workout.Stage);
        u.Add(new WeatherCard());
        u.Add(new TipsOverlay());
        u.Add(edit);
        u.Add(new ToastOverlay());

        // Klanglandschaften: „Wetter“ und „Fokus“
        Soundscape.FocusActive = () => pomodoro.FocusRunning;
        Registered.AddOrUpdate(r, new Parts { Edit = edit, Alarms = alarms, Lyrics = lyrics });
    }

    /// <summary>
    /// Pillen für einen Nebenbildschirm: nur die, deren Zustand allen gemeinsam gehört (Musik, Gebetszeiten, Wetter).
    /// Wasser, Pomodoro, Workout, Klang und Wecker führen je Pille einen eigenen Stand und bleiben auf dem
    /// Hauptbildschirm – zwei Exemplare würden sich gegenseitig überschreiben (und Wecker doppelt klingeln).
    /// </summary>
    public static void RegisterSecondary(StillRenderer r)
    {
        var u = r.Ui;
        u.Add(new MusicPill());
        u.Add(new PrayerPill());
        u.Add(new WeatherCard());
    }

    /// <summary>Wetter für die Klanglandschaft „passend zum Wetter“ (WMO-Code).</summary>
    public static void SetWeatherCode(int wmo) => Soundscape.WeatherKind = () => Soundscape.KindFromWmo(wmo);

    /// <summary>Der Liedtext des Hauptbildschirms (für die Timing-Tasten in den Einstellungen).</summary>
    public static LyricsOverlay? Lyrics(StillRenderer? r) => r != null && Registered.TryGetValue(r, out var p) ? p.Lyrics : null;

    /// <summary>Anordnen-Modus öffnen.</summary>
    public static void EditLayout(StillRenderer? r)
    {
        if (r != null && Registered.TryGetValue(r, out var p)) p.Edit.Active = true;
    }

    /// <summary>Kleine Verbindungsebene: gibt Werte zwischen Teilen weiter (ganz hinten, zeichnet nichts).</summary>
    private sealed class Glue(StillUi ui, LyricsOverlay lyrics) : IOverlay
    {
        public int Z => -1000;
        public bool Animating => false;
        public void Update(UiContext c, double dt) => lyrics.BottomRowHeight = ui.BottomRowHeight;
        public void Draw(ID2D1DeviceContext ctx, UiContext c) { }
        public bool OnPointer(UiContext c, PointerEvent e) => false;
        public bool HitTest(UiContext c, Vector2 p) => false;
    }
}

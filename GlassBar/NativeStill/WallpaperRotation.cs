using System.Windows.Threading;

namespace GlassBar.NativeStill;

/// <summary>
/// Automatischer Wechsel der Hintergrundeffekte: nach Zeitabstand reihum oder passend zur Tageszeit.
/// Rein aus der Uhrzeit berechnet (kein gespeicherter Zustand) – nach einem Neustart geht es nahtlos weiter.
/// </summary>
public static class WallpaperRotation
{
    /// <summary>Die animierten Effekte in der Reihenfolge des Wechsels.</summary>
    public static readonly string[] Effects = { "Light", "Aurora", "Caustics", "Bokeh", "Smoke" };

    private static DispatcherTimer? _timer;

    /// <summary>Effekt, der jetzt zu sehen sein soll.</summary>
    public static string Current(Config c, DateTime now)
    {
        // Bild und Einfarbig wechseln nie; ebenso ohne Wechsel
        if (!Effects.Contains(c.WallpaperField)) return c.WallpaperField;
        switch (c.WallpaperRotation)
        {
            case "Interval":
                int minutes = Math.Clamp(c.WallpaperRotationMinutes, 1, 24 * 60);
                // ab dem Zeitpunkt der Auswahl beim gewählten Effekt beginnen, dann reihum weiter
                var since = c.WallpaperRotationSince is { } t && t <= now ? t : now.Date;
                long slot = (long)(now - since).TotalMinutes / minutes;
                int start = Array.IndexOf(Effects, c.WallpaperField);
                return Effects[(int)((start + slot) % Effects.Length)];
            case "TimeOfDay":
                return ByTimeOfDay(now.TimeOfDay);
            default:
                return c.WallpaperField;
        }
    }

    /// <summary>Morgens Nebel, vormittags Lichtfeld, mittags Kaustik, abends Bokeh-Lichter, nachts Polarlicht.</summary>
    public static string ByTimeOfDay(TimeSpan t) => t.TotalHours switch
    {
        >= 5 and < 8 => "Smoke",
        >= 8 and < 12 => "Light",
        >= 12 and < 17 => "Caustics",
        >= 17 and < 21 => "Bokeh",
        _ => "Aurora",
    };

    /// <summary>Regelmäßig prüfen, ob ein anderer Effekt dran ist (billig: nur ein Vergleich).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => StillRenderer.ApplyEffect(App.Config);
        _timer.Start();
    }
}

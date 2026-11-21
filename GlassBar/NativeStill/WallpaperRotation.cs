using System.Windows.Threading;

namespace GlassBar.NativeStill;

/// <summary>
/// Automatischer Wechsel der Hintergrundeffekte: nach Zeitabstand reihum oder passend zur Tageszeit.
/// Rein aus der Uhrzeit berechnet (kein gespeicherter Zustand) – nach einem Neustart geht es nahtlos weiter.
/// </summary>
public static class WallpaperRotation
{
    /// <summary>Die animierten Effekte in der Reihenfolge des Wechsels.</summary>
    public static readonly string[] Effects = Shaders.Effects.Select(e => e.Key).Prepend("Light").ToArray();

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

    /// <summary>
    /// Einmalig beim Start: Früher gab es zusätzlich „Tempo des Hintergrundlichts“ (Still, Standard 400 %), das mit
    /// dem Tempo je Effekt malgenommen wurde. Jetzt gibt es nur noch das Tempo je Effekt. Wer das alte Tempo verstellt
    /// hatte, bekommt es hier in alle Effekte übernommen; danach steht das alte wieder auf Standard.
    /// </summary>
    public static void MigrateMotion(Config c)
    {
        if (c.MotionMigrated) return;
        c.MotionMigrated = true;
        NativeStillStore.Load();
        double motion = NativeStillStore.Values.Num("motion");
        const double Standard = 4.0;
        if (Math.Abs(motion - Standard) > 0.001)
        {
            double factor = Math.Max(0, motion) / Standard;
            foreach (var key in Effects)
                (c.EffectSpeed ??= new())[key] = Math.Round(Math.Clamp(c.SpeedOf(key) * factor, 0, 300));
            NativeStillStore.Set("motion", Standard);
            Log.Info($"Tempo des Hintergrundlichts ({motion * 100:0} %) in das Tempo der Effekte übernommen.");
        }
        c.Save();
    }

    /// <summary>Regelmäßig prüfen, ob ein anderer Effekt dran ist (billig: nur ein Vergleich).</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => StillRenderer.ApplyEffect(App.Config);
        _timer.Start();
    }
}

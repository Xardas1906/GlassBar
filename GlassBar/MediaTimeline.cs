namespace GlassBar;

/// <summary>
/// Rechenregeln für die Zeitleiste der Windows-Mediensteuerung (ohne Windows-Aufrufe, damit sie testbar bleiben):
/// Position relativ zum Liedanfang, brauchbarer Zeitstempel und das kurze Festhalten nach einem Sprung.
/// </summary>
internal static class MediaTimeline
{
    /// <summary>So lange gilt nach einem Sprung die vorweggenommene Position, falls der Player noch Altes meldet.</summary>
    public const double HoldSeconds = 3;
    /// <summary>Weicht eine Meldung höchstens so weit ab, hat der Player den Sprung übernommen.</summary>
    public const double HoldTolerance = 2;
    /// <summary>Läge die hochgerechnete Position so weit hinter dem Liedende, ist der Zeitstempel unbrauchbar.</summary>
    public const double StaleSlack = 5;

    private static readonly DateTime OldestStamp = new(2000, 1, 1);

    public readonly record struct Normalized(TimeSpan Start, double Duration, double Position);

    /// <summary>Dauer und Position in Sekunden ab dem Liedanfang (StartTime muss nicht 0 sein).</summary>
    public static Normalized Normalize(TimeSpan start, TimeSpan end, TimeSpan position)
    {
        double duration = Math.Max(0, (end - start).TotalSeconds);
        double pos = Math.Max(0, (position - start).TotalSeconds);
        if (duration > 0) pos = Math.Min(pos, duration);
        return new Normalized(start, duration, pos);
    }

    /// <summary>
    /// Ab wann die gemeldete Position gilt. Unbrauchbare Stempel (uralt, in der Zukunft, weit hinter dem Liedende)
    /// werden zu „jetzt“; vor dem Fortsetzen der Wiedergabe (<paramref name="playingSince"/>) lief nichts weiter.
    /// </summary>
    public static DateTime SanitizeStamp(DateTime reported, DateTime now, double position, double duration,
                                         bool playing, DateTime playingSince)
    {
        if (reported < OldestStamp || reported > now) return now;
        if (playing && reported < playingSince) reported = playingSince;
        if (playing && duration > 0 && position + (now - reported).TotalSeconds > duration + StaleSlack) return now;
        return reported;
    }

    /// <summary>Position zum Zeitpunkt <paramref name="now"/> (läuft nur beim Abspielen weiter).</summary>
    public static double Advance(double position, DateTime stamp, DateTime now, bool playing, double duration)
    {
        double p = position + (playing ? Math.Max(0, (now - stamp).TotalSeconds) : 0);
        return duration > 0 ? Math.Min(p, duration) : p;
    }

    /// <summary>
    /// Nach einem Sprung: true = die Meldung ist noch die alte, es gilt weiter <paramref name="expected"/>.
    /// false = der Player hat übernommen oder die Frist ist um; dann zählt seine Meldung.
    /// </summary>
    public static bool HoldSeek(double target, DateTime seekAt, DateTime until, double reported,
                                DateTime now, bool playing, double duration, out double expected)
    {
        expected = Advance(target, seekAt, now, playing, duration);
        return now < until && Math.Abs(reported - expected) > HoldTolerance;
    }

    /// <summary>Sprungziel für die Mediensteuerung: absolute Zeitleiste in 100-ns-Schritten.</summary>
    public static long SeekTicks(TimeSpan start, double seconds) => start.Ticks + TimeSpan.FromSeconds(seconds).Ticks;

    /// <summary>Ziel in Sekunden für einen Anteil des Balkens; nicht ganz ans Ende, sonst springt sofort das nächste Lied an.</summary>
    public static double SeekTarget(double fraction, double duration)
    {
        if (duration <= 0) return 0;
        double t = Math.Clamp(fraction, 0, 1) * duration;
        return duration > 2 ? Math.Min(t, duration - 1) : t;
    }
}

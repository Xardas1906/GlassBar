namespace GlassBar.Tests;

public class MediaTimelineTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 12, 0, 0);
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Position_und_Dauer_zählen_ab_StartTime()
    {
        var n = MediaTimeline.Normalize(S(10), S(190), S(40));
        Assert.Equal(180, n.Duration);
        Assert.Equal(30, n.Position);
        Assert.Equal(S(10), n.Start);
    }

    [Theory]
    [InlineData(10, 100, 5, 90, 0)]     // vor dem Anfang
    [InlineData(0, 100, 130, 100, 100)] // hinter dem Ende
    [InlineData(0, 0, 12, 0, 12)]       // Dauer unbekannt
    [InlineData(50, 20, 60, 0, 10)]     // Ende vor Anfang
    public void Normalize_begrenzt(double start, double end, double pos, double duration, double expected)
    {
        var n = MediaTimeline.Normalize(S(start), S(end), S(pos));
        Assert.Equal(duration, n.Duration);
        Assert.Equal(expected, n.Position);
    }

    [Fact]
    public void Brauchbarer_Stempel_bleibt()
    {
        var at = Now.AddSeconds(-4);
        Assert.Equal(at, MediaTimeline.SanitizeStamp(at, Now, 10, 100, true, DateTime.MinValue));
    }

    [Fact]
    public void Uralter_oder_künftiger_Stempel_wird_jetzt()
    {
        Assert.Equal(Now, MediaTimeline.SanitizeStamp(default, Now, 10, 100, true, DateTime.MinValue));
        Assert.Equal(Now, MediaTimeline.SanitizeStamp(new DateTime(1999, 12, 31), Now, 10, 100, false, DateTime.MinValue));
        Assert.Equal(Now, MediaTimeline.SanitizeStamp(Now.AddSeconds(1), Now, 10, 100, true, DateTime.MinValue));
    }

    [Fact]
    public void Stempel_weit_hinter_dem_Liedende_wird_jetzt()
    {
        Assert.Equal(Now, MediaTimeline.SanitizeStamp(Now.AddSeconds(-60), Now, 50, 100, true, DateTime.MinValue));
        // Pausiert läuft nichts weiter, der Stempel stört dann nicht.
        var at = Now.AddSeconds(-60);
        Assert.Equal(at, MediaTimeline.SanitizeStamp(at, Now, 50, 100, false, DateTime.MinValue));
    }

    [Fact]
    public void Nach_dem_Fortsetzen_zählt_die_Pause_nicht_mit()
    {
        var resumed = Now.AddSeconds(-2);
        var stamp = MediaTimeline.SanitizeStamp(Now.AddSeconds(-30), Now, 10, 100, true, resumed);
        Assert.Equal(resumed, stamp);
        Assert.Equal(12, MediaTimeline.Advance(10, stamp, Now, true, 100), 3);
    }

    [Fact]
    public void Advance_läuft_nur_beim_Abspielen_und_endet_am_Liedende()
    {
        var at = Now.AddSeconds(-5);
        Assert.Equal(15, MediaTimeline.Advance(10, at, Now, true, 100), 3);
        Assert.Equal(10, MediaTimeline.Advance(10, at, Now, false, 100), 3);
        Assert.Equal(12, MediaTimeline.Advance(10, at, Now, true, 12), 3);
        Assert.Equal(10, MediaTimeline.Advance(10, Now.AddSeconds(5), Now, true, 100), 3);
    }

    [Fact]
    public void Alte_Meldung_wird_während_der_Frist_ignoriert()
    {
        var at = Now.AddSeconds(-1);
        Assert.True(MediaTimeline.HoldSeek(60, at, at.AddSeconds(3), 10, Now, true, 100, out double expected));
        Assert.Equal(61, expected, 3);
    }

    [Fact]
    public void Passende_Meldung_beendet_das_Festhalten()
    {
        var at = Now.AddSeconds(-1);
        Assert.False(MediaTimeline.HoldSeek(60, at, at.AddSeconds(3), 62.5, Now, true, 100, out _));
    }

    [Fact]
    public void Nach_der_Frist_gilt_wieder_der_Player()
    {
        var at = Now.AddSeconds(-3);
        Assert.False(MediaTimeline.HoldSeek(60, at, at.AddSeconds(3), 10, Now, true, 100, out _));
    }

    [Fact]
    public void Pausiert_bleibt_das_Ziel_stehen()
    {
        var at = Now.AddSeconds(-2);
        Assert.False(MediaTimeline.HoldSeek(60, at, at.AddSeconds(3), 60.5, Now, false, 100, out double expected));
        Assert.Equal(60, expected, 3);
    }

    [Fact]
    public void Sprungziel_ist_absolut_in_100ns()
    {
        Assert.Equal(TimeSpan.FromSeconds(42.5).Ticks, MediaTimeline.SeekTicks(S(10), 32.5));
        Assert.Equal(425_000_000L, MediaTimeline.SeekTicks(TimeSpan.Zero, 42.5));
    }

    [Theory]
    [InlineData(0.5, 100, 50)]
    [InlineData(1, 100, 99)]    // nicht ganz ans Ende
    [InlineData(2, 100, 99)]
    [InlineData(-1, 100, 0)]
    [InlineData(1, 2, 2)]       // sehr kurz: keine Kappung
    [InlineData(0.5, 0, 0)]
    public void Sprungziel_aus_dem_Balken(double fraction, double duration, double expected)
    {
        Assert.Equal(expected, MediaTimeline.SeekTarget(fraction, duration), 6);
    }
}

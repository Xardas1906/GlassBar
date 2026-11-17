using GlassBar.NativeStill.Ui;

namespace GlassBar.Tests;

public class PrayTimesTests
{
    private static DateTime Utc(double unixMs) => DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs).UtcDateTime;

    private static PrayTimes.Day Berlin(string asr = "standard") =>
        // Tagundnachtgleiche in Berlin (52,52° N, 13,40° O)
        PrayTimes.Times(2026, 3, 20, 52.52, 13.40, new PrayTimes.Options { Method = "mwl", Asr = asr });

    [Fact]
    public void ZeitenKommenInDerRichtigenReihenfolge()
    {
        var d = Berlin();
        Assert.True(d.Fajr < d.Sunrise);
        Assert.True(d.Sunrise < d.Dhuhr);
        Assert.True(d.Dhuhr < d.Asr);
        Assert.True(d.Asr < d.Maghrib);
        Assert.True(d.Maghrib < d.Isha);
    }

    [Fact]
    public void MittagLiegtBeimSonnenhöchststand()
    {
        // 13,4° östlich: wahrer Mittag ≈ 12:00 − 54 min − 7 min Zeitgleichung ≈ 11:00 UTC (Dhuhr ein paar Minuten danach)
        var noon = Utc(Berlin().Dhuhr);
        Assert.InRange(noon.TimeOfDay, new TimeSpan(10, 55, 0), new TimeSpan(11, 15, 0));
    }

    [Fact]
    public void TagUndNachtSindZurTagundnachtgleicheEtwaGleichLang()
    {
        var d = Berlin();
        double hours = (d.Sunset - d.Sunrise) / 3_600_000;
        Assert.InRange(hours, 11.8, 12.4);
    }

    [Fact]
    public void HanafiAsrIstSpäter()
    {
        Assert.True(Berlin("hanafi").Asr > Berlin("standard").Asr);
    }

    [Fact]
    public void UnbekanntesLand_NimmtMwlUndStandard()
    {
        Assert.Equal(("mwl", "standard"), PrayTimes.Defaults("XX"));
        Assert.Equal(("mwl", "standard"), PrayTimes.Defaults(null));
    }
}

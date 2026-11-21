using GlassBar;
using GlassBar.NativeStill;
using Xunit;

public class WallpaperRotationTests
{
    [Fact]
    public void Off_ShowsChosenEffect()
    {
        var c = new Config { WallpaperField = "Bokeh", WallpaperRotation = "Off" };
        Assert.Equal("Bokeh", WallpaperRotation.Current(c, DateTime.Now));
    }

    [Fact]
    public void Interval_StartsAtChosenEffectAndCycles()
    {
        var since = new DateTime(2026, 1, 1, 10, 0, 0);
        var c = new Config { WallpaperField = "Aurora", WallpaperRotation = "Interval", WallpaperRotationMinutes = 30, WallpaperRotationSince = since };
        Assert.Equal("Aurora", WallpaperRotation.Current(c, since.AddMinutes(29)));
        Assert.Equal("Caustics", WallpaperRotation.Current(c, since.AddMinutes(31)));
        // nach vier weiteren Wechseln: vier Plätze weiter in der Liste (am Ende geht es vorne weiter)
        var effects = WallpaperRotation.Effects;
        Assert.Equal(effects[(Array.IndexOf(effects, "Aurora") + 4) % effects.Length], WallpaperRotation.Current(c, since.AddMinutes(30 * 4 + 1)));
        Assert.Equal(effects[(Array.IndexOf(effects, "Aurora") + effects.Length) % effects.Length], WallpaperRotation.Current(c, since.AddMinutes(30 * effects.Length + 1)));
    }

    [Theory]
    [InlineData(6, "Smoke")]
    [InlineData(9, "Light")]
    [InlineData(13, "Caustics")]
    [InlineData(19, "Bokeh")]
    [InlineData(23, "Aurora")]
    [InlineData(2, "Aurora")]
    public void TimeOfDay(int hour, string expected) =>
        Assert.Equal(expected, WallpaperRotation.ByTimeOfDay(TimeSpan.FromHours(hour)));

    [Fact]
    public void ImageAndSolid_NeverRotate()
    {
        var c = new Config { WallpaperField = "Image", WallpaperRotation = "TimeOfDay" };
        Assert.Equal("Image", WallpaperRotation.Current(c, DateTime.Now));
    }

    [Fact]
    public void Percent_DefaultsAndClamps()
    {
        Assert.Equal(100, Config.Percent(null, "Light"));
        Assert.Equal(200, Config.Percent(new() { ["Light"] = 999 }, "Light"));
        Assert.Equal(50, Config.Percent(new() { ["Light"] = 50 }, "Light"));
    }
}

public class EffectWeatherTests
{
    [Theory]
    [InlineData(0, 0f)]   // klar
    [InlineData(3, 1f)]   // bedeckt
    [InlineData(45, 2f)]  // Nebel
    [InlineData(63, 3f)]  // Regen
    [InlineData(73, 4f)]  // Schnee
    [InlineData(95, 5f)]  // Gewitter
    public void FromWmo_Art(int code, float kind) => Assert.Equal(kind, GlassBar.NativeStill.EffectWeather.FromWmo(code).Kind);

    [Fact]
    public void FromWmo_StarkerRegenStaerkerAlsNiesel() =>
        Assert.True(GlassBar.NativeStill.EffectWeather.FromWmo(65).Amount > GlassBar.NativeStill.EffectWeather.FromWmo(51).Amount);
}

public class PrecipitationTests
{
    [Fact]
    public void MehrRegenMehrTropfen()
    {
        float niesel = GlassBar.NativeStill.EffectWeather.FromPrecipitation(0.1, false);
        float regen = GlassBar.NativeStill.EffectWeather.FromPrecipitation(1.5, false);
        float stark = GlassBar.NativeStill.EffectWeather.FromPrecipitation(8, false);
        Assert.True(niesel < regen && regen < stark);
        Assert.Equal(1f, stark, 3);
        Assert.True(GlassBar.NativeStill.EffectWeather.FromPrecipitation(0, false) >= 0.12f); // regnet es laut Code, bleibt etwas sichtbar
    }
}

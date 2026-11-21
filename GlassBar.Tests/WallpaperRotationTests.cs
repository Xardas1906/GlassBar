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
        Assert.Equal("Light", WallpaperRotation.Current(c, since.AddMinutes(30 * 4 + 1))); // Smoke -> Light
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

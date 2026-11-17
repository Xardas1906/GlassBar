namespace GlassBar.Tests;

[Collection("Datenordner")]
public class ConfigTests
{
    private static string ConfigFile => Path.Combine(Config.Folder, "config.json");

    [Fact]
    public void DatenordnerIstUmgeleitet()
    {
        Assert.Equal(TestData.Folder, Config.Folder);
    }

    [Fact]
    public void AlteEinstellung_InfoPlacement_WirdÜbernommen()
    {
        File.WriteAllText(ConfigFile, """{ "InfoPlacement": "Wallpaper", "MusicPlacement": "", "WeatherPlacement": "Both" }""");
        var c = Config.Load();
        Assert.Equal("Wallpaper", c.MusicPlacement);
        Assert.Equal("Both", c.WeatherPlacement);
    }

    [Fact]
    public void SpeichernUndLaden_BehältWerte()
    {
        var c = new Config { IconSize = 36, Theme = "Light", WinNumberKeys = false, Pinned = { @"C:\x.lnk" } };
        c.Save();
        var back = Config.Load();
        Assert.Equal(36, back.IconSize);
        Assert.Equal("Light", back.Theme);
        Assert.False(back.WinNumberKeys);
        Assert.Equal(new[] { @"C:\x.lnk" }, back.Pinned);
    }

    [Fact]
    public void KaputteConfig_LädtSicherung()
    {
        new Config { IconSize = 33 }.Save();
        new Config { IconSize = 44 }.Save();     // 33 liegt jetzt in config.json.bak
        File.WriteAllText(ConfigFile, "{ \"IconSize\": ");
        Assert.Equal(33, Config.Load().IconSize);
    }

    [Theory]
    [InlineData("#102030", 50, 0x80102030u)]
    [InlineData("#181818", 0, 0x00181818u)]
    [InlineData("#181818", 100, 0xFF181818u)]
    [InlineData("kaputt", 100, 0xFF181818u)] // ungültige Farbe -> Standard-Dunkelgrau
    public void Tint_SetztAlphaAusDeckkraft(string color, double opacity, uint expected)
    {
        Assert.Equal(expected, new Config { TintColor = color }.Tint(opacity));
    }

    [Fact]
    public void Zurücksetzen_BehältInhalte()
    {
        var c = new Config { IconSize = 40, Theme = "Light", Pinned = { @"C:\a.lnk" }, WeatherCity = "Berlin" };
        c.ResetAppearanceAndBehavior();
        Assert.Equal(new Config().IconSize, c.IconSize);
        Assert.Equal("Dark", c.Theme);
        Assert.Equal(new[] { @"C:\a.lnk" }, c.Pinned);
        Assert.Equal("Berlin", c.WeatherCity);
    }
}

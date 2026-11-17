namespace GlassBar.Tests;

public class UpdaterTests
{
    private const string Release = """
        {
          "tag_name": "v1.3.0",
          "name": "GlassBar 1.3.0",
          "body": "Neu: helles Design",
          "assets": [
            { "name": "GlassBar-Setup-1.3.0.exe", "size": 7000000,
              "browser_download_url": "https://example.invalid/setup.exe",
              "digest": "sha256:7AD5A4BF904AABF6312B30DD5E5397290C8AE72550BE67768CA1226C9BDA3260" },
            { "name": "GlassBar-v1.3.0-win-x64.zip", "size": 9000000,
              "browser_download_url": "https://example.invalid/app.zip",
              "digest": "sha256:f014e3a5f0337d8ee5fec87ba86755aaa58a4bfbffe5df985b5399f146d36d3e" },
            { "name": "notes.md", "size": 10, "browser_download_url": "https://example.invalid/notes.md" }
          ]
        }
        """;

    [Fact]
    public void LiestVersionZipSetupUndPrüfsummen()
    {
        var r = Updater.ParseRelease(Release)!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal("GlassBar 1.3.0", r.Name);
        Assert.Equal("Neu: helles Design", r.Notes);
        Assert.Equal("https://example.invalid/app.zip", r.Zip!.Url);
        Assert.Equal("f014e3a5f0337d8ee5fec87ba86755aaa58a4bfbffe5df985b5399f146d36d3e", r.Zip.Sha256);
        Assert.Equal("GlassBar-Setup-1.3.0.exe", r.Setup!.Name);
        // Prüfsumme immer klein geschrieben, ohne „sha256:“
        Assert.Equal("7ad5a4bf904aabf6312b30dd5e5397290c8ae72550be67768ca1226c9bda3260", r.Setup.Sha256);
    }

    [Fact]
    public void OhnePrüfsumme_IstShaNull()
    {
        var r = Updater.ParseRelease("""
            { "tag_name": "1.4", "assets": [ { "name": "GlassBar-v1.4-win-x64.zip", "size": 1, "browser_download_url": "u" } ] }
            """)!;
        Assert.Equal(new Version(1, 4, 0), r.Version); // fehlende dritte Stelle = 0
        Assert.Null(r.Zip!.Sha256);
        Assert.Null(r.Setup);
    }

    [Theory]
    [InlineData("""{ "tag_name": "nightly", "assets": [ { "name": "GlassBar-v1-win-x64.zip", "size": 1, "browser_download_url": "u" } ] }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "assets": [ { "name": "readme.txt", "size": 1, "browser_download_url": "u" } ] }""")]
    public void UnbrauchbaresRelease_GibtNull(string json)
    {
        Assert.Null(Updater.ParseRelease(json));
    }

    [Fact]
    public void LaufendeVersionHatDreiStellen()
    {
        Assert.Equal(-1, Updater.Current.Revision);
        Assert.True(Updater.Current >= new Version(1, 2, 0));
    }
}

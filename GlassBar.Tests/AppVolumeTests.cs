using GlassBar;
using Xunit;

public class AppVolumeTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify.exe", null, null, true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify.exe", null, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", true)]
    [InlineData("MSEdge", "msedge.exe", null, null, true)]
    [InlineData("Chrome", "chrome.exe", null, null, true)]
    [InlineData("308046B0AF4A39CB", "firefox.exe", null, null, true)]
    [InlineData("Chrome", "msedge.exe", null, null, false)]
    [InlineData("Spotify.exe", "Discord.exe", null, null, false)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.Media.Player.exe", null, "Microsoft.ZuneMusic_8wekyb3d8bbwe", true)]
    public void Matches(string source, string process, string? aumid, string? family, bool expected) =>
        Assert.Equal(expected, AppVolume.Matches(source, process, aumid, family));
}

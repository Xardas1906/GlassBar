namespace GlassBar.Tests;

public class PackagedAppsTests
{
    private const string SpotifyAumid = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
    private const string SpotifyFamily = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";
    private const string SpotifyExe = @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.308.620.0_x64__zpdnekdrzrea0\Spotify.exe";
    private const string SpotifyPin = @"shell:AppsFolder\" + SpotifyAumid;

    private static string? SpotifyOnly(string family, string? exe) => family == SpotifyFamily ? SpotifyAumid : null;

    [Fact]
    public void ShellPfad_HinUndZurück()
    {
        Assert.Equal(SpotifyPin, PackagedApps.PathFromAumid(SpotifyAumid));
        Assert.Equal(SpotifyAumid, PackagedApps.AumidFromPath(SpotifyPin));
        Assert.Equal(SpotifyAumid, PackagedApps.AumidFromPath(@"SHELL:appsfolder\" + SpotifyAumid));
        Assert.True(PackagedApps.IsShellPath(SpotifyPin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"shell:AppsFolder\")]
    [InlineData(@"C:\Programme\App\app.exe")]
    public void KeinShellPfad(string? path)
    {
        Assert.False(PackagedApps.IsShellPath(path));
        Assert.Null(PackagedApps.AumidFromPath(path));
    }

    [Theory]
    [InlineData(SpotifyAumid, SpotifyFamily)]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm")]
    [InlineData("Chrome._crx_abc", null)]         // Web-App: kein Paket
    [InlineData("com.squirrel.slack.slack", null)]
    [InlineData("OhneHerausgeber!App", null)]
    [InlineData("Name_pub!", null)]
    [InlineData("!App", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FamilieAusAumid(string? aumid, string? family)
    {
        Assert.Equal(family, PackagedApps.FamilyFromAumid(aumid));
        Assert.Equal(family != null, PackagedApps.IsPackagedAumid(aumid));
    }

    [Fact]
    public void AppIdAusAumid() => Assert.Equal("Spotify", PackagedApps.AppIdFromAumid(SpotifyAumid));

    [Theory]
    [InlineData(SpotifyExe, SpotifyFamily)]
    [InlineData(@"c:\program files\windowsapps\SpotifyAB.SpotifyMusic_1.308.620.0_x64__zpdnekdrzrea0\sub\Helper.exe", SpotifyFamily)]
    [InlineData(@"D:\WindowsApps\5319275A.WhatsAppDesktop_2.2600.1.0_x64__cv1g1gvanyjgm\WhatsApp.Root.exe", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm")]
    public void FamilieAusVersioniertemPfad(string path, string family)
    {
        Assert.Equal(family, PackagedApps.FamilyFromPackagePath(path));
        Assert.True(PackagedApps.IsPackageExe(path));
    }

    [Theory]
    [InlineData(@"C:\Users\kai\AppData\Local\Microsoft\WindowsApps\Spotify.exe")] // Startalias
    [InlineData(@"C:\Users\kai\AppData\Local\Microsoft\WindowsApps\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\Spotify.exe")]
    [InlineData(@"C:\Users\kai\AppData\Local\Microsoft\WindowsApps\SpotifyAB.SpotifyMusic_1.308.620.0_x64__zpdnekdrzrea0\Spotify.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Spotify.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Deleted\Spotify.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.308.620.0_x64__zpdnekdrzrea0")] // nur der Ordner
    [InlineData(@"C:\Program Files\ModifiableWindowsApps\Game_1.0.0.0_x64__abc\game.exe")]
    [InlineData(@"C:\Program Files\Spotify\Spotify.exe")]
    public void KeinPaketPfad(string path)
    {
        Assert.Null(PackagedApps.FamilyFromPackagePath(path));
        Assert.False(PackagedApps.IsPackageExe(path));
    }

    [Theory]
    [InlineData("Name_1.2.3.4_x64__pub", "Name_pub")]
    [InlineData("Name_1.2.3.4_neutral_split.scale-100_pub", "Name_pub")]
    [InlineData("Name_pub", null)]
    [InlineData("Name_keineVersion_x64__pub", null)]
    public void FamilieAusPaketname(string fullName, string? family) =>
        Assert.Equal(family, PackagedApps.FamilyFromFullName(fullName));

    [Fact]
    public void Dateien_InPaketordnern_SindKeineProgramme() =>
        Assert.False(PackagedApps.IsPackageExe(@"C:\Program Files\WindowsApps\Name_1.2.3.4_x64__pub\readme.txt"));

    [Fact]
    public void Schlüssel_IgnoriertGroßschreibung() =>
        Assert.Equal(PackagedApps.KeyFor(SpotifyAumid), PackagedApps.KeyFor(SpotifyAumid.ToUpperInvariant()));

    [Fact]
    public void FensterOhneAumid_TeilenKeinenSchlüssel()
    {
        Assert.NotEqual(PackagedApps.WindowKey("", 1), PackagedApps.WindowKey("", 2));
        Assert.NotEqual(PackagedApps.WindowKey(null, 1), PackagedApps.WindowKey(null, 2));
        Assert.NotEqual("aumid:", PackagedApps.WindowKey("", 1));
        Assert.Equal(PackagedApps.KeyFor("App!Id"), PackagedApps.WindowKey("App!Id", 1));
    }

    [Fact]
    public void Normalisieren_VersionierterPfad_WirdZuShellPfad() =>
        Assert.Equal(SpotifyPin, PackagedApps.NormalizePath(SpotifyExe, SpotifyOnly));

    [Theory]
    [InlineData(@"C:\Programme\App\app.exe")]
    [InlineData(@"C:\Users\kai\AppData\Local\Microsoft\WindowsApps\Spotify.exe")]
    [InlineData(@"C:\Users\kai\Desktop\App.lnk")]
    [InlineData(@"C:\Users\kai\Ordner")]
    public void Normalisieren_LässtAnderePfadeInRuhe(string path) =>
        Assert.Equal(path, PackagedApps.NormalizePath(path, (_, _) => throw new InvalidOperationException("nicht nachschlagen")));

    [Fact]
    public void Normalisieren_NichtInstalliert_BleibtUnverändert() =>
        Assert.Equal(SpotifyExe, PackagedApps.NormalizePath(SpotifyExe, (_, _) => null));

    [Fact]
    public void Umstellen_AlterVersionsordner_NurAusDemText()
    {
        // Diesen Ordner gibt es nicht (mehr): es zählt nur der Text
        const string old = @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_0.0.1.0_x64__zpdnekdrzrea0\Spotify.exe";
        Assert.False(File.Exists(old));
        Assert.Equal(new[] { SpotifyPin }, PackagedApps.MigratePins(new[] { old }, SpotifyOnly));
    }

    [Fact]
    public void Umstellen_EntferntDoppelte_UndBehältReihenfolge()
    {
        var pins = new[] { @"C:\a.exe", SpotifyExe, @"C:\b.lnk", SpotifyPin.ToUpperInvariant(), @"C:\A.EXE" };
        Assert.Equal(new[] { @"C:\a.exe", SpotifyPin, @"C:\b.lnk" }, PackagedApps.MigratePins(pins, SpotifyOnly));
    }

    [Fact]
    public void Umstellen_IstWiederholbar()
    {
        var once = PackagedApps.MigratePins(new[] { SpotifyExe, @"C:\a.exe" }, SpotifyOnly);
        Assert.Equal(once, PackagedApps.MigratePins(once, (_, _) => throw new InvalidOperationException("nichts mehr zu tun")));
    }

    [Fact]
    public void AppWählen_EinzigeAppGiltImmer() =>
        Assert.Equal("F_p!A", PackagedApps.PickAumid(new (string, string?)[] { ("F_p!A", "SpotifyMigrator.exe") }, "Spotify.exe"));

    [Fact]
    public void AppWählen_MehrereApps_NachProgrammdatei()
    {
        var apps = new (string, string?)[] { ("F_p!A", "a.exe"), ("F_p!B", @"sub\b.exe") };
        Assert.Equal("F_p!B", PackagedApps.PickAumid(apps, "B.EXE"));
        Assert.Equal("F_p!A", PackagedApps.PickAumid(apps, "andere.exe"));
        Assert.Null(PackagedApps.PickAumid(Array.Empty<(string, string?)>(), "a.exe"));
    }

    [Fact]
    public void Manifest_LiefertExecutableDerApp()
    {
        const string xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="Spotify" Executable="SpotifyMigrator.exe" EntryPoint="Windows.FullTrustApplication" />
                <Application Id="Zweite" Executable="sub\Zweite.exe" />
              </Applications>
            </Package>
            """;
        Assert.Equal("SpotifyMigrator.exe", PackagedApps.ManifestExecutable(xml, "Spotify"));
        Assert.Equal(@"sub\Zweite.exe", PackagedApps.ManifestExecutable(xml, "zweite"));
        Assert.Null(PackagedApps.ManifestExecutable(xml, "Fehlt"));
    }

    [Fact]
    public void Symbolquellen_InRichtigerReihenfolge()
    {
        var list = PackagedApps.IconExeCandidates(SpotifyExe, @"C:\Pkg", "Spotify", "SpotifyMigrator.exe");
        Assert.Equal(new[] { SpotifyExe, @"C:\Pkg\Spotify.exe", @"C:\Pkg\SpotifyMigrator.exe" }, list);
    }

    [Fact]
    public void Symbolquellen_OhneDoppelte_UndNurImPaket()
    {
        Assert.Equal(new[] { @"C:\Pkg\App.exe" }, PackagedApps.IconExeCandidates(null, @"C:\Pkg", "App", "app.exe"));
        Assert.Empty(PackagedApps.IconExeCandidates(null, @"C:\Pkg", "a:b?", @"..\fremd.exe"));
        Assert.Empty(PackagedApps.IconExeCandidates(null, @"C:\Pkg", null, @"C:\fremd.exe"));
        Assert.Empty(PackagedApps.IconExeCandidates(null, null, "App", "app.exe"));
    }

    [Fact]
    public void Symbolquellen_SchrägstrichImManifest_WirdZuBackslash() =>
        Assert.Equal(new[] { @"C:\Pkg\app\ChatGPT.exe" }, PackagedApps.IconExeCandidates(null, @"C:\Pkg", null, "app/ChatGPT.exe"));

    [Theory]
    [InlineData(@"EntryPoint=""Windows.FullTrustApplication""", true)]
    [InlineData(@"uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""win32App""", true)]   // z. B. VS Code, ohne EntryPoint
    [InlineData(@"EntryPoint=""AppObject.EntryPoint""", false)]                               // z. B. Einstellungen
    [InlineData(@"EntryPoint=""App.App"" uap10:TrustLevel=""appContainer""", false)]
    public void Manifest_ErkenntDesktopApps(string attributes, bool expected)
    {
        string xml = $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
              <Applications><Application Id="App" Executable="App.exe" {attributes} /></Applications>
            </Package>
            """;
        Assert.Equal(expected, PackagedApps.ManifestIsDesktopApp(xml, "app"));
        Assert.Null(PackagedApps.ManifestIsDesktopApp(xml, "Fehlt"));
    }

    [Fact]
    public void PaketExe_LiefertAumid_AnderePfadeNicht()
    {
        Assert.Equal(SpotifyAumid, PackagedApps.PackageExeAumid(SpotifyExe, SpotifyOnly));
        Assert.Null(PackagedApps.PackageExeAumid(SpotifyExe, (_, _) => null));
        Assert.Null(PackagedApps.PackageExeAumid(@"C:\Programme\App\app.exe", (_, _) => throw new InvalidOperationException("nicht nachschlagen")));
        Assert.Null(PackagedApps.PackageExeAumid(null, (_, _) => throw new InvalidOperationException("nicht nachschlagen")));
    }

    [Fact]
    public void Verknüpfung_AufAltenPaketordner_FührtZurApp()
    {
        // Der Versionsordner existiert nicht: das Ziel muss trotzdem aus der Verknüpfung lesbar sein
        const string old = @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_0.0.1.0_x64__zpdnekdrzrea0\Spotify.exe";
        string lnk = Path.Combine(Path.GetTempPath(), $"GlassBarTest_{Guid.NewGuid():N}.lnk");
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = old;
            shortcut.Save();

            Assert.Equal(SpotifyAumid, PackagedApps.PackageExeAumid(Native.ResolveShortcut(lnk).Target, SpotifyOnly));
        }
        finally { File.Delete(lnk); }
    }

    [Fact]
    public void Sprungliste_PaketExe_ÜberAppEintrag()
    {
        Assert.Equal(SpotifyPin, PackagedApps.LaunchFile(SpotifyExe, SpotifyOnly));
        Assert.Null(PackagedApps.LaunchFile(SpotifyExe, (_, _) => null)); // dann bleibt die ID-Liste
    }

    [Theory]
    [InlineData(@"C:\Programme\App\app.exe", @"C:\Programme\App\app.exe")]
    [InlineData(@"C:\Users\kai\Datei.txt", @"C:\Users\kai\Datei.txt")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Sprungliste_AnderePfade_Unverändert(string? file, string? expected) =>
        Assert.Equal(expected, PackagedApps.LaunchFile(file, (_, _) => throw new InvalidOperationException("nicht nachschlagen")));

    // ---------- Pixel ----------

    private static byte[] Image(int size, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                int i = (y * size + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, a);
            }
        return px;
    }

    private static bool InGlyph(int x, int y) => x is >= 8 and < 24 && y is >= 8 and < 24;
    private static byte[] Glyph(byte v) => Image(32, (x, y) => InGlyph(x, y) ? (v, v, v, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));

    [Fact]
    public void Leer_Durchsichtig() => Assert.True(PackagedApps.IsBlank(new byte[32 * 32 * 4]));

    [Fact]
    public void Leer_EinfarbigDeckend() =>
        Assert.True(PackagedApps.IsBlank(Image(32, (_, _) => (255, 255, 255, 255))));

    [Fact]
    public void NichtLeer_Motiv() => Assert.False(PackagedApps.IsBlank(Glyph(255)));

    [Fact]
    public void FehlenderAlphakanal_WirdDeckend_LeeresBleibtLeer()
    {
        var color = Image(4, (_, _) => (10, 20, 30, 0));
        PackagedApps.FixMissingAlpha(color);
        Assert.Equal(255, color[3]);

        var empty = new byte[4 * 4 * 4];
        PackagedApps.FixMissingAlpha(empty);
        Assert.True(empty.All(b => b == 0));
    }

    [Fact]
    public void NichtVormultipliziert_WirdUmgerechnet()
    {
        var px = new byte[] { 200, 100, 0, 128, 255, 255, 255, 255 };
        PackagedApps.PremultiplyIfStraight(px);
        Assert.Equal(new byte[] { 100, 50, 0, 128, 255, 255, 255, 255 }, px);
    }

    [Fact]
    public void Vormultipliziert_BleibtUnverändert()
    {
        var px = new byte[] { 100, 50, 0, 128, 0, 0, 0, 0 };
        PackagedApps.PremultiplyIfStraight(px);
        Assert.Equal(new byte[] { 100, 50, 0, 128, 0, 0, 0, 0 }, px);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(0)]
    public void WeißesOderSchwarzesMotiv_WirdErkannt(int value) =>
        Assert.True(PackagedApps.IsMonochromeGlyph(Glyph((byte)value)));

    [Fact]
    public void BuntesMotiv_IstKeinGlyph() =>
        Assert.False(PackagedApps.IsMonochromeGlyph(Image(32, (x, y) => InGlyph(x, y) ? ((byte)0x54, (byte)0xB9, (byte)0x1D, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0))));

    [Fact]
    public void DeckendeKachel_IstKeinGlyph() =>
        Assert.False(PackagedApps.IsMonochromeGlyph(Image(32, (x, _) => x < 16 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255))));

    [Theory]
    [InlineData(255)]
    [InlineData(0)]
    public void Platte_IstDunkel_MitHellemMotiv(int value)
    {
        const int size = 32;
        var px = PackagedApps.OnDarkPlate(Glyph((byte)value), size, size);
        byte[] At(int x, int y) => px[((y * size + x) * 4)..((y * size + x) * 4 + 4)];

        Assert.Equal(0, At(0, 0)[3]);            // abgerundete Ecke
        Assert.Equal(255, At(size / 2, 2)[3]);   // Platte deckend …
        Assert.True(At(size / 2, 2)[2] < 0x40);  // … und dunkel
        Assert.True(At(size / 2, size / 2)[2] > 0xF0); // Motiv hell, auch wenn es schwarz war
        Assert.False(PackagedApps.IsBlank(px));
        Assert.False(PackagedApps.IsMonochromeGlyph(px));
    }

    [Collection("Datenordner")]
    public class ConfigRoundTrip
    {
        [Fact]
        public void ShellPin_Übersteht_SpeichernUndLaden()
        {
            new Config { Pinned = { SpotifyPin } }.Save();
            Assert.Equal(new[] { SpotifyPin }, Config.Load().Pinned);
        }
    }
}

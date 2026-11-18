using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace GlassBar;

/// <summary>Eine sichtbare App eines installierten Pakets (Eintrag in „Alle Apps“).</summary>
internal sealed class PackagedAppInfo
{
    private readonly Lazy<string?> _manifestExe;
    private readonly Lazy<bool?> _manifestDesktopApp;

    public string Aumid { get; }
    public string DisplayName { get; }
    public string? InstallFolder { get; }

    /// <summary>Executable aus dem Paketmanifest (relativ zum Installationsordner).</summary>
    public string? ManifestExecutable => _manifestExe.Value;

    /// <summary>Läuft die App als Desktop-Programm? false = echte UWP-App, null = unbekannt.</summary>
    public bool? IsDesktopApp => _manifestDesktopApp.Value;

    public PackagedAppInfo(string aumid, string displayName, string? installFolder)
    {
        Aumid = aumid;
        DisplayName = displayName;
        InstallFolder = installFolder;
        _manifestExe = new Lazy<string?>(ReadManifestExecutable);
        _manifestDesktopApp = new Lazy<bool?>(ReadManifestDesktopApp);
    }

    private bool? ReadManifestDesktopApp()
    {
        if (InstallFolder == null || PackagedApps.AppIdFromAumid(Aumid) is not { } appId) return null;
        try { return PackagedApps.ManifestIsDesktopApp(File.ReadAllText(Path.Combine(InstallFolder, "AppxManifest.xml")), appId); }
        catch { return null; }
    }

    private string? ReadManifestExecutable()
    {
        if (InstallFolder == null || PackagedApps.AppIdFromAumid(Aumid) is not { } appId) return null;
        try { return PackagedApps.ManifestExecutable(File.ReadAllText(Path.Combine(InstallFolder, "AppxManifest.xml")), appId); }
        catch { return null; }
    }
}

/// <summary>
/// Paket-Identität eines Fensters. Exact = der Prozess nennt genau einen sichtbaren App-Eintrag;
/// sonst ist Aumid nur die beste Zuordnung innerhalb der Paketfamilie (z. B. Spotify: Starter und Fenster-Prozess unterscheiden sich).
/// </summary>
internal readonly record struct PackagedWindow(string Aumid, string? Family, bool Exact);

/// <summary>
/// Apps aus dem Microsoft Store (MSIX). Ihre Programmdateien unter \WindowsApps\ lassen sich nicht direkt starten
/// und ändern mit jedem Update den Ordner – deshalb werden sie über shell:AppsFolder\&lt;AUMID&gt; angesprochen.
/// </summary>
internal static class PackagedApps
{
    public const string ShellPrefix = @"shell:AppsFolder\";

    // ---------- Reine Text-Helfer ----------

    public static bool IsShellPath(string? path) => AumidFromPath(path) != null;

    /// <summary>AUMID aus shell:AppsFolder\&lt;AUMID&gt;, sonst null.</summary>
    public static string? AumidFromPath(string? path)
    {
        if (path == null || !path.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string aumid = path[ShellPrefix.Length..].Trim();
        return aumid.Length > 0 ? aumid : null;
    }

    public static string PathFromAumid(string aumid) => ShellPrefix + aumid;

    /// <summary>Paketfamilie aus Familie!AppId; null, wenn es keine Paket-AUMID ist (z. B. die einer Web-App).</summary>
    public static string? FamilyFromAumid(string? aumid)
    {
        if (aumid == null) return null;
        int bang = aumid.IndexOf('!');
        if (bang <= 0 || bang == aumid.Length - 1 || aumid.IndexOf('!', bang + 1) >= 0) return null;
        string family = aumid[..bang];
        var parts = family.Split('_');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? family : null;
    }

    public static string? AppIdFromAumid(string? aumid) =>
        FamilyFromAumid(aumid) != null ? aumid![(aumid!.IndexOf('!') + 1)..] : null;

    public static bool IsPackagedAumid(string? aumid) => FamilyFromAumid(aumid) != null;

    /// <summary>Name_Version_Architektur_Ressource_Herausgeber -> Name_Herausgeber.</summary>
    public static string? FamilyFromFullName(string? fullName)
    {
        var p = fullName?.Split('_');
        if (p is not { Length: 5 } || p[0].Length == 0 || p[2].Length == 0 || p[4].Length == 0) return null;
        return Regex.IsMatch(p[1], @"^\d+(\.\d+){3}$") ? p[0] + "_" + p[4] : null;
    }

    /// <summary>
    /// Paketfamilie aus ...\WindowsApps\&lt;Paketname&gt;\.... Geht nur über den Text, der Ordner muss nicht mehr existieren.
    /// %LOCALAPPDATA%\Microsoft\WindowsApps enthält nur Startaliase und zählt nicht.
    /// </summary>
    public static string? FamilyFromPackagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var seg = path.Split('\\', '/');
        for (int i = 1; i + 2 < seg.Length; i++)
        {
            if (!seg[i].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            if (seg[i - 1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            if (seg[^1].Length > 0 && FamilyFromFullName(seg[i + 1]) is { } family) return family;
        }
        return null;
    }

    /// <summary>Programmdatei in einem Paketordner – darf nie direkt gestartet werden.</summary>
    public static bool IsPackageExe(string? path) =>
        path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && FamilyFromPackagePath(path) != null;

    /// <summary>Schlüssel, über den Pin und Fenster derselben App zusammenfinden.</summary>
    public static string KeyFor(string aumid) => "aumid:" + aumid.ToLowerInvariant();

    /// <summary>Schlüssel für Fenster ohne Programmdatei: nach AUMID, ohne AUMID je Fenster ein eigener.</summary>
    public static string WindowKey(string? aumid, IntPtr handle) =>
        string.IsNullOrEmpty(aumid) ? "hwnd:" + handle : KeyFor(aumid);

    /// <summary>Wandelt einen Pfad anhand des Textes um; resolve(Familie, Programmname) liefert die AUMID oder null.</summary>
    public static string NormalizePath(string path, Func<string, string?, string?> resolve)
    {
        if (AumidFromPath(path) is { } aumid) return PathFromAumid(aumid);
        if (!IsPackageExe(path)) return path;
        return resolve(FamilyFromPackagePath(path)!, FileName(path)) is { Length: > 0 } resolved ? PathFromAumid(resolved) : path;
    }

    /// <summary>AUMID zu einer Programmdatei in einem Paketordner; null bei anderen Pfaden oder nicht auflösbarem Paket.</summary>
    public static string? PackageExeAumid(string? exe, Func<string, string?, string?> resolve) =>
        IsPackageExe(exe) && resolve(FamilyFromPackagePath(exe)!, FileName(exe)) is { Length: > 0 } aumid ? aumid : null;

    /// <summary>
    /// Was die Shell für einen Sprunglisten-Eintrag öffnen soll: Programme aus Paketordnern über ihren App-Eintrag,
    /// alles andere unverändert. null = kein brauchbarer Pfad (dann bleibt nur die ID-Liste).
    /// </summary>
    public static string? LaunchFile(string? file, Func<string, string?, string?> resolve)
    {
        if (string.IsNullOrEmpty(file)) return null;
        if (!IsPackageExe(file)) return file;
        return PackageExeAumid(file, resolve) is { } aumid ? PathFromAumid(aumid) : null;
    }

    /// <summary>Angeheftete Pfade umstellen und Doppelte entfernen (die Reihenfolge bleibt). Mehrfach anwendbar.</summary>
    public static List<string> MigratePins(IEnumerable<string> pins, Func<string, string?, string?> resolve)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var pin in pins)
        {
            string path = NormalizePath(pin, resolve);
            if (seen.Add(path)) result.Add(path);
        }
        return result;
    }

    /// <summary>Welche App eines Pakets gemeint ist: die einzige, sonst die mit passender Programmdatei, sonst die erste.</summary>
    public static string? PickAumid(IReadOnlyList<(string Aumid, string? Executable)> apps, string? exeName)
    {
        if (apps.Count == 0) return null;
        if (apps.Count > 1 && !string.IsNullOrEmpty(exeName))
            foreach (var a in apps)
                if (string.Equals(FileName(a.Executable), exeName, StringComparison.OrdinalIgnoreCase)) return a.Aumid;
        return apps[0].Aumid;
    }

    /// <summary>Executable der App mit dieser Id aus dem Text von AppxManifest.xml.</summary>
    public static string? ManifestExecutable(string manifestXml, string appId)
    {
        return (string?)ManifestApplication(manifestXml, appId)?.Attribute("Executable") is { Length: > 0 } exe ? exe : null;
    }

    /// <summary>
    /// Desktop-Programm laut Manifest: Einstiegspunkt Windows.FullTrustApplication oder uap10:TrustLevel="mediumIL"
    /// (so deklarieren es z. B. VS Code und Edge, ganz ohne EntryPoint). null = App nicht im Manifest.
    /// </summary>
    public static bool? ManifestIsDesktopApp(string manifestXml, string appId)
    {
        if (ManifestApplication(manifestXml, appId) is not { } app) return null;
        string? Attr(string name) => app.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
        return string.Equals(Attr("EntryPoint"), "Windows.FullTrustApplication", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Attr("TrustLevel"), "mediumIL", StringComparison.OrdinalIgnoreCase);
    }

    private static XElement? ManifestApplication(string manifestXml, string appId) =>
        XDocument.Parse(manifestXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
            && string.Equals((string?)e.Attribute("Id"), appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Programmdateien, deren Symbol in Frage kommt, in dieser Reihenfolge: laufendes Fenster,
    /// &lt;Anzeigename&gt;.exe im Paket, Executable aus dem Manifest. Nichts außerhalb des Paketordners.
    /// </summary>
    public static List<string> IconExeCandidates(string? runningExe, string? installFolder, string? displayName, string? manifestExe)
    {
        var result = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrEmpty(p) && !result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
        }
        string? InPackage(string relative)
        {
            if (string.IsNullOrEmpty(installFolder) || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(relative)
                || relative.Split('\\', '/').Contains("..")) return null;
            // Manifeste dürfen „/“ enthalten (app/ChatGPT.exe), die Shell lehnt solche Pfade aber ab
            return Path.Combine(installFolder, relative.Replace('/', '\\'));
        }

        Add(runningExe);
        if (!string.IsNullOrWhiteSpace(displayName) && displayName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            Add(InPackage(displayName.Trim() + ".exe"));
        if (!string.IsNullOrWhiteSpace(manifestExe) && manifestExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            Add(InPackage(manifestExe.Trim()));
        return result;
    }

    private static string? FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        int i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? path[(i + 1)..] : path;
    }

    // ---------- Pixel (Pbgra32, Zeile für Zeile von oben) ----------

    private const byte VisibleAlpha = 32;

    /// <summary>Bilder ohne jeden Alphawert, aber mit Farbe, sind deckend gemeint.</summary>
    public static void FixMissingAlpha(byte[] px)
    {
        bool anyAlpha = false, anyColor = false;
        for (int i = 0; i + 3 < px.Length && !anyAlpha; i += 4)
        {
            anyAlpha = px[i + 3] != 0;
            anyColor |= px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0;
        }
        if (anyAlpha || !anyColor) return;
        for (int i = 3; i < px.Length; i += 4) px[i] = 0xFF;
    }

    /// <summary>
    /// Die Shell liefert App-Symbole teils mit nicht vormultiplizierten Farben (erkennbar an Farbe > Alpha).
    /// Ungerechnet ergäbe das helle Ränder.
    /// </summary>
    public static void PremultiplyIfStraight(byte[] px)
    {
        bool straight = false;
        for (int i = 0; i + 3 < px.Length && !straight; i += 4)
            straight = px[i] > px[i + 3] || px[i + 1] > px[i + 3] || px[i + 2] > px[i + 3];
        if (!straight) return;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            int a = px[i + 3];
            for (int c = 0; c < 3; c++) px[i + c] = (byte)((px[i + c] * a + 127) / 255);
        }
    }

    /// <summary>Leer: (fast) nichts sichtbar, oder die ganze Fläche hat eine einzige Farbe.</summary>
    public static bool IsBlank(byte[] px)
    {
        int total = px.Length / 4, visible = 0;
        if (total == 0) return true;
        bool oneColor = true;
        int first = -1;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i + 3] < VisibleAlpha) continue;
            visible++;
            int c = px[i] | px[i + 1] << 8 | px[i + 2] << 16 | px[i + 3] << 24;
            if (first == -1 && visible == 1) first = c;
            else if (c != first) oneColor = false;
        }
        return visible < total / 100 + 1 || (oneColor && visible >= total * 98 / 100);
    }

    /// <summary>Einfarbig weißes oder schwarzes Motiv auf Transparenz – auf hellem bzw. dunklem Dock wäre es unsichtbar.</summary>
    public static bool IsMonochromeGlyph(byte[] px)
    {
        int total = px.Length / 4, visible = 0, white = 0, black = 0;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a < VisibleAlpha) continue;
            visible++;
            // Vormultiplizierte Werte zurückrechnen
            int b = px[i] * 255 / a, g = px[i + 1] * 255 / a, r = px[i + 2] * 255 / a;
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min > 24) continue;
            if (min >= 205) white++;
            else if (max <= 50) black++;
        }
        if (visible == 0 || visible >= total * 9 / 10) return false; // ohne Transparenz ist es eine normale Kachel
        int needed = visible * 97 / 100;
        return white >= needed || black >= needed;
    }

    /// <summary>Setzt das Motiv weiß und mittig auf eine kleine, abgerundete dunkle Platte (sichtbar in beiden Designs).</summary>
    public static byte[] OnDarkPlate(byte[] px, int w, int h)
    {
        const byte PlateB = 0x30, PlateG = 0x2B, PlateR = 0x2B;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] >= VisibleAlpha)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }

        var result = new byte[w * h * 4];
        double radius = Math.Min(w, h) * 0.22;
        double glyphW = maxX - minX + 1, glyphH = maxY - minY + 1;
        double scale = maxX < 0 ? 1 : Math.Min(w * 0.62 / glyphW, h * 0.62 / glyphH);

        double SourceAlpha(double sx, double sy)
        {
            int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
            double fx = sx - x0, fy = sy - y0;
            double At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : px[(y * w + x) * 4 + 3];
            return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy)
                 + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Abstand zur abgerundeten Kante, für weiche Ecken
                double qx = Math.Abs(x + 0.5 - w / 2.0) - (w / 2.0 - radius), qy = Math.Abs(y + 0.5 - h / 2.0) - (h / 2.0 - radius);
                double dist = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                double cover = Math.Clamp(0.5 - dist, 0, 1);
                if (cover <= 0) continue;

                double glyph = 0;
                if (maxX >= 0)
                {
                    // Mehrfach abtasten, damit das Verkleinern keine Treppen erzeugt
                    const int Samples = 3;
                    for (int sy = 0; sy < Samples; sy++)
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            double dx = x + (sx + 0.5) / Samples - w / 2.0, dy = y + (sy + 0.5) / Samples - h / 2.0;
                            glyph += SourceAlpha(dx / scale + (minX + maxX) / 2.0, dy / scale + (minY + maxY) / 2.0);
                        }
                    glyph /= Samples * Samples * 255.0;
                }

                int i = (y * w + x) * 4;
                result[i] = (byte)Math.Round((PlateB + (255 - PlateB) * glyph) * cover);
                result[i + 1] = (byte)Math.Round((PlateG + (255 - PlateG) * glyph) * cover);
                result[i + 2] = (byte)Math.Round((PlateR + (255 - PlateR) * glyph) * cover);
                result[i + 3] = (byte)Math.Round(255 * cover);
            }
        return result;
    }

    // ---------- Installierte Pakete ----------

    private sealed record FamilyEntry(IReadOnlyList<PackagedAppInfo>? Apps, DateTime Until);
    private static readonly Dictionary<string, FamilyEntry> Families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sichtbare Apps einer Paketfamilie (leer = nicht installiert, null = Abfrage fehlgeschlagen).</summary>
    private static IReadOnlyList<PackagedAppInfo>? AppsOfFamily(string family)
    {
        lock (Families)
            if (Families.TryGetValue(family, out var hit) && hit.Until > DateTime.UtcNow) return hit.Apps;

        List<PackagedAppInfo>? apps = new();
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser("", family))
            {
                string? folder = null;
                try { folder = package.InstalledLocation.Path; } catch { }
                foreach (var entry in package.GetAppListEntries())
                    if (!apps.Any(a => a.Aumid.Equals(entry.AppUserModelId, StringComparison.OrdinalIgnoreCase)))
                        apps.Add(new PackagedAppInfo(entry.AppUserModelId, entry.DisplayInfo.DisplayName, folder));
            }
        }
        catch { apps = null; }

        // Gefundenes lange merken; „nicht da“ kann sich durch eine Installation bald ändern
        var ttl = apps == null ? TimeSpan.FromSeconds(30) : apps.Count > 0 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
        lock (Families) Families[family] = new FamilyEntry(apps, DateTime.UtcNow + ttl);
        return apps;
    }

    public static PackagedAppInfo? Find(string? aumid) =>
        FamilyFromAumid(aumid) is { } family
            ? AppsOfFamily(family)?.FirstOrDefault(a => a.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>AUMID der App einer Paketfamilie; exeName entscheidet nur, wenn das Paket mehrere Apps hat.</summary>
    public static string? AumidForFamily(string family, string? exeName)
    {
        if (AppsOfFamily(family) is not { Count: > 0 } apps) return null;
        return PickAumid(apps.Count == 1
            ? new[] { (apps[0].Aumid, (string?)null) }
            : apps.Select(a => (a.Aumid, a.ManifestExecutable)).ToList(), exeName);
    }

    /// <summary>Gibt es das Ziel noch? Scheitert nur die Abfrage, bleibt der Eintrag lieber erhalten.</summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (AumidFromPath(path) is not { } aumid) return File.Exists(path) || Directory.Exists(path);
        if (FamilyFromAumid(aumid) is { } family)
        {
            var apps = AppsOfFamily(family);
            if (apps == null || apps.Any(a => a.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return JumpList.ShellItemExists(PathFromAumid(aumid));
    }

    /// <summary>
    /// Pfad, wie er angeheftet wird: Programme aus Paketordnern und Verknüpfungen auf Paket-Apps werden zu
    /// shell:AppsFolder\&lt;AUMID&gt;. Alles andere (und nicht auflösbare Pakete) bleibt unverändert.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !IsShellPath(path))
        {
            if (ShortcutAumid(path) is { } aumid && Find(aumid) != null) return PathFromAumid(aumid);
            var (target, _) = Native.ResolveShortcut(path);
            return IsPackageExe(target) && NormalizePath(target, AumidForFamily) is var p && IsShellPath(p) ? p : path;
        }
        return NormalizePath(path, AumidForFamily);
    }

    /// <summary>Paket-AUMID, die in einer Verknüpfung eingetragen ist.</summary>
    public static string? ShortcutAumid(string lnkPath) =>
        JumpList.ShortcutAppId(lnkPath) is { } id && IsPackagedAumid(id) ? id : null;

    /// <summary>Einmal beim Start, bevor es Docks gibt: alte Pins auf Paketordner umstellen.</summary>
    public static void MigrateConfig(Config config)
    {
        try
        {
            var migrated = MigratePins(config.Pinned, AumidForFamily);
            if (migrated.SequenceEqual(config.Pinned)) return;
            config.Pinned = migrated;
            config.Save();
        }
        catch (Exception ex) { Log.Error("Angeheftete Apps umstellen", ex); }
    }

    // ---------- Identität laufender Prozesse ----------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);

    private readonly record struct ProcessIdentity(uint Pid, string? Aumid, string? Family);
    private static readonly ConcurrentDictionary<IntPtr, ProcessIdentity> Identities = new();

    private static ProcessIdentity IdentityOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return default;
        if (Identities.TryGetValue(hwnd, out var hit) && hit.Pid == pid) return hit;

        string? aumid = null, family = null;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                uint len = 256;
                var sb = new StringBuilder((int)len);
                if (GetApplicationUserModelId(process, ref len, sb) == 0) aumid = sb.ToString();
                len = 256;
                sb = new StringBuilder((int)len);
                if (GetPackageFamilyName(process, ref len, sb) == 0) family = sb.ToString();
            }
            finally { CloseHandle(process); }
        }
        if (Identities.Count > 1024) Identities.Clear();
        return Identities[hwnd] = new ProcessIdentity(pid, aumid, family);
    }

    /// <summary>
    /// Zu welcher Paket-App ein normales (nicht von ApplicationFrameHost gehostetes) Fenster gehört; null = keine.
    /// Prozesse mit Paket-Identität ohne App-Eintrag außerhalb von \WindowsApps\ bleiben normale Programme.
    /// </summary>
    public static PackagedWindow? ForWindow(IntPtr hwnd, string? exePath)
    {
        var id = IdentityOf(hwnd);
        string? pathFamily = FamilyFromPackagePath(exePath);
        string? family = id.Family ?? FamilyFromAumid(id.Aumid) ?? pathFamily;
        if (family == null) return null;

        var apps = AppsOfFamily(family);
        if (id.Aumid != null && apps?.FirstOrDefault(a => a.Aumid.Equals(id.Aumid, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return new PackagedWindow(exact.Aumid, family, true);
        if (apps is { Count: > 0 } && AumidForFamily(family, FileName(exePath)) is { } best)
            return new PackagedWindow(best, family, false);
        return id.Aumid != null && pathFamily != null ? new PackagedWindow(id.Aumid, family, false) : null;
    }

    // ---------- Symbole ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    private static readonly ConcurrentDictionary<string, bool?> IconResources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enthält die Datei eigene Symbole? null = unbekannt (Abfrage fehlgeschlagen).</summary>
    public static bool? HasIconResources(string exe) => IconResources.GetOrAdd(exe, static path =>
    {
        try
        {
            if (!File.Exists(path)) return false;
            uint count = ExtractIconEx(path, -1, IntPtr.Zero, IntPtr.Zero, 0);
            return count == uint.MaxValue ? null : count > 0;
        }
        catch { return null; }
    });

    private const int IconDip = 48;

    private static int IconPixels
    {
        get
        {
            uint dpi = 96;
            try { dpi = GetDpiForSystem(); } catch { }
            return Math.Clamp((int)Math.Round(IconDip * dpi / 96.0), IconDip, 256);
        }
    }

    private sealed class IconEntry
    {
        public ImageSource? Image;
        public bool Loading, FirstAttemptDone;
        public DateTime RetryAfter;
        public readonly HashSet<Action> Waiting = new();
    }

    private static readonly Dictionary<string, IconEntry> Icons = new();
    private static readonly BlockingCollection<Action> IconQueue = new();
    private static bool _iconThreadStarted;
    private static readonly int[] RetryDelaysMs = { 1000, 4000 };

    /// <summary>
    /// Symbol einer Paket-App. Lädt beim ersten Aufruf im Hintergrund (je App nur einmal gleichzeitig) und ruft danach
    /// loaded auf – aus einem Hintergrund-Thread. pending = der erste Ladeversuch läuft noch.
    /// </summary>
    public static ImageSource? GetIcon(string aumid, string? runningExe, Action loaded, out bool pending)
    {
        int size = IconPixels;
        string key = $"{aumid}|{size}|{runningExe}".ToLowerInvariant();
        lock (Icons)
        {
            if (!Icons.TryGetValue(key, out var e)) Icons[key] = e = new IconEntry();
            pending = false;
            if (e.Image != null) return e.Image;
            if (!e.Loading && DateTime.UtcNow >= e.RetryAfter)
            {
                e.Loading = true;
                e.Waiting.Add(loaded);
                Enqueue(() => LoadIcon(e, aumid, runningExe, size, 0));
            }
            else if (e.Loading) e.Waiting.Add(loaded);
            pending = e.Loading && !e.FirstAttemptDone;
            return null;
        }
    }

    private static void Enqueue(Action job)
    {
        IconQueue.Add(job);
        lock (IconQueue)
        {
            if (_iconThreadStarted) return;
            _iconThreadStarted = true;
        }
        // Shell-Objekte brauchen einen STA-Thread; einer für alle, damit nichts parallel die Festplatte beschäftigt
        var t = new Thread(() =>
        {
            foreach (var work in IconQueue.GetConsumingEnumerable())
                try { work(); } catch (Exception ex) { Log.Error("App-Symbol laden", ex); }
        }) { IsBackground = true, Name = "GlassBar App-Symbole" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static void LoadIcon(IconEntry e, string aumid, string? runningExe, int size, int attempt)
    {
        ImageSource? image = null;
        try { image = RenderAppIcon(aumid, runningExe, size); }
        catch (Exception ex) { Log.Error("App-Symbol laden", ex); }

        Action[] notify;
        lock (Icons)
        {
            e.FirstAttemptDone = true;
            e.Image = image;
            bool retry = image == null && attempt < RetryDelaysMs.Length;
            if (!retry)
            {
                e.Loading = false;
                // Fehlschläge nicht für immer merken (z. B. Symbol-Cache nach der Anmeldung noch kalt)
                if (image == null) e.RetryAfter = DateTime.UtcNow.AddMinutes(2);
            }
            notify = e.Waiting.ToArray();
            if (!retry) e.Waiting.Clear();
            if (retry)
                _ = Task.Delay(RetryDelaysMs[attempt]).ContinueWith(_ => Enqueue(() => LoadIcon(e, aumid, runningExe, size, attempt + 1)), TaskScheduler.Default);
        }
        foreach (var n in notify)
            try { n(); } catch { }
    }

    /// <summary>
    /// Programmdatei mit echten Symbolen, sonst das Symbol aus „Alle Apps“. Bei echten UWP-Apps ist die Programmdatei
    /// nur der Host mit oft veraltetem Symbol (Einstellungen: blaues Quadrat), dort gilt immer „Alle Apps“.
    /// </summary>
    private static ImageSource? RenderAppIcon(string aumid, string? runningExe, int size)
    {
        var info = Find(aumid);
        if (info?.IsDesktopApp != false)
            foreach (var exe in IconExeCandidates(runningExe, info?.InstallFolder, info?.DisplayName, info?.ManifestExecutable))
                if (HasIconResources(exe) == true && Render(exe, size) is { } fromExe) return fromExe;
        return Render(PathFromAumid(aumid), size);
    }

    private static ImageSource? Render(string parsingName, int size)
    {
        var px = JumpList.RenderShellItem(parsingName, size, out int w, out int h);
        if (px == null) return null;
        FixMissingAlpha(px);
        PremultiplyIfStraight(px);
        if (IsBlank(px)) return null;
        if (IsMonochromeGlyph(px)) px = OnDarkPlate(px, w, h);
        double dpi = 96.0 * w / IconDip;
        var bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace GlassBar;

/// <summary>
/// Updates über die GitHub-Releases von GlassBar: neueste Version abfragen, Datei laden und prüfen (SHA-256), dann
/// – mit dem Installer installiert: das neue Setup still ausführen (hält „Apps & Features“ aktuell),
/// – sonst (Zip-Version): nach dem Beenden die Dateien ersetzen und GlassBar neu starten.
/// Außerdem sucht GlassBar einmal am Tag leise nach Updates und meldet sie im Rechtsklick-Menü des Docks.
/// </summary>
public static class Updater
{
    private const string Repo = "Xardas1906/GlassBar";
    /// <summary>AppId aus installer\GlassBar.iss (Inno Setup hängt „_is1“ an).</summary>
    private const string SetupUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6B0F3C2E-8F4D-4C55-9C2B-3E1F7A9D4B21}_is1";

    public sealed record Asset(string Name, string Url, long Size, string? Sha256);
    public sealed record Release(Version Version, string Tag, string Name, string Notes, Asset? Zip, Asset? Setup);

    /// <summary>Laufende Version (aus dem Projekt, z. B. 1.1.0).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GlassBar-Updater/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Neueste Version auf GitHub (null, wenn keine passende gefunden wurde).</summary>
    public static async Task<Release?> GetLatestAsync()
    {
        using var http = Http();
        return ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
    }

    /// <summary>Antwort von „releases/latest“ auslesen (null, wenn Version oder Zip fehlen).</summary>
    internal static Release? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        Asset? zip = null, setup = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var asset = new Asset(name, a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(),
                digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) zip ??= asset;
            else if (name.StartsWith("GlassBar-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup ??= asset;
        }
        if (zip == null && setup == null) return null;
        return new Release(
            new Version(version.Major, version.Minor, Math.Max(version.Build, 0)), tag,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            zip, setup);
    }

    /// <summary>Wurde GlassBar mit dem Installer in genau diesen Ordner installiert?</summary>
    public static bool InstalledWithSetup
    {
        get
        {
            try
            {
                foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(SetupUninstallKey);
                    if (key?.GetValue("InstallLocation") is string loc && SameFolder(loc, AppContext.BaseDirectory)) return true;
                }
            }
            catch { }
            return false;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lädt das Update und startet die Installation. Rückgabe true: GlassBar muss sich jetzt selbst beenden
    /// (Zip-Version: ein Skript ersetzt danach die Dateien). false: das Setup beendet GlassBar selbst.
    /// </summary>
    public static async Task<bool> PrepareAsync(Release release, IProgress<double>? progress = null)
    {
        string work = Path.Combine(Path.GetTempPath(), "GlassBar-Update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        if (release.Setup != null && (InstalledWithSetup || release.Zip == null))
        {
            string setup = Path.Combine(work, release.Setup.Name);
            await DownloadAsync(release.Setup, setup, progress);
            // Das Setup fragt nach Administratorrechten, beendet GlassBar sauber, installiert und startet es wieder.
            // Abbruch der Rückfrage -> Win32Exception (wird im Einstellungsfenster gemeldet).
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-") { UseShellExecute = true });
            return false;
        }

        if (release.Zip == null) throw new InvalidOperationException(L.T("Im Update fehlt GlassBar.exe."));
        string zip = Path.Combine(work, "update.zip");
        string files = Path.Combine(work, "files");
        await DownloadAsync(release.Zip, zip, progress);

        ZipFile.ExtractToDirectory(zip, files);
        // Zip mit Unterordner? Dann den Ordner nehmen, in dem GlassBar.exe liegt
        string? exe = Directory.EnumerateFiles(files, "GlassBar.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe == null) throw new InvalidOperationException(L.T("Im Update fehlt GlassBar.exe."));
        string source = Path.GetDirectoryName(exe)!;

        string target = AppContext.BaseDirectory.TrimEnd('\\');
        string targetExe = Environment.ProcessPath ?? Path.Combine(target, "GlassBar.exe");
        int pid = Environment.ProcessId;

        // Wartet aufs Beenden (auch des Wächters, der GlassBar.exe belegt), kopiert, startet neu.
        // Robocopy wiederholt, falls eine Datei noch kurz gesperrt ist.
        string script = Path.Combine(work, "update.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{pid}} -Timeout 30
            for ($i = 0; $i -lt 50; $i++) {
                $m = $null
                if (-not [System.Threading.Mutex]::TryOpenExisting('GlassBar_Watchdog', [ref]$m)) { break }
                $m.Dispose(); Start-Sleep -Milliseconds 100
            }
            Start-Sleep -Milliseconds 500
            robocopy "{{source}}" "{{target}}" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            # über den Explorer starten: so läuft GlassBar wieder als normaler Nutzer, auch wenn dieses Skript Adminrechte hat
            Start-Process -FilePath "explorer.exe" -ArgumentList '"{{targetExe}}"'
            """, new System.Text.UTF8Encoding(true));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // Programmordner nicht beschreibbar (z. B. unter C:\Program Files) -> mit Administratorrechten kopieren
        if (!CanWrite(target)) psi.Verb = "runas";
        Process.Start(psi);
        return true;
    }

    /// <summary>Datei laden und – wenn GitHub eine Prüfsumme liefert – mit SHA-256 prüfen.</summary>
    private static async Task DownloadAsync(Asset asset, string file, IProgress<double>? progress)
    {
        using var http = Http();
        using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? asset.Size;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(file))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                sha.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (asset.Sha256 != null && !string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException(L.T("Die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht)."));
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, ".update-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ---------- leise Suche im Hintergrund ----------

    private static DispatcherTimer? _timer;

    /// <summary>Gefundenes neueres Release (null = keines bekannt).</summary>
    public static Release? Available { get; private set; }

    /// <summary>Ein neueres Release wurde gefunden.</summary>
    public static event Action? AvailableChanged;

    /// <summary>Zwei Minuten nach dem Start, danach einmal am Tag nachsehen (Einstellung „Automatisch nach Updates suchen“).</summary>
    public static void StartBackgroundCheck()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(24);
            if (!App.Config.AutoCheckUpdates) return;
            try
            {
                var r = await GetLatestAsync();
                if (r != null && r.Version > Current) Remember(r);
            }
            catch { /* offline o. Ä.: morgen wieder */ }
        };
        _timer.Start();
    }

    /// <summary>Gefundenes Update merken (auch aus dem Einstellungsfenster).</summary>
    public static void Remember(Release r)
    {
        if (Available?.Version == r.Version) return;
        Available = r;
        AvailableChanged?.Invoke();
    }
}

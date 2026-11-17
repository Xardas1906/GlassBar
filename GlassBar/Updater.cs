using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace GlassBar;

/// <summary>
/// Updates über die GitHub-Releases von GlassBar: neueste Version abfragen, Zip laden,
/// nach dem Beenden die Dateien ersetzen und GlassBar neu starten.
/// </summary>
public static class Updater
{
    private const string Repo = "Xardas1906/GlassBar";

    public sealed record Release(Version Version, string Tag, string Name, string Notes, string ZipUrl, long Size);

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
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) continue;
            return new Release(
                new Version(version.Major, version.Minor, Math.Max(version.Build, 0)), tag,
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64());
        }
        return null;
    }

    /// <summary>
    /// Lädt das Update, entpackt es und startet ein kleines Skript, das nach dem Beenden von GlassBar
    /// die Dateien ersetzt und GlassBar wieder startet. Danach muss GlassBar sich beenden.
    /// </summary>
    public static async Task PrepareAsync(Release release, IProgress<double>? progress = null)
    {
        string work = Path.Combine(Path.GetTempPath(), "GlassBar-Update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "update.zip");
        string files = Path.Combine(work, "files");

        using (var http = Http())
        using (var resp = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        ZipFile.ExtractToDirectory(zip, files);
        // Zip mit Unterordner? Dann den Ordner nehmen, in dem GlassBar.exe liegt
        string? exe = Directory.EnumerateFiles(files, "GlassBar.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe == null) throw new InvalidOperationException("Im Update fehlt GlassBar.exe.");
        string source = Path.GetDirectoryName(exe)!;

        string target = AppContext.BaseDirectory.TrimEnd('\\');
        string targetExe = Environment.ProcessPath ?? Path.Combine(target, "GlassBar.exe");
        int pid = Environment.ProcessId;

        // Wartet aufs Beenden, kopiert, startet neu. Robocopy wiederholt, falls eine Datei noch kurz gesperrt ist.
        string script = Path.Combine(work, "update.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{pid}} -Timeout 30
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
}

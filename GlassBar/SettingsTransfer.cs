using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassBar;

/// <summary>
/// Einstellungen als Datei exportieren und wieder importieren (z. B. für einen neuen PC).
/// Die Datei ist ein Zip mit config.json und den Einstellungen des Hintergrunds „Still“.
///
/// Import in zwei Schritten: Import prüft die Datei und legt sie als import.zip in den Einstellungsordner,
/// danach startet GlassBar neu und übernimmt sie gleich beim Start (vor dem Laden der Einstellungen).
/// So überschreibt das laufende GlassBar beim Beenden die importierten Werte nicht mehr.
/// </summary>
public static class SettingsTransfer
{
    /// <summary>Dateien, die exportiert und beim Import übernommen werden (sonst nichts – keine Protokolle, keine Schriften).</summary>
    private static readonly string[] Files = { "config.json", "still.json", "still-state.json" };

    /// <summary>Werte, die zu diesem PC gehören und beim Import erhalten bleiben.</summary>
    private static readonly string[] LocalOnly = { nameof(Config.TaskbarStateBeforeGlassBar), nameof(Config.TrayPromotedImported), nameof(Config.TaskbarAutoHideAsked), nameof(Config.LastSeenVersion) };

    private static string PendingPath => Path.Combine(Config.Folder, "import.zip");

    public const string FileFilter = "GlassBar-Einstellungen (*.glassbar)|*.glassbar|Zip-Datei (*.zip)|*.zip";

    /// <summary>Aktuelle Einstellungen in <paramref name="target"/> schreiben.</summary>
    public static void Export(string target)
    {
        string tmp = target + ".tmp";
        File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var name in Files)
            {
                string path = Path.Combine(Config.Folder, name);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, name);
            }
        }
        File.Move(tmp, target, overwrite: true);
    }

    /// <summary>Datei prüfen und für den nächsten Start vormerken. Gibt bei einer ungültigen Datei eine Fehlermeldung zurück.</summary>
    public static string? Prepare(string source)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(source))
            {
                var entry = zip.GetEntry("config.json");
                if (entry == null) return L.T("Die Datei enthält keine GlassBar-Einstellungen.");
                using var r = new StreamReader(entry.Open());
                if (JsonSerializer.Deserialize<Config>(r.ReadToEnd()) == null) return L.T("Die Einstellungen in der Datei sind beschädigt.");
            }
            File.Copy(source, PendingPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return L.T("Die Datei enthält keine GlassBar-Einstellungen.");
        }
    }

    /// <summary>Beim Start: vorgemerkten Import übernehmen (alte Dateien bleiben als *.vor-import.bak erhalten).</summary>
    public static void ApplyPending()
    {
        if (!File.Exists(PendingPath)) return;
        try
        {
            // Werte, die zu diesem PC gehören, aus der bisherigen config.json retten
            string configPath = Path.Combine(Config.Folder, "config.json");
            JsonObject? old = null;
            try { if (File.Exists(configPath)) old = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject; } catch { }

            using (var zip = ZipFile.OpenRead(PendingPath))
            {
                foreach (var name in Files)
                {
                    if (zip.GetEntry(name) is not { } entry) continue;
                    string path = Path.Combine(Config.Folder, name);
                    if (File.Exists(path)) File.Copy(path, path + ".vor-import.bak", overwrite: true);
                    using var r = new StreamReader(entry.Open());
                    string text = r.ReadToEnd();
                    if (name == "config.json" && JsonNode.Parse(text) is JsonObject imported)
                    {
                        foreach (var key in LocalOnly)
                        {
                            imported.Remove(key);
                            if (old?[key] is { } v) imported[key] = v.DeepClone();
                        }
                        text = imported.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    }
                    SafeFile.WriteAllText(path, text);
                }
            }
            Log.Info("Einstellungen importiert.");
        }
        catch (Exception ex) { Log.Error("Einstellungen importieren", ex); }
        finally { try { File.Delete(PendingPath); } catch { } }
    }
}

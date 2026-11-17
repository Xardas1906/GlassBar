using System.Text;

namespace GlassBar;

/// <summary>
/// Einstellungsdateien sicher schreiben und lesen.
/// Schreiben: erst in „datei.tmp“, auf die Platte zwingen, dann austauschen – die alte Fassung bleibt als „datei.bak“.
/// So bleibt nach Absturz oder Stromausfall nie eine halb geschriebene Datei zurück.
/// Lesen: ist die Datei kaputt, wird sie als „datei.broken-…“ beiseitegelegt (nicht überschrieben)
/// und stattdessen die Sicherung geladen.
/// </summary>
internal static class SafeFile
{
    private static readonly object Lock = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Text sicher schreiben (Fehler werden weitergereicht).</summary>
    public static void WriteAllText(string path, string text)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Utf8.GetBytes(text);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); }
                catch (IOException)
                {
                    // z. B. Datei kurz von einem anderen Programm gesperrt: Sicherung von Hand, dann austauschen
                    try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
                    File.Move(tmp, path, overwrite: true);
                }
            }
            else File.Move(tmp, path);
        }
    }

    /// <summary>
    /// Datei lesen und mit <paramref name="parse"/> umwandeln; bei kaputter Datei die Sicherung (.bak) nehmen.
    /// null, wenn es weder eine brauchbare Datei noch eine brauchbare Sicherung gibt.
    /// <paramref name="parse"/> soll bei ungültigem Inhalt eine Ausnahme werfen oder null liefern.
    /// </summary>
    public static T? Read<T>(string path, Func<string, T?> parse) where T : class
    {
        lock (Lock)
        {
            foreach (string file in new[] { path, path + ".bak" })
            {
                string text;
                try
                {
                    if (!File.Exists(file)) continue;
                    text = File.ReadAllText(file, Utf8);
                }
                catch { continue; } // gesperrt o. Ä.: Datei nicht anfassen, nur die Sicherung versuchen

                try
                {
                    if (!string.IsNullOrWhiteSpace(text) && parse(text) is { } result)
                    {
                        if (file != path) GlassBar.Log.Info($"{Path.GetFileName(path)} war beschädigt, die Sicherung wurde geladen.");
                        return result;
                    }
                }
                catch { }

                // Inhalt unbrauchbar: beiseitelegen, damit er nicht beim nächsten Speichern verloren geht
                if (file == path) SetAside(path);
            }
            return null;
        }
    }

    private static void SetAside(string path)
    {
        try
        {
            string broken = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, broken);
            GlassBar.Log.Info($"{Path.GetFileName(path)} war beschädigt und wurde als {Path.GetFileName(broken)} beiseitegelegt.");
        }
        catch { }
    }
}

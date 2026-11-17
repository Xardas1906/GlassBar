namespace GlassBar;

/// <summary>
/// Fehlerprotokoll in %AppData%\GlassBar\error.log.
/// Gleiche Meldungen werden höchstens einmal pro Minute geschrieben (manche Fehler träten sonst 30-mal pro Sekunde auf),
/// und die Datei wird gekürzt, bevor sie größer als 1 MB wird.
/// </summary>
internal static class Log
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, DateTime> LastWritten = new();
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath => Path.Combine(Config.Folder, "error.log");

    /// <summary>Fehler mit Ort protokollieren, z. B. Log.Error("Wetter laden", ex).</summary>
    public static void Error(string where, Exception ex) => Write($"{where}: {ex}", $"{where}|{ex.GetType().FullName}|{ex.Message}");

    /// <summary>Hinweis protokollieren (z. B. „Sicherung geladen“).</summary>
    public static void Info(string message) => Write(message, message);

    private static void Write(string text, string key)
    {
        try
        {
            lock (Lock)
            {
                var now = DateTime.Now;
                if (LastWritten.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
                if (LastWritten.Count > 200) LastWritten.Clear();
                LastWritten[key] = now;

                Directory.CreateDirectory(Config.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    // ältere Hälfte verwerfen
                    string all = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, "[…]\n" + all[(all.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"[{now}] {text}\n\n");
            }
        }
        catch { /* Protokoll darf nie selbst einen Fehler auslösen */ }
    }
}

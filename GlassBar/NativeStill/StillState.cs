using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassBar.NativeStill;

/// <summary>
/// Dauerhafter Zustand des nativen „Still“ (das, was das Original in localStorage neben den Einstellungen ablegt:
/// getrunkene Menge, Pomodoro-Statistik, Trainingsverlauf, Wecker, Pillen-Anordnung, gesehene Tipps …).
/// Datei: %AppData%\GlassBar\still-state.json, ein JSON-Objekt Schlüssel → beliebiges JSON. Threadsicher;
/// Schreiben wird gebündelt (~1 s). Schlüssel bitte mit Bereich, z. B. "water.today", "pomo.stats".
/// </summary>
public static class StillState
{
    private static readonly object Lock = new();
    private static JsonObject? _data;
    private static Timer? _timer;
    private static string FilePath => Path.Combine(Config.Folder, "still-state.json");

    static StillState() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();

    private static JsonObject Data
    {
        get
        {
            if (_data != null) return _data;
            // kaputte Datei -> Sicherung (.bak); fehlt beides -> leer
            _data = SafeFile.Read(FilePath, json => JsonNode.Parse(json) as JsonObject);
            return _data ??= new JsonObject();
        }
    }

    /// <summary>Wert lesen (oder fallback, wenn nicht vorhanden/unlesbar).</summary>
    public static T Get<T>(string key, T fallback)
    {
        lock (Lock)
        {
            try { return Data[key] is { } n ? n.Deserialize<T>() ?? fallback : fallback; }
            catch { return fallback; }
        }
    }

    /// <summary>Wert setzen (null = löschen) und bald speichern.</summary>
    public static void Set<T>(string key, T value)
    {
        lock (Lock)
        {
            if (value is null) Data.Remove(key);
            else Data[key] = JsonSerializer.SerializeToNode(value);
            _timer ??= new Timer(_ => Flush());
            _timer.Change(1000, Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        lock (Lock)
        {
            if (_data == null) return;
            try
            {
                SafeFile.WriteAllText(FilePath, _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error("Still-Zustand speichern", ex); }
        }
    }
}

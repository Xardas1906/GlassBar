using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassBar.NativeStill;

/// <summary>
/// Speichert die Einstellungen von „Still“ für den nativen Hintergrund in %AppData%\GlassBar\still.json
/// (nur Werte, die vom Standard abweichen – wie das Original). Übernimmt beim ersten Mal die Werte aus dem
/// Browser-Hintergrund (localStorage "still.settings.v2", über TaskbarWindow.ExportWallpaperStorageAsync).
/// Format der Datei: ein JSON-Objekt Schlüssel → bool/Zahl/Text, genau wie "still.settings.v2".
/// Threadsicher; fehlerhafte Dateien werden ignoriert (dann gelten die Standardwerte).
/// </summary>
public static class NativeStillStore
{
    private static readonly object _lock = new();
    private static bool _loaded;
    private static bool _dirty;
    private static Timer? _saveTimer;
    private const int SaveDelayMs = 500;

    private static string FilePath => Path.Combine(Config.Folder, "still.json");

    static NativeStillStore()
    {
        // Beim Beenden noch ausstehende Änderungen schreiben
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>Die aktuellen Werte (lebendes Objekt; Änderungen lösen StillValues.Changed aus).</summary>
    public static StillValues Values { get; } = new();

    /// <summary>Aus der Datei laden (beim Start). Mehrfacher Aufruf schadet nicht.</summary>
    public static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            // kaputte Datei -> Sicherung (.bak); fehlt beides -> Standardwerte
            if (SafeFile.Read(FilePath, ParseStrict) is { } values)
                foreach (var kv in values)
                    Values.Set(kv.Key, kv.Value);
        }
    }

    /// <summary>Wert setzen und (verzögert) speichern.</summary>
    public static void Set(string key, object value)
    {
        lock (_lock)
        {
            EnsureLoaded();
            Values.Set(key, value);
            ScheduleSave();
        }
    }

    /// <summary>Gibt es schon eine still.json?</summary>
    public static bool Exists
    {
        get { try { return File.Exists(FilePath); } catch { return false; } }
    }

    /// <summary>Werte aus dem Browser-Speicher übernehmen (JSON-Text von "still.settings.v2") und sofort speichern.</summary>
    public static void ImportFromBrowserJson(string json)
    {
        lock (_lock)
        {
            EnsureLoaded();
            Dictionary<string, object> imported;
            try { imported = Parse(json); }
            catch { imported = new(); }

            // Was im Browser fehlt, steht dort auf Standard
            foreach (var key in Values.Snapshot().Keys)
                if (!imported.ContainsKey(key) && StillValues.OriginalDefaults.TryGetValue(key, out var d))
                    Values.Set(key, d);
            foreach (var kv in imported)
                Values.Set(kv.Key, kv.Value);

            _dirty = true;
            SaveNow();
        }
    }

    /// <summary>Ausstehende Änderungen sofort schreiben.</summary>
    public static void Flush()
    {
        lock (_lock)
        {
            if (_dirty) SaveNow();
        }
    }

    // ---------- intern ----------

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        Load(); // gleicher Lock, darf erneut betreten werden
    }

    private static void ScheduleSave()
    {
        _dirty = true;
        _saveTimer ??= new Timer(_ => Flush());
        _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
    }

    /// <summary>Nur Werte schreiben, die vom Standard des Originals abweichen. Aufruf nur unter _lock.</summary>
    private static void SaveNow()
    {
        _dirty = false;
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            var obj = new JsonObject();
            foreach (var kv in Values.Snapshot().OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (StillValues.OriginalDefaults.TryGetValue(kv.Key, out var def) && SameValue(def, kv.Value)) continue;
                JsonNode? node = kv.Value switch
                {
                    bool b => JsonValue.Create(b),
                    double d => JsonValue.Create(d),
                    string s => JsonValue.Create(s),
                    _ => null,
                };
                if (node != null) obj[kv.Key] = node;
            }

            SafeFile.WriteAllText(FilePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("Still-Einstellungen speichern", ex); }
    }

    private static bool SameValue(object a, object b) => (a, b) switch
    {
        (double x, double y) => Math.Abs(x - y) < 1e-9,
        _ => Equals(a, b),
    };

    /// <summary>Wie <see cref="Parse"/>, aber null, wenn die Datei kein JSON-Objekt enthält (gilt dann als kaputt).</summary>
    private static Dictionary<string, object>? ParseStrict(string json)
    {
        using (var doc = JsonDocument.Parse(json))
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return Parse(json);
    }

    /// <summary>JSON-Objekt in Schlüssel → bool/double/string umwandeln; andere Werte werden übersprungen.</summary>
    private static Dictionary<string, object> Parse(string json)
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.True: result[p.Name] = true; break;
                case JsonValueKind.False: result[p.Name] = false; break;
                case JsonValueKind.Number when p.Value.TryGetDouble(out var d): result[p.Name] = d; break;
                case JsonValueKind.String: result[p.Name] = p.Value.GetString() ?? ""; break;
            }
        }
        return result;
    }
}

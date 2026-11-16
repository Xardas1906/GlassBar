using System.Collections.Concurrent;

namespace GlassBar.NativeStill;

/// <summary>
/// Die Einstellungen von „Still“ als Schlüssel/Wert (gleiche Schlüssel wie im Original, z. B. "theme", "grain",
/// "clockScale"). Werte sind bool, double oder string. Fehlende Schlüssel fallen auf <see cref="StillSettings.Defaults"/> zurück.
/// Threadsicher: wird vom UI-Thread geschrieben und vom Render-Thread gelesen.
/// </summary>
public sealed class StillValues
{
    private readonly ConcurrentDictionary<string, object> _values = new();

    /// <summary>Wird bei jeder Änderung ausgelöst (Schlüssel).</summary>
    public event Action<string>? Changed;

    public StillValues() { }
    public StillValues(IEnumerable<KeyValuePair<string, object>> values) { foreach (var kv in values) _values[kv.Key] = Normalize(kv.Value); }

    public object Get(string key) =>
        _values.TryGetValue(key, out var v) ? v
        : OriginalDefaults.TryGetValue(key, out var o) ? o
        : StillSettings.Defaults.TryGetValue(key, out var d) ? Normalize(d) : 0.0;

    /// <summary>Alle Standardwerte des Originals (app.js, DEFAULTS).</summary>
    public static readonly IReadOnlyDictionary<string, object> OriginalDefaults = new Dictionary<string, object>
    {
        ["theme"] = "ink",
        ["followAlbum"] = true,
        ["tintStrength"] = 0.8,
        ["grain"] = 0.15,
        ["motion"] = 4.0,
        ["audioGlow"] = 0.75,
        ["parallax"] = false,
        ["quality"] = "high",
        ["font"] = "manrope",
        ["clockScale"] = 0.85,
        ["uiScale"] = 1.0,
        ["clockWeight"] = "bold",
        ["clockBlend"] = 0.59,
        ["clockX"] = 0.0,
        ["clockY"] = -0.12,
        ["showClock"] = true,
        ["clock24"] = true,
        ["seconds"] = "off",
        ["showDate"] = true,
        ["showFocus"] = true,
        ["celebrate"] = true,
        ["taskSound"] = true,
        ["showPill"] = true,
        ["pillStyle"] = "compact",
        ["pillPos"] = "bottom",
        ["hoverExpand"] = true,
        ["announce"] = true,
        ["visualizer"] = true,
        ["showControls"] = true,
        ["lyrics"] = "stage",
        ["lyricsSize"] = 0.74,
        ["lyricsWeight"] = "light",
        ["lyricsBright"] = 0.56,
        ["lyricsTiming"] = "hover",
        ["water"] = true,
        ["waterStrength"] = 0.5,
        ["waterSize"] = 0.5,
        ["waterDecay"] = 0.5,
        ["waterShine"] = 0.27,
        ["waterClick"] = true,
        ["hydrate"] = true,
        ["waterGoal"] = 2000.0,
        ["cupSize"] = 250.0,
        ["waterUnit"] = "ml",
        ["waterRemind"] = true,
        ["remindEvery"] = 60.0,
        ["activeFrom"] = 8.0,
        ["activeTo"] = 22.0,
        ["waterChime"] = false,
        ["pomo"] = true,
        ["focusMin"] = 25.0,
        ["shortMin"] = 5.0,
        ["longMin"] = 15.0,
        ["longEvery"] = 4.0,
        ["autoBreaks"] = true,
        ["autoFocus"] = false,
        ["pomoGoal"] = 8.0,
        ["pomoChime"] = true,
        ["prayer"] = false,
        ["prayerMethod"] = "auto",
        ["prayerAsr"] = "auto",
        ["prayerIsha"] = "fajr",
        ["prayerStart"] = true,
        ["prayerBefore"] = 20.0,
        ["prayerSound"] = "dewdrop",
        ["prayerAdjFajr"] = 0.0,
        ["prayerAdjDhuhr"] = 0.0,
        ["prayerAdjAsr"] = 0.0,
        ["prayerAdjMaghrib"] = 0.0,
        ["prayerAdjIsha"] = 0.0,
        ["hijriAdj"] = 0.0,
        ["weather"] = true,
        ["weatherPos"] = "beside",
        ["weatherUnit"] = "auto",
        ["weatherMood"] = true,
        ["clockTap"] = true,
        ["timerSound"] = "bamboo",
        ["alarmSound"] = "horizon",
        ["snoozeMin"] = 9.0,
        ["cardShadow"] = 0.8,
        ["cardColors"] = false,
        ["sounds"] = true,
        ["soundVol"] = 0.8,
        ["soundMatch"] = false,
        ["soundFocus"] = false,
        ["soundDuck"] = "quiet",
        ["soundFx"] = true,
        ["rainIntensity"] = 0.48,
        ["rainMuffle"] = 5000.0,
        ["rainDynamic"] = true,
        ["rainGlass"] = true,
        ["glassAmount"] = 1.1,
        ["glassLook"] = 1.1,
        ["streamIntensity"] = 0.5,
        ["streamDistance"] = 6750.0,
        ["windIntensity"] = 0.5,
        ["wavesIntensity"] = 0.5,
        ["wavesDistance"] = 5000.0,
        ["brownSoft"] = 0.3,
        ["tips"] = true,
        ["workout"] = true,
        ["workoutFocus"] = "muscle",
        ["workoutGoal"] = 3.0,
        ["workoutRest"] = 1.0,
        ["workoutChime"] = true,
        ["workoutTable"] = false,
        ["workoutBmi"] = true,
    };

    public double Num(string key) => Get(key) switch { double d => d, int i => i, float f => f, bool b => b ? 1 : 0, string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) => p, _ => 0 };
    public bool Bool(string key) => Get(key) switch { bool b => b, double d => d != 0, string s => s == "true", _ => false };
    public string Str(string key) => Get(key)?.ToString() ?? "";

    public void Set(string key, object value)
    {
        _values[key] = Normalize(value);
        Changed?.Invoke(key);
    }

    /// <summary>Alle gesetzten Werte (ohne Standardwerte).</summary>
    public IReadOnlyDictionary<string, object> Snapshot() => new Dictionary<string, object>(_values);

    private static object Normalize(object o) => o switch { int i => (double)i, float f => (double)f, long l => (double)l, _ => o };
}

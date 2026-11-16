using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using DRect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

// =====================================================================================================
// Wo du bist (place.js): die Stadt aus GlassBar (WeatherService.EffectiveCity), einmal über den kostenlosen
// Geocoder von Open-Meteo gesucht und gemerkt; „Breitengrad, Längengrad“ geht auch, ganz ohne Suche.
// Gebetszeiten und die Wetterkarte arbeiten beide damit.
// =====================================================================================================

/// <summary>Der gemeinsame Ort für Gebetszeiten und Wetterkarte. Suchen nur im Hintergrund, nie im Render-Thread.</summary>
public static class StillPlace
{
    public sealed record Info(string Q, string Name, string Country, string Cc, double Lat, double Lng);

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex Coords = new(@"^\s*(-?\d{1,2}(?:\.\d+)?)\s*[,;\s]\s*(-?\d{1,3}(?:\.\d+)?)\s*$");
    private const string Key = "place";

    private static volatile Info? _place = StillState.Get<Info?>(Key, null);
    private static volatile string _status = "none";   // none | looking | ok | notfound | offline
    private static string _lastQuery = "\0";
    private static int _busy;
    private static long _nextAt;

    /// <summary>none | looking | ok | notfound | offline</summary>
    public static string Status => _status;
    public static Info? Place => _place;
    public static bool Ready => _status == "ok" && _place != null;

    /// <summary>Regelmäßig aus Update aufrufen (billig): prüft höchstens alle 30 s im Hintergrund, ob sich die Stadt geändert hat.</summary>
    public static void Poll(bool wanted)
    {
        if (!wanted) return;
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextAt)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Interlocked.Exchange(ref _nextAt, now + 30_000);
        _ = Task.Run(async () =>
        {
            try { await Resolve().ConfigureAwait(false); }
            catch { }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    private static async Task Resolve()
    {
        string q = (WeatherService.EffectiveCity() ?? "").Trim();
        if (q == _lastQuery) return;   // nichts Neues (offline: _lastQuery wird zurückgesetzt)
        _lastQuery = q;
        if (q.Length == 0) { _status = "none"; return; }

        var m = Coords.Match(q);
        if (m.Success)
        {
            double lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double lng = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(lat) > 90 || Math.Abs(lng) > 180) { _status = "notfound"; return; }
            _place = new Info(q, "", "", PrayTimes.ZoneCountry(), lat, lng);
            StillState.Set(Key, _place);
            _status = "ok";
            return;
        }
        if (_place is { } p && p.Q == q) { _status = "ok"; return; }

        _status = "looking";
        try
        {
            var found = await Geocode(q).ConfigureAwait(false);
            if (found != null)
            {
                _place = found;
                StillState.Set(Key, found);
            }
            _status = found != null ? "ok" : "notfound";
        }
        catch
        {
            // Gleich neuer Versuch (nach zwei Minuten)
            _lastQuery = "\0";
            Interlocked.Exchange(ref _nextAt, Environment.TickCount64 + 120_000);
            _status = "offline";
        }
    }

    // „Stadt, Land“ wählt die richtige, wo mehrere Orte gleich heißen.
    private static async Task<Info?> Geocode(string q)
    {
        var parts = q.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (parts.Length == 0) return null;
        string name = parts[0], hint = string.Join(' ', parts.Skip(1)).ToLowerInvariant();
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=10&language=en&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var list) || list.GetArrayLength() == 0) return null;

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        JsonElement pick = list[0];
        if (hint.Length > 0)
        {
            foreach (var x in list.EnumerateArray())
            {
                if (new[] { Str(x, "country"), Str(x, "country_code"), Str(x, "admin1") }
                    .Any(v => v.Length > 0 && hint.Contains(v.ToLowerInvariant())))
                { pick = x; break; }
            }
        }
        return new Info(q, Str(pick, "name"), Str(pick, "country"), Str(pick, "country_code").ToUpperInvariant(),
            pick.GetProperty("latitude").GetDouble(), pick.GetProperty("longitude").GetDouble());
    }
}

/// <summary>Verlauf der Gebete (gespeichert unter "prayer.record").</summary>
internal sealed class PrayerRecord
{
    public Dictionary<string, PrayerDayRecord> Days { get; set; } = new();
    public int Qada { get; set; }
    public int Best { get; set; }
}

/// <summary>Ein Gebetstag: S = on | late | missed je Gebet; Seen = lief während seiner Zeit; Said = schon gegebene Erinnerungen;
/// Q = in welchem Teil seiner Zeit gebetet (best | good | closing).</summary>
internal sealed class PrayerDayRecord
{
    public Dictionary<string, string> S { get; set; } = new();
    public Dictionary<string, int> Seen { get; set; } = new();
    public Dictionary<string, int> Said { get; set; } = new();
    public Dictionary<string, string> Q { get; set; } = new();
    public bool Excused { get; set; }
}

/// <summary>
/// Gebetszeiten (prayer.js): eine Pille folgt den fünf täglichen Gebeten – wessen Zeit es ist und wie viel bleibt,
/// eine Erinnerung zu Beginn und vor dem Ende (wenn noch nicht abgehakt), und ein Verlauf: pünktliche Gebete,
/// nachzuholende (Qada) und eine Serie von Tagen mit allen fünf pünktlich. Die Zeiten rechnet dieser PC aus (PrayTimes).
/// </summary>
public sealed class PrayerPill : Pill
{
    public override string Id => "prayer";

    private const double MinMs = 60000;
    private static readonly string[] Ids = { "fajr", "dhuhr", "asr", "maghrib", "isha" };
    private static readonly Dictionary<string, string> Names = new()
    {
        ["fajr"] = "Fadschr", ["dhuhr"] = "Dhuhr", ["asr"] = "Asr", ["maghrib"] = "Maghrib", ["isha"] = "Ischa",
    };
    private static readonly Dictionary<string, string> AdjKeys = new()
    {
        ["fajr"] = "prayerAdjFajr", ["dhuhr"] = "prayerAdjDhuhr", ["asr"] = "prayerAdjAsr", ["maghrib"] = "prayerAdjMaghrib", ["isha"] = "prayerAdjIsha",
    };
    private static readonly int[] Milestones = { 7, 30, 40, 100, 365 };
    private static readonly (string Kind, string Label)[] Picks = { ("best", "Optimal"), ("good", "Gut"), ("closing", "Spät"), ("qada", "Nachholen") };

    // Die drei Teile einer Gebetszeit und die Farbe der Pille, solange jeder dauert.
    private static readonly Vector3 SegBest = new(110 / 255f, 214 / 255f, 170 / 255f);
    private static readonly Vector3 SegGood = new(140 / 255f, 190 / 255f, 255 / 255f);
    private static readonly Vector3 SegClosing = new(255 / 255f, 186 / 255f, 104 / 255f);
    private static Vector3 SegColor(string? kind) => kind == "good" ? SegGood : kind == "closing" ? SegClosing : SegBest;

    private const string RecKey = "prayer.record";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- Tage und Fenster ----------

    private sealed class Seg { public string Kind = ""; public double From, To; }
    private sealed class Win { public string Id = ""; public double Start, End; public List<Seg> Segs = new(); }
    private sealed class PDay
    {
        public string Key = "";
        public PrayTimes.Day T, N;
        public Win[] Windows = Array.Empty<Win>();
        public bool Ok, Friday;
    }

    private readonly Dictionary<string, PrayTimes.Day> _cache = new();
    private PrayerRecord _rec = StillState.Get(RecKey, new PrayerRecord());
    private StillValues _set = new();

    // ---------- Zustand ----------

    private PDay? _p;
    private Win? _cur;
    private string _alertFor = "";
    private string _flash = "";
    private long _flashUntil;
    private string? _picking;
    private bool _alert;
    private double _alertAt;
    private double _tickAt = -1;
    private double _time;
    private UiContext? _c;

    // Was gezeigt wird (alle 0,5 s neu bestimmt)
    private string _mode = "looking";
    private string _value = "", _title = "Gebetszeiten", _meta = "", _sub = "", _msg = "";
    private string _streak = "", _qadaText = "", _hijri = "";
    private string? _seg;
    private bool _soon, _isNight, _ready;
    private double _frac;
    private readonly RowView[] _rows = new RowView[5];
    private readonly string?[] _dotS = new string?[5];
    private readonly string?[] _dotQ = new string?[5];
    private readonly bool[] _dotNow = new bool[5];
    private int _qada;
    private bool _excused;
    private bool _hasCur;

    private sealed class RowView
    {
        public string Name = "", Note = "", Time = "", State = "upcoming";
        public string? Q;
        public bool Now, Locked, Disabled;
        public readonly Label NameL = new(), NoteL = new(), TimeL = new();
        public readonly Label[] PickL = { new(), new(), new(), new() };
        public readonly DRect[] PickR = new DRect[4];
        public string[] Options = Array.Empty<string>();
    }

    // Weiche Übergänge
    private Vector3 _tp = SegBest;
    private float _night;
    private double _celebrateAt = -10;
    private readonly double[] _popAt = { -10, -10, -10, -10, -10 };

    // Maus
    private string _hover = "", _pressed = "";
    private int _hoverRow = -1;          // Zeile unter der Maus (für das Zeichnen, ohne Zeichenketten)
    private string? _hoverKind;          // Auswahl unter der Maus
    private Matrix3x2 _base = Matrix3x2.Identity;
    private DRect _madeUpR, _excusedR;

    // ---------- Zeichenmittel (je Gerätekontext) ----------

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private ID2D1PathGeometry? _moon, _check, _arc;
    private double _arcFrac = -1;
    private float _alpha = 1;

    private float _fmtK = -1;
    private string _fmtFont = "";
    private readonly Dictionary<int, IDWriteTextFormat> _formats = new();

    private readonly Label _valueL = new(), _titleL = new(), _metaL = new(), _subL = new(), _msgL = new();
    private readonly Label _streakL = new(), _qadaL = new(), _madeUpL = new(), _hijriL = new(), _excusedL = new();

    public PrayerPill()
    {
        for (int i = 0; i < 5; i++) _rows[i] = new RowView();
    }

    // ---------- Pille ----------

    public override bool Shown(UiContext c) => c.Settings.Bool("prayer");

    public override Vector2 CompactSize(UiContext c)
    {
        // .tp-compact: links 36, Wert, Punkte (11 Abstand, 5 × 6 + 4 × 4), rechts 15
        float k = c.K;
        float w = 36 + (_valueL.W / Math.Max(0.01f, k)) + 11 + 46 + 15;
        return new Vector2(Math.Max(40, w) * k, 40 * k);
    }

    public override Vector2 CardSize(UiContext c) => new(320 * c.K, (_ready ? 314 : 112) * c.K);

    public override bool Alert => _alert;

    public override bool Animating =>
        (_alert && _time - _alertAt < 8) || _time - _celebrateAt < 1.2 || Popping
        || _colorMoving || (_night > 0.001f && _night < 0.999f);

    private bool Popping
    {
        get
        {
            foreach (var t in _popAt) if (_time - t < 0.7) return true;
            return false;
        }
    }

    private bool _colorMoving;

    public override void Update(UiContext c, double dt)
    {
        _c = c;
        _set = c.Settings;
        _time = c.Time;
        bool on = _set.Bool("prayer");
        StillPlace.Poll(on || _set.Bool("weather"));
        if (!on) return;

        EnsureFormats(c);
        if (Hovered && _alert) _alert = false;   // Drüberfahren nimmt die Hervorhebung weg

        if (_tickAt < 0 || c.Time - _tickAt > 0.5)
        {
            _tickAt = c.Time;
            Check();
            Render();
        }
        // Eine geschlossene Karte legt angebotene Auswahl weg.
        if (_picking != null && Open < 0.05f) { _picking = null; Render(); }

        // Farbe und Tag/Nacht weich nachführen
        float a = 1 - MathF.Exp(-(float)dt * 6);
        var target = _soon ? SegClosing : SegColor(_seg);
        _tp += (target - _tp) * a;
        _colorMoving = Vector3.DistanceSquared(_tp, target) > 1e-6f;
        if (!_colorMoving) _tp = target;
        float nt = _isNight ? 1 : 0;
        _night += (nt - _night) * (1 - MathF.Exp(-(float)dt * 5));
        if (MathF.Abs(nt - _night) < 0.001f) _night = nt;

        // Breite der Pille hängt am Wert
        _valueL.Set(c.DWrite, _value, Fmt(c, 13.5f, 600));
    }

    // ---------- Wann: die heutigen Fenster ----------

    private (string Method, string Asr) Choice()
    {
        var d = PrayTimes.Defaults(StillPlace.Place?.Cc);
        string m = _set.Str("prayerMethod"), a = _set.Str("prayerAsr");
        return (m == "auto" || !PrayTimes.Methods.ContainsKey(m) ? d.Method : m, a == "auto" || (a != "hanafi" && a != "standard") ? d.Asr : a);
    }

    private static double NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTime Local(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
    private static DateTime NoonOf(DateTime d, int n) => d.Date.AddDays(n).AddHours(12);
    private static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", Inv);

    private PrayTimes.Day DayTimes(DateTime d)
    {
        var place = StillPlace.Place!;
        var ch = Choice();
        double hadj = _set.Num("hijriAdj");
        double a1 = _set.Num(AdjKeys["fajr"]), a2 = _set.Num(AdjKeys["dhuhr"]), a3 = _set.Num(AdjKeys["asr"]),
            a4 = _set.Num(AdjKeys["maghrib"]), a5 = _set.Num(AdjKeys["isha"]);
        string key = string.Create(Inv, $"{DayKey(d)}|{place.Lat}|{place.Lng}|{ch.Method}|{ch.Asr}|{a1},{a2},{a3},{a4},{a5}|{hadj}");
        if (_cache.TryGetValue(key, out var t)) return t;
        if (_cache.Count > 16) _cache.Clear();
        var opt = new PrayTimes.Options
        {
            Method = ch.Method, Asr = ch.Asr, Ramadan = PrayTimes.IsRamadan(d, hadj),
            AdjFajr = a1, AdjDhuhr = a2, AdjAsr = a3, AdjMaghrib = a4, AdjIsha = a5,
        };
        t = PrayTimes.Times(d.Year, d.Month, d.Day, place.Lat, place.Lng, opt);
        _cache[key] = t;
        return t;
    }

    /// <summary>Wann Schatten doppelt so lang sind: Ende der besten Asr-Zeit für Schulen, die mit einfacher Länge beginnen.</summary>
    private double Asr2(DateTime d, PrayTimes.Day t)
    {
        if (Choice().Asr == "hanafi") return t.Asr;
        var place = StillPlace.Place!;
        var opt = new PrayTimes.Options
        {
            Method = Choice().Method, Asr = "hanafi", Ramadan = PrayTimes.IsRamadan(d, _set.Num("hijriAdj")),
            AdjAsr = _set.Num(AdjKeys["asr"]),
        };
        return PrayTimes.Times(d.Year, d.Month, d.Day, place.Lat, place.Lng, opt).Asr;
    }

    /* Jede Gebetszeit in drei Teilen, wie die Schulen sie beschreiben: Beste Zeit, Gute Zeit und Endet bald
       (noch pünktlich – nachzuholen ist ein Gebet erst, wenn seine Zeit vorbei ist). */
    private List<Seg> Segments(Win w, PDay p, double asr2)
    {
        double s = w.Start, e = w.End, len = e - s;
        var t = p.T;
        bool hanafi = Choice().Asr == "hanafi";
        double night = p.N.Fajr - t.Sunset;
        double b0, b1, close;
        switch (w.Id)
        {
            case "fajr":
                // Früh im Dunkeln oder (Hanafi) wenn das Licht sich ausgebreitet hat; nie in den letzten Minuten.
                close = e - 20 * MinMs;
                (b0, b1) = hanafi ? (s + len / 2, close) : (s, s + len / 2);
                break;
            case "dhuhr":
                close = e - Math.Min(30 * MinMs, len * 0.2);
                (b0, b1) = (s, s + len * (hanafi ? 0.5 : 1.0 / 3));
                break;
            case "asr":
                // Die letzten Minuten, wenn die Sonne gelb wird; davor Beste Zeit, bis Schatten doppelt so lang sind.
                close = e - 20 * MinMs;
                (b0, b1) = (s, hanafi ? close : Math.Min(asr2, close));
                break;
            case "maghrib":
                // Beeilt; das letzte Drittel, wenn die Dämmerung schwindet.
                close = s + len * 2 / 3;
                (b0, b1) = (s, s + Math.Min(20 * MinMs, len / 3));
                break;
            default:
                // Ischa: Beste Zeit im ersten Drittel der Nacht, Endet bald nach Mitternacht.
                close = t.Sunset + night / 2;
                (b0, b1) = (s, t.Sunset + night / 3);
                break;
        }
        var cuts = new[] { s, b0, b1, close, e }.Select(v => Math.Clamp(v, s, Math.Max(s, e))).Distinct().OrderBy(v => v).ToArray();
        var res = new List<Seg>();
        for (int i = 0; i < cuts.Length - 1; i++)
        {
            double from = cuts[i], to = cuts[i + 1];
            if (to - from < MinMs) continue;
            double mid = (from + to) / 2;
            string kind = mid >= close ? "closing" : mid >= b0 && mid < b1 ? "best" : "good";
            if (res.Count > 0 && res[^1].Kind == kind) res[^1].To = to;
            else res.Add(new Seg { Kind = kind, From = from, To = to });
        }
        return res;
    }

    private static Seg? SegAt(Win w, double t)
    {
        foreach (var g in w.Segs) if (t >= g.From && t < g.To) return g;
        return null;
    }

    // Der Gebetstag läuft von Fadschr bis zum nächsten Fadschr; ein Ischa nach Mitternacht gehört noch zu seinem Tag.
    private PDay PrayerDay(double t)
    {
        var d = NoonOf(Local(t), 0);
        var T = DayTimes(d);
        if (t < T.Fajr)
        {
            d = NoonOf(d, -1);
            T = DayTimes(d);
        }
        var N = DayTimes(NoonOf(d, 1));
        double ishaEnd = _set.Str("prayerIsha") == "midnight" ? T.Sunset + (N.Fajr - T.Sunset) / 2 : N.Fajr;
        var p = new PDay
        {
            Key = DayKey(d), T = T, N = N,
            Windows = new[]
            {
                new Win { Id = "fajr", Start = T.Fajr, End = T.Sunrise },
                new Win { Id = "dhuhr", Start = T.Dhuhr, End = T.Asr },
                new Win { Id = "asr", Start = T.Asr, End = T.Maghrib },
                new Win { Id = "maghrib", Start = T.Maghrib, End = T.Isha },
                new Win { Id = "isha", Start = T.Isha, End = ishaEnd },
            },
        };
        p.Ok = double.IsFinite(T.Fajr) && double.IsFinite(T.Dhuhr) && double.IsFinite(T.Asr) && double.IsFinite(T.Maghrib)
            && double.IsFinite(T.Isha) && double.IsFinite(N.Fajr);
        p.Friday = p.Ok && Local(T.Dhuhr).DayOfWeek == DayOfWeek.Friday;
        if (p.Ok)
        {
            double asr2 = Asr2(d, T);
            foreach (var w in p.Windows) w.Segs = Segments(w, p, asr2);
        }
        return p;
    }

    private static string NameOf(string id, PDay? p) => id == "dhuhr" && p is { Friday: true } ? "Dschumu’a" : Names[id];

    // ---------- Verlauf ----------

    private void Save() => StillState.Set(RecKey, _rec);

    private PrayerDayRecord DayRec(string key)
    {
        if (!_rec.Days.TryGetValue(key, out var r)) _rec.Days[key] = r = new PrayerDayRecord();
        return r;
    }

    private void Prune()
    {
        var keys = _rec.Days.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (int i = 0; i < keys.Count - 400; i++) _rec.Days.Remove(keys[i]);
    }

    // Verpasst zählt ein Gebet nur, wenn seine Zeit endete, während der Hintergrund lief; eines, dessen Zeit bei
    // ausgeschaltetem PC verging, bleibt unmarkiert und zählt beim späteren Abhaken als pünktlich.
    private bool Settle(PDay p, double t)
    {
        var r = DayRec(p.Key);
        bool dirty = false;
        foreach (var w in p.Windows)
        {
            if (t >= w.Start && t < w.End && !r.Seen.ContainsKey(w.Id)) { r.Seen[w.Id] = 1; dirty = true; }
            if (t >= w.End && !r.S.ContainsKey(w.Id) && r.Seen.ContainsKey(w.Id) && !r.Excused)
            {
                r.S[w.Id] = "missed";
                _rec.Qada++;
                dirty = true;
            }
        }
        return dirty;
    }

    private static string DayStatus(PrayerDayRecord? r)
    {
        if (r == null || r.Excused) return "skip";
        bool all = true, broken = false;
        foreach (var id in Ids)
        {
            r.S.TryGetValue(id, out var v);
            if (v != "on") all = false;
            if (v is "missed" or "late") broken = true;
        }
        return all ? "full" : broken ? "broken" : "skip";
    }

    // Tage in Folge mit allen fünf pünktlich; Tage ohne Urteil (PC aus, entschuldigt) zählen nicht und brechen nicht.
    private int Streak()
    {
        int n = 0;
        foreach (var k in _rec.Days.Keys.OrderByDescending(k => k, StringComparer.Ordinal))
        {
            var st = DayStatus(_rec.Days[k]);
            if (st == "full") n++;
            else if (st == "broken") break;
        }
        if (n > _rec.Best) { _rec.Best = n; Save(); }
        return n;
    }

    // start: ein Gebet beginnt; soon: es endet bald (etwas leiser); done: alle fünf gebetet.
    private void Sound(string kind) => Tones.Play(_set.Str("prayerSound"), kind == "soon" ? 0.75f : 1f);

    private void Flash(string text, int ms)
    {
        _flash = text;
        _flashUntil = Environment.TickCount64 + ms;
    }

    private void SetAlert(bool on)
    {
        if (on && !_alert) _alertAt = _time;
        _alert = on;
    }

    // ---------- Abhaken ----------

    /* Was ein Gebet bis jetzt gewesen sein kann: die begonnenen Teile seiner Zeit in ihrer Reihenfolge, und nach
       ihrem Ende auch Qada. */
    private static string[] Choices(Win w, double t)
    {
        var kinds = new List<string>();
        foreach (var g in w.Segs) if (g.From <= t && !kinds.Contains(g.Kind)) kinds.Add(g.Kind);
        if (t >= w.End) kinds.Add("qada");
        return kinds.ToArray();
    }

    /* Klick auf ein begonnenes Gebet. Im ersten Teil seiner Zeit gibt es nichts anderes, also gleich abgehakt.
       Danach fragt die Zeile, wann gebetet wurde – und bietet nur an, was möglich ist. Abgehakt ist gesperrt. */
    private void Toggle(string id)
    {
        var p = _p;
        if (p is not { Ok: true }) return;
        double t = NowMs();
        var w = p.Windows.First(x => x.Id == id);
        var r = DayRec(p.Key);
        r.S.TryGetValue(id, out var cur);
        if (t < w.Start || cur == "on" || cur == "late") return;
        var options = Choices(w, t);
        if (options.Length > 1)
        {
            _picking = _picking == id ? null : id;
            Render();
            return;
        }
        _picking = null;
        Mark(id, "on", options.Length > 0 ? options[0] : null);
    }

    private void Pick(string id, string kind)
    {
        if (_p is not { Ok: true }) return;
        _picking = null;
        if (kind == "qada") Mark(id, "late", null);
        else Mark(id, "on", kind);
    }

    // q: in welchem Teil seiner Zeit gebetet. Ein nachzuholendes, das jetzt abgehakt wird, ist eines weniger.
    private void Mark(string id, string s, string? q)
    {
        var p = _p!;
        var r = DayRec(p.Key);
        var before = DayStatus(r);
        if (r.S.TryGetValue(id, out var old) && old == "missed") _rec.Qada = Math.Max(0, _rec.Qada - 1);
        r.S[id] = s;
        if (q != null) r.Q[id] = q;
        else r.Q.Remove(id);
        Save();
        _popAt[Array.IndexOf(Ids, id)] = _time;
        if (_cur != null && _cur.Id == id) SetAlert(false);
        if (before != "full" && DayStatus(r) == "full") Celebrate();
        Render();
    }

    private void Celebrate()
    {
        int n = Streak();
        bool milestone = Milestones.Contains(n);
        Flash(milestone ? $"{n} Tage in Folge. MashaAllah." : "Alle fünf gebetet. Alhamdulillah.", 9000);
        AnnounceFor = 7;
        Sound("done");
        _celebrateAt = _time;
        var b = Bounds;
        _c?.Bloom(b.Left + b.Width / 2, b.Top + b.Height / 2, milestone ? 2.2f : 1.4f);
    }

    private void Check()
    {
        if (!StillPlace.Ready) { _p = null; return; }
        double t = NowMs();
        var p = PrayerDay(t);
        _p = p;
        if (!p.Ok) return;
        var prev = PrayerDay(p.T.Fajr - 1);
        bool dirty = false;
        if (prev.Ok) dirty = Settle(prev, t) || dirty;
        dirty = Settle(p, t) || dirty;

        Win? cur = null;
        foreach (var w in p.Windows) if (t >= w.Start && t < w.End) { cur = w; break; }
        _cur = cur;
        var r = DayRec(p.Key);
        string tag = cur != null ? $"{p.Key}:{cur.Id}" : "";
        if (_alertFor.Length > 0 && (_alertFor != tag || cur == null || r.S.ContainsKey(cur.Id) || r.Excused))
        {
            SetAlert(false);
            _alertFor = "";
        }
        if (cur != null && !r.Excused)
        {
            if (!r.Said.ContainsKey($"{cur.Id}:start"))
            {
                r.Said[$"{cur.Id}:start"] = 1;
                dirty = true;
                // Ein lange vergangener Beginn (Hintergrund pausiert oder aus) vergeht still.
                if (_set.Bool("prayerStart") && t - cur.Start < 3 * MinMs)
                {
                    Flash($"{NameOf(cur.Id, p)} hat begonnen", 9000);
                    AnnounceFor = 7;
                    Sound("start");
                }
            }
            double before = _set.Num("prayerBefore") * MinMs;
            if (before > 0 && !r.S.ContainsKey(cur.Id) && t >= cur.End - before && !r.Said.ContainsKey($"{cur.Id}:end"))
            {
                r.Said[$"{cur.Id}:end"] = 1;
                dirty = true;
                _alertFor = tag;
                SetAlert(true);
                AnnounceFor = 8;
                Sound("soon");
            }
        }
        if (dirty)
        {
            Prune();
            Save();
        }
    }

    // ---------- Texte ----------

    private string FmtTime(double ms)
    {
        var d = Local(ms);
        if (_set.Bool("clock24")) return d.ToString("HH:mm", Inv);
        int h12 = d.Hour % 12 == 0 ? 12 : d.Hour % 12;
        return $"{h12}:{d.Minute:00} {(d.Hour < 12 ? "AM" : "PM")}";
    }

    private static string FmtLeft(double ms)
    {
        int m = Math.Max(1, (int)Math.Ceiling(ms / MinMs));
        return m >= 60 ? $"{m / 60}h{(m % 60 != 0 ? $" {m % 60}m" : "")}" : $"{m}m";
    }

    private static readonly Dictionary<string, string> Msg = new()
    {
        ["none"] = "Trage deine Stadt in den GlassBar-Einstellungen ein (oder Breitengrad, Längengrad).",
        ["looking"] = "Deine Stadt wird gesucht…",
        ["notfound"] = "Ort nicht gefunden. Versuch „Stadt, Land“ oder Breitengrad, Längengrad.",
        ["offline"] = "Der Kartendienst ist nicht erreichbar, um deine Stadt zu finden. Gleich neuer Versuch.",
        ["polar"] = "Die Sonne erreicht hier heute nicht die Gebetswinkel.",
    };
    private static readonly Dictionary<string, string> Compact = new()
    {
        ["none"] = "Stadt festlegen", ["looking"] = "Deine Stadt wird gesucht…", ["notfound"] = "Stadt nicht gefunden",
        ["offline"] = "Offline", ["polar"] = "Heute keine Zeiten",
    };

    private void Render()
    {
        double t = NowMs();
        var p = _p;
        _mode = !StillPlace.Ready ? StillPlace.Status : p is { Ok: false } ? "polar" : p != null ? "ok" : "looking";
        _ready = _mode == "ok";
        if (!_ready || p == null)
        {
            _value = Compact.GetValueOrDefault(_mode, "");
            _title = "Gebetszeiten";
            _meta = "";
            _msg = Msg.GetValueOrDefault(_mode, "");
            _soon = false;
            _seg = null;
            _isNight = false;
            _frac = 0;
            _hasCur = false;
            for (int i = 0; i < 5; i++) { _dotS[i] = null; _dotQ[i] = null; _dotNow[i] = false; }
            return;
        }

        var r = DayRec(p.Key);
        var cur = _cur;
        double before = _set.Num("prayerBefore") * MinMs;
        Win? nextW = null;
        foreach (var w in p.Windows) if (w.Start > t) { nextW = w; break; }
        bool nextDay = nextW == null;
        double nextStart = nextW?.Start ?? p.N.Fajr;
        string nextName = nextDay ? Names["fajr"] : NameOf(nextW!.Id, p);
        bool open = cur != null && !r.S.ContainsKey(cur.Id) && !r.Excused;
        bool soon = open && before > 0 && t >= cur!.End - before;
        var seg = cur != null ? SegAt(cur, t) : null;

        // Kompakt: wessen Zeit es ist und was bleibt, sonst das nächste.
        _value = open
            ? soon ? $"{NameOf(cur!.Id, p)} endet in {FmtLeft(cur.End - t)}" : $"{NameOf(cur!.Id, p)} · noch {FmtLeft(cur.End - t)}"
            : $"{nextName} um {FmtTime(nextStart)}";
        _soon = soon;
        // Die Farbe sagt, welcher Teil der Zeit es ist: beste, gute oder endet bald.
        _seg = open && seg != null ? seg.Kind : null;

        _frac = cur != null ? Math.Clamp((t - cur.Start) / (cur.End - cur.Start), 0, 1) : 0;
        _hasCur = cur != null;
        string nightId = cur != null ? cur.Id : nextDay ? "fajr" : nextW!.Id;
        _isNight = nightId is "fajr" or "maghrib" or "isha";

        for (int i = 0; i < 5; i++)
        {
            string id = Ids[i];
            _dotS[i] = r.S.GetValueOrDefault(id);
            _dotQ[i] = _dotS[i] == "on" ? r.Q.GetValueOrDefault(id) : null;
            _dotNow[i] = cur != null && cur.Id == id && !r.S.ContainsKey(id);
        }

        // Kopf der Karte: das aktuelle Gebet und sein Fenster, sonst das nächste.
        _title = cur != null ? NameOf(cur.Id, p) : nextName;
        _meta = cur != null ? $"bis {FmtTime(cur.End)}" : $"um {FmtTime(nextStart)}";
        string sub;
        if (Environment.TickCount64 < _flashUntil) sub = _flash;
        else if (r.Excused) sub = "Heute entschuldigt. Erinnerungen sind pausiert.";
        else if (open && soon) sub = $"Endet in {FmtLeft(cur!.End - t)}. Hake es ab, sobald du gebetet hast.";
        else if (open && seg != null)
        {
            sub = seg.Kind == "best" ? $"Beste Zeit · noch {FmtLeft(seg.To - t)}"
                : seg.Kind == "good" ? $"Gute Zeit · noch {FmtLeft(seg.To - t)}"
                : $"Endet bald · noch {FmtLeft(cur!.End - t)}";
        }
        else if (open) sub = $"noch {FmtLeft(cur!.End - t)}";
        else if (cur != null) sub = $"Gebetet. {nextName} um {FmtTime(nextStart)}.";
        else sub = $"{nextName} in {FmtLeft(nextStart - t)}";
        _sub = sub;

        for (int i = 0; i < 5; i++)
        {
            var w = p.Windows[i];
            var row = _rows[i];
            r.S.TryGetValue(w.Id, out var s);
            bool isNow = cur != null && cur.Id == w.Id;
            string st = t < w.Start ? "upcoming" : s ?? (t < w.End ? "open" : "unmarked");
            string? q = s == "on" ? r.Q.GetValueOrDefault(w.Id) : null;
            var nowSeg = st == "open" && isNow ? SegAt(w, t) : null;
            row.State = st;
            row.Now = isNow;
            row.Q = q ?? nowSeg?.Kind;
            row.Locked = s is "on" or "late";
            row.Disabled = st == "upcoming";
            row.Options = _picking == w.Id ? Choices(w, t) : Array.Empty<string>();
            row.Name = NameOf(w.Id, p);
            row.Time = FmtTime(w.Start);
            string note = "";
            if (st == "open")
            {
                note = isNow && before > 0 && t >= w.End - before ? $"endet in {FmtLeft(w.End - t)}"
                    : nowSeg != null && nowSeg.Kind != "good" ? (nowSeg.Kind == "best" ? "beste zeit" : "endet bald") : "jetzt";
            }
            else if (q == "best") note = "beste Zeit";
            else if (q == "closing") note = "spät";
            else if (st == "late") note = "nachgeholt";
            else if (st == "missed") note = "nachzuholen";
            else if (st == "unmarked") note = "nicht markiert";
            row.Note = note;
        }

        int n = Streak();
        int best = Math.Max(_rec.Best, n);
        _streak = n > 0 ? $"{n}-Tage-Serie{(best > n ? $" · Rekord {best}" : "")}" : best > 0 ? $"Längste Serie {best} Tage" : "Noch keine Serie";
        _qada = _rec.Qada;
        _qadaText = _rec.Qada > 0 ? $"{_rec.Qada} nachzuholen" : "Alles erledigt";
        _hijri = PrayTimes.Hijri(DateTime.Now, _set.Num("hijriAdj"));
        _excused = r.Excused;
    }

    // ---------- Maus ----------

    public override bool OnPointer(UiContext c, PointerEvent e)
    {
        _hoverRow = -1;
        _hoverKind = null;
        if (e.Kind == PointerKind.Leave) { _hover = ""; _pressed = ""; return false; }
        var b = Bounds;
        bool inside = e.Pos.X >= b.Left && e.Pos.X <= b.Right && e.Pos.Y >= b.Top && e.Pos.Y <= b.Bottom;
        _hover = inside && Open > 0.6f && _ready ? Target(c, e.Pos) : "";
        if (_hover.Length == 0) { _hoverRow = -1; _hoverKind = null; }
        switch (e.Kind)
        {
            case PointerKind.Down when e.Button == 0:
                _pressed = _hover;
                return inside && _hover.Length > 0;
            case PointerKind.Up when e.Button == 0:
                var hit = _pressed.Length > 0 && _pressed == _hover ? _pressed : "";
                _pressed = "";
                if (hit.Length > 0) { Click(hit); return true; }
                return false;
        }
        return false;
    }

    private string Target(UiContext c, Vector2 pos)
    {
        float k = c.K;
        var b = Bounds;
        float x = pos.X - b.Left, y = pos.Y - b.Top;
        if (Contains(_madeUpR, pos) && _qada > 0) return "madeUp";
        if (Contains(_excusedR, pos)) return "excused";
        for (int i = 0; i < 5; i++)
        {
            var row = _rows[i];
            float top = (86 + i * 29) * k;
            if (x < 10 * k || x > 310 * k || y < top || y > top + 28 * k) continue;
            _hoverRow = i;
            if (_picking == Ids[i])
                for (int j = 0; j < 4; j++)
                    if (row.Options.Contains(Picks[j].Kind) && Contains(row.PickR[j], pos))
                    {
                        _hoverKind = Picks[j].Kind;
                        return $"pick:{Ids[i]}:{Picks[j].Kind}";
                    }
            return row.Disabled ? "" : $"row:{Ids[i]}";
        }
        return "";
    }

    private static bool Contains(DRect r, Vector2 p) => r.Width > 0 && p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

    private void Click(string target)
    {
        if (target == "madeUp")
        {
            _rec.Qada = Math.Max(0, _rec.Qada - 1);
            Save();
            Render();
        }
        else if (target == "excused")
        {
            if (_p == null) return;
            var r = DayRec(_p.Key);
            r.Excused = !r.Excused;
            Save();
            if (r.Excused) SetAlert(false);
            Render();
        }
        else if (target.StartsWith("row:", StringComparison.Ordinal)) Toggle(target[4..]);
        else if (target.StartsWith("pick:", StringComparison.Ordinal))
        {
            var parts = target.Split(':');
            Pick(parts[1], parts[2]);
        }
    }

    // ---------- Zeichnen ----------

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static RoundedRectangle Rr(DRect r, float radius) =>
        new(new System.Drawing.RectangleF(r.Left, r.Top, r.Width, r.Height), radius, radius);

    private ID2D1SolidColorBrush Brush(Vector3 col, float a)
    {
        _brush!.Color = new Color4(col.X, col.Y, col.Z, Math.Clamp(a * _alpha, 0, 1));
        return _brush;
    }

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (!c.Settings.Bool("prayer")) return;
        EnsureDevice(ctx, c);
        EnsureFormats(c);
        float k = c.K, e = Math.Clamp(Open, 0, 1);
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var fg = c.Fg;
        var prevTransform = ctx.Transform;
        _base = prevTransform;
        try
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(b.Left, b.Top, b.Right, b.Bottom), AntialiasMode.PerPrimitive);

            // Hervorhebung: ein innerer Ring in der Farbe der Pille, der dreimal atmet.
            if (_alert)
            {
                double at = _time - _alertAt;
                float breathe = at < 7.8 ? 0.65f + 0.35f * MathF.Cos((float)(at / 2.6 * Math.PI * 2)) : 1;
                _alpha = breathe;
                float rr = Math.Min(b.Height / 2, Lerp(20, 26, e) * k);
                var rrect = Rr(new DRect(b.Left + 0.75f * k, b.Top + 0.75f * k, b.Width - 1.5f * k, b.Height - 1.5f * k), rr);
                ctx.DrawRoundedRectangle(rrect, Brush(_tp, 0.85f), 1.5f * k);
                var soft = Rr(new DRect(b.Left + 4 * k, b.Top + 4 * k, b.Width - 8 * k, b.Height - 8 * k), rr);
                ctx.DrawRoundedRectangle(soft, Brush(_tp, 0.12f), 6 * k);
            }

            DrawIcon(ctx, c, e);

            float oc = 1 - Smooth(0.05f, 0.4f, e);
            float ok = Smooth(0.4f, 0.9f, e);
            if (oc > 0.01f) DrawCompact(ctx, c, oc);
            if (ok > 0.01f) DrawCard(ctx, c, ok);

            ctx.PopAxisAlignedClip();
        }
        finally
        {
            ctx.Transform = prevTransform;
            _alpha = 1;
        }
    }

    private void DrawIcon(ID2D1DeviceContext ctx, UiContext c, float e)
    {
        float k = c.K;
        var b = Bounds;
        float size = Lerp(20, 30, e) * k, pad = Lerp(10, 16, e) * k;
        var m = Matrix3x2.CreateScale(size / 24) * Matrix3x2.CreateTranslation(b.Left + pad, b.Top + pad) * _base;
        ctx.Transform = m;
        _alpha = 1;
        var fg = c.Fg;
        // Ein Ring für den vergangenen Teil der Gebetszeit, um eine Sonne am Tag und eine Sichel von Maghrib bis Fadschr.
        ctx.DrawEllipse(new Ellipse(new Vector2(12, 12), 9, 9), Brush(fg, 0.16f), 2.2f);
        if (_hasCur && _frac > 0.002)
        {
            if (_frac >= 0.999) ctx.DrawEllipse(new Ellipse(new Vector2(12, 12), 9, 9), Brush(_tp, 1), 2.2f);
            else
            {
                if (Math.Abs(_arcFrac - _frac) > 1e-4 || _arc == null) BuildArc(c);
                ctx.DrawGeometry(_arc!, Brush(_tp, 1), 2.2f, _round);
            }
        }
        float n = _night;
        if (n < 0.999f)
        {
            float s = 1 - 0.6f * n;
            ctx.FillEllipse(new Ellipse(new Vector2(12, 12), 2.6f * s, 2.6f * s), Brush(_tp, 1 - n));
        }
        if (n > 0.001f)
        {
            float s = 0.6f + 0.4f * n;
            ctx.Transform = Matrix3x2.CreateRotation((1 - n) * -30 * MathF.PI / 180, new Vector2(12, 12))
                * Matrix3x2.CreateScale(s, new Vector2(12, 12)) * m;
            ctx.FillGeometry(_moon!, Brush(_tp, n));
        }
        ctx.Transform = _base;
    }

    private void BuildArc(UiContext c)
    {
        _arc?.Dispose();
        _arcFrac = _frac;
        double a = -Math.PI / 2 + _frac * Math.PI * 2;
        _arc = c.D2D.CreatePathGeometry();
        using var sink = _arc.Open();
        sink.BeginFigure(new Vector2(12, 3), FigureBegin.Hollow);
        sink.AddArc(new ArcSegment
        {
            Point = new Vector2(12 + 9 * (float)Math.Cos(a), 12 + 9 * (float)Math.Sin(a)),
            Size = new Size(9, 9),
            RotationAngle = 0,
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = _frac > 0.5 ? ArcSize.Large : ArcSize.Small,
        });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
    }

    private void DrawCompact(ID2D1DeviceContext ctx, UiContext c, float oc)
    {
        float k = c.K;
        var b = Bounds;
        var fg = c.Fg;
        _alpha = oc;
        float x = b.Left + 36 * k + (1 - oc) * 6 * k;
        float midY = b.Top + 20 * k;
        var l = _valueL.Set(c.DWrite, _value, Fmt(c, 13.5f, 600));
        ctx.DrawTextLayout(new Vector2(x, midY - _valueL.H / 2), l, Brush(fg, 0.92f));
        x += _valueL.W + 11 * k;
        for (int i = 0; i < 5; i++)
        {
            float cx = x + 3 * k + i * 10 * k;
            float sc = 1;
            double ct = _time - _celebrateAt - i * 0.09;
            if (ct > 0 && ct < 0.52) sc = 1 + 0.9f * MathF.Sin((float)(ct / 0.52 * Math.PI));
            float r = 3 * k * sc;
            var el = new Ellipse(new Vector2(cx, midY), r, r);
            var dc = SegColor(_dotQ[i]);
            if (_dotNow[i]) ctx.DrawEllipse(new Ellipse(el.Point, r - 0.65f * k, r - 0.65f * k), Brush(_tp, 1), 1.3f * k);
            else if (_dotS[i] == "on")
            {
                ctx.FillEllipse(new Ellipse(el.Point, r * 1.9f, r * 1.9f), Brush(dc, 0.14f));
                ctx.FillEllipse(el, Brush(dc, 1));
            }
            else if (_dotS[i] == "late") ctx.FillEllipse(el, Brush(fg, 0.5f));
            else if (_dotS[i] == "missed") ctx.DrawEllipse(new Ellipse(el.Point, r - 0.65f * k, r - 0.65f * k), Brush(SegClosing, 0.9f), 1.3f * k);
            else ctx.FillEllipse(el, Brush(fg, 0.18f));
        }
    }

    private void DrawCard(ID2D1DeviceContext ctx, UiContext c, float ok)
    {
        float k = c.K;
        var b = Bounds;
        var fg = c.Fg;
        _alpha = ok;
        float ox = b.Left, oy = b.Top + (1 - ok) * 6 * k;
        var dw = c.DWrite;

        // Kopf: Titel links, Zeit rechts, auf einer Grundlinie
        var tl = _titleL.Set(dw, _title, Fmt(c, 14.5f, 600));
        float baseY = oy + 18 * k + _titleL.Base;
        ctx.DrawTextLayout(new Vector2(ox + 58 * k, baseY - _titleL.Base), tl, Brush(fg, 0.96f));
        if (_meta.Length > 0)
        {
            var ml = _metaL.Set(dw, _meta, Fmt(c, 12, 550));
            ctx.DrawTextLayout(new Vector2(ox + 302 * k - _metaL.W, baseY - _metaL.Base), ml, Brush(fg, 0.55f));
        }

        if (!_ready)
        {
            // Bis es einen Ort gibt, erklärt die Karte nur, was zu tun ist.
            var msg = _msgL.Set(dw, _msg, Fmt(c, 12, 500), 244 * k);
            ctx.DrawTextLayout(new Vector2(ox + 58 * k, oy + 44 * k), msg, Brush(fg, 0.62f));
            _madeUpR = _excusedR = default;
            return;
        }

        // Balken: Beste, Gute und Endet-bald-Zeit über die Gebetszeit, vorn blass, hinten hell, mit einer Marke für jetzt.
        float bx = ox + 58 * k, bw = 244 * k, by = oy + 46 * k, bh = 6 * k;
        var barR = Rr(new DRect(bx, by, bw, bh), bh / 2);
        ctx.FillRoundedRectangle(barR, Brush(fg, 0.08f));
        if (_hasCur && _cur != null && _cur.End > _cur.Start)
        {
            var cur = _cur;
            double len = cur.End - cur.Start;
            float doneX = bx + bw * (float)_frac;
            using var geo = c.D2D.CreateRoundedRectangleGeometry(barR);
            var lp = new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(bx, by, bx + bw, by + bh),
                GeometricMask = geo,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                LayerOptions = LayerOptions1.None,
            };
            ctx.PushLayer(ref lp, null);
            for (int i = 0; i < cur.Segs.Count; i++)
            {
                var g = cur.Segs[i];
                float x0 = bx + bw * (float)((g.From - cur.Start) / len), x1 = bx + bw * (float)((g.To - cur.Start) / len);
                if (i > 0) x0 += 1.5f * k;
                if (x1 <= x0) continue;
                var col = SegColor(g.Kind);
                ctx.FillRectangle(new DRect(x0, by, x1 - x0, bh), Brush(col, 0.3f));
                float d1 = Math.Min(x1, doneX);
                if (d1 > x0) ctx.FillRectangle(new DRect(x0, by, d1 - x0, bh), Brush(col, 0.7f));
            }
            ctx.PopLayer();
            // Marke für jetzt
            var mp = new Vector2(doneX, by + bh / 2);
            ctx.FillEllipse(new Ellipse(mp, 9 * k, 9 * k), Brush(new Vector3(1), 0.08f));
            ctx.FillEllipse(new Ellipse(mp, 7 * k, 7 * k), Brush(Vector3.Zero, 0.22f));
            ctx.FillEllipse(new Ellipse(mp, 5 * k, 5 * k), Brush(new Vector3(1), 1));
        }

        // Unterzeile
        var sl = _subL.Set(dw, _sub, Fmt(c, 12, 500));
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(ox + 58 * k, oy + 58 * k, ox + 302 * k, oy + 82 * k), AntialiasMode.Aliased);
        ctx.DrawTextLayout(new Vector2(ox + 58 * k, oy + 62 * k), sl, _alert ? Brush(_tp, 1) : Brush(fg, 0.55f));
        ctx.PopAxisAlignedClip();

        // Zeilen
        for (int i = 0; i < 5; i++) DrawRow(ctx, c, i, ox, oy);

        // Trennlinie und Fuß
        ctx.FillRectangle(new DRect(ox + 18 * k, oy + 240 * k, 284 * k, Math.Max(1, k)), Brush(fg, 0.08f));
        float fx = ox + 18 * k, fr = ox + 304 * k;
        float r1 = oy + 250 * k, r2 = oy + 278 * k, rowH = 22 * k;

        var stl = _streakL.Set(dw, _streak, Fmt(c, 12, 600));
        ctx.DrawTextLayout(new Vector2(fx, r1 + rowH / 2 - _streakL.H / 2), stl, Brush(fg, 0.85f));
        float right = fr;
        if (_qada > 0)
        {
            _madeUpR = MiniButton(ctx, c, _madeUpL, "−1", right, r1, false, _hover == "madeUp");
            right = _madeUpR.Left - 8 * k;
        }
        else _madeUpR = default;
        var ql = _qadaL.Set(dw, _qadaText, Fmt(c, 12, 500));
        ctx.DrawTextLayout(new Vector2(right - _qadaL.W, r1 + rowH / 2 - _qadaL.H / 2), ql, Brush(fg, 0.55f));

        var hl = _hijriL.Set(dw, _hijri, Fmt(c, 12, 500));
        ctx.DrawTextLayout(new Vector2(fx, r2 + rowH / 2 - _hijriL.H / 2), hl, Brush(fg, 0.55f));
        _excusedR = MiniButton(ctx, c, _excusedL, "Heute entschuldigt", fr, r2, _excused, _hover == "excused");
    }

    private DRect MiniButton(ID2D1DeviceContext ctx, UiContext c, Label label, string text, float right, float top, bool on, bool hover)
    {
        float k = c.K;
        var fg = c.Fg;
        var l = label.Set(c.DWrite, text, Fmt(c, 11.5f, 600));
        float w = label.W + 20 * k, h = 22 * k;
        var r = new DRect(right - w, top, w, h);
        var rr = Rr(r, h / 2);
        if (on)
        {
            ctx.FillRoundedRectangle(rr, Brush(_tp, 0.24f));
            ctx.DrawRoundedRectangle(Rr(new DRect(r.Left + 0.5f, r.Top + 0.5f, r.Width - 1, r.Height - 1), h / 2), Brush(_tp, 0.35f), 1);
        }
        else ctx.FillRoundedRectangle(rr, Brush(fg, hover ? 0.14f : 0.08f));
        ctx.DrawTextLayout(new Vector2(r.Left + 10 * k, top + h / 2 - label.H / 2), l, Brush(fg, on || hover ? 1 : 0.72f));
        return r;
    }

    private void DrawRow(ID2D1DeviceContext ctx, UiContext c, int i, float ox, float oy)
    {
        float k = c.K;
        var fg = c.Fg;
        var row = _rows[i];
        string id = Ids[i];
        float x0 = ox + 10 * k, x1 = ox + 310 * k, top = oy + (86 + i * 29) * k, h = 28 * k, mid = top + h / 2;
        bool hover = _hoverRow == i && _hover.Length > 0;
        bool canHover = !row.Disabled && !row.Locked;
        var rc = SegColor(row.Q);
        var rr = Rr(new DRect(x0, top, x1 - x0, h), 10 * k);
        if (row.Now)
        {
            ctx.FillRoundedRectangle(rr, Brush(_tp, 0.1f));
            ctx.DrawRoundedRectangle(Rr(new DRect(x0 + 0.5f, top + 0.5f, x1 - x0 - 1, h - 1), 10 * k), Brush(_tp, 0.2f), 1);
        }
        else if (hover && canHover) ctx.FillRoundedRectangle(rr, Brush(fg, 0.07f));

        // Haken
        float cs = 18 * k / 20;   // SVG 20er-Kasten auf 18 k
        var cc = new Vector2(x0 + 10 * k + 9 * k, mid);
        float pop = 1;
        double pt = _time - _popAt[i];
        if (pt >= 0 && pt < 0.6)
        {
            float u = (float)(pt / 0.6);
            pop = u < 0.33f ? Lerp(1, 0.78f, u / 0.33f) : u < 0.66f ? Lerp(0.78f, 1.18f, (u - 0.33f) / 0.33f) : Lerp(1.18f, 1, (u - 0.66f) / 0.34f);
        }
        float rad = 8.6f * cs * pop;
        bool marked = row.State is "on" or "late";
        if (marked)
        {
            if (row.State == "on") ctx.FillEllipse(new Ellipse(cc, rad * 1.45f, rad * 1.45f), Brush(rc, 0.12f));
            ctx.FillEllipse(new Ellipse(cc, rad, rad), row.State == "late" ? Brush(fg, 0.34f) : Brush(rc, 1));
            var prev = ctx.Transform;
            ctx.Transform = Matrix3x2.CreateTranslation(-10, -10) * Matrix3x2.CreateScale(cs * pop) * Matrix3x2.CreateTranslation(cc) * prev;
            ctx.DrawGeometry(_check!, Brush(new Vector3(1), 1), 2.1f, _round);
            ctx.Transform = prev;
        }
        else
        {
            if (hover && canHover) ctx.FillEllipse(new Ellipse(cc, rad, rad), Brush(fg, 0.08f));
            if (row.State == "missed")
            {
                // gestrichelter Ring in Orange
                for (int d = 0; d < 12; d++)
                {
                    float a0 = d * MathF.PI * 2 / 12, a1 = a0 + MathF.PI * 2 / 12 * 0.53f;
                    ctx.DrawLine(cc + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * rad, cc + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * rad,
                        Brush(SegClosing, 0.9f), 1.3f * cs);
                }
            }
            else
            {
                float ra = row.Disabled ? 0.22f : hover && canHover ? 0.85f : 0.5f;
                ctx.DrawEllipse(new Ellipse(cc, rad, rad), Brush(fg, ra), 1.3f * cs);
            }
        }

        // Name, Hinweis, Zeit
        var nl = row.NameL.Set(c.DWrite, row.Name, Fmt(c, 13.5f, 550));
        float nameX = x0 + 10 * k + 18 * k + 10 * k;
        ctx.DrawTextLayout(new Vector2(nameX, mid - row.NameL.H / 2), nl, Brush(fg, row.Disabled ? 0.45f : 0.88f));

        bool picking = _picking == id && row.Options.Length > 0;
        if (!picking)
        {
            var tl = row.TimeL.Set(c.DWrite, row.Time, Fmt(c, 13.5f, 500));
            float timeR = x1 - 10 * k, timeW = Math.Max(4.4f * 13.5f * k, row.TimeL.W);
            ctx.DrawTextLayout(new Vector2(timeR - row.TimeL.W, mid - row.TimeL.H / 2), tl, Brush(fg, 0.6f));
            if (row.Note.Length > 0)
            {
                var nol = row.NoteL.Set(c.DWrite, row.Note, Fmt(c, 11.5f, 550));
                float noteR = timeR - timeW - 10 * k;
                var noteBrush = row.State == "missed" ? Brush(SegClosing, 0.9f)
                    : row.Now ? Brush(rc, 1)
                    : row.Q is "best" or "closing" ? Brush(rc, 0.85f)
                    : Brush(fg, 0.45f);
                ctx.DrawTextLayout(new Vector2(noteR - row.NoteL.W, mid - row.NoteL.H / 2), nol, noteBrush);
            }
            for (int j = 0; j < 4; j++) row.PickR[j] = default;
            return;
        }

        // Wann gebetet: kleine Auswahl über Hinweis und Zeit, in der Reihenfolge, in der die Teile kamen.
        float right = x1 - 6 * k;
        for (int oi = row.Options.Length - 1; oi >= 0; oi--)
        {
            string kind = row.Options[oi];
            int j = Array.FindIndex(Picks, p => p.Kind == kind);
            if (j < 0) continue;
            var pl = row.PickL[j].Set(c.DWrite, Picks[j].Label, Fmt(c, 11.5f, 600));
            float w = row.PickL[j].W + 18 * k, ph = 22 * k;
            var r = new DRect(right - w, mid - ph / 2, w, ph);
            row.PickR[j] = r;
            var pc = kind == "qada" ? fg : SegColor(kind);
            bool ph0 = _hoverRow == i && _hoverKind == kind;
            var prr = Rr(r, ph / 2);
            ctx.FillRoundedRectangle(prr, Brush(pc, ph0 ? 0.3f : 0.14f));
            ctx.DrawRoundedRectangle(Rr(new DRect(r.Left + 0.5f, r.Top + 0.5f, r.Width - 1, r.Height - 1), ph / 2), Brush(pc, 0.32f), 1);
            ctx.DrawTextLayout(new Vector2(r.Left + 9 * k, mid - row.PickL[j].H / 2), pl, Brush(fg, 0.92f));
            right = r.Left - 4 * k;
        }
    }

    // ---------- Mittel ----------

    private void EnsureDevice(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });

        // Sichel: Kreis (12, 12, 4,3) ohne Kreis (14,4, 10,2, 3,7)
        using (var a = c.D2D.CreateEllipseGeometry(new Ellipse(new Vector2(12, 12), 4.3f, 4.3f)))
        using (var cut = c.D2D.CreateEllipseGeometry(new Ellipse(new Vector2(14.4f, 10.2f), 3.7f, 3.7f)))
        {
            _moon = c.D2D.CreatePathGeometry();
            using var sink = _moon.Open();
            a.CombineWithGeometry(cut, CombineMode.Exclude, sink);
            sink.Close();
        }

        // Haken (M6.1 10.3 l2.6 2.6 5.3-5.6)
        _check = c.D2D.CreatePathGeometry();
        using (var sink = _check.Open())
        {
            sink.BeginFigure(new Vector2(6.1f, 10.3f), FigureBegin.Hollow);
            sink.AddLine(new Vector2(8.7f, 12.9f));
            sink.AddLine(new Vector2(14.0f, 7.3f));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        _arcFrac = -1;
    }

    private void EnsureFormats(UiContext c)
    {
        string font = c.Settings.Str("font");
        if (c.K == _fmtK && font == _fmtFont) return;
        _fmtK = c.K;
        _fmtFont = font;
        // Die Formate gehören UiFonts (Speicher dort); hier nur die Verweise vergessen.
        _formats.Clear();
    }

    private IDWriteTextFormat Fmt(UiContext c, float size, int weight)
    {
        int key = (int)(size * 10) * 1000 + weight;
        if (!_formats.TryGetValue(key, out var f))
        {
            f = c.Fonts.Format(c, size * c.K, weight);
            f.WordWrapping = WordWrapping.NoWrap;
            _formats[key] = f;
        }
        return f;
    }

    private void DisposeDevice()
    {
        _brush?.Dispose(); _brush = null;
        _round?.Dispose(); _round = null;
        _moon?.Dispose(); _moon = null;
        _check?.Dispose(); _check = null;
        _arc?.Dispose(); _arc = null;
        _ctxPtr = 0;
    }

    public override void Dispose()
    {
        DisposeDevice();
        foreach (var l in new[] { _valueL, _titleL, _metaL, _subL, _msgL, _streakL, _qadaL, _madeUpL, _hijriL, _excusedL }) l.Dispose();
        foreach (var r in _rows)
        {
            r.NameL.Dispose(); r.NoteL.Dispose(); r.TimeL.Dispose();
            foreach (var p in r.PickL) p.Dispose();
        }
    }

    /// <summary>Ein Textbaustein, der nur bei Änderung neu gesetzt wird.</summary>
    private sealed class Label : IDisposable
    {
        private IDWriteTextLayout? _l;
        private string? _text;
        private IDWriteTextFormat? _fmt;
        private float _maxW;
        public float W, H, Base;

        public IDWriteTextLayout Set(IDWriteFactory dw, string text, IDWriteTextFormat fmt, float maxW = 0)
        {
            if (_l != null && text == _text && ReferenceEquals(fmt, _fmt) && maxW == _maxW) return _l;
            _l?.Dispose();
            _text = text; _fmt = fmt; _maxW = maxW;
            _l = dw.CreateTextLayout(text, fmt, maxW > 0 ? maxW : 8192, 8192);
            _l.WordWrapping = maxW > 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;
            var m = _l.Metrics;
            W = m.WidthIncludingTrailingWhitespace;
            H = m.Height;
            var lines = _l.LineMetrics;
            Base = lines.Length > 0 ? lines[0].Baseline : 0;
            return _l;
        }

        public void Dispose() { _l?.Dispose(); _l = null; _text = null; }
    }
}

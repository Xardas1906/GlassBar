using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using DRect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Das Wetter neben dem Datum (weather.js): Fährt die Maus über die Wetteranzeige der Datumszeile, öffnet sich eine
/// Glaskarte mit jetzt, heute und den nächsten Stunden. Die Werte kommen von Open-Meteo (kostenlos, ohne Konto) für den
/// Ort aus <see cref="StillPlace"/>, alle halbe Stunde, im Hintergrund abgerufen und in StillState zwischengespeichert.
/// </summary>
public sealed class WeatherCard : IOverlay, IDisposable
{
    public int Z => 60;

    private const double MinMs = 60000, HourMs = 60 * MinMs;
    private const string Key = "weather.forecast";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- Messwerte ----------

    public sealed record NowW(double T, double Feels, int Code, double? Cloud, double? Vis, bool Wet);
    public sealed record HourW(double At, double T, int Code, double? Rain, double? Cloud, double? Vis, bool Wet);
    public sealed record DayW(double Day, double Hi, double Lo, double? Rain, double Rise, double Set);
    public sealed record Wx(double Lat, double Lng, double At, NowW Now, HourW[] Hours, DayW[] Days);

    private static volatile Wx? _wx = LoadCached();
    private static int _fetching;
    private static volatile bool _failed;
    private static long _retryAt;

    private static Wx? LoadCached()
    {
        try { return StillState.Get<Wx?>(Key, null); } catch { return null; }
    }

    // ---------- Was die Codes bedeuten (WMO) ----------

    private static readonly Dictionary<int, (string Kind, string Label)> Codes = new()
    {
        [0] = ("clear", "Klar"), [1] = ("partly", "Überwiegend klar"), [2] = ("partly", "Teilweise bewölkt"), [3] = ("cloudy", "Bewölkt"),
        [45] = ("fog", "Nebel"), [48] = ("fog", "Gefrierender Nebel"),
        [51] = ("drizzle", "Leichter Nieselregen"), [53] = ("drizzle", "Nieselregen"), [55] = ("drizzle", "Starker Nieselregen"),
        [56] = ("drizzle", "Gefrierender Nieselregen"), [57] = ("drizzle", "Gefrierender Nieselregen"),
        [61] = ("rain", "Leichter Regen"), [63] = ("rain", "Regen"), [65] = ("rain", "Starker Regen"), [66] = ("rain", "Gefrierender Regen"), [67] = ("rain", "Gefrierender Regen"),
        [71] = ("snow", "Leichter Schneefall"), [73] = ("snow", "Schnee"), [75] = ("snow", "Starker Schneefall"), [77] = ("snow", "Schneegriesel"),
        [80] = ("rain", "Leichte Schauer"), [81] = ("rain", "Schauer"), [82] = ("rain", "Starke Schauer"), [85] = ("snow", "Schneeschauer"), [86] = ("snow", "Starke Schneeschauer"),
        [95] = ("storm", "Gewitter"), [96] = ("storm", "Gewitter, Hagel"), [99] = ("storm", "Gewitter, Hagel"),
    };

    // Der Himmel nach seinem Wolkenanteil, in den Worten der Vorhersagen.
    private static (string, string) Sky(double cloud) =>
        cloud < 20 ? ("clear", "Klar") : cloud < 40 ? ("partly", "Überwiegend klar") : cloud < 70 ? ("partly", "Teilweise bewölkt")
        : cloud < 90 ? ("cloudy", "Überwiegend bewölkt") : ("cloudy", "Bewölkt");

    /* Vorhersagemodelle melden Nebel und leichten Regen bereitwilliger, als sie kommen; die brauchen Rückhalt: Nebel nur,
       wenn er wirklich dicht ist, Regen oder Schnee nur, wenn etwas fällt. Sonst sagt der Himmel, wie er aussieht. */
    private static (string Kind, string Label) Condition(int code, double? cloud, double? vis, bool wet)
    {
        var c = Codes.TryGetValue(code, out var x) ? x : Codes[3];
        if (cloud == null || c.Kind == "storm") return c;
        if (c.Kind == "fog" && vis is < 200) return c;
        if (c.Kind is "drizzle" or "rain" or "snow" && wet) return c;
        return Sky(cloud.Value);
    }

    // ---------- Abrufen ----------

    private static double Round2(double v) => Math.Round(v * 100) / 100;
    private static double NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Jede halbe Stunde, sofort für einen neuen Ort, nie zweimal gleichzeitig.
    private static bool Due()
    {
        if (Volatile.Read(ref _fetching) == 1 || !StillPlace.Ready) return false;
        if (Environment.TickCount64 < Interlocked.Read(ref _retryAt)) return false;
        var p = StillPlace.Place!;
        var wx = _wx;
        return wx == null || wx.Lat != Round2(p.Lat) || wx.Lng != Round2(p.Lng) || NowMs() - wx.At > 30 * MinMs;
    }

    private static void Refresh()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        var p = StillPlace.Place!;
        double lat = Round2(p.Lat), lng = Round2(p.Lng);   // auf etwa einen Kilometer: genau genug fürs Wetter
        _ = Task.Run(async () =>
        {
            try
            {
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(Inv)}&longitude={lng.ToString(Inv)}"
                    + "&current=temperature_2m,apparent_temperature,weather_code,cloud_cover,visibility,precipitation"
                    + "&hourly=temperature_2m,weather_code,precipitation_probability,cloud_cover,visibility,precipitation"
                    + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset"
                    + "&timezone=auto&forecast_days=2&timeformat=unixtime";
                using var doc = JsonDocument.Parse(await StillPlace.Http.GetStringAsync(url).ConfigureAwait(false));
                var wx = Keep(doc.RootElement, lat, lng);
                _wx = wx;
                StillState.Set(Key, wx);
                _failed = false;
                Interlocked.Exchange(ref _retryAt, 0);
            }
            catch
            {
                _failed = true;
                Interlocked.Exchange(ref _retryAt, Environment.TickCount64 + 5 * 60_000);
            }
            finally { Volatile.Write(ref _fetching, 0); }
        });
    }

    private static double? N(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
    // Fehlende Werte als 0 (NaN ließe sich nicht als JSON speichern)
    private static double D(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
    private static int I(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)e.GetDouble() : 3;

    private static Wx Keep(JsonElement j, double lat, double lng)
    {
        var c = j.GetProperty("current");
        var h = j.GetProperty("hourly");
        var d = j.GetProperty("daily");
        // wet: es fällt wirklich etwas (in der letzten Viertelstunde bzw. in der Stunde)
        var now = new NowW(D(c.GetProperty("temperature_2m")), D(c.GetProperty("apparent_temperature")), I(c.GetProperty("weather_code")),
            N(c.GetProperty("cloud_cover")), N(c.GetProperty("visibility")), (N(c.GetProperty("precipitation")) ?? 0) > 0);
        var ht = h.GetProperty("time");
        var hours = new HourW[ht.GetArrayLength()];
        for (int i = 0; i < hours.Length; i++)
        {
            hours[i] = new HourW(D(ht[i]) * 1000, D(h.GetProperty("temperature_2m")[i]), I(h.GetProperty("weather_code")[i]),
                N(h.GetProperty("precipitation_probability")[i]), N(h.GetProperty("cloud_cover")[i]), N(h.GetProperty("visibility")[i]),
                (N(h.GetProperty("precipitation")[i]) ?? 0) >= 0.1);
        }
        var dt = d.GetProperty("time");
        var days = new DayW[dt.GetArrayLength()];
        for (int i = 0; i < days.Length; i++)
        {
            days[i] = new DayW(D(dt[i]) * 1000, D(d.GetProperty("temperature_2m_max")[i]), D(d.GetProperty("temperature_2m_min")[i]),
                N(d.GetProperty("precipitation_probability_max")[i]), D(d.GetProperty("sunrise")[i]) * 1000, D(d.GetProperty("sunset")[i]) * 1000);
        }
        return new Wx(lat, lng, NowMs(), now, hours, days);
    }

    // ---------- Die Werte lesen ----------

    private static readonly HashSet<string> FCountries = new() { "US", "LR", "MM", "BS", "BZ", "KY", "PW", "FM", "MH" };
    private bool _fahrenheit;
    private string Deg(double c) => $"{Math.Round(_fahrenheit ? c * 9 / 5 + 32 : c, MidpointRounding.AwayFromZero).ToString("0", Inv)}°";

    private static DayW DayOf(Wx wx, double t)
    {
        foreach (var d in wx.Days) if (t >= d.Day && t < d.Day + 24 * HourMs) return d;
        return wx.Days[0];
    }
    private static bool IsNight(Wx wx, double t)
    {
        if (wx.Days.Length == 0) return false;
        var d = DayOf(wx, t);
        return t < d.Rise || t >= d.Set;
    }

    // Der letzte Messwert, solange er frisch ist; danach die Vorhersage für diese Stunde.
    private static NowW? Current(Wx wx, double t)
    {
        if (t - wx.At < 45 * MinMs) return wx.Now;
        foreach (var x in wx.Hours)
            if (t >= x.At && t < x.At + HourMs) return new NowW(x.T, x.T, x.Code, x.Cloud, x.Vis, x.Wet);
        return null;
    }

    private string FmtHour(double ms, bool c24)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
        if (c24) return d.ToString("HH", Inv);
        return $"{(d.Hour % 12 == 0 ? 12 : d.Hour % 12)} {(d.Hour < 12 ? "AM" : "PM")}";
    }
    private string FmtTime(double ms, bool c24)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
        if (c24) return d.ToString("HH:mm", Inv);
        return $"{(d.Hour % 12 == 0 ? 12 : d.Hour % 12)}:{d.Minute:00} {(d.Hour < 12 ? "AM" : "PM")}";
    }
    private static string Ago(double at)
    {
        long m = (long)Math.Round((NowMs() - at) / MinMs);
        return m < 1 ? L.T("gerade eben") : m < 60 ? L.F("vor {0} Min.", m) : L.F("vor {0} Std.", Math.Round(m / 60.0));
    }

    // ---------- Zustand der Karte ----------

    private bool _show;
    private float _openT;              // 0..1, Fortschritt des Öffnens
    private double _leftAt = -1;       // wann die Maus die Anzeige verlassen hat
    private bool _hover;
    private double _time;
    private bool _still;
    private double _stateAt = -10;
    private Wx? _stateFor;

    private DRect _zone, _card;
    private string? _placeName;
    private string _placeText = "";
    private float _cardH;

    // Texte
    private string _kind = "clear";
    private bool _night;
    private string _temp = "", _label = "", _feels = "", _hilo = "", _line = "", _foot = "";
    private readonly string[] _hTime = new string[6], _hTemp = new string[6], _hKind = new string[6];
    private readonly bool[] _hNight = new bool[6];
    private int _hCount;

    private readonly Label _measureL = new(), _measurePlaceL = new();
    private readonly Label _tempL = new(), _labelL = new(), _feelsL = new(), _hiloL = new(), _lineL = new(), _footL = new();
    private readonly Label[] _hTimeL = { new(), new(), new(), new(), new(), new() };
    private readonly Label[] _hTempL = { new(), new(), new(), new(), new(), new() };

    public bool Animating => _openT > 0 && (_openT < 1 || !_still) || (_hover != (_openT >= 1));

    public void Update(UiContext c, double dt)
    {
        _time = c.Time;
        var s = c.Settings;
        bool want = s.Bool("weather");
        StillPlace.Poll(want || s.Bool("prayer"));
        if (want && Due()) Refresh();

        var wx = _wx;
        _show = want && wx != null && wx.Days.Length > 0 && StillPlace.Ready && c.DateRect.Width > 1 && c.StageBusy < 0.5f && !c.Editing;
        if (!_show) { _openT = 0; _hover = false; _zone = _card = default; return; }

        EnsureFormats(c);
        _still = s.Num("motion") == 0;
        var cc = StillPlace.Place?.Cc ?? "";
        string unit = s.Str("weatherUnit");
        _fahrenheit = unit == "auto" ? FCountries.Contains(cc) : unit == "f";

        // Texte höchstens einmal je Sekunde neu
        if (c.Time - _stateAt > 1 || !ReferenceEquals(_stateFor, wx))
        {
            _stateAt = c.Time;
            _stateFor = wx;
            BuildState(c, wx!);
        }

        LayoutZone(c);
        LayoutCard(c);

        // Drüberfahren: Anzeige oder (offene) Karte; Schließen mit kurzer Verzögerung wie im Original (.12 s)
        var p = c.Pointer;
        bool over = c.PointerInside && (Contains(_zone, p) || (_openT > 0.05f && Contains(_card, p)));
        if (over) { _hover = true; _leftAt = -1; }
        else if (_hover)
        {
            if (_leftAt < 0) _leftAt = c.Time;
            if (c.Time - _leftAt > 0.12) _hover = false;
        }
        float step = (float)(dt / 0.45);
        _openT = Math.Clamp(_openT + (_hover ? step : -step), 0, 1);
    }

    private void BuildState(UiContext c, Wx wx)
    {
        double t = NowMs();
        bool c24 = c.Settings.Bool("clock24");
        var cur = Current(wx, t) ?? wx.Now;
        var (kind, label) = Condition(cur.Code, cur.Cloud, cur.Vis, cur.Wet);
        _kind = kind;
        _night = IsNight(wx, t);
        _temp = Deg(cur.T);
        _label = L.T(label);
        _feels = L.F("Gefühlt {0}", Deg(cur.Feels));
        var today = DayOf(wx, t);
        _hilo = L.F("H {0} · T {1}", Deg(today.Hi), Deg(today.Lo));
        string sunNext = t < today.Rise ? L.F("Sonnenaufgang {0}", FmtTime(today.Rise, c24)) : t < today.Set ? L.F("Sonnenuntergang {0}", FmtTime(today.Set, c24)) : "";
        // Dunstig, aber kein Nebel: sagen, wie weit man sieht, statt es Nebel zu nennen.
        string haze = kind != "fog" && cur.Vis is { } v && v < 1000 ? L.F("Sicht {0:0.0} km", v / 1000) : "";
        _line = string.Join(" · ", new[] { today.Rain is { } rain ? L.F("{0:0} % Regenwahrscheinlichkeit", rain) : "", sunNext, haze }.Where(x => x.Length > 0));

        // „Jetzt“ ist der aktuelle Messwert, damit es immer zum Kopf der Karte passt.
        _hCount = 0;
        foreach (var x in wx.Hours)
        {
            if (x.At <= t - HourMs) continue;
            int i = _hCount++;
            _hTime[i] = i == 0 ? L.T("Jetzt") : FmtHour(x.At, c24);
            _hTemp[i] = Deg(i == 0 ? cur.T : x.T);
            _hKind[i] = i == 0 ? kind : Condition(x.Code, x.Cloud, x.Vis, x.Wet).Kind;
            _hNight[i] = i == 0 ? _night : IsNight(wx, x.At + HourMs / 2);
            if (_hCount == 6) break;
        }

        string where = StillPlace.Place is { Name.Length: > 0 } pl ? pl.Name : L.T("Dein Standort");
        bool stale = t - wx.At > 3 * HourMs;
        _foot = stale || _failed ? L.F("{0} · aktualisiert {1}", where, Ago(wx.At)) : $"{where} · Open-Meteo · {Ago(wx.At)}";
    }

    // Wo die Wetteranzeige steht: neben dem Datum (rechts in der Datumszeile) oder als eigene Zeile darüber.
    private void LayoutZone(UiContext c)
    {
        float k = c.K;
        var dr = c.DateRect;
        bool above = c.Settings.Str("weatherPos") == "above";
        float fs = (above ? 16.5f : 19f) * k;
        _measureL.Set(c.DWrite, _temp, Fmt(c, above ? 16.5f : 19f, 500));
        float w = 1.25f * fs + 0.32f * fs + _measureL.W;
        if (above)
        {
            string place = StillPlace.Place?.Name ?? "";
            if (place.Length > 0)
            {
                if (!ReferenceEquals(place, _placeName)) { _placeName = place; _placeText = "· " + place; }
                _measurePlaceL.Set(c.DWrite, _placeText, Fmt(c, 16.5f, 500));
                w += 0.5f * fs + _measurePlaceL.W;
            }
            float h = 1.25f * fs;
            float cx = dr.Left + dr.Width / 2;
            _zone = new DRect(cx - w / 2 - 2 * k, dr.Top, w + 4 * k, h);
        }
        else
        {
            w = Math.Min(w, dr.Width);
            _zone = new DRect(dr.Right - w - 2 * k, dr.Top, w + 4 * k, dr.Height);
        }
    }

    private void LayoutCard(UiContext c)
    {
        float k = c.K;
        var dw = c.DWrite;
        // Kopf
        _tempL.Set(dw, _temp, BigFmt(c));
        _labelL.Set(dw, _label, Fmt(c, 13, 550));
        _feelsL.Set(dw, _feels, Fmt(c, 12.5f, 500));
        _hiloL.Set(dw, _hilo, Fmt(c, 12.5f, 500));
        float headH = Math.Max(42 * k, _tempL.H * 0.92f + _labelL.H);
        _lineL.Set(dw, _line, Fmt(c, 12.5f, 500), 268 * k);
        float lineH = _line.Length > 0 ? _lineL.H : 0;
        float hourH = 0;
        for (int i = 0; i < _hCount; i++)
        {
            _hTimeL[i].Set(dw, _hTime[i], Fmt(c, 11.5f, 500));
            _hTempL[i].Set(dw, _hTemp[i], Fmt(c, 12.5f, 600));
            hourH = Math.Max(hourH, _hTimeL[i].H + 5 * k + 20 * k + 5 * k + _hTempL[i].H);
        }
        _footL.Set(dw, _foot, Fmt(c, 11, 500), 268 * k);
        _cardH = 16 * k + headH + 10 * k + lineH + 12 * k + 10 * k + hourH + 11 * k + _footL.H + 13 * k;

        float w = 304 * k;
        float x = _zone.Left + _zone.Width / 2 - w / 2;
        x = Math.Clamp(x, 16, Math.Max(16, c.Width - 16 - w));
        _card = new DRect(x, _zone.Bottom + 12 * k, w, _cardH);
    }

    private static bool Contains(DRect r, Vector2 p) => r.Width > 0 && p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

    public bool HitTest(UiContext c, Vector2 p) => _show && (Contains(_zone, p) || (_openT > 0.05f && Contains(_card, p)));

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        if (!_show || e.Kind == PointerKind.Leave) return false;
        // Die offene Karte schluckt die Maus (sie liegt über allem darunter).
        return _openT > 0.3f && Contains(_card, e.Pos);
    }

    // ---------- Zeichnen ----------

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private ID2D1PathGeometry? _cloud, _moonClear, _moonPartly, _bolt, _maskPartlyDay, _maskPartlyNight, _maskCloudy;
    private float _alpha = 1;
    private Matrix3x2 _outer = Matrix3x2.Identity;

    private static readonly Vector3 White = new(1);

    private ID2D1SolidColorBrush Brush(Vector3 col, float a)
    {
        _brush!.Color = new Color4(col.X, col.Y, col.Z, Math.Clamp(a * _alpha, 0, 1));
        return _brush;
    }

    private static float EaseOut(float t) => 1 - MathF.Pow(1 - t, 3);

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (!_show || _openT <= 0) return;
        EnsureDevice(ctx, c);
        float k = c.K;
        // Deckung .25 s, Bewegung .45 s (aus 6 k höher und 97 % Größe)
        float op = Math.Clamp(_openT * 0.45f / 0.25f, 0, 1);
        op = op * op * (3 - 2 * op);
        float mv = EaseOut(_openT);
        float scale = 0.97f + 0.03f * mv, dy = -6 * k * (1 - mv);
        var r = _card;
        var origin = new Vector2(r.Left + r.Width / 2, r.Top);
        var local = Matrix3x2.CreateScale(scale, origin) * Matrix3x2.CreateTranslation(0, dy);
        var tl = Vector2.Transform(new Vector2(r.Left, r.Top), local);
        var br = Vector2.Transform(new Vector2(r.Right, r.Bottom), local);

        var prev = ctx.Transform;
        _outer = local * prev;
        try
        {
            ctx.Transform = prev;
            c.Glass.Panel(ctx, c, new DRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y), 24 * k * scale, op);
            ctx.Transform = _outer;
            _alpha = op;
            DrawContent(ctx, c);
        }
        finally
        {
            ctx.Transform = prev;
            _alpha = 1;
        }
    }

    private void DrawContent(ID2D1DeviceContext ctx, UiContext c)
    {
        float k = c.K;
        var fg = c.Fg;
        var dw = c.DWrite;
        float x = _card.Left + 18 * k, w = 268 * k, y = _card.Top + 16 * k;

        // Kopf: großes Symbol, Temperatur und Zustand, rechts gefühlt und Höchst/Tiefst
        float headH = Math.Max(42 * k, _tempL.H * 0.92f + _labelL.H);
        DrawIcon(ctx, _kind, _night, x, y + headH / 2 - 21 * k, 42 * k, fg, 0.92f);
        float mainX = x + 42 * k + 12 * k;
        float mainTop = y + headH / 2 - (_tempL.H * 0.92f + _labelL.H) / 2;
        ctx.DrawTextLayout(new Vector2(mainX, mainTop), _tempL.Layout!, Brush(fg, 0.96f));
        ctx.DrawTextLayout(new Vector2(mainX, mainTop + _tempL.H * 0.92f), _labelL.Layout!, Brush(fg, 0.78f));
        float sideH = _feelsL.H + 3 * k + _hiloL.H, sideTop = y + headH / 2 - sideH / 2;
        ctx.DrawTextLayout(new Vector2(x + w - _feelsL.W, sideTop), _feelsL.Layout!, Brush(fg, 0.6f));
        ctx.DrawTextLayout(new Vector2(x + w - _hiloL.W, sideTop + _feelsL.H + 3 * k), _hiloL.Layout!, Brush(fg, 0.6f));
        y += headH + 10 * k;

        if (_line.Length > 0)
        {
            ctx.DrawTextLayout(new Vector2(x, y), _lineL.Layout!, Brush(fg, 0.6f));
            y += _lineL.H;
        }
        y += 12 * k;

        // Die nächsten Stunden
        ctx.FillRectangle(new DRect(x, y, w, Math.Max(1, k)), Brush(fg, 0.08f));
        y += 10 * k;
        float colW = w / 6, hourH = 0;
        for (int i = 0; i < _hCount; i++)
        {
            float cx = x + colW * (i + 0.5f), yy = y;
            ctx.DrawTextLayout(new Vector2(cx - _hTimeL[i].W / 2, yy), _hTimeL[i].Layout!, Brush(fg, 0.6f));
            yy += _hTimeL[i].H + 5 * k;
            DrawIcon(ctx, _hKind[i], _hNight[i], cx - 10 * k, yy, 20 * k, fg, 0.88f);
            yy += 25 * k;
            ctx.DrawTextLayout(new Vector2(cx - _hTempL[i].W / 2, yy), _hTempL[i].Layout!, Brush(fg, 0.88f));
            hourH = Math.Max(hourH, yy + _hTempL[i].H - y);
        }
        y += hourH + 11 * k;

        ctx.DrawTextLayout(new Vector2(x, y), _footL.Layout!, Brush(fg, 0.38f));
    }

    // ---------- Symbole ----------
    /* Feine Linienzeichnungen in einem 24er-Kasten, in der Farbe des Textes. Jedes bewegt sich ein wenig:
       Strahlen drehen sich, Wolken treiben, Regen und Schnee fallen, Blitze flackern. */

    private static readonly (float X, float Y, float S) RainCloud = (1.6f, -3.2f, 0.88f);
    private static readonly (float X, float Y, float S) PartlyDay = (3.6f, 2.4f, 0.8f), PartlyNight = (4.6f, 3.6f, 0.76f);
    private static readonly (float X, float Y, float S) CloudBack = (8.4f, -2.2f, 0.62f), CloudFront = (-0.8f, 1.6f, 0.9f);

    private Matrix3x2 _iconM;
    private Vector3 _iconCol;
    private float _iconA;

    private void DrawIcon(ID2D1DeviceContext ctx, string kind, bool night, float x, float y, float size, Vector3 col, float a)
    {
        _iconM = Matrix3x2.CreateScale(size / 24) * Matrix3x2.CreateTranslation(x, y) * _outer;
        _iconCol = col;
        _iconA = a;
        ctx.Transform = _iconM;
        double t = _time;
        switch (kind)
        {
            case "clear":
                if (night)
                {
                    ctx.DrawGeometry(_moonClear!, Brush(col, a), 1.6f, _round);
                    float tw = _still ? 1 : 0.25f + 0.75f * Alternate(t, 3.2, false);
                    ctx.DrawLine(new Vector2(18.6f, 3.6f), new Vector2(18.6f, 6.2f), Brush(col, a * tw), 1.2f, _round);
                    ctx.DrawLine(new Vector2(17.3f, 4.9f), new Vector2(19.9f, 4.9f), Brush(col, a * tw), 1.2f, _round);
                }
                else DrawSun(ctx, 12, 12, 4.2f, 6.7f, 8.7f);
                break;
            case "partly":
            {
                // Nachts sitzt die Wolke etwas tiefer, damit mehr vom Mond zu sehen ist.
                var tf = night ? PartlyNight : PartlyDay;
                PushMask(ctx, night ? _maskPartlyNight! : _maskPartlyDay!);
                if (night) ctx.DrawGeometry(_moonPartly!, Brush(col, a), 1.6f, _round);
                else DrawSun(ctx, 8.6f, 8.6f, 3, 5, 6.4f);
                ctx.PopLayer();
                DrawCloud(ctx, tf, Drift(t, 7, false), 1);
                break;
            }
            case "cloudy":
                PushMask(ctx, _maskCloudy!);
                DrawCloud(ctx, CloudBack, Drift(t, 9, true), 0.55f);
                ctx.PopLayer();
                DrawCloud(ctx, CloudFront, Drift(t, 7, false), 1);
                break;
            case "fog":
            {
                float d1 = Drift(t, 6, false), d2 = Drift(t, 7.5, true);
                Line(ctx, 4 + d1, 8.5f, 14.5f + d1, 8.5f, 1);
                Line(ctx, 17.5f + d1, 8.5f, 20 + d1, 8.5f, 1);
                Line(ctx, 6 + d2, 12.5f, 19.5f + d2, 12.5f, 1);
                Line(ctx, 4.5f + d1, 16.5f, 8.5f + d1, 16.5f, 1);
                Line(ctx, 11.5f + d1, 16.5f, 19.5f + d1, 16.5f, 1);
                break;
            }
            case "drizzle":
                DrawCloud(ctx, RainCloud, Drift(t, 7, false), 1);
                for (int i = 0; i < 3; i++)
                {
                    float bx = 8.6f + 4 * i, by = i == 1 ? 17.4f : 16.4f;
                    var (oy, o) = Drip(t, 1.8, i, 3);
                    Line(ctx, bx, by + oy, bx, by + 0.6f + oy, o);
                }
                break;
            case "rain":
                DrawCloud(ctx, RainCloud, Drift(t, 7, false), 1);
                for (int i = 0; i < 3; i++)
                {
                    var (ox, oy, o) = Fall(t, 1.1, i, 3);
                    Line(ctx, 9.2f + 4 * i + ox, 15.6f + oy, 8.2f + 4 * i + ox, 18.2f + oy, o);
                }
                break;
            case "storm":
            {
                DrawCloud(ctx, RainCloud, Drift(t, 7, false), 1);
                ctx.Transform = _iconM;
                float f = _still ? 1 : Flash(t);
                ctx.DrawGeometry(_bolt!, Brush(col, a * f), 1.6f, _round);
                for (int i = 0; i < 2; i++)
                {
                    float sx = i == 0 ? 8.2f : 17.6f;
                    var (ox, oy, o) = Fall(t, 1.1, i, 2);
                    Line(ctx, sx + ox, 15.6f + oy, sx - 0.9f + ox, 17.9f + oy, o);
                }
                break;
            }
            case "snow":
                DrawCloud(ctx, RainCloud, Drift(t, 7, false), 1);
                ctx.Transform = _iconM;
                for (int i = 0; i < 3; i++)
                {
                    var (ox, oy, o) = Snow(t, 2.8, i, 3);
                    float bx = 8.6f + 4 * i, by = i == 1 ? 18 : 16.6f;
                    ctx.FillEllipse(new Ellipse(new Vector2(bx + ox, by + oy), 0.95f, 0.95f), Brush(col, a * o));
                }
                break;
        }
        ctx.Transform = _outer;
    }

    private void Line(ID2D1DeviceContext ctx, float x0, float y0, float x1, float y1, float o)
    {
        if (o <= 0.003f) return;
        ctx.Transform = _iconM;
        ctx.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), Brush(_iconCol, _iconA * o), 1.6f, _round);
    }

    private void DrawSun(ID2D1DeviceContext ctx, float cx, float cy, float r, float r0, float r1)
    {
        ctx.Transform = _iconM;
        var b = Brush(_iconCol, _iconA);
        ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), b, 1.6f);
        double turn = _still ? 0 : (_time / 48 % 1) * Math.PI * 2;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4 + turn;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            ctx.DrawLine(new Vector2(cx + c * r0, cy + s * r0), new Vector2(cx + c * r1, cy + s * r1), b, 1.6f, _round);
        }
    }

    private void DrawCloud(ID2D1DeviceContext ctx, (float X, float Y, float S) c, float dx, float o)
    {
        ctx.Transform = Matrix3x2.CreateScale(c.S) * Matrix3x2.CreateTranslation(c.X + dx, c.Y) * _iconM;
        ctx.DrawGeometry(_cloud!, Brush(_iconCol, _iconA * o), 1.6f / c.S, _round);
        ctx.Transform = _iconM;
    }

    // Was hinter einer Wolke liegt, wird dort ausgespart, wo die Wolke ist – Linien kreuzen sich nie.
    private void PushMask(ID2D1DeviceContext ctx, ID2D1Geometry mask)
    {
        ctx.Transform = _iconM;
        var lp = new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(-4, -4, 28, 28),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1,
            LayerOptions = LayerOptions1.None,
        };
        ctx.PushLayer(ref lp, null);
    }

    // ---------- Bewegungen (Keyframes aus style.css) ----------

    private static float EaseInOut(float u) => u * u * (3 - 2 * u);

    // alternate: hin und zurück; reverse = alternate-reverse
    private static float Alternate(double t, double period, bool reverse)
    {
        double u = t / period % 2;
        if (u > 1) u = 2 - u;
        if (reverse) u = 1 - u;
        return EaseInOut((float)u);
    }

    // wx-drift: von −0,5 nach 0,6
    private float Drift(double t, double period, bool reverse) => _still ? 0 : -0.5f + 1.1f * Alternate(t, period, reverse);

    private static float Phase(double t, double period, int i, int n) => (float)(((t + period * i / n) / period) % 1);

    // wx-fall: (0,5, −2,4) → (−0,5, 2,4), Deckung 0 → 1 (25 %) … 1 (70 %) → 0
    private (float X, float Y, float O) Fall(double t, double period, int i, int n)
    {
        if (_still) return (0, 0, 1);
        float u = Phase(t, period, i, n);
        float o = u < 0.25f ? u / 0.25f : u < 0.7f ? 1 : 1 - (u - 0.7f) / 0.3f;
        return (0.5f - u, -2.4f + 4.8f * u, o);
    }

    // wx-drip: −2 → 2,6 (ease-in), Deckung 0 → 1 (30 %) → 0
    private (float Y, float O) Drip(double t, double period, int i, int n)
    {
        if (_still) return (0, 1);
        float u = Phase(t, period, i, n);
        float o = u < 0.3f ? u / 0.3f : 1 - (u - 0.3f) / 0.7f;
        return (-2 + 4.6f * MathF.Pow(u, 1.7f), o);
    }

    // wx-snow: (−0,6, −2) → (0,6, 0,8) bei 60 % → (−0,3, 2,8); Deckung 0 → 1 (30 %) → 0
    private (float X, float Y, float O) Snow(double t, double period, int i, int n)
    {
        if (_still) return (0, 0, 1);
        float u = Phase(t, period, i, n);
        float o = u < 0.3f ? u / 0.3f : 1 - (u - 0.3f) / 0.7f;
        float x, y;
        if (u < 0.6f) { float v = EaseInOut(u / 0.6f); x = -0.6f + 1.2f * v; y = -2 + 2.8f * v; }
        else { float v = EaseInOut((u - 0.6f) / 0.4f); x = 0.6f - 0.9f * v; y = 0.8f + 2f * v; }
        return (x, y, o);
    }

    // wx-flash: 0–84 % 1, 86 % .15, 88 % 1, 90 % .3, 100 % 1
    private float Flash(double t)
    {
        float u = (float)(t / 5 % 1);
        if (u < 0.84f) return 1;
        if (u < 0.86f) return 1 - 0.85f * (u - 0.84f) / 0.02f;
        if (u < 0.88f) return 0.15f + 0.85f * (u - 0.86f) / 0.02f;
        if (u < 0.90f) return 1 - 0.7f * (u - 0.88f) / 0.02f;
        return 0.3f + 0.7f * (u - 0.9f) / 0.1f;
    }

    // ---------- Mittel ----------

    private void EnsureDevice(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        var f = c.D2D;
        _round = f.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });

        // Wolke: M17.3 18.5H8.8a5.5 5.5 0 1 1 5.06-7.65 4.3 4.3 0 1 1 3.44 7.65z
        _cloud = f.CreatePathGeometry();
        using (var s = _cloud.Open())
        {
            s.BeginFigure(new Vector2(17.3f, 18.5f), FigureBegin.Filled);
            s.AddLine(new Vector2(8.8f, 18.5f));
            s.AddArc(new ArcSegment { Point = new Vector2(13.86f, 10.85f), Size = new Size(5.5f, 5.5f), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Large });
            s.AddArc(new ArcSegment { Point = new Vector2(17.3f, 18.5f), Size = new Size(4.3f, 4.3f), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Large });
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        _moonClear = Moon(f, 11.5f, 12.5f, 7);
        _moonPartly = Moon(f, 8.4f, 8.6f, 5.6f);

        // Blitz: M13.2 14l-2.4 3.7h3.1l-2.3 3.8
        _bolt = f.CreatePathGeometry();
        using (var s = _bolt.Open())
        {
            s.BeginFigure(new Vector2(13.2f, 14), FigureBegin.Hollow);
            s.AddLine(new Vector2(10.8f, 17.7f));
            s.AddLine(new Vector2(13.9f, 17.7f));
            s.AddLine(new Vector2(11.6f, 21.5f));
            s.EndFigure(FigureEnd.Open);
            s.Close();
        }

        _maskPartlyDay = Mask(f, PartlyDay);
        _maskPartlyNight = Mask(f, PartlyNight);
        _maskCloudy = Mask(f, CloudFront);
    }

    // Sichel als Umriss: Kreis um (cx, cy) ohne einen Kreis oben rechts.
    private static ID2D1PathGeometry Moon(ID2D1Factory1 f, float cx, float cy, float r)
    {
        float ox = cx + r * 0.55f, oy = cy - r * 0.45f, R = r * 0.9f;
        float dx = ox - cx, dy = oy - cy, d = MathF.Sqrt(dx * dx + dy * dy);
        float a = (r * r - R * R + d * d) / (2 * d), k = MathF.Sqrt(r * r - a * a);
        float mx = cx + a * dx / d, my = cy + a * dy / d;
        var p = new Vector2(mx + k * dy / d, my - k * dx / d);
        var q = new Vector2(mx - k * dy / d, my + k * dx / d);
        var g = f.CreatePathGeometry();
        using var s = g.Open();
        s.BeginFigure(p, FigureBegin.Filled);
        s.AddArc(new ArcSegment { Point = q, Size = new Size(r, r), SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Large });
        s.AddArc(new ArcSegment { Point = p, Size = new Size(R, R), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return g;
    }

    // Maske: Kasten (−4 … 28) ohne die Wolke samt breitem Rand (3,4 / s in Wolkeneinheiten).
    private ID2D1PathGeometry Mask(ID2D1Factory1 f, (float X, float Y, float S) c)
    {
        var m = Matrix3x2.CreateScale(c.S) * Matrix3x2.CreateTranslation(c.X, c.Y);
        using var placed = f.CreateTransformedGeometry(_cloud!, m);
        using var wide = f.CreatePathGeometry();
        using (var s = wide.Open())
        {
            placed.Widen(3.4f, _round!, 0.05f, s);
            s.Close();
        }
        using var body = f.CreatePathGeometry();
        using (var s = body.Open())
        {
            wide.CombineWithGeometry(placed, CombineMode.Union, s);
            s.Close();
        }
        using var box = f.CreateRectangleGeometry(new Vortice.RawRectF(-4, -4, 28, 28));
        var mask = f.CreatePathGeometry();
        using (var s = mask.Open())
        {
            box.CombineWithGeometry(body, CombineMode.Exclude, s);
            s.Close();
        }
        return mask;
    }

    private float _fmtK = -1;
    private string _fmtFont = "", _fmtWeight = "";
    private readonly Dictionary<int, IDWriteTextFormat> _formats = new();
    private IDWriteTextFormat? _bigFmt;

    private void EnsureFormats(UiContext c)
    {
        string font = c.Settings.Str("font"), weight = c.Settings.Str("clockWeight");
        if (c.K == _fmtK && font == _fmtFont && weight == _fmtWeight) return;
        _fmtK = c.K;
        _fmtFont = font;
        _fmtWeight = weight;
        // Die Formate gehören UiFonts; hier nur die Verweise vergessen.
        _formats.Clear();
        _bigFmt = null;
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

    // .wx-big-temp: Uhrschrift, 30 k, Stärke der Uhr
    private IDWriteTextFormat BigFmt(UiContext c)
    {
        if (_bigFmt != null) return _bigFmt;
        int w = c.Settings.Str("clockWeight") switch { "thin" => 200, "light" => 300, "regular" => 400, "medium" => 500, _ => 650 };
        _bigFmt = c.Fonts.Format(c, 30 * c.K, w, display: true);
        _bigFmt.WordWrapping = WordWrapping.NoWrap;
        return _bigFmt;
    }

    private void DisposeDevice()
    {
        _brush?.Dispose(); _brush = null;
        _round?.Dispose(); _round = null;
        foreach (var g in new[] { _cloud, _moonClear, _moonPartly, _bolt, _maskPartlyDay, _maskPartlyNight, _maskCloudy }) g?.Dispose();
        _cloud = _moonClear = _moonPartly = _bolt = _maskPartlyDay = _maskPartlyNight = _maskCloudy = null;
        _ctxPtr = 0;
    }

    public void Dispose()
    {
        DisposeDevice();
        foreach (var l in new[] { _measureL, _measurePlaceL, _tempL, _labelL, _feelsL, _hiloL, _lineL, _footL }) l.Dispose();
        foreach (var l in _hTimeL) l.Dispose();
        foreach (var l in _hTempL) l.Dispose();
    }

    /// <summary>Ein Textbaustein, der nur bei Änderung neu gesetzt wird.</summary>
    private sealed class Label : IDisposable
    {
        private string? _text;
        private IDWriteTextFormat? _fmt;
        private float _maxW;
        public IDWriteTextLayout? Layout;
        public float W, H;

        public IDWriteTextLayout Set(IDWriteFactory dw, string text, IDWriteTextFormat fmt, float maxW = 0)
        {
            if (Layout != null && text == _text && ReferenceEquals(fmt, _fmt) && maxW == _maxW) return Layout;
            Layout?.Dispose();
            _text = text; _fmt = fmt; _maxW = maxW;
            Layout = dw.CreateTextLayout(text, fmt, maxW > 0 ? maxW : 8192, 8192);
            Layout.WordWrapping = maxW > 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;
            var m = Layout.Metrics;
            W = m.WidthIncludingTrailingWhitespace;
            H = m.Height;
            return Layout;
        }

        public void Dispose() { Layout?.Dispose(); Layout = null; _text = null; }
    }
}

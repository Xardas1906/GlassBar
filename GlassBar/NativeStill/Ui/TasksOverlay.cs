using System.Numerics;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>Gespeicherte erledigte Aufgaben eines Tages ("tasks.today").</summary>
internal sealed class TasksDay
{
    public string Day { get; set; } = "";
    public string[] Done { get; set; } = [];
}

/// <summary>
/// Heutige Aufgaben unter der Uhr (app.js „Today's tasks“): eine Zeile zur Zeit, mit dünnem Kreis zum Abhaken.
/// Ein Klick hakt sie mit einer kleinen Feier ab (Kreis springt, Ring und Funken, das Licht blüht auf, ein Ton),
/// darunter ein kurzes Lob; nach einem Moment steigt die nächste an ihren Platz. Ein Klick auf eine erledigte
/// holt sie zurück. Erledigtes bleibt bis Mitternacht gemerkt. Quelle: App.Config.WallpaperTasks (mit ; getrennt).
/// </summary>
public sealed class TasksOverlay : IOverlay, IDisposable
{
    private const string Key = "tasks.today";
    private static readonly string[] Cheers = ["Gut gemacht.", "Wunderbar gemacht.", "Super.", "Schön gemacht.", "Eins weniger.", "Wohlverdient.", "Genau so."];
    private static readonly char[] Separators = [';', '\n', '|'];

    // Kurven wie im Stylesheet
    private static readonly Bezier EaseCss = new(0.25, 0.1, 0.25, 1);
    private static readonly Bezier Spring = new(0.3, 1.3, 0.5, 1);
    private static readonly Bezier EaseOut = new(0.16, 1, 0.3, 1);
    private static readonly Bezier StrikeCurve = new(0.65, 0, 0.35, 1);
    private static readonly Bezier PopCurve = new(0.3, 1.4, 0.5, 1);
    private static readonly Bezier BurstCurve = new(0.2, 0.7, 0.3, 1);
    private static readonly Bezier OutCurve = new(0.4, 0, 1, 1);
    private static readonly Bezier InCurve = new(0.2, 0.8, 0.2, 1);

    public int Z => 40;

    public bool Animating =>
        _rowPhase != 0 || _fill.Busy(_time) || _mark.Busy(_time) || _ring.Busy(_time) || _hover.Busy(_time)
        || _strike.Busy(_time) || _dim.Busy(_time) || _cheerA.Busy(_time) || _cheerY.Busy(_time) || _vis.Busy(_time)
        || _time - _popStart < 0.7 || _time - _burstStart < 0.85 || _sparksLive;

    private readonly ClockUiPaint _p = new();
    private double _time;

    // ---------- Liste und Zustand ----------
    private string? _src;
    private readonly List<string> _list = new();
    private readonly HashSet<string> _done = new();
    private DateTime _day;
    private bool _loaded;
    private string? _shown;
    private bool _isDone, _celebrating;
    private int _lastCheer = -1;

    // Geplantes (statt setTimeout): Zeitpunkte in _time, -1 = nichts
    private double _swapAt = -1, _cheerOffAt = -1, _calmAt = -1;
    private string? _swapNext;

    // ---------- Animationen ----------
    private Tween _fill, _mark, _ring, _hover, _strike, _dim, _cheerA, _cheerY, _vis;
    private int _rowPhase;          // 0 ruhig, 1 hinaus, 2 herein
    private double _rowStart;
    private double _popStart = -10, _burstStart = -10;
    private bool _sparksLive;
    private struct Spark { public float Ang, Dist, Dur; public double Start; public bool White; }
    private readonly Spark[] _sparks = new Spark[12];

    private string _cheerMain = "", _cheerSub = "";

    // ---------- Anordnung ----------
    private IDWriteTextLayout? _layout;
    private string? _layoutText;
    private float _layoutMax = -1, _layoutSize = -1;
    private IDWriteTextFormat? _layoutFmt;
    private Vector2 _textSize;
    private readonly List<(float X0, float X1, float Y)> _strikeLines = new();
    private float _strikeTotal;
    private Rect _row;
    private Vector2 _check;
    private float _textX, _textY;
    private bool _on, _showSetting;
    private bool _pressed;

    public TasksOverlay()
    {
        _ring.Set(1); _cheerY.Set(1);
    }

    // =================================================================================================
    // Liste
    // =================================================================================================

    private static string TodayKey(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private void Load(DateTime today)
    {
        _loaded = true;
        _day = today.Date;
        var s = StillState.Get(Key, new TasksDay());
        if (s.Day == TodayKey(_day) && s.Done != null) foreach (var t in s.Done) _done.Add(t);
    }

    private void Save() => StillState.Set(Key, new TasksDay { Day = TodayKey(_day), Done = _done.ToArray() });

    private string? NextTask()
    {
        foreach (var t in _list) if (!_done.Contains(t)) return t;
        return null;
    }

    private int DoneCount()
    {
        int n = 0;
        foreach (var t in _list) if (_done.Contains(t)) n++;
        return n;
    }

    /// <summary>Hält alles Geplante an: wartende Übergaben und einen laufenden Wechsel.</summary>
    private void ClearTimers()
    {
        _swapAt = _cheerOffAt = _calmAt = -1;
        _swapNext = null;
        _rowPhase = 0;
    }

    private void ShowTask(string text, bool done)
    {
        _shown = text;
        if (done != _isDone)
        {
            _isDone = done;
            float t = done ? 1 : 0;
            _fill.To(t, _time, 0.5, 0, Spring);
            _mark.To(t, _time, 0.4, done ? 0.14 : 0, EaseCss);
            _ring.To(1 - t, _time, 0.3, 0, EaseCss);
            _strike.To(t, _time, 0.75, 0, StrikeCurve);
            _dim.To(t, _time, 0.6, 0.3, EaseCss);
        }
    }

    private void SetDoneInstant(bool done)
    {
        _isDone = done;
        float t = done ? 1 : 0;
        _fill.Set(t); _mark.Set(t); _ring.Set(1 - t); _strike.Set(t); _dim.Set(t);
    }

    /// <summary>Die Zeile leise mit der Liste in Einklang bringen (ohne Feier).</summary>
    private void ApplyFocus(UiContext c, bool instant)
    {
        _list.Clear();
        foreach (var part in (_src ?? "").Split(Separators))
        {
            var t = part.Trim();
            if (t.Length > 0) _list.Add(t);
        }
        _on = c.Settings.Bool("showFocus") && _list.Count > 0;
        if (!_on || _celebrating) return;
        var next = NextTask();
        string text = next ?? _list[^1];
        bool done = next == null;
        if (instant || _shown == null) { _shown = text; SetDoneInstant(done); }
        else ShowTask(text, done);
    }

    private void Cheer(string main, string sub, double seconds)
    {
        _cheerMain = main;
        _cheerSub = sub;
        _cheerA.To(1, _time, 0.55, 0, EaseCss);
        _cheerY.Set(1);
        _cheerY.To(0, _time, 0.8, 0, EaseOut);
        _cheerOffAt = seconds > 0 ? _time + seconds : -1;
    }

    private void CheerOff()
    {
        _cheerA.To(0, _time, 0.55, 0, EaseCss);
        _cheerY.To(1, _time, 0.8, 0, EaseOut);
    }

    private string PickCheer()
    {
        int i;
        do i = Random.Shared.Next(Cheers.Length); while (i == _lastCheer);
        _lastCheer = i;
        return L.T(Cheers[i]);
    }

    // Der Kreis springt; ein Ring und ein paar Funken verlassen ihn; das Licht selbst kräuselt sich.
    private void Celebrate(UiContext c)
    {
        _popStart = _time;
        if (!c.Settings.Bool("celebrate")) return;
        _burstStart = _time;
        for (int i = 0; i < _sparks.Length; i++)
        {
            _sparks[i] = new Spark
            {
                Ang = (float)(i / 12.0 * Math.PI * 2 + Random.Shared.NextDouble() * 0.45),
                Dist = (float)(24 + Random.Shared.NextDouble() * 24),
                Dur = (float)(0.65 + Random.Shared.NextDouble() * 0.35),
                Start = _time,
                White = i % 2 == 1,
            };
        }
        _sparksLive = true;
        c.Bloom(_check.X, _check.Y, 1.6f);
    }

    private void CompleteTask(UiContext c)
    {
        var t = _shown;
        if (t == null || _done.Contains(t)) return;
        ClearTimers();
        _done.Add(t);
        Save();
        _celebrating = true;
        ShowTask(t, true);
        Celebrate(c);
        if (c.Settings.Bool("taskSound")) { try { Tones.Play("task"); } catch { } }
        var next = NextTask();
        bool many = _list.Count > 1;
        if (next != null)
        {
            // Einen Moment genießen, dann die nächste hereinholen
            Cheer(PickCheer(), many ? L.F("{0} von {1} erledigt", DoneCount(), _list.Count) : "", 0);
            _swapAt = _time + 3.4;
            _swapNext = next;
        }
        else
        {
            Cheer(many ? L.T("Das ist alles. Wunderbar gemacht.") : PickCheer(), L.T("Weitere in GlassBar → Einstellungen → Heutige Aufgaben"), 9);
            _calmAt = _time + 0.8;
        }
    }

    private void UndoTask()
    {
        var t = _shown;
        if (t == null) return;
        ClearTimers();
        _done.Remove(t);
        Save();
        _celebrating = false;
        CheerOff();
        ShowTask(t, false);
    }

    // =================================================================================================
    // Bild für Bild
    // =================================================================================================

    public void Update(UiContext c, double dt)
    {
        _time = c.Time;
        _p.Begin(c);
        var today = c.Now.Date;
        if (!_loaded) Load(today);

        // Ein neuer Tag beginnt mit einer leeren Liste
        if (today != _day)
        {
            _day = today;
            _done.Clear();
            Save();
            ClearTimers();
            _celebrating = false;
            CheerOff();
            ApplyFocus(c, false);
        }

        string src = App.Config.WallpaperTasks ?? "";
        bool show = c.Settings.Bool("showFocus");
        if (src != _src || show != _showSetting)
        {
            bool first = _src == null;
            _src = src;
            _showSetting = show;
            ApplyFocus(c, first);
        }

        // Geplantes
        if (_cheerOffAt >= 0 && _time >= _cheerOffAt) { _cheerOffAt = -1; CheerOff(); }
        if (_calmAt >= 0 && _time >= _calmAt) { _calmAt = -1; _celebrating = false; }
        if (_swapAt >= 0 && _time >= _swapAt)
        {
            _swapAt = -1;
            CheerOff();
            _rowPhase = 1;
            _rowStart = _time;
        }
        if (_rowPhase == 1 && _time - _rowStart >= 0.42)
        {
            _celebrating = false;
            if (_swapNext != null) { _shown = _swapNext; SetDoneInstant(false); }
            _swapNext = null;
            _rowPhase = 2;
            _rowStart = _time;
        }
        else if (_rowPhase == 2 && _time - _rowStart >= 0.62) _rowPhase = 0;

        // Sichtbarkeit: tritt zurück, wenn der Wecker oder etwas anderes die Bühne braucht
        float veil = Math.Max(AlarmsOverlay.TaskVeil, Math.Clamp(c.StageBusy, 0, 1));
        float visTarget = _on && !c.Editing ? 1 - veil : 0;
        if (Math.Abs(visTarget - _vis.Target) > 0.001f)
        {
            // Ziffernblatt/Klingeln haben eigene Übergänge: dem Wert direkt folgen, sonst 0,4 s
            if (veil > 0 && veil < 1) _vis.Set(visTarget);
            else _vis.To(visTarget, _time, 0.4, 0, EaseCss);
        }

        Layout(c);

        bool hov = _on && _vis.Value(_time) > 0.5f && c.PointerInside && _row.Contains(c.Pointer);
        if (hov != _hover.Target > 0.5f) _hover.To(hov ? 1 : 0, _time, 0.3, 0, EaseCss);

        if (_sparksLive)
        {
            bool live = false;
            foreach (var s in _sparks) if (_time - s.Start < s.Dur) live = true;
            _sparksLive = live;
        }
    }

    private void Layout(UiContext c)
    {
        float K = c.K;
        if (_shown == null) { _row = default; return; }
        float size = 20 * K;
        var fmt = _p.Fmt(c, size, 500);
        float box = 20 * K, gap = 12 * K;
        float maxW = Math.Max(40 * K, Math.Min(0.62f * c.Width, 900 * K) - box - gap);
        if (_layout == null || _layoutText != _shown || _layoutMax != maxW || _layoutSize != size || !ReferenceEquals(_layoutFmt, fmt))
            BuildLayout(c, _shown, fmt, maxW, size);

        var cr = c.ClockRect;
        bool clockOk = cr.Width > 1 && cr.Height > 1 && c.Settings.Bool("showClock");
        float cx = clockOk ? cr.X + cr.Width / 2 : c.Width / 2f;
        float top;
        if (clockOk) top = cr.Bottom + 24 * K;
        else if (c.DateRect.Height > 1) top = c.DateRect.Bottom + 24 * K;
        else top = c.Height * 0.47f;

        float rowW = box + gap + _textSize.X;
        float rowH = Math.Max(box, _textSize.Y);
        _row = new Rect(cx - rowW / 2, top, rowW, rowH);
        _check = new Vector2(_row.X + box / 2, top + rowH / 2);
        _textX = _row.X + box + gap;
        _textY = top + (rowH - _textSize.Y) / 2;
    }

    private void BuildLayout(UiContext c, string text, IDWriteTextFormat fmt, float maxW, float size)
    {
        _layout?.Dispose();
        _layoutText = text; _layoutMax = maxW; _layoutSize = size; _layoutFmt = fmt;
        _layout = MakeLayout(c, text, fmt, maxW, size);
        var m = _layout.Metrics;
        // text-wrap: balance – über mehrere Zeilen so schmal wie möglich bei gleicher Zeilenzahl
        if (m.LineCount > 1)
        {
            uint lines = m.LineCount;
            float lo = maxW / lines * 0.8f, hi = maxW;
            for (int i = 0; i < 9; i++)
            {
                float mid = (lo + hi) / 2;
                _layout.MaxWidth = mid;
                if (_layout.Metrics.LineCount > lines) lo = mid; else hi = mid;
            }
            _layout.MaxWidth = hi;
            m = _layout.Metrics;
        }
        _textSize = new Vector2(m.Width + m.Left, m.Height);

        // Strich je Zeile (folgt dem Text über die Zeilen)
        _strikeLines.Clear();
        _strikeTotal = 0;
        uint pos = 0;
        float y = 0;
        foreach (var lm in _layout.LineMetrics)
        {
            uint len = lm.Length - lm.TrailingWhitespaceLength;
            if (len > 0)
            {
                _layout.HitTestTextPosition(pos, false, out float x0, out _, out _);
                _layout.HitTestTextPosition(pos + len - 1, true, out float x1, out _, out _);
                float sy = y + (lm.Height - Math.Max(1, 1.5f * c.K)) * 0.58f;
                _strikeLines.Add((Math.Min(x0, x1), Math.Max(x0, x1), sy));
                _strikeTotal += Math.Abs(x1 - x0);
            }
            pos += lm.Length;
            y += lm.Height;
        }
    }

    private static IDWriteTextLayout MakeLayout(UiContext c, string text, IDWriteTextFormat fmt, float maxW, float size)
    {
        var l = c.DWrite.CreateTextLayout(text, fmt, maxW, 8192);
        l.WordWrapping = WordWrapping.Wrap;
        l.TextAlignment = TextAlignment.Leading;
        // line-height: 1.35
        l.SetLineSpacing(LineSpacingMethod.Uniform, 1.35f * size, 1.35f * size * 0.78f);
        return l;
    }

    // =================================================================================================
    // Maus
    // =================================================================================================

    public bool HitTest(UiContext c, Vector2 p) => _on && _shown != null && _vis.Value(_time) > 0.5f && _row.Contains(p);

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        switch (e.Kind)
        {
            case PointerKind.Down:
                if (e.Button != 0 || !HitTest(c, e.Pos)) return false;
                _pressed = true;
                return true;
            case PointerKind.Up:
                if (!_pressed) return false;
                _pressed = false;
                if (HitTest(c, e.Pos))
                {
                    if (_isDone) UndoTask();
                    else CompleteTask(c);
                }
                return true;
            case PointerKind.Leave:
                _pressed = false;
                return false;
        }
        return false;
    }

    // =================================================================================================
    // Zeichnen
    // =================================================================================================

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        float vis = _vis.Value(_time);
        if (vis <= 0.003f || _layout == null || _shown == null) return;
        _p.Device(ctx, c);
        float K = c.K;
        var prev = ctx.Transform;
        try
        {
            // Wechsel: hinaus nach oben, herein von unten (10 px, wie im Original)
            float rowA = 1, rowY = 0;
            if (_rowPhase == 1)
            {
                float t = OutCurve.Ease((_time - _rowStart) / 0.42);
                rowA = 1 - t; rowY = -10 * t;
            }
            else if (_rowPhase == 2)
            {
                float t = InCurve.Ease((_time - _rowStart) / 0.62);
                rowA = t; rowY = 10 * (1 - t);
            }
            float a = vis * rowA;
            var baseT = Matrix3x2.CreateTranslation(0, rowY) * prev;
            ctx.Transform = baseT;

            if (a > 0.003f)
            {
                DrawCheck(ctx, c, a, baseT);
                ctx.Transform = baseT;

                // Text: wird beim Erledigen blasser; der Strich wächst von links nach rechts
                float dim = _dim.Value(_time);
                float textA = 0.88f + (0.45f - 0.88f) * dim;
                ctx.DrawTextLayout(new Vector2(_textX, _textY), _layout, _p.Brush(c.Fg, textA * a), DrawTextOptions.None);
                float st = _strike.Value(_time);
                if (st > 0.001f && _strikeTotal > 0)
                {
                    float left = st * _strikeTotal, th = Math.Max(1, 1.5f * K);
                    var b = _p.Brush(c.Fg, textA * a);
                    foreach (var (x0, x1, y) in _strikeLines)
                    {
                        if (left <= 0) break;
                        float w = Math.Min(left, x1 - x0);
                        ctx.FillRectangle(new System.Drawing.RectangleF(_textX + x0, _textY + y, w, th), b);
                        left -= w;
                    }
                }
            }
            ctx.Transform = prev;

            // Lob darunter (außerhalb des Flusses)
            float ca = _cheerA.Value(_time) * vis;
            if (ca > 0.003f && _cheerMain.Length > 0)
            {
                float y = _row.Bottom + 12 * K + 8 * K * _cheerY.Value(_time);
                float cx = _row.X + _row.Width / 2;
                var fMain = _p.Fmt(c, 16 * K, 600);
                var ms = _p.Size(c, _cheerMain, fMain);
                // Leises Leuchten hinter dem Lob (text-shadow)
                var glow = _p.Brush(c.Accent, 0.06f * ca);
                for (int i = 0; i < 3; i++)
                {
                    float gx = ms.X / 2 + (6 + 6 * i) * K, gy = ms.Y / 2 + (2 + 4 * i) * K;
                    ctx.FillEllipse(new Ellipse(new Vector2(cx, y + ms.Y / 2), gx, gy), glow);
                }
                _p.Text(ctx, c, _cheerMain, fMain, cx - ms.X / 2, y, c.Accent, ca);
                if (_cheerSub.Length > 0)
                {
                    var fSub = _p.Fmt(c, 12.5f * K, 500);
                    var ss = _p.Size(c, _cheerSub, fSub);
                    _p.Text(ctx, c, _cheerSub, fSub, cx - ss.X / 2, y + ms.Y + 4 * K, c.Fg, 0.45f * ca);
                }
            }
        }
        finally { ctx.Transform = prev; }
    }

    private void DrawCheck(ID2D1DeviceContext ctx, UiContext c, float a, Matrix3x2 baseT)
    {
        float K = c.K, u = K;   // Kreis 20 × 20 Einheiten = 20 K
        var ctr = _check;

        // Sprung beim Abhaken: 1 → .78 → 1.18 → 1 (640 ms)
        float pop = 1;
        double pt = (_time - _popStart) / 0.64;
        if (pt is >= 0 and < 1)
        {
            float e = PopCurve.Ease(pt);
            pop = e < 1 / 3f ? 1 - 0.22f * (e * 3) : e < 2 / 3f ? 0.78f + 0.40f * ((e - 1 / 3f) * 3) : 1.18f - 0.18f * ((e - 2 / 3f) * 3);
        }
        ctx.Transform = Matrix3x2.CreateScale(pop, ctr) * baseT;

        float fill = _fill.Value(_time);
        float hov = _hover.Value(_time);
        float ring = _ring.Value(_time);
        float r = 8.6f * u;

        // Leuchten um den erledigten Kreis (drop-shadow)
        if (fill > 0.01f)
        {
            float g = Math.Clamp(fill, 0, 1) * a;
            for (int i = 0; i < 4; i++)
                ctx.FillEllipse(new Ellipse(ctr, r + (2 + 2.5f * i) * K, r + (2 + 2.5f * i) * K), _p.Brush(c.Accent, 0.07f * g));
        }
        if (hov > 0.003f) ctx.FillEllipse(new Ellipse(ctr, r, r), _p.Brush(c.Fg, 0.08f * hov * a));
        if (ring > 0.003f)
            ctx.DrawEllipse(new Ellipse(ctr, r, r), _p.Brush(c.Fg, (0.5f + 0.4f * hov) * ring * a), 1.3f * u);
        if (fill > 0.001f)
        {
            float fr = r * Math.Max(0, fill);
            ctx.FillEllipse(new Ellipse(ctr, fr, fr), _p.Brush(c.Accent, a));
        }

        // Häkchen: M6.1 10.3 l2.6 2.6 5.3 -5.6, von vorn nach hinten gezeichnet
        float mk = _mark.Value(_time);
        if (mk > 0.001f)
        {
            var o = ctr - new Vector2(10 * u, 10 * u);
            var p0 = o + new Vector2(6.1f, 10.3f) * u;
            var p1 = o + new Vector2(8.7f, 12.9f) * u;
            var p2 = o + new Vector2(14f, 7.3f) * u;
            float l1 = Vector2.Distance(p0, p1), l2 = Vector2.Distance(p1, p2);
            float len = Math.Clamp(mk, 0, 1) * (l1 + l2);
            var b = _p.Brush(new Vector3(1, 1, 1), a);
            float w = 2.1f * u;
            if (len <= l1) ctx.DrawLine(p0, Vector2.Lerp(p0, p1, len / l1), b, w, _p.Round);
            else
            {
                ctx.DrawLine(p0, p1, b, w, _p.Round);
                ctx.DrawLine(p1, Vector2.Lerp(p1, p2, (len - l1) / l2), b, w, _p.Round);
            }
        }
        ctx.Transform = baseT;

        // Ring, der sich vom Kreis löst (800 ms)
        double bt = (_time - _burstStart) / 0.8;
        if (bt is >= 0 and < 1)
        {
            float e = BurstCurve.Ease(bt);
            float br = 10 * u * (1 + 1.9f * e);
            ctx.DrawEllipse(new Ellipse(ctr, br, br), _p.Brush(c.Accent, 0.9f * (1 - e) * a), 1.5f * K);
        }

        // Funken
        if (_sparksLive)
        {
            foreach (var s in _sparks)
            {
                double st = (_time - s.Start) / s.Dur;
                if (st < 0 || st >= 1) continue;
                float e = BurstCurve.Ease(st);
                var pos = ctr + new Vector2(MathF.Cos(s.Ang), MathF.Sin(s.Ang)) * s.Dist * K * e;
                float rad = (s.White ? 1.75f : 2.5f) * K * (1 - e);
                if (rad <= 0.05f) continue;
                if (!s.White) ctx.FillEllipse(new Ellipse(pos, rad * 2.2f, rad * 2.2f), _p.Brush(c.Accent, 0.25f * (1 - e) * a));
                ctx.FillEllipse(new Ellipse(pos, rad, rad), _p.Brush(s.White ? new Vector3(1, 1, 1) : c.Accent, (1 - e) * a));
            }
        }
    }

    public void Dispose()
    {
        _layout?.Dispose();
        _layout = null;
        _p.Dispose();
    }

    // =================================================================================================
    // Hilfen
    // =================================================================================================

    /// <summary>Ein Übergang wie eine CSS-Transition: von dort, wo er gerade ist, zum Ziel (mit Verzögerung).</summary>
    private struct Tween
    {
        public float From, Target;
        public double Start, Dur, Delay;
        public Bezier? Curve;

        public void Set(float v) { From = Target = v; Dur = 0; }

        public void To(float target, double now, double dur, double delay, Bezier curve)
        {
            From = Value(now);
            Target = target;
            Start = now;
            Dur = dur;
            Delay = delay;
            Curve = curve;
        }

        public readonly float Value(double now)
        {
            if (Dur <= 0 || Curve == null) return Target;
            double t = (now - Start - Delay) / Dur;
            if (t <= 0) return From;
            if (t >= 1) return Target;
            return From + (Target - From) * Curve.Ease(t);
        }

        public readonly bool Busy(double now) => Dur > 0 && now - Start < Delay + Dur + 0.02;
    }

    /// <summary>cubic-bezier(x1, y1, x2, y2) wie in CSS.</summary>
    private sealed class Bezier(double x1, double y1, double x2, double y2)
    {
        private static double B(double a, double b, double t) => 3 * a * t * (1 - t) * (1 - t) + 3 * b * t * t * (1 - t) + t * t * t;

        public float Ease(double x)
        {
            x = Math.Clamp(x, 0, 1);
            if (x <= 0 || x >= 1) return (float)x;
            double lo = 0, hi = 1, t = x;
            for (int i = 0; i < 24; i++)
            {
                t = (lo + hi) / 2;
                if (B(x1, x2, t) < x) lo = t; else hi = t;
            }
            return (float)B(y1, y2, t);
        }
    }
}

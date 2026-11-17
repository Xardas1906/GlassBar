using System.Numerics;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Die Fokus-Pille (pills.js „Focus timer“, Pomodoro): als Pille ein Ring, der sich mit der Zeit schließt, und die
/// Restzeit; als Karte Phase mit Punkten für die Runde, „N von Ziel heute“, die große Zeit, ein dünner Balken und
/// Zurücksetzen / Start-Pause / Weiter. Fokus → kurze Pause, jede n-te → lange Pause, jede Pause → Fokus.
/// Läuft über Neustarts weiter (Endzeitpunkt gespeichert); eine Runde, die bei geschlossenem Fenster endete, zählt still.
/// Gespeichert in <see cref="StillState"/> unter "pomo.timer".
/// </summary>
public sealed class PomodoroPill : Pill
{
    public override string Id => "timer";

    private const string StateKey = "pomo.timer";
    private const string ResetPath = "M4 12a8 8 0 1 0 2.35-5.65M4 4v4.5h4.5";
    private const string PlayPath = "M8 5.6v12.8a1.1 1.1 0 0 0 1.7.93l10-6.4a1.1 1.1 0 0 0 0-1.86l-10-6.4A1.1 1.1 0 0 0 8 5.6z";
    private const string SkipPath = "M5 6.2v11.6a.9.9 0 0 0 1.4.75l8.3-5.8a.9.9 0 0 0 0-1.5L6.4 5.45A.9.9 0 0 0 5 6.2z";

    private static readonly Vector3 Coral = new(255 / 255f, 138 / 255f, 122 / 255f);   // Fokus
    private static readonly Vector3 Mint = new(110 / 255f, 231 / 255f, 168 / 255f);    // kurze Pause
    private static readonly Vector3 Sky = new(120 / 255f, 200 / 255f, 1f);             // lange Pause

    /// <summary>Was das Original in localStorage („still.timer.v1“) ablegt.</summary>
    private sealed class TimerState
    {
        public string Phase { get; set; } = "focus";    // focus | short | long
        public string Status { get; set; } = "idle";    // idle | running | paused
        public double EndsAt { get; set; }
        public double Remaining { get; set; }
        public int Cycle { get; set; }
        public string Day { get; set; } = "";
        public int Done { get; set; }
    }

    private readonly WaterTimerKit _kit = new();
    private readonly TimerState _ts;

    /// <summary>Läuft gerade eine Fokus-Einheit? (für die Klanglandschaft „Fokus“)</summary>
    public bool FocusRunning => _ts.Phase == "focus" && _ts.Status == "running";
    private StillValues _settings = new();
    private bool _alert, _booted;
    private double _tick = 1;
    private double _lastFocus = -1, _lastShort = -1, _lastLong = -1;

    // Anzeige
    private string _valueText = "", _metaText = "", _title = "";
    private string _mText = "", _sText = "";
    private double _frac;
    private int _dots = 4, _filled;

    // Bewegung
    private readonly WaterTimerKit.Spring _bar = new(0, 0.62, 0.75);   // .tp-fill transition .9s spring
    private float _arc;                                                 // .ring-arc transition .6s ease-out
    private float _runA, _dotA = 1, _valueA = 0.55f, _timeA = 0.96f;
    private Vector3 _tp = Coral;

    // Texte
    private readonly WaterTimerKit.Txt _value = new(), _head = new(), _meta = new(), _min = new(), _colon = new(), _sec = new();

    // Geometrie und Pinsel
    private ID2D1PathGeometry? _reset, _play, _skip, _arcGeo;
    private ID2D1RoundedRectangleGeometry? _pause1, _pause2, _skipBar;
    private ID2D1GeometryGroup? _pause, _skipIcon;
    private int _arcKey = -1;
    private ID2D1LinearGradientBrush? _fillBrush;
    private Vector3 _brushTp = new(-1);

    // Knöpfe
    private Rect _rReset, _rMain, _rSkip;
    private int _pressed;   // 1 = Zurücksetzen, 2 = Start/Pause, 3 = Weiter

    public PomodoroPill()
    {
        _ts = StillState.Get<TimerState?>(StateKey, null) ?? new TimerState { Day = WaterTimerKit.DayKey(DateTime.Now) };
        if (_ts.Phase is not ("focus" or "short" or "long")) _ts.Phase = "focus";
        if (_ts.Status is not ("idle" or "running" or "paused")) _ts.Status = "idle";
    }

    // ---------------------------------------------------------------------------------------------
    // Pille
    // ---------------------------------------------------------------------------------------------

    public override bool Shown(UiContext c) => c.Settings.Bool("pomo");

    public override bool Alert => _alert;

    public override bool Animating =>
        _kit.Moving || _bar.Moving || _ts.Status == "running"
        || MathF.Abs(_arc - (float)_frac) > 0.001f || _runA is > 0.002f and < 0.998f;

    public override Vector2 CompactSize(UiContext c)
    {
        Prepare(c);
        float w = Math.Max(40, 36 + _value.Width / Math.Max(0.01f, c.K) + 15);
        return new Vector2(w * c.K, 40 * c.K);
    }

    // Etwas breiter als die Wasser-Karte: die Überschrift behält denselben Platz
    public override Vector2 CardSize(UiContext c) => new(328 * c.K, 168 * c.K);

    /// <summary>Heute zurücksetzen (wie S.timer.resetToday).</summary>
    public void ResetToday()
    {
        _ts.Done = 0;
        _ts.Cycle = 0;
        Save();
        _tick = 1;
    }

    /// <summary>Heute abgeschlossene Fokus-Runden.</summary>
    public int TodayDone => _ts.Done;

    public override void Update(UiContext c, double dt)
    {
        _settings = c.Settings;
        Boot(c);
        _kit.StepOpen(Open, dt);
        if (Hovered) _alert = false;
        // (die Ankündigung zählt die Oberfläche herunter)

        _tick += dt;
        if (_tick >= 0.25 || _valueText.Length == 0)
        {
            _tick = 0;
            WatchDurations();
            Rollover(c);
            CheckTimer(c);
            Render(c);
        }

        float a6 = 1 - MathF.Exp(-(float)dt / 0.2f);    // .6s
        float a4 = 1 - MathF.Exp(-(float)dt / 0.13f);   // .4s
        float a25 = 1 - MathF.Exp(-(float)dt / 0.08f);  // .25s
        bool running = _ts.Status == "running";
        _runA += ((running ? 1f : 0f) - _runA) * a25;
        _dotA += ((running ? 0f : 1f) - _dotA) * a4;
        _valueA += ((running ? 0.92f : 0.55f) - _valueA) * a4;
        _timeA += ((_ts.Status == "paused" ? 0.6f : 0.96f) - _timeA) * a4;
        // ease-out: zügig los, sanft an
        _arc += ((float)_frac - _arc) * (1 - MathF.Exp(-(float)dt / 0.15f));
        var tp = WaterTimerKit.CardColor(c, _ts.Phase == "short" ? Mint : _ts.Phase == "long" ? Sky : Coral);
        _tp += (tp - _tp) * a6;
        _bar.Target = _frac;
        _bar.Step(dt);
    }

    private void Boot(UiContext c)
    {
        if (_booted) return;
        _booted = true;
        _settings = c.Settings;
        if (_ts.Remaining <= 0) { _ts.Remaining = PhaseLen(_ts.Phase); Save(); }
        // Eine Runde, die bei geschlossenem Fenster endete, zählt trotzdem – still
        if (_ts.Status == "running" && NowMs(c) >= _ts.EndsAt) Advance(c, true, quiet: true);
    }

    private void Prepare(UiContext c)
    {
        _settings = c.Settings;
        Boot(c);
        if (_valueText.Length == 0) Render(c);
        _kit.CheckFonts(c);
        _value.Set(c.DWrite, _kit.Fmt(c, 13.5f, 600), _valueText, _kit.Tnum(c));
    }

    // ---------------------------------------------------------------------------------------------
    // Logik (pills.js)
    // ---------------------------------------------------------------------------------------------

    private static double NowMs(UiContext c) => WaterTimerKit.NowMs(WaterTimerKit.Now(c));

    private void Save() => StillState.Set(StateKey, _ts);

    private double PhaseLen(string p) =>
        Math.Max(1.0 / 60, p == "focus" ? _settings.Num("focusMin") : p == "short" ? _settings.Num("shortMin") : _settings.Num("longMin")) * 60;

    private int LongEvery => Math.Clamp((int)Math.Round(_settings.Num("longEvery")), 1, 12);

    private double TimeLeft(double now) => _ts.Status == "running" ? Math.Max(0, (_ts.EndsAt - now) / 1000) : _ts.Remaining;

    /// <summary>Neue Dauern gelten sofort für einen Timer, der nicht läuft.</summary>
    private void WatchDurations()
    {
        double f = _settings.Num("focusMin"), s = _settings.Num("shortMin"), l = _settings.Num("longMin");
        bool changed = _lastFocus >= 0 && (f != _lastFocus || s != _lastShort || l != _lastLong);
        _lastFocus = f; _lastShort = s; _lastLong = l;
        if (changed && _ts.Status == "idle")
        {
            _ts.Remaining = PhaseLen(_ts.Phase);
            Save();
        }
    }

    private void Rollover(UiContext c)
    {
        string day = WaterTimerKit.DayKey(WaterTimerKit.Now(c));
        if (_ts.Day == day) return;
        _ts.Day = day;
        _ts.Done = 0;
        Save();
    }

    private void Start(UiContext c)
    {
        _ts.Status = "running";
        _ts.EndsAt = NowMs(c) + _ts.Remaining * 1000;
        Save();
        Render(c);
    }

    private void Pause(UiContext c)
    {
        _ts.Remaining = TimeLeft(NowMs(c));
        _ts.Status = "paused";
        Save();
        Render(c);
    }

    private void Reset(UiContext c)
    {
        _ts.Status = "idle";
        _ts.Remaining = PhaseLen(_ts.Phase);
        Save();
        Render(c);
    }

    // Fokus → kurze Pause, jede n-te Fokus-Runde → lange Pause, jede Pause → Fokus
    private void Advance(UiContext c, bool completed, bool quiet = false)
    {
        if (_ts.Phase == "focus")
        {
            if (completed)
            {
                Rollover(c);
                _ts.Done++;
                _ts.Cycle++;
            }
            _ts.Phase = completed && _ts.Cycle % LongEvery == 0 ? "long" : "short";
        }
        else
        {
            if (_ts.Phase == "long") _ts.Cycle = 0;
            _ts.Phase = "focus";
        }
        _ts.Remaining = PhaseLen(_ts.Phase);
        bool auto = _ts.Phase == "focus" ? _settings.Bool("autoFocus") : _settings.Bool("autoBreaks");
        if (auto && !quiet)
        {
            _ts.Status = "running";
            _ts.EndsAt = NowMs(c) + _ts.Remaining * 1000;
        }
        else _ts.Status = "idle";
        Save();
        Render(c);
    }

    private void CheckTimer(UiContext c)
    {
        if (_ts.Status != "running" || NowMs(c) < _ts.EndsAt) return;
        bool wasFocus = _ts.Phase == "focus";
        Advance(c, true);
        if (_settings.Bool("pomoChime")) Tones.Play(wasFocus ? "pomo-focus" : "pomo-break");
        _alert = true;
        AnnounceFor = 6;
    }

    private void Render(UiContext c)
    {
        _settings = c.Settings;
        double left = TimeLeft(NowMs(c)), total = PhaseLen(_ts.Phase);
        _frac = _ts.Status == "idle" ? 0 : Math.Clamp(1 - left / total, 0, 1);
        long s = (long)Math.Ceiling(left - 1e-9);
        string m = (s / 60).ToString(), sec = (s % 60).ToString("00");
        if (m != _mText || sec != _sText)
        {
            _mText = m;
            _sText = sec;
            _valueText = m + ":" + sec;
        }
        string title = L.T(_ts.Phase == "focus" ? "Fokus" : _ts.Phase == "short" ? "Kurze Pause" : "Lange Pause");
        if (title != _title) _title = title;
        _dots = LongEvery;
        _filled = _ts.Phase == "long" ? _dots : _ts.Cycle % _dots;
        string meta = L.F("{0} von {1} heute", _ts.Done, WaterTimerKit.Fixed2(_settings.Num("pomoGoal")));
        if (meta != _metaText) _metaText = meta;
    }

    // ---------------------------------------------------------------------------------------------
    // Maus
    // ---------------------------------------------------------------------------------------------

    private int HitButton(Vector2 p)
    {
        if (_kit.Ok < 0.6f) return 0;
        if (WaterTimerKit.In(_rReset, p)) return 1;
        if (WaterTimerKit.In(_rMain, p)) return 2;
        if (WaterTimerKit.In(_rSkip, p)) return 3;
        return 0;
    }

    public override bool OnPointer(UiContext c, PointerEvent e)
    {
        switch (e.Kind)
        {
            case PointerKind.Down when e.Button == 0:
                _pressed = HitButton(e.Pos);
                return _pressed != 0;
            case PointerKind.Up when e.Button == 0:
            {
                int hit = HitButton(e.Pos), was = _pressed;
                _pressed = 0;
                if (hit == 0 || hit != was) return was != 0;
                _settings = c.Settings;
                if (hit == 1) Reset(c);
                else if (hit == 2) { if (_ts.Status == "running") Pause(c); else Start(c); }
                else Advance(c, false);
                return true;
            }
            case PointerKind.Leave:
                _pressed = 0;
                return false;
            default:
                return HitButton(e.Pos) != 0;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Zeichnen
    // ---------------------------------------------------------------------------------------------

    private void EnsureGeometry(UiContext c)
    {
        if (_reset != null) return;
        var f = c.D2D;
        _reset = WaterTimerKit.Svg(f, ResetPath);
        _play = WaterTimerKit.Svg(f, PlayPath);
        _skip = WaterTimerKit.Svg(f, SkipPath);
        _pause1 = f.CreateRoundedRectangleGeometry(WaterTimerKit.Rr(new Rect(6.2f, 4.8f, 4.2f, 14.4f), 1.4f));
        _pause2 = f.CreateRoundedRectangleGeometry(WaterTimerKit.Rr(new Rect(13.6f, 4.8f, 4.2f, 14.4f), 1.4f));
        _skipBar = f.CreateRoundedRectangleGeometry(WaterTimerKit.Rr(new Rect(16.5f, 5.5f, 2.6f, 13), 1.3f));
        _pause = f.CreateGeometryGroup(FillMode.Winding, [_pause1, _pause2]);
        _skipIcon = f.CreateGeometryGroup(FillMode.Winding, [_skip, _skipBar]);
    }

    /// <summary>Bogen des Rings (r = 9 im 24er-Raster) von oben im Uhrzeigersinn; nur bei Änderung neu.</summary>
    private ID2D1PathGeometry? Arc(UiContext c, float frac)
    {
        int key = (int)MathF.Round(Math.Clamp(frac, 0, 1) * 1000);
        if (key <= 0) return null;
        if (_arcGeo != null && key == _arcKey) return _arcGeo;
        _arcGeo?.Dispose();
        _arcKey = key;
        float a = Math.Min(key, 999.5f) / 1000f * MathF.Tau;
        var g = c.D2D.CreatePathGeometry();
        using (var sink = g.Open())
        {
            var start = new Vector2(12, 3);
            var end = new Vector2(12 + 9 * MathF.Sin(a), 12 - 9 * MathF.Cos(a));
            sink.BeginFigure(start, FigureBegin.Hollow);
            sink.AddArc(new ArcSegment
            {
                Point = end,
                Size = new Size(9, 9),
                RotationAngle = 0,
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = a > MathF.PI ? ArcSize.Large : ArcSize.Small,
            });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        _arcGeo = g;
        return g;
    }

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_kit.EnsureDevice(ctx)) { _fillBrush?.Dispose(); _fillBrush = null; }
        EnsureGeometry(c);
        Prepare(c);
        if (_fillBrush == null || Vector3.DistanceSquared(_brushTp, _tp) > 1e-6f)
        {
            _fillBrush?.Dispose();
            _brushTp = _tp;
            _fillBrush = WaterTimerKit.Gradient(ctx, _tp, 0.7f, 1f);
        }
        var brush = _kit.Brush!;
        var prev = ctx.Transform;
        try
        {
            float k = c.K, e = Open, ec = Math.Clamp(e, 0, 1);
            float x = Bounds.Left, y = Bounds.Top, W = Bounds.Width;

            // Kompakt: Restzeit neben dem Ring
            float oc = _kit.Oc;
            if (oc > 0.01f)
            {
                _kit.Color(c.Fg, _valueA * oc);
                _value.Draw(ctx, brush, x + 36 * k + (1 - oc) * 6 * k, y + (40 * k - _value.Height) / 2);
            }

            DrawRing(ctx, c, prev, x, y, e, ec);

            float ok = _kit.Ok;
            if (ok > 0.01f) DrawCard(ctx, c, prev, x, y + (1 - ok) * 6 * k, W, ok);
            else _rReset = _rMain = _rSkip = default;
        }
        finally
        {
            ctx.Transform = prev;
        }
    }

    private void DrawRing(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, float x, float y, float e, float ec)
    {
        float k = c.K;
        float size = WaterTimerKit.Lerp(20, 30, e) * k, ip = WaterTimerKit.Lerp(10, 16, ec) * k;
        ctx.Transform = Matrix3x2.CreateScale(size / 24) * Matrix3x2.CreateTranslation(x + ip, y + ip) * prev;
        var brush = _kit.Brush!;
        var center = new Vector2(12, 12);
        _kit.Color(c.Fg, 0.16f);
        ctx.DrawEllipse(new Ellipse(center, 9, 9), brush, 2.2f);
        var arc = Arc(c, _arc);
        if (arc != null)
        {
            _kit.Color(_tp, 1);
            if (_arcKey >= 1000) ctx.DrawEllipse(new Ellipse(center, 9, 9), brush, 2.2f);
            else ctx.DrawGeometry(arc, brush, 2.2f, _kit.RoundStroke(c));
        }
        if (_dotA > 0.01f)
        {
            _kit.Color(_tp, _dotA);
            ctx.FillEllipse(new Ellipse(center, 2.2f, 2.2f), brush);
        }
        ctx.Transform = prev;
    }

    private void DrawCard(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, float x, float y, float W, float ok)
    {
        float k = c.K;
        var brush = _kit.Brush!;
        var fg = c.Fg;

        // Kopf: Phase und Punkte links, „N von Ziel heute“ rechts
        _head.Set(c.DWrite, _kit.Fmt(c, 14.5f, 600), _title, null);
        _meta.Set(c.DWrite, _kit.Fmt(c, 12, 550), _metaText, _kit.Tnum(c), trim: true);
        float headL = x + 58 * k, headR = x + W - 18 * k, headTop = y + 18 * k;
        float baseline = headTop + Math.Max(_head.Baseline, _meta.Baseline);
        _kit.Color(fg, 0.96f * ok);
        _head.Draw(ctx, brush, headL, baseline - _head.Baseline);

        float dotR = 3 * k, dotX = headL + _head.Width + 10 * k + dotR, dotY = baseline - _head.Baseline + _head.Height / 2;
        for (int i = 0; i < _dots; i++)
        {
            var center = new Vector2(dotX + i * 10 * k, dotY);
            if (i < _filled)
            {
                // .is-on: Farbe mit leisem Schein
                _kit.Color(_tp, 0.12f * ok);
                ctx.FillEllipse(new Ellipse(center, dotR + 3 * k, dotR + 3 * k), brush);
                _kit.Color(_tp, 0.22f * ok);
                ctx.FillEllipse(new Ellipse(center, dotR + 1.5f * k, dotR + 1.5f * k), brush);
                _kit.Color(_tp, ok);
            }
            else _kit.Color(fg, 0.18f * ok);
            ctx.FillEllipse(new Ellipse(center, dotR, dotR), brush);
        }
        float headUsed = _head.Width + 10 * k + (_dots * 10 - 4) * k;
        float metaRoom = headR - headL - headUsed - 10 * k;
        if (metaRoom > 4)
        {
            _meta.MaxWidth(metaRoom);
            float mw = Math.Min(_meta.Width, metaRoom);
            _kit.Color(fg, 0.55f * ok);
            _meta.Draw(ctx, brush, headR - mw, baseline - _meta.Baseline);
        }

        // Große Zeit in der Uhrschrift, Doppelpunkt etwas angehoben wie bei der Uhr
        var (weight, track) = WaterTimerKit.ClockFont(c.Settings);
        float size = 44 * k;
        var fmt = _kit.Fmt(c, 44, weight, display: true);
        var tnum = _kit.Tnum(c);
        _min.Set(c.DWrite, fmt, _mText, tnum, track: track * size);
        _colon.Set(c.DWrite, fmt, ":", null, track: track * size);
        _sec.Set(c.DWrite, fmt, _sText, tnum, track: track * size);
        float total = _min.Width + _colon.Width + _sec.Width;
        float tx = x + 18 * k + (W - 36 * k - total) / 2;
        float ty = y + 40 * k + (1.1f * size - _min.Height) / 2;
        _kit.Color(fg, _timeA * ok);
        _min.Draw(ctx, brush, tx, ty);
        _colon.Draw(ctx, brush, tx + _min.Width, ty - 0.06f * size);
        _sec.Draw(ctx, brush, tx + _min.Width + _colon.Width, ty);

        // Dünner Balken
        var bar = new Rect(x + 18 * k, y + 100 * k, Math.Max(0, W - 36 * k), 4 * k);
        WaterTimerKit.Bar(ctx, _kit, bar, (float)Math.Clamp(_bar.X, 0, 1), fg, _fillBrush!, ok);

        // Knöpfe: Zurücksetzen · Start/Pause · Weiter
        float rowL = x + 16 * k, rowR = x + W - 16 * k, mid = y + 114 * k + 22 * k;
        float r36 = 36 * k, r44 = 44 * k, gap = 28 * k;
        float cx = (rowL + rowR) / 2;
        _rMain = new Rect(cx - r44 / 2, mid - r44 / 2, r44, r44);
        _rReset = new Rect(_rMain.Left - gap - r36, mid - r36 / 2, r36, r36);
        _rSkip = new Rect(_rMain.Right + gap, mid - r36 / 2, r36, r36);

        bool inside = Hovered && c.PointerInside;
        var p = c.Pointer;

        DrawRound(ctx, c, prev, _rReset, _reset!, stroke: true, inside && WaterTimerKit.In(_rReset, p), _pressed == 1, ok);
        DrawRound(ctx, c, prev, _rSkip, _skipIcon!, stroke: false, inside && WaterTimerKit.In(_rSkip, p), _pressed == 3, ok);

        // Start/Pause: getönt, Symbole tauschen sich mit Drehung
        bool hm = inside && WaterTimerKit.In(_rMain, p);
        WaterTimerKit.Press(ctx, _rMain, _pressed == 2 && hm ? 0.88f : 1, prev);
        var mc = WaterTimerKit.Center(_rMain);
        _kit.Color(_tp, (hm ? 0.34f : 0.24f) * ok);
        ctx.FillEllipse(new Ellipse(mc, r44 / 2, r44 / 2), brush);
        _kit.Color(_tp, 0.35f * ok);
        ctx.DrawEllipse(new Ellipse(mc, r44 / 2 - 0.5f, r44 / 2 - 0.5f), brush, 1);
        float run = _runA;
        if (run < 0.99f)
        {
            _kit.Color(fg, (1 - run) * ok);
            _kit.Icon(ctx, c, _play!, mc, 20 * k, stroke: false, WaterTimerKit.Lerp(1, 0.5f, run), WaterTimerKit.Lerp(0, -30, run));
        }
        if (run > 0.01f)
        {
            _kit.Color(fg, run * ok);
            _kit.Icon(ctx, c, _pause!, mc, 20 * k, stroke: false, WaterTimerKit.Lerp(0.5f, 1, run), WaterTimerKit.Lerp(30, 0, run));
        }
        ctx.Transform = prev;
    }

    private void DrawRound(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, Rect r, ID2D1Geometry icon, bool stroke, bool hover,
        bool pressed, float ok)
    {
        var brush = _kit.Brush!;
        WaterTimerKit.Press(ctx, r, pressed && hover ? 0.88f : 1, prev);
        var center = WaterTimerKit.Center(r);
        _kit.Color(c.Fg, (hover ? 0.14f : 0.08f) * ok);
        ctx.FillEllipse(new Ellipse(center, r.Width / 2, r.Height / 2), brush);
        _kit.Color(c.Fg, (hover ? 1f : 0.8f) * ok);
        _kit.Icon(ctx, c, icon, center, 18 * c.K, stroke);
        ctx.Transform = prev;
    }

    public override void Dispose()
    {
        _value.Dispose(); _head.Dispose(); _meta.Dispose(); _min.Dispose(); _colon.Dispose(); _sec.Dispose();
        _pause?.Dispose(); _skipIcon?.Dispose();
        _reset?.Dispose(); _play?.Dispose(); _skip?.Dispose(); _arcGeo?.Dispose();
        _pause1?.Dispose(); _pause2?.Dispose(); _skipBar?.Dispose();
        _pause = _skipIcon = null;
        _reset = _play = _skip = _arcGeo = null;
        _pause1 = _pause2 = _skipBar = null;
        _arcKey = -1;
        _fillBrush?.Dispose();
        _fillBrush = null;
        _kit.Dispose();
    }
}

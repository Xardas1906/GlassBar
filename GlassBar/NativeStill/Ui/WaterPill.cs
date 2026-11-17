using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Die Wasser-Pille (pills.js „Water“): zeigt, wie viel heute getrunken wurde, und füllt sich dabei wie ein Glas –
/// ein weicher Farbton steigt von unten, oben treiben zwei langsame Wellen gegeneinander. Als Karte: Überschrift mit
/// Stand, Balken, ein Satz zum Stand, „+ Becher“, „Später“ (nur wenn eine Erinnerung fällig ist) und Rückgängig.
/// Erinnert in den aktiven Stunden eine Weile nach dem letzten Becher (einmal je fälliger Zeit, auch über Neustarts).
/// Gespeichert in <see cref="StillState"/> unter "water.today".
/// </summary>
public sealed class WaterPill : Pill
{
    public override string Id => "water";

    private const double Oz = 29.5735;
    private const string StateKey = "water.today";
    private const string DropPath = "M12 2.6c-.35 0-.67.18-.86.47C9.05 6.25 5.3 10.95 5.3 14.7a6.7 6.7 0 0 0 13.4 0c0-3.75-3.75-8.45-5.84-11.63A1.03 1.03 0 0 0 12 2.6z";
    private const string UndoPath = "M9 7H5V3M5.4 7.1A8 8 0 1 1 4 12";
    private static readonly Vector3 Blue = new(120 / 255f, 200 / 255f, 1f);

    /// <summary>Was das Original in localStorage („still.water.v1“) ablegt.</summary>
    private sealed class WaterDay
    {
        public string Day { get; set; } = "";
        public double Ml { get; set; }
        public List<Cup> Log { get; set; } = new();
        public double LastAt { get; set; }
        public double SnoozeUntil { get; set; }
        public double AlertFor { get; set; }
    }

    private sealed class Cup
    {
        public double T { get; set; }
        public double Ml { get; set; }
    }

    private readonly WaterTimerKit _kit = new();
    private readonly WaterDay _ws;
    private readonly double _bootAt;
    private bool _alert;
    private double _tick = 1;
    private StillValues? _settings;

    // Anzeige (bei jedem Takt neu bestimmt)
    private string _valueText = "", _metaText = "", _subText = "", _drinkText = "";
    private bool _alerting, _full, _canUndo;
    private double _frac;

    // Bewegung
    private readonly WaterTimerKit.Spring _lvl = new(0, 0.62, 0.9);      // Wasserstand (transition 1.2s spring)
    private readonly WaterTimerKit.Spring _bar = new(0, 0.62, 0.75);     // Balken (transition .9s spring)
    private float _waterA;                                               // [data-empty] → Deckkraft 0
    private float _laterA;
    private double _rippleAt = -10;
    private Vector3 _tp = Blue;

    // Texte
    private readonly WaterTimerKit.Txt _value = new(), _title = new(), _meta = new(), _sub = new(), _drink = new(), _later = new();

    // Geometrie (Fabrik-Ressourcen) und Pinsel (Geräte-Ressourcen)
    private ID2D1PathGeometry? _drop, _undo, _waveBack, _waveFront;
    private ID2D1RoundedRectangleGeometry? _clip;
    private Vector3 _clipKey;
    private ID2D1LinearGradientBrush? _bodyBrush, _fillBrush;
    private Vector3 _brushTp = new(-1);

    // Knöpfe (absolute Rechtecke vom letzten Zeichnen)
    private Rect _rDrink, _rLater, _rUndo;
    private int _pressed;   // 1 = Becher, 2 = Später, 3 = Rückgängig

    public WaterPill()
    {
        _bootAt = WaterTimerKit.NowMs(DateTime.Now);
        _ws = StillState.Get<WaterDay?>(StateKey, null) ?? new WaterDay { Day = WaterTimerKit.DayKey(DateTime.Now) };
        _ws.Log ??= new();
    }

    // ---------------------------------------------------------------------------------------------
    // Pille
    // ---------------------------------------------------------------------------------------------

    public override bool Shown(UiContext c) => c.Settings.Bool("hydrate");

    public override bool Alert => _alert;

    public override bool Animating =>
        _kit.Moving || _lvl.Moving || _bar.Moving || _waterA is > 0.003f and < 0.997f || _laterA is > 0.003f and < 0.997f
        || (_waterA > 0.003f && _lvl.X > 0.0005)   // die Wellen treiben immer
        || _rippleAt > 0;

    public override Vector2 CompactSize(UiContext c)
    {
        Prepare(c);
        float w = Math.Max(40, 36 + _value.Width / Math.Max(0.01f, c.K) + 15);
        return new Vector2(w * c.K, 40 * c.K);
    }

    public override Vector2 CardSize(UiContext c) => new(300 * c.K, 150 * c.K);

    /// <summary>Heute zurücksetzen (wie S.water.resetToday).</summary>
    public void ResetToday()
    {
        _ws.Ml = 0;
        _ws.Log.Clear();
        _ws.LastAt = _ws.SnoozeUntil = _ws.AlertFor = 0;
        Save();
        _tick = 1;
    }

    /// <summary>Heute getrunken (ml).</summary>
    public double TodayMl => _ws.Ml;

    public override void Update(UiContext c, double dt)
    {
        _settings = c.Settings;
        _kit.StepOpen(Open, dt);
        if (Hovered) _alert = false;   // mouseenter nimmt die Hervorhebung zurück
        // (die Ankündigung zählt die Oberfläche herunter)

        _tick += dt;
        if (_tick >= 0.25 || _valueText.Length == 0)
        {
            _tick = 0;
            Check(c);
            Render(c);
        }

        float a = 1 - MathF.Exp(-(float)dt / 0.2f);   // transition .6s ease
        _waterA += ((_frac > 0 ? 1f : 0f) - _waterA) * a;
        _laterA = _alerting ? 1 : 0;
        _lvl.Target = _frac;
        _bar.Target = _frac;
        _lvl.Step(dt);
        _bar.Step(dt);
        var tp = WaterTimerKit.CardColor(c, Blue);
        _tp += (tp - _tp) * a;
        if (_rippleAt > 0 && c.Time - _rippleAt > 0.6) _rippleAt = -10;
    }

    private void Prepare(UiContext c)
    {
        _settings = c.Settings;
        if (_valueText.Length == 0) Render(c);
        _kit.CheckFonts(c);
        _value.Set(c.DWrite, _kit.Fmt(c, 13.5f, 600), _valueText, _kit.Tnum(c));
    }

    // ---------------------------------------------------------------------------------------------
    // Logik (pills.js)
    // ---------------------------------------------------------------------------------------------

    private double Goal => Math.Max(1, _settings?.Num("waterGoal") ?? 2000);
    private double CupMl => Math.Max(1, _settings?.Num("cupSize") ?? 250);
    private bool OzUnit => _settings?.Str("waterUnit") == "oz";

    private void Save() => StillState.Set(StateKey, _ws);

    private void Rollover(DateTime now)
    {
        string day = WaterTimerKit.DayKey(now);
        if (_ws.Day == day) return;
        _ws.Day = day;
        _ws.Ml = 0;
        _ws.Log.Clear();
        _ws.SnoozeUntil = 0;
        _ws.AlertFor = 0;
        Save();
    }

    private bool InActiveHours(DateTime t)
    {
        double a = _settings!.Num("activeFrom"), b = _settings.Num("activeTo");
        int hr = t.Hour;
        return a < b ? hr >= a && hr < b : hr >= a || hr < b;
    }

    private double ActiveStartToday(DateTime now) => WaterTimerKit.NowMs(now.Date.AddHours(_settings!.Num("activeFrom")));

    // Fällig ein Intervall nach dem letzten Becher (oder dem Beginn des aktiven Tages), nie früher als zwei
    // Minuten nach dem Start und nie vor dem Ende eines „Später“.
    private double DueAt(DateTime now)
    {
        double baseAt = Math.Max(_ws.LastAt, ActiveStartToday(now));
        return Math.Max(Math.Max(baseAt + _settings!.Num("remindEvery") * 60000, _bootAt + 120000), _ws.SnoozeUntil);
    }

    private bool Alerting(DateTime now) =>
        _settings!.Bool("hydrate") && _settings.Bool("waterRemind") && _ws.Ml < Goal && InActiveHours(now)
        && WaterTimerKit.NowMs(now) >= DueAt(now);

    private void Check(UiContext c)
    {
        var now = WaterTimerKit.Now(c);
        Rollover(now);
        if (Alerting(now))
        {
            double due = DueAt(now);
            if (_ws.AlertFor != due)
            {
                // Einmal je fälliger Zeit, auch über Neustarts hinweg
                _ws.AlertFor = due;
                Save();
                _alert = true;
                AnnounceFor = 7;
                if (c.Settings.Bool("waterChime")) Tones.Play("water");
            }
        }
        else _alert = false;
    }

    private string FmtVol(double ml)
    {
        if (OzUnit) return $"{WaterTimerKit.JsRound(ml / Oz)} oz";
        if (ml >= 1000) return $"{WaterTimerKit.Fixed2(ml / 1000)} L";
        return $"{WaterTimerKit.JsRound(ml)} ml";
    }

    private string FmtPair(double ml, double goal) =>
        OzUnit ? L.F("{0} von {1} oz", WaterTimerKit.JsRound(ml / Oz), WaterTimerKit.JsRound(goal / Oz))
               : L.F("{0} von {1} L", WaterTimerKit.Fixed2(ml / 1000), WaterTimerKit.Fixed2(goal / 1000));

    private static string UntilText(double ms)
    {
        long m = Math.Max(1, WaterTimerKit.JsRound(ms / 60000));
        return m >= 60 ? $"{m / 60} h {(m % 60 != 0 ? $"{m % 60} min" : "")}".Trim() : $"{m} min";
    }

    private void Render(UiContext c)
    {
        _settings = c.Settings;
        var now = WaterTimerKit.Now(c);
        double goal = Goal;
        _frac = Math.Clamp(_ws.Ml / goal, 0, 1);
        _alerting = Alerting(now);
        _full = _frac >= 1;
        _canUndo = _ws.Log.Count > 0;
        SetIfChanged(ref _valueText, FmtVol(_ws.Ml));
        SetIfChanged(ref _metaText, FmtPair(_ws.Ml, goal));
        SetIfChanged(ref _drinkText, "+ " + FmtVol(CupMl));
        string sub;
        if (_ws.Ml >= goal) sub = L.T("Ziel erreicht. Schön gemacht.");
        else if (_alerting) sub = L.T("Zeit für ein Glas Wasser.");
        else if (!c.Settings.Bool("waterRemind")) sub = L.F("Heute noch {0}.", FmtVol(goal - _ws.Ml));
        else if (!InActiveHours(now)) sub = L.F("Erinnerungen gehen um {0:00}:00 weiter.", (int)c.Settings.Num("activeFrom"));
        else sub = L.F("Nächste Erinnerung in {0}.", UntilText(DueAt(now) - WaterTimerKit.NowMs(now)));
        SetIfChanged(ref _subText, sub);
    }

    private static void SetIfChanged(ref string field, string value)
    {
        if (field != value) field = value;
    }

    private void Drink(UiContext c)
    {
        var now = WaterTimerKit.Now(c);
        Rollover(now);
        double cup = CupMl, t = WaterTimerKit.NowMs(now);
        _ws.Ml += cup;
        _ws.Log.Add(new Cup { T = t, Ml = cup });
        if (_ws.Log.Count > 60) _ws.Log.RemoveAt(0);
        _ws.LastAt = t;
        _ws.SnoozeUntil = 0;
        Save();
        _alert = false;
        Render(c);
        _rippleAt = c.Time;   // eine kleine Welle durch den Tropfen bestätigt den Becher
    }

    private void UndoCup(UiContext c)
    {
        if (_ws.Log.Count == 0) return;
        var last = _ws.Log[^1];
        _ws.Log.RemoveAt(_ws.Log.Count - 1);
        _ws.Ml = Math.Max(0, _ws.Ml - last.Ml);
        _ws.LastAt = _ws.Log.Count > 0 ? _ws.Log[^1].T : 0;
        Save();
        Render(c);
    }

    private void Snooze(UiContext c)
    {
        _ws.SnoozeUntil = WaterTimerKit.NowMs(WaterTimerKit.Now(c)) + 15 * 60000;
        Save();
        _alert = false;
        Render(c);
    }

    // ---------------------------------------------------------------------------------------------
    // Maus
    // ---------------------------------------------------------------------------------------------

    private int HitButton(Vector2 p)
    {
        if (_kit.Ok < 0.6f) return 0;
        if (WaterTimerKit.In(_rDrink, p)) return 1;
        if (_alerting && WaterTimerKit.In(_rLater, p)) return 2;
        if (_canUndo && WaterTimerKit.In(_rUndo, p)) return 3;
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
                if (hit == 1) Drink(c);
                else if (hit == 2) Snooze(c);
                else Undo(c);
                return true;
            }
            case PointerKind.Leave:
                _pressed = 0;
                return false;
            default:
                return HitButton(e.Pos) != 0;
        }
    }

    private void Undo(UiContext c) => UndoCup(c);

    // ---------------------------------------------------------------------------------------------
    // Zeichnen
    // ---------------------------------------------------------------------------------------------

    private void EnsureGeometry(UiContext c)
    {
        if (_drop != null) return;
        _drop = WaterTimerKit.Svg(c.D2D, DropPath);
        _undo = WaterTimerKit.Svg(c.D2D, UndoPath);
        // Zwei Wellenkämme über die Pille (vier über den Streifen, der um seine halbe Breite gleitet)
        _waveBack = WaterTimerKit.Svg(c.D2D, Wave(8));
        _waveFront = WaterTimerKit.Svg(c.D2D, Wave(7));
    }

    private static string Wave(double a) => string.Create(CultureInfo.InvariantCulture,
        $"M0 10Q12.5 {10 - a} 25 10T50 10T75 10T100 10T125 10T150 10T175 10T200 10V20H0Z");

    private void EnsureBrushes(ID2D1DeviceContext ctx)
    {
        if (_bodyBrush != null && Vector3.DistanceSquared(_brushTp, _tp) < 1e-6f) return;
        _bodyBrush?.Dispose();
        _fillBrush?.Dispose();
        _brushTp = _tp;
        _bodyBrush = WaterTimerKit.Gradient(ctx, _tp, 0.22f, 0.34f);
        _fillBrush = WaterTimerKit.Gradient(ctx, _tp, 0.7f, 1f);
    }

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_kit.EnsureDevice(ctx))
        {
            _bodyBrush?.Dispose(); _bodyBrush = null;
            _fillBrush?.Dispose(); _fillBrush = null;
        }
        EnsureGeometry(c);
        Prepare(c);
        EnsureBrushes(ctx);
        var brush = _kit.Brush!;
        var prev = ctx.Transform;
        try
        {
            float k = c.K, e = Open, ec = Math.Clamp(e, 0, 1);
            float x = Bounds.Left, y = Bounds.Top, W = Bounds.Width, H = Bounds.Height;
            // Rundung wie die Glasfläche der Oberfläche (eher runder, damit das Wasser nie über das Glas ragt)
            float R = Math.Min(H / 2, WaterTimerKit.Lerp(20, 30, ec) * k);

            DrawWater(ctx, c, prev, x, y, W, H, R);

            // Kompakt: Wert neben dem Tropfen
            float oc = _kit.Oc;
            if (oc > 0.01f)
            {
                float tx = x + 36 * k + (1 - oc) * 6 * k;
                float ty = y + (40 * k - _value.Height) / 2;
                _kit.Color(c.Fg, 0.92f * oc);
                _value.Draw(ctx, brush, tx, ty);
            }

            DrawDrop(ctx, c, prev, x, y, e, ec);

            float ok = _kit.Ok;
            if (ok > 0.01f) DrawCard(ctx, c, prev, x, y + (1 - ok) * 6 * k, W, ok);
            else _rDrink = _rLater = _rUndo = default;
        }
        finally
        {
            ctx.Transform = prev;
        }
    }

    private void DrawWater(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, float x, float y, float W, float H, float R)
    {
        float lvl = (float)Math.Clamp(_lvl.X, 0, 1.05);
        if (_waterA < 0.004f || lvl <= 0.0005f || W < 1 || H < 1) return;

        var key = new Vector3(MathF.Round(W * 4), MathF.Round(H * 4), MathF.Round(R * 4));
        if (_clip == null || key != _clipKey)
        {
            _clip?.Dispose();
            _clip = c.D2D.CreateRoundedRectangleGeometry(WaterTimerKit.Rr(new Rect(0, 0, W, H), R));
            _clipKey = key;
        }
        ctx.Transform = Matrix3x2.CreateTranslation(x, y) * prev;
        ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Rect(0, 0, W, H),
            GeometricMask = _clip,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = _waterA,
            LayerOptions = LayerOptions1.None,
        }, null!);

        float k = c.K, surface = H - lvl * H;
        // Wasserkörper: Verlauf .22 → .34
        _bodyBrush!.StartPoint = new Vector2(0, surface);
        _bodyBrush.EndPoint = new Vector2(0, H);
        ctx.FillRectangle(new Rect(0, surface, W, H - surface + 1), _bodyBrush);

        // Zwei Wellen, die gegeneinander treiben (7 s und 4,6 s rückwärts)
        double t = c.Time;
        float offBack = -(float)(t % 7.0 / 7.0) * W;
        float offFront = -(1 - (float)(t % 4.6 / 4.6)) * W;
        DrawWave(ctx, _waveBack!, offBack, surface, 2 * W, 14 * k, 0.22f * 0.6f, x, y, prev);
        DrawWave(ctx, _waveFront!, offFront, surface, 2 * W, 10 * k, 0.22f, x, y, prev);

        ctx.Transform = Matrix3x2.CreateTranslation(x, y) * prev;
        ctx.PopLayer();
        ctx.Transform = prev;
    }

    private void DrawWave(ID2D1DeviceContext ctx, ID2D1PathGeometry g, float offX, float surface, float stripW, float h, float alpha,
        float x, float y, Matrix3x2 prev)
    {
        ctx.Transform = Matrix3x2.CreateScale(stripW / 200, h / 20) * Matrix3x2.CreateTranslation(offX, surface - h)
            * Matrix3x2.CreateTranslation(x, y) * prev;
        _kit.Color(_tp, alpha);
        ctx.FillGeometry(g, _kit.Brush!);
    }

    private void DrawDrop(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, float x, float y, float e, float ec)
    {
        float k = c.K;
        float size = WaterTimerKit.Lerp(20, 30, e) * k, ip = WaterTimerKit.Lerp(10, 16, ec) * k;
        float s = 1;
        if (_rippleAt > 0)
        {
            // scale 1 → 1,18 → 1 in 520 ms, federnd
            float p = (float)Math.Clamp((c.Time - _rippleAt) / 0.52, 0, 1);
            s = 1 + 0.18f * MathF.Sin(MathF.PI * WaterTimerKit.EaseSpring(p));
        }
        float scale = size / 24 * s;
        float cx = x + ip + size / 2, cy = y + ip + size / 2;
        ctx.Transform = Matrix3x2.CreateTranslation(-12, -12) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(cx, cy) * prev;
        var brush = _kit.Brush!;
        _kit.Color(_tp, 0.12f);
        ctx.FillGeometry(_drop!, brush);
        if (_full)
        {
            // .is-full: ein feiner Schein um die Linie
            _kit.Color(_tp, 0.22f);
            ctx.DrawGeometry(_drop!, brush, 4.5f, _kit.RoundStroke(c));
            _kit.Color(_tp, 0.3f);
            ctx.DrawGeometry(_drop!, brush, 3f, _kit.RoundStroke(c));
        }
        _kit.Color(_tp, 1);
        ctx.DrawGeometry(_drop!, brush, 1.5f, _kit.RoundStroke(c));
        ctx.Transform = prev;
    }

    private void DrawCard(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 prev, float x, float y, float W, float ok)
    {
        float k = c.K;
        var brush = _kit.Brush!;
        var fg = c.Fg;

        // Kopf: „Wasser“ links, Stand rechts (auf gemeinsamer Grundlinie, Stand wird bei Platzmangel gekürzt)
        _title.Set(c.DWrite, _kit.Fmt(c, 14.5f, 600), L.T("Wasser"), null);
        _meta.Set(c.DWrite, _kit.Fmt(c, 12, 550), _metaText, _kit.Tnum(c), trim: true);
        float headL = x + 58 * k, headR = x + W - 18 * k, headTop = y + 18 * k;
        float baseline = headTop + Math.Max(_title.Baseline, _meta.Baseline);
        _kit.Color(fg, 0.96f * ok);
        _title.Draw(ctx, brush, headL, baseline - _title.Baseline);
        float metaRoom = headR - headL - _title.Width - 10 * k;
        if (metaRoom > 4)
        {
            _meta.MaxWidth(metaRoom);
            float mw = Math.Min(_meta.Width, metaRoom);
            _kit.Color(fg, 0.55f * ok);
            _meta.Draw(ctx, brush, headR - mw, baseline - _meta.Baseline);
        }

        // Balken
        var bar = new Rect(headL, y + 46 * k, Math.Max(0, headR - headL), 8 * k);
        WaterTimerKit.Bar(ctx, _kit, bar, (float)Math.Clamp(_bar.X, 0, 1), fg, _fillBrush!, ok);

        // Satz zum Stand
        _sub.Set(c.DWrite, _kit.Fmt(c, 12, 500), _subText, null, trim: true);
        _sub.MaxWidth(Math.Max(1, headR - headL));
        float subTop = y + 62 * k + (12 * k * 1.4f - _sub.Height) / 2;
        if (_alert) _kit.Color(_tp, ok);
        else _kit.Color(fg, 0.55f * ok);
        _sub.Draw(ctx, brush, headL, subTop);

        // Knöpfe: „+ 250 ml“ (füllt), „Später“ (nur fällig), Rückgängig (rund)
        float rowL = x + 16 * k, rowR = x + W - 16 * k, top = y + 98 * k, h = 36 * k, gap = 8 * k;
        _drink.Set(c.DWrite, _kit.Fmt(c, 13, 600), _drinkText, _kit.Tnum(c));
        _later.Set(c.DWrite, _kit.Fmt(c, 13, 600), L.T("Später"), null);
        float undoX = rowR - h;
        float laterW = _later.Width + 32 * k;
        bool later = _alerting;
        float drinkR = later ? undoX - gap - laterW - gap : undoX - gap;
        _rDrink = new Rect(rowL, top, Math.Max(0, drinkR - rowL), h);
        _rLater = later ? new Rect(drinkR + gap, top, laterW, h) : default;
        _rUndo = new Rect(undoX, top, h, h);

        bool inside = Hovered && c.PointerInside;
        var p = c.Pointer;

        // Becher
        bool hov = inside && WaterTimerKit.In(_rDrink, p);
        WaterTimerKit.Press(ctx, _rDrink, _pressed == 1 && hov ? 0.95f : 1, prev);
        {
            _kit.Color(_tp, (hov ? 0.34f : 0.24f) * ok);
            ctx.FillRoundedRectangle(WaterTimerKit.Rr(_rDrink), brush);
            _kit.Color(_tp, 0.35f * ok);
            ctx.DrawRoundedRectangle(WaterTimerKit.Rr(WaterTimerKit.Inset(_rDrink, 0.5f)), brush, 1);
            _kit.Color(new Vector3(1), 0.12f * ok);
            ctx.DrawLine(new Vector2(_rDrink.Left + h / 2, _rDrink.Top + 0.5f), new Vector2(_rDrink.Right - h / 2, _rDrink.Top + 0.5f), brush, 1);
            _kit.Color(fg, ok);
            _drink.Draw(ctx, brush, _rDrink.Left + (_rDrink.Width - _drink.Width) / 2, _rDrink.Top + (h - _drink.Height) / 2);
        }
        ctx.Transform = prev;

        // Später
        if (later)
        {
            bool hl = inside && WaterTimerKit.In(_rLater, p);
            WaterTimerKit.Press(ctx, _rLater, _pressed == 2 && hl ? 0.95f : 1, prev);
            {
                _kit.Color(fg, (hl ? 0.14f : 0.08f) * ok);
                ctx.FillRoundedRectangle(WaterTimerKit.Rr(_rLater), brush);
                _kit.Color(fg, (hl ? 1f : 0.75f) * ok);
                _later.Draw(ctx, brush, _rLater.Left + (_rLater.Width - _later.Width) / 2, _rLater.Top + (h - _later.Height) / 2);
            }
            ctx.Transform = prev;
        }

        // Rückgängig
        bool hu = _canUndo && inside && WaterTimerKit.In(_rUndo, p);
        float ua = (_canUndo ? 1 : 0.35f) * ok;
        WaterTimerKit.Press(ctx, _rUndo, _pressed == 3 && hu ? 0.88f : 1, prev);
        {
            _kit.Color(fg, (hu ? 0.14f : 0.08f) * ua);
            ctx.FillEllipse(new Ellipse(WaterTimerKit.Center(_rUndo), h / 2, h / 2), brush);
            _kit.Color(fg, (hu ? 1f : 0.8f) * ua);
            _kit.Icon(ctx, c, _undo!, WaterTimerKit.Center(_rUndo), 18 * k, stroke: true);
        }
        ctx.Transform = prev;
    }

    public override void Dispose()
    {
        _value.Dispose(); _title.Dispose(); _meta.Dispose(); _sub.Dispose(); _drink.Dispose(); _later.Dispose();
        _drop?.Dispose(); _undo?.Dispose(); _waveBack?.Dispose(); _waveFront?.Dispose(); _clip?.Dispose();
        _drop = _undo = _waveBack = _waveFront = null;
        _clip = null;
        _bodyBrush?.Dispose(); _fillBrush?.Dispose();
        _bodyBrush = _fillBrush = null;
        _kit.Dispose();
    }
}

/// <summary>
/// Gemeinsames Werkzeug der Wasser- und der Fokus-Pille (ToolPill in pills.js): Ein-/Ausblenden der Inhalte beim
/// Öffnen, Schriften und Textbausteine, SVG-Pfade als Geometrie, Knöpfe, Balken, Farben.
/// </summary>
internal sealed class WaterTimerKit : IDisposable
{
    public static readonly Vector3 Mono = new(226 / 255f, 230 / 255f, 240 / 255f);

    // ---------- Öffnen: Kompakt-Inhalt weicht, Karten-Inhalt kommt (wie layout() im Original) ----------

    public float Oc = 1, Ok;
    private float _lastOpen;
    private bool _opening;
    public bool Moving { get; private set; }

    public void StepOpen(float e, double dt)
    {
        if (e > _lastOpen + 1e-4f) _opening = true;
        else if (e < _lastOpen - 1e-4f) _opening = false;
        else if (e >= 0.999f) _opening = true;
        else if (e <= 0.001f) _opening = false;
        bool changed = MathF.Abs(e - _lastOpen) > 1e-4f;
        _lastOpen = e;
        float a = 1 - MathF.Exp(-(float)dt * 22);
        float tOc = _opening ? 1 - Smooth(0.02f, 0.35f, e) : 1 - Smooth(0.3f, 0.65f, e);
        float tOk = _opening ? Smooth(0.35f, 0.85f, e) : Smooth(0.66f, 0.96f, e);
        Oc += (tOc - Oc) * a;
        Ok += (tOk - Ok) * a;
        Moving = changed || MathF.Abs(tOc - Oc) > 0.002f || MathF.Abs(tOk - Ok) > 0.002f;
    }

    // ---------- Geräte-Ressourcen ----------

    private nint _ctxPtr;
    public ID2D1SolidColorBrush? Brush;
    private ID2D1StrokeStyle? _round;
    private nint _factoryPtr;

    /// <summary>true = Gerätekontext neu (eigene Geräte-Ressourcen der Pille neu anlegen).</summary>
    public bool EnsureDevice(ID2D1DeviceContext ctx)
    {
        if (_ctxPtr == ctx.NativePointer && Brush != null) return false;
        Brush?.Dispose();
        _ctxPtr = ctx.NativePointer;
        Brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        return true;
    }

    public ID2D1StrokeStyle RoundStroke(UiContext c)
    {
        if (_round != null && _factoryPtr == c.D2D.NativePointer) return _round;
        _round?.Dispose();
        _factoryPtr = c.D2D.NativePointer;
        _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
        return _round;
    }

    public void Color(Vector3 rgb, float a) => Brush!.Color = new Color4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0, 1));

    // ---------- Schriften ----------

    private readonly Dictionary<int, IDWriteTextFormat> _formats = new();
    private float _fontK = -1;
    private string _fontKey = "", _weightKey = "";
    private UiFonts? _fontsRef;
    private IDWriteTypography? _tnum;
    private nint _dwPtr;
    /// <summary>Wird bei jeder Schriftänderung erhöht (Textbausteine bauen sich dann neu).</summary>
    public int FontVersion { get; private set; }

    public void CheckFonts(UiContext c)
    {
        string font = c.Settings.Str("font"), weight = c.Settings.Str("clockWeight");
        if (c.K == _fontK && font == _fontKey && weight == _weightKey && ReferenceEquals(c.Fonts, _fontsRef)) return;
        _fontK = c.K; _fontKey = font; _weightKey = weight; _fontsRef = c.Fonts;
        // Formate gehören UiFonts (nicht freigeben)
        _formats.Clear();
        FontVersion++;
    }

    /// <summary>Textformat (aus UiFonts, je Größe/Stärke einmal geholt). Größe in k-Einheiten.</summary>
    public IDWriteTextFormat Fmt(UiContext c, float sizeK, int weight, bool display = false)
    {
        CheckFonts(c);
        int key = ((int)MathF.Round(sizeK * 10) * 1000 + weight) * 2 + (display ? 1 : 0);
        if (_formats.TryGetValue(key, out var f)) return f;
        f = c.Fonts.Format(c, sizeK * c.K, weight, display);
        _formats[key] = f;
        return f;
    }

    public IDWriteTypography Tnum(UiContext c)
    {
        if (_tnum != null && _dwPtr == c.DWrite.NativePointer) return _tnum;
        _tnum?.Dispose();
        _dwPtr = c.DWrite.NativePointer;
        _tnum = c.DWrite.CreateTypography();
        _tnum.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
        return _tnum;
    }

    /// <summary>Stärke und Laufweite der Uhrschrift (app.js FONTS / CLOCK_WEIGHTS).</summary>
    public static (int Weight, float Track) ClockFont(StillValues s)
    {
        (int w, float t) = s.Str("font") switch
        {
            "poppins" => (200, -0.04f),
            "outfit" => (200, -0.04f),
            "serif" => (400, -0.02f),
            "system" => (300, -0.035f),
            _ => (200, -0.05f),
        };
        w = s.Str("clockWeight") switch { "thin" => 200, "light" => 300, "regular" => 400, "medium" => 500, "bold" => 650, _ => w };
        return (w, t);
    }

    /// <summary>Ein Textbaustein: Layout nur bei geändertem Text oder Format neu.</summary>
    public sealed class Txt : IDisposable
    {
        private IDWriteTextLayout? _layout;
        private IDWriteInlineObject? _ellipsis;
        private IDWriteTextFormat? _fmt;
        private string _text = "";
        private float _maxW;
        public float Width, Baseline, Height;

        public void Set(IDWriteFactory dw, IDWriteTextFormat fmt, string text, IDWriteTypography? tnum, bool trim = false, float track = 0)
        {
            if (_layout != null && ReferenceEquals(fmt, _fmt) && text == _text) return;
            Dispose();
            _fmt = fmt;
            _text = text;
            _layout = dw.CreateTextLayout(text, fmt, 100000, 100000);
            _layout.WordWrapping = WordWrapping.NoWrap;
            var range = new TextRange(0, (uint)text.Length);
            if (tnum != null && text.Length > 0) _layout.SetTypography(tnum, range);
            if (track != 0 && text.Length > 0)
            {
                using var l1 = _layout.QueryInterfaceOrNull<IDWriteTextLayout1>();
                l1?.SetCharacterSpacing(0, track, 0, range);
            }
            if (trim)
            {
                _ellipsis = dw.CreateEllipsisTrimmingSign(_layout);
                _layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, _ellipsis);
            }
            _maxW = 100000;
            Width = _layout.Metrics.WidthIncludingTrailingWhitespace;
            var lines = _layout.LineMetrics;
            Baseline = lines.Length > 0 ? lines[0].Baseline : 0;
            Height = lines.Length > 0 ? lines[0].Height : _layout.Metrics.Height;
        }

        public void MaxWidth(float w)
        {
            if (_layout == null) return;
            w = Math.Max(1, w);
            if (MathF.Abs(w - _maxW) < 0.5f) return;
            _layout.MaxWidth = w;
            _maxW = w;
        }

        public void Draw(ID2D1DeviceContext ctx, ID2D1Brush brush, float x, float y)
        {
            if (_layout != null && _text.Length > 0) ctx.DrawTextLayout(new Vector2(x, y), _layout, brush, DrawTextOptions.None);
        }

        public void Dispose()
        {
            _layout?.Dispose();
            _ellipsis?.Dispose();
            _layout = null;
            _ellipsis = null;
        }
    }

    // ---------- Formen ----------

    private static readonly Regex Tokens = new(@"[A-DF-Za-df-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>SVG-Pfad (M L H V C A Z, absolut und relativ) als Direct2D-Geometrie.</summary>
    public static ID2D1PathGeometry Svg(ID2D1Factory1 f, string d)
    {
        var g = f.CreatePathGeometry();
        var sink = g.Open();
        try
        {
            var tok = Tokens.Matches(d);
            int i = 0;
            char cmd = 'M';
            Vector2 cur = default, start = default;
            bool open = false;
            Vector2 lastQ = default;
            float N() => float.Parse(tok[i++].Value, CultureInfo.InvariantCulture);
            while (i < tok.Count)
            {
                string tv = tok[i].Value;
                if (char.IsLetter(tv[0])) { cmd = tv[0]; i++; }
                bool rel = char.IsLower(cmd);
                Vector2 o = rel ? cur : Vector2.Zero;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        if (open) sink.EndFigure(FigureEnd.Open);
                        cur = o + new Vector2(N(), N());
                        start = cur;
                        sink.BeginFigure(cur, FigureBegin.Filled);
                        open = true;
                        cmd = rel ? 'l' : 'L';
                        break;
                    case 'L':
                        cur = o + new Vector2(N(), N());
                        sink.AddLine(cur);
                        break;
                    case 'H':
                        cur = new Vector2((rel ? cur.X : 0) + N(), cur.Y);
                        sink.AddLine(cur);
                        break;
                    case 'V':
                        cur = new Vector2(cur.X, (rel ? cur.Y : 0) + N());
                        sink.AddLine(cur);
                        break;
                    case 'C':
                    {
                        var p1 = o + new Vector2(N(), N());
                        var p2 = o + new Vector2(N(), N());
                        var p3 = o + new Vector2(N(), N());
                        sink.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                        cur = p3;
                        break;
                    }
                    case 'Q':
                    {
                        var p1 = o + new Vector2(N(), N());
                        var p2 = o + new Vector2(N(), N());
                        sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
                        lastQ = p1;
                        cur = p2;
                        break;
                    }
                    case 'T':
                    {
                        var p1 = cur * 2 - lastQ;
                        var p2 = o + new Vector2(N(), N());
                        sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
                        lastQ = p1;
                        cur = p2;
                        break;
                    }
                    case 'A':
                    {
                        float rx = N(), ry = N(), rot = N();
                        bool large = N() != 0, sweep = N() != 0;
                        cur = o + new Vector2(N(), N());
                        sink.AddArc(new ArcSegment
                        {
                            Point = cur,
                            Size = new Size(rx, ry),
                            RotationAngle = rot,
                            SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                            ArcSize = large ? ArcSize.Large : ArcSize.Small,
                        });
                        break;
                    }
                    case 'Z':
                        if (open) sink.EndFigure(FigureEnd.Closed);
                        open = false;
                        cur = start;
                        break;
                    default:
                        i++;
                        break;
                }
            }
            if (open) sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        finally
        {
            sink.Dispose();
        }
        return g;
    }

    /// <summary>Senkrechter oder waagerechter Verlauf in einer Farbe (Deckkraft a0 → a1), Endpunkte setzt der Aufrufer.</summary>
    public static ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext ctx, Vector3 rgb, float a0, float a1)
    {
        using var stops = ctx.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0, Color = new Color4(rgb.X, rgb.Y, rgb.Z, a0) },
            new GradientStop { Position = 1, Color = new Color4(rgb.X, rgb.Y, rgb.Z, a1) },
        ]);
        return ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = Vector2.Zero, EndPoint = new Vector2(0, 1) }, stops);
    }

    /// <summary>.tp-bar mit .tp-fill: Spur fg/.12, Füllung als Verlauf tp/.7 → tp.</summary>
    public static void Bar(ID2D1DeviceContext ctx, WaterTimerKit kit, Rect bar, float frac, Vector3 fg, ID2D1LinearGradientBrush fill, float alpha)
    {
        if (bar.Width <= 0) return;
        float r = bar.Height / 2;
        kit.Color(fg, 0.12f * alpha);
        ctx.FillRoundedRectangle(Rr(bar), kit.Brush!);
        float w = bar.Width * frac;
        if (w <= 0.25f) return;
        fill.StartPoint = new Vector2(bar.Left, 0);
        fill.EndPoint = new Vector2(bar.Left + w, 0);
        fill.Opacity = alpha;
        float rr = Math.Min(r, w / 2);
        ctx.FillRoundedRectangle(Rr(new Rect(bar.Left, bar.Top, w, bar.Height), rr), fill);
    }

    /// <summary>Knopf beim Drücken um seine Mitte verkleinern (danach Transform zurücksetzen).</summary>
    public static void Press(ID2D1DeviceContext ctx, Rect r, float scale, Matrix3x2 prev) =>
        ctx.Transform = scale != 1 ? Matrix3x2.CreateScale(scale, Center(r)) * prev : prev;

    /// <summary>Symbol (24er-Raster) mittig, in Größe <paramref name="size"/>; Strich 1,8 oder gefüllt.</summary>
    public void Icon(ID2D1DeviceContext ctx, UiContext c, ID2D1Geometry g, Vector2 center, float size, bool stroke,
        float scale = 1, float rotateDeg = 0)
    {
        var prev = ctx.Transform;
        float s = size / 24 * scale;
        ctx.Transform = Matrix3x2.CreateTranslation(-12, -12) * Matrix3x2.CreateRotation(rotateDeg * MathF.PI / 180)
            * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(center) * prev;
        if (stroke) ctx.DrawGeometry(g, Brush!, 1.8f, RoundStroke(c));
        else ctx.FillGeometry(g, Brush!);
        ctx.Transform = prev;
    }

    // ---------- Kleinigkeiten ----------

    /// <summary>Farbe der Karte: eigene Farbe nur bei „Farbige Karten“, sonst weiß-grau (mono-cards).</summary>
    public static Vector3 CardColor(UiContext c, Vector3 own) => c.Settings.Bool("cardColors") ? own : Mono;

    public static DateTime Now(UiContext c) => c.Now == default ? DateTime.Now : c.Now;
    public static double NowMs(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Math.round wie in JavaScript (halb nach oben).</summary>
    public static long JsRound(double v) => (long)Math.Floor(v + 0.5);

    /// <summary>+(v).toFixed(2): höchstens zwei Nachkommastellen, ohne Nullen am Ende, mit Punkt wie im Original.</summary>
    public static string Fixed2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Ungefähr cubic-bezier(.3, 1.4, .5, 1) für das kurze Aufwallen.</summary>
    public static float EaseSpring(float p) => Math.Clamp(1 - MathF.Pow(1 - p, 3) + 0.12f * MathF.Sin(MathF.PI * p), 0, 1.2f);

    public static bool In(Rect r, Vector2 p) => r.Width > 0 && p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    public static Vector2 Center(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
    public static Rect Inset(Rect r, float d) => new(r.Left + d, r.Top + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));
    public static RoundedRectangle Rr(Rect r) => Rr(r, r.Height / 2);

    public static RoundedRectangle Rr(Rect r, float radius) =>
        new(new System.Drawing.RectangleF(r.Left, r.Top, r.Width, r.Height), radius, radius);

    /// <summary>Feder wie im Original (Dämpfung + Ansprechzeit).</summary>
    public sealed class Spring(double x, double damping, double response)
    {
        public double X = x, V, Target = x;
        private readonly double _k = Math.Pow(2 * Math.PI / response, 2), _c = 2 * damping * (2 * Math.PI / response);

        public bool Moving => Math.Abs(X - Target) > 0.0005 || Math.Abs(V) > 0.001;

        public double Step(double dt)
        {
            dt = Math.Clamp(dt, 0, 0.1);
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += (-_k * (X - Target) - _c * V) * h;
                X += V * h;
            }
            return X;
        }

        public void Snap(double v) { X = Target = v; V = 0; }
    }

    public void Dispose()
    {
        Brush?.Dispose();
        Brush = null;
        _round?.Dispose();
        _round = null;
        _tnum?.Dispose();
        _tnum = null;
        _formats.Clear();
        _ctxPtr = 0;
    }
}

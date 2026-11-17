using System.Globalization;
using System.Numerics;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui;

// =====================================================================================================
// Die Pille der Klangwelten (Port von soundscape.js „The pill“ und style.css „Soundscapes“).
// Klein: drei Balken, die sich mit dem Klang bewegen, und solange etwas spielt, was spielt.
// Als Karte zwei Ansichten unter der Überschrift: die sechs Klänge mit Lautstärke und Schaltern, und die
// Einstellungen eines Klangs, die von rechts hereingleiten (der Klang ist dann allein zu hören).
// Kachel antippen = Klang an/aus, über die Kachel ziehen = seine Stufe.
// =====================================================================================================

public sealed class SoundPill : Pill
{
    public override string Id => "sound";
    public override bool Shown(UiContext c) => c.Settings.Bool("sounds");
    public override Vector2 CompactSize(UiContext c) => new(_cw * c.K, 40 * c.K);
    public override Vector2 CardSize(UiContext c) => new(CardW * c.K, _cardH * c.K);

    // ---------- Maße (in K wie im Original) ----------
    private const float CardW = 316, Head = 54, Foot = 16, MainH = 272;
    private const float TileW = 138, TileH = 54, Gap = 8, ControlsY = Head + 3 * TileH + 2 * Gap + 14, ChipsY = ControlsY + 40 + 12;
    private const float VolX0 = 68, VolX1 = 252;
    private static readonly Vector3 Purple = new(186 / 255f, 166 / 255f, 1f);
    private static readonly Vector3 Quiet = new(226 / 255f, 230 / 255f, 240 / 255f);

    // ---------- Einstellungen eines Klangs ----------
    private enum RowKind { Level, Slider, Switch }
    private enum Fmt { Pct, KHz, NearOr }
    private sealed record RowDef(RowKind Kind, string Key, string Label, double From = 0, double To = 1, Fmt Format = Fmt.Pct, double Step = 0);

    // Gedämpft und Entfernung laufen vom leichtesten (links) zum stärksten.
    private static readonly RowDef[][] Tune =
    {
        new RowDef[] { new(RowKind.Slider, "rainIntensity", "Intensität"), new(RowKind.Switch, "rainDynamic", "Dynamische Intensität"), new(RowKind.Slider, "rainMuffle", "Gedämpft", 7000, 1200, Fmt.KHz, 50) },
        new RowDef[] { new(RowKind.Slider, "streamIntensity", "Intensität"), new(RowKind.Slider, "streamDistance", "Entfernung", 8000, 1200, Fmt.NearOr, 50) },
        new RowDef[] { new(RowKind.Slider, "windIntensity", "Intensität") },
        new RowDef[] { new(RowKind.Slider, "wavesIntensity", "Intensität"), new(RowKind.Slider, "wavesDistance", "Entfernung", 8000, 1200, Fmt.NearOr, 50) },
        Array.Empty<RowDef>(),
        new RowDef[] { new(RowKind.Slider, "brownSoft", "Weichheit") },
    };
    private static readonly RowDef LevelRow = new(RowKind.Level, "", "Stufe");
    private readonly RowDef[] _rows = new RowDef[4];
    private int _rowCount;

    // ---------- Zustand ----------
    private int _tuning = -1;            // Klang, dessen Einstellungen offen sind
    private float _view;                 // 0 = alle Klänge, 1 = Einstellungen
    private float _cardH = Head + MainH + Foot;
    private float _cw = 40;
    private readonly float[] _fillW = new float[6], _fillOp = new float[6], _tileHover = new float[6];
    private readonly float[] _bars = { 0.42f, 0.78f, 0.58f };
    private float _pp;                   // Abspielen ↔ Pause
    private double _tickAcc;
    private int _seenVersion = -1;
    private string _value = "";
    private float _valueW;
    private string _meta = L.T("Aus");
    private bool _playing, _solo;
    private float _oc = 1, _ok;

    // Maus
    private const int HNone = 0, HTile = 100, HTweak = 200, HPlay = 300, HVol = 301, HChip = 400, HBack = 500, HBar = 600, HReset = 700, HSwitch = 800;
    private int _hover, _press;
    private int _drag;                   // HTile+i, HVol, HBar+i oder 0
    private float _dragX0;
    private bool _moved;
    private Vector2 _local;

    // ---------- Ressourcen ----------
    private nint _ctxPtr, _factoryPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1LinearGradientBrush? _grad;
    private ID2D1GradientStopCollection? _stops;
    private Vector3 _gradTp = new(-1);
    private ID2D1StrokeStyle? _round;
    private Icon[]? _soundIcons;
    private Icon? _cloud, _ring, _spark, _sliders, _back, _undo, _play;
    private float _fmtK = -1;
    private IDWriteTextFormat? _fTitle, _fMeta, _fValue, _fName, _fNum, _fChip, _fLabel;
    private readonly float[] _chipW = new float[3], _nameW = new float[6];
    private static readonly string[] ChipLabels = { "Wetter", "Fokus", "Effekte" };
    private static readonly string[] ChipKeys = { "soundMatch", "soundFocus", "soundFx" };
    private static readonly string[] Pct = Enumerable.Range(0, 101).Select(i => i + "%").ToArray();
    private readonly Dictionary<int, string> _khz = new();

    private Vector3 _fg, _tp;
    private float _alpha = 1;
    private Matrix3x2 _t0;

    public SoundPill() { }

    // =================================================================================================
    // Update
    // =================================================================================================

    public override void Update(UiContext c, double dt)
    {
        float fdt = (float)Math.Min(dt, 0.1);
        Soundscape.Apply(c.Settings);
        Soundscape.Duck(c.Media.Playing);
        _tickAcc += dt;
        if (_tickAcc >= 0.25) { _tickAcc = 0; Soundscape.Tick(); }

        bool on = c.Settings.Bool("sounds");
        if (!on && _tuning >= 0) CloseTune();
        // Eine geschlossene Karte öffnet wieder auf allen Klängen, und der abgestimmte Klang spielt nicht mehr allein.
        if (_tuning >= 0 && Open < 0.05f && !Hovered && _drag == 0) CloseTune();
        // Ziehen endet spätestens, wenn die Maus die Karte verlassen hat und das Loslassen woanders ankam
        // (z. B. Klick auf die Uhr) – sonst hielte sich die Karte für immer offen.
        if (_drag != 0 && !Hovered) { _drag = 0; _press = HNone; }
        if (_drag != 0) AnnounceFor = Math.Max(AnnounceFor, 0.3); // läuft ab, sobald das Ziehen endet

        int v = Soundscape.Version;
        if (v != _seenVersion) { _seenVersion = v; Refresh(c); }
        else if (_duckSeen != Soundscape.DuckForMusic) Refresh(c);

        float f = 1 - MathF.Exp(-fdt * 12);
        _oc = Ease(_oc, 1 - Smooth(0.05f, 0.4f, Open), 1 - MathF.Exp(-fdt * 22));
        _ok = Ease(_ok, Smooth(0.45f, 0.9f, Open), 1 - MathF.Exp(-fdt * 22));
        _view = Ease(_view, _tuning >= 0 ? 1 : 0, 1 - MathF.Exp(-fdt * 7));
        float target = _tuning >= 0 ? Head + RowsHeight() + Foot : Head + MainH + Foot;
        _cardH = Open < 0.01f ? target : Ease(_cardH, target, 1 - MathF.Exp(-fdt * 9));
        _pp = Ease(_pp, _playing ? 1 : 0, 1 - MathF.Exp(-fdt * 14));

        for (int i = 0; i < 6; i++)
        {
            string id = Soundscape.Ids[i];
            bool isOn = Soundscape.IsOn(id);
            _fillW[i] = _drag == HTile + i ? (float)Soundscape.Level(id) : Ease(_fillW[i], (float)Soundscape.Level(id), 1 - MathF.Exp(-fdt * 10));
            _fillOp[i] = Ease(_fillOp[i], isOn ? (_playing ? 1f : 0.45f) : 0.16f, 1 - MathF.Exp(-fdt * 8));
            _tileHover[i] = Ease(_tileHover[i], _hover == HTile + i || _hover == HTweak + i ? 1 : 0, 1 - MathF.Exp(-fdt * 16));
        }

        // Die Balken: tief, mittel, hoch, geglättet, damit sie schwingen statt zu flackern.
        for (int i = 0; i < 3; i++) _bars[i] += (Soundscape.Meter(i) - _bars[i]) * f;

        EnsureFormats(c);
        if (_valueDirty) MeasureValue(c);
    }

    private double _duckSeen = 1;
    private bool _valueDirty = true;

    private void Refresh(UiContext c)
    {
        _playing = Soundscape.Playing;
        _solo = Soundscape.Solo != null;
        double duck = Soundscape.DuckForMusic;
        _duckSeen = duck;
        // Was spielt (bei mehr als zwei: „Regen + 2“).
        string value = "";
        if (_playing)
        {
            int n = 0, first = -1, second = -1;
            for (int i = 0; i < 6; i++)
                if (Soundscape.IsOn(Soundscape.Ids[i])) { if (first < 0) first = i; else if (second < 0) second = i; n++; }
            value = n == 0 ? "" : n == 1 ? Soundscape.Name(first)
                : n == 2 ? Soundscape.Name(first) + " + " + Soundscape.Name(second)
                : Soundscape.Name(first) + " + " + (n - 1).ToString(CultureInfo.InvariantCulture);
        }
        if (value != _value) { _value = value; _valueDirty = true; }
        _meta = L.T(_solo ? (Soundscape.DuckNow < 1 ? "Allein · leiser" : "Läuft allein")
            : !_playing ? "Aus"
            : duck == 0 ? "Pausiert wegen Musik"
            : duck < 1 ? "Leiser bei Musik"
            : Soundscape.ByFocus ? "Mit Fokus"
            : Soundscape.Matched.Length > 0 ? "Für das Wetter" : "Läuft");
    }

    private void MeasureValue(UiContext c)
    {
        if (_fValue == null) return;
        _valueDirty = false;
        if (_value.Length == 0) { _valueW = 0; _cw = 40; return; }
        using var l = c.DWrite.CreateTextLayout(_value, _fValue, 4096, 100);
        _valueW = l.Metrics.WidthIncludingTrailingWhitespace;
        _cw = Math.Max(40, 36 + _valueW / c.K + 15);
    }

    public override bool Animating =>
        _drag != 0 || _playing || _solo || Math.Abs(_view - (_tuning >= 0 ? 1 : 0)) > 0.002f || Math.Abs(_pp - (_playing ? 1 : 0)) > 0.002f
        || TilesMoving();

    private bool TilesMoving()
    {
        for (int i = 0; i < 6; i++)
        {
            float h = _hover == HTile + i || _hover == HTweak + i ? 1 : 0;
            if (Math.Abs(_tileHover[i] - h) > 0.01f) return true;
            if (Math.Abs(_fillW[i] - (float)Soundscape.Level(Soundscape.Ids[i])) > 0.002f) return true;
        }
        return false;
    }

    private float RowsHeight()
    {
        float h = 0;
        for (int i = 0; i < _rowCount; i++) h += (i > 0 ? 10 : 0) + (_rows[i].Kind == RowKind.Switch ? 28 : 48);
        return h;
    }

    private void OpenTune(int k)
    {
        _tuning = k;
        _rows[0] = LevelRow;
        _rowCount = 1;
        foreach (var r in Tune[k]) _rows[_rowCount++] = r;
        Soundscape.SetSolo(Soundscape.Ids[k]);
    }

    private void CloseTune()
    {
        if (_tuning < 0) return;
        _tuning = -1;
        Soundscape.SetSolo(null);
    }

    // =================================================================================================
    // Maus
    // =================================================================================================

    public override bool OnPointer(UiContext c, PointerEvent e)
    {
        float K = c.K;
        var q = new Vector2((e.Pos.X - Bounds.Left) / K, (e.Pos.Y - Bounds.Top) / K);
        _local = q;
        bool live = Open > 0.6f && _ok > 0.6f;
        switch (e.Kind)
        {
            case PointerKind.Move:
                _hover = live ? Hit(c, q) : HNone;
                if (_drag != 0) { DragTo(c, q); return true; }
                return live && _hover != HNone;
            case PointerKind.Down:
                if (e.Button != 0 || !live) return false;
                _press = Hit(c, q);
                _hover = _press;
                if (_press >= HTile && _press < HTile + 6) { _drag = _press; _dragX0 = q.X; _moved = false; }
                else if (_press == HVol) { _drag = HVol; DragTo(c, q); }
                else if (_press >= HBar && _press < HBar + 4) { _drag = _press; DragTo(c, q); }
                return _press != HNone || InsideCard(q);
            case PointerKind.Up:
            {
                if (e.Button != 0) return false;
                int hit = live ? Hit(c, q) : HNone;
                bool had = _drag != 0 || _press != HNone;
                if (_drag >= HTile && _drag < HTile + 6)
                {
                    if (!_moved) Soundscape.Toggle(Soundscape.Ids[_drag - HTile]);
                    Soundscape.Save();
                }
                else if (_drag >= HBar && _drag < HBar + 4 && _rows[_drag - HBar].Kind == RowKind.Level) Soundscape.Save();
                else if (_drag == 0 && _press != HNone && hit == _press) Click(c, hit);
                _drag = 0;
                _press = HNone;
                return had || (live && InsideCard(q));
            }
            case PointerKind.Leave:
                _hover = HNone;
                if (_drag != 0)
                {
                    if (_drag >= HTile && _drag < HTile + 6 || (_drag >= HBar && _rows[_drag - HBar].Kind == RowKind.Level)) Soundscape.Save();
                    _drag = 0;
                }
                _press = HNone;
                return false;
            default:
                return false;
        }
    }

    private static bool InsideCard(Vector2 q) => q.X >= 0 && q.Y >= 0 && q.X <= CardW;

    private void DragTo(UiContext c, Vector2 q)
    {
        if (_drag >= HTile && _drag < HTile + 6)
        {
            int i = _drag - HTile;
            if (!_moved && Math.Abs(q.X - _dragX0) < 5) return;
            _moved = true;
            var r = TileRect(i);
            Soundscape.SetLevel(Soundscape.Ids[i], Math.Clamp((q.X - r.X) / r.Z, 0, 1));
        }
        else if (_drag == HVol)
        {
            double t = Math.Clamp((q.X - VolX0) / (VolX1 - VolX0), 0, 1);
            c.Settings.Set("soundVol", Math.Round(t * 100) / 100);
        }
        else if (_drag >= HBar && _drag < HBar + 4)
        {
            var row = _rows[_drag - HBar];
            double t = Math.Clamp((q.X - 16) / 284, 0, 1);
            double v = row.From + t * (row.To - row.From);
            v = row.Step > 0 ? Math.Round(v / row.Step) * row.Step : Math.Round(v * 100) / 100;
            Put(c, row, v);
        }
    }

    private void Click(UiContext c, int hit)
    {
        if (hit >= HTweak && hit < HTweak + 6) OpenTune(hit - HTweak);
        else if (hit == HPlay) Soundscape.PlayPause();
        else if (hit >= HChip && hit < HChip + 3) c.Settings.Set(ChipKeys[hit - HChip], !c.Settings.Bool(ChipKeys[hit - HChip]));
        else if (hit == HBack) CloseTune();
        else if (hit >= HReset && hit < HReset + 4) { var row = _rows[hit - HReset]; Put(c, row, Default(row)); if (row.Kind == RowKind.Level) Soundscape.Save(); }
        else if (hit >= HSwitch && hit < HSwitch + 4) { var row = _rows[hit - HSwitch]; c.Settings.Set(row.Key, !c.Settings.Bool(row.Key)); }
    }

    private int Hit(UiContext c, Vector2 q)
    {
        if (_tuning < 0)
        {
            for (int i = 0; i < 6; i++)
            {
                var r = TileRect(i);
                if (q.X < r.X || q.Y < r.Y || q.X > r.X + r.Z || q.Y > r.Y + r.W) continue;
                var tc = new Vector2(r.X + r.Z - 7 - 15, r.Y + r.W / 2);
                return Vector2.Distance(q, tc) <= 15 ? HTweak + i : HTile + i;
            }
            if (Vector2.Distance(q, new Vector2(36, ControlsY + 20)) <= 20) return HPlay;
            if (q.X >= VolX0 - 4 && q.X <= VolX1 + 4 && Math.Abs(q.Y - (ControlsY + 20)) <= 12) return HVol;
            float x = 16;
            for (int i = 0; i < 3; i++)
            {
                float w = ChipWidth(i);
                if (q.X >= x && q.X <= x + w && q.Y >= ChipsY && q.Y <= ChipsY + 28) return HChip + i;
                x += w + 8;
            }
            return HNone;
        }
        if (q.Y >= 14 && q.Y <= 42 && q.X >= 50 && q.X <= 71 + _nameW[_tuning] + 6) return HBack;
        float y = Head;
        for (int i = 0; i < _rowCount; i++)
        {
            var row = _rows[i];
            if (row.Kind == RowKind.Switch)
            {
                if (q.Y >= y && q.Y <= y + 28 && q.X >= 258 && q.X <= 304) return HSwitch + i;
                y += 28 + 10;
                continue;
            }
            if (q.Y >= y && q.Y <= y + 24 && Vector2.Distance(q, new Vector2(292, y + 12)) <= 13 && Math.Abs(Get(c, row) - Default(row)) > 1e-6) return HReset + i;
            if (q.Y >= y + 22 && q.Y <= y + 48 && q.X >= 10 && q.X <= 306) return HBar + i;
            y += 48 + 10;
        }
        return HNone;
    }

    private static Vector4 TileRect(int i) => new(16 + (i % 2) * (TileW + Gap), Head + (i / 2) * (TileH + Gap), TileW, TileH);
    private float ChipWidth(int i) => 10 + 14 + 6 + _chipW[i] + 12;

    private double Get(UiContext c, RowDef r) => r.Kind switch
    {
        RowKind.Level => Soundscape.Level(Soundscape.Ids[_tuning]),
        RowKind.Switch => c.Settings.Bool(r.Key) ? 1 : 0,
        _ => c.Settings.Num(r.Key),
    };

    private static double Default(RowDef r) => r.Kind == RowKind.Level ? Soundscape.DefaultLevel
        : StillValues.OriginalDefaults.TryGetValue(r.Key, out var d) && d is double v ? v : 0;

    private void Put(UiContext c, RowDef r, double v)
    {
        if (r.Kind == RowKind.Level) Soundscape.SetOwnLevel(Soundscape.Ids[_tuning], v);
        else c.Settings.Set(r.Key, v);
    }

    private string Format(RowDef r, double v)
    {
        if (r.Format == Fmt.Pct) return Pct[Math.Clamp((int)Math.Round(v * 100), 0, 100)];
        if (r.Format == Fmt.NearOr && v >= 7990) return L.T("Nah");
        int key = (int)Math.Round(v / 10);
        if (!_khz.TryGetValue(key, out var s))
            _khz[key] = s = L.F("unter {0} kHz", (key / 100.0).ToString("0.0", CultureInfo.InvariantCulture));
        return s;
    }

    // =================================================================================================
    // Zeichnen
    // =================================================================================================

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        EnsureDevice(ctx, c);
        EnsureFormats(c);
        if (_brush == null || _fTitle == null) return;
        float K = c.K;
        _t0 = ctx.Transform;
        _fg = c.Fg;
        _tp = c.Settings.Bool("cardColors") ? Purple : c.LightTheme ? c.Fg : Quiet;
        EnsureGradient(ctx);

        var B = Bounds;
        ctx.PushAxisAlignedClip(B, AntialiasMode.PerPrimitive);

        // Symbol: drei Balken, beim Öffnen größer und weiter innen.
        float e = Math.Clamp(Open, 0, 1);
        float isz = Lerp(20, 30, e) * K, ip = Lerp(10, 16, e) * K;
        DrawBars(ctx, B.Left + ip, B.Top + ip, isz);

        // Klein: was spielt.
        if (_oc > 0.01f && _value.Length > 0)
        {
            _alpha = _oc;
            Col(_fg, 0.92f);
            ctx.DrawText(_value, _fValue!, new Rect(B.Left + 36 * K + (1 - _oc) * 6 * K, B.Top, _valueW + 4 * K, 40 * K), _brush);
        }

        // Karte
        if (_ok > 0.01f)
        {
            float ox = B.Left, oy = B.Top + (1 - _ok) * 6 * K;
            DrawHead(ctx, c, ox, oy, K);
            float vm = 1 - _view;
            if (vm > 0.01f) { _alpha = _ok * Math.Clamp(vm * 1.4f - 0.2f, 0, 1); DrawMain(ctx, c, ox - 28 * K * _view, oy, K); }
            if (_view > 0.01f && _tuning >= 0) { _alpha = _ok * Math.Clamp(_view * 1.4f - 0.2f, 0, 1); DrawTune(ctx, c, ox + 28 * K * (1 - _view), oy, K); }
        }

        ctx.PopAxisAlignedClip();
        ctx.Transform = _t0;
        _alpha = 1;
    }

    private void DrawBars(ID2D1DeviceContext ctx, float x, float y, float size)
    {
        float s = size / 24;
        ctx.Transform = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(x, y) * _t0;
        _alpha = 1;
        Col(_tp, _playing || _solo ? 1f : 0.7f);
        ReadOnlySpan<float> xs = stackalloc float[] { 4.6f, 10.4f, 16.2f };
        for (int i = 0; i < 3; i++)
        {
            float h = 14 * Math.Clamp(_bars[i], 0.05f, 1.2f), r = Math.Min(1.6f, h / 2);
            ctx.FillRoundedRectangle(RR(xs[i], 12 - h / 2, 3.2f, h, r, r), _brush!);
        }
        ctx.Transform = _t0;
    }

    private void DrawHead(ID2D1DeviceContext ctx, UiContext c, float ox, float oy, float K)
    {
        // „Klangwelten“ bzw. „‹ Regen“, rechts der Zustand.
        float tv = _tuning >= 0 ? _view : 0;
        if (tv < 0.99f)
        {
            _alpha = _ok * (1 - tv);
            Col(_fg, 0.96f);
            ctx.DrawText(L.T("Klangwelten"), _fTitle!, new Rect(ox + 58 * K - 8 * K * tv, oy + 18 * K, 200 * K, 20 * K), _brush!);
        }
        if (_tuning >= 0 && tv > 0.01f)
        {
            _alpha = _ok * tv;
            bool hov = _hover == HBack;
            Col(_fg, hov ? 1f : 0.96f);
            float sh = (1 - tv) * 8 * K;
            DrawIcon(ctx, _back!, ox + 52 * K + sh - (hov ? 2 * K : 0), oy + 19.5f * K, 17 * K, 2f);
            Col(_fg, 0.96f);
            ctx.DrawText(Soundscape.Name(_tuning), _fTitle!, new Rect(ox + 71 * K + sh, oy + 18 * K, 220 * K, 20 * K), _brush!);
        }
        _alpha = _ok;
        if (_solo) Col(_tp, 1f); else Col(_fg, 0.55f);
        ctx.DrawText(_meta, _fMeta!, new Rect(ox + 120 * K, oy + 18 * K, (CardW - 18 - 120) * K, 20 * K), _brush!);
    }

    private void DrawMain(ID2D1DeviceContext ctx, UiContext c, float ox, float oy, float K)
    {
        float a0 = _alpha;
        for (int i = 0; i < 6; i++) DrawTile(ctx, i, ox, oy, K);

        // Abspielen / Pause
        var pc = new Vector2(ox + 36 * K, oy + (ControlsY + 20) * K);
        bool ph = _hover == HPlay;
        Col(_tp, ph ? 0.34f : 0.24f);
        ctx.FillEllipse(new Ellipse(pc, 20 * K, 20 * K), _brush!);
        Col(_tp, 0.35f);
        ctx.DrawEllipse(new Ellipse(pc, 19.5f * K, 19.5f * K), _brush!, 1 * K);
        float isz = 20 * K;
        var ix = pc.X - isz / 2; var iy = pc.Y - isz / 2;
        if (_pp < 0.99f)
        {
            _alpha = a0 * (1 - _pp);
            Col(_fg, 1f);
            ctx.Transform = Morph(Lerp(1, 0.5f, _pp), -30 * _pp) * Matrix3x2.CreateScale(isz / 24) * Matrix3x2.CreateTranslation(ix, iy) * _t0;
            ctx.FillGeometry(_play!.Path!, _brush!);
        }
        if (_pp > 0.01f)
        {
            _alpha = a0 * _pp;
            Col(_fg, 1f);
            ctx.Transform = Morph(Lerp(0.5f, 1, _pp), 30 * (1 - _pp)) * Matrix3x2.CreateScale(isz / 24) * Matrix3x2.CreateTranslation(ix, iy) * _t0;
            ctx.FillRoundedRectangle(RR(6.2f, 4.8f, 4.2f, 14.4f, 1.4f, 1.4f), _brush!);
            ctx.FillRoundedRectangle(RR(13.6f, 4.8f, 4.2f, 14.4f, 1.4f, 1.4f), _brush!);
        }
        ctx.Transform = _t0;
        _alpha = a0;

        // Lautstärke
        float vol = (float)Math.Clamp(c.Settings.Num("soundVol"), 0, 1);
        DrawSlider(ctx, ox + VolX0 * K, ox + VolX1 * K, pc.Y, vol, _drag == HVol, K);
        Col(_fg, 0.55f);
        ctx.DrawText(Pct[(int)Math.Round(vol * 100)], _fNum!, new Rect(ox + (VolX1 + 12) * K, pc.Y - 10 * K, 34 * K, 20 * K), _brush!);

        // Schalter: Wetter, Fokus, Effekte
        float x = ox + 16 * K, y = oy + ChipsY * K;
        for (int i = 0; i < 3; i++)
        {
            float w = ChipWidth(i) * K;
            bool pressed = c.Settings.Bool(ChipKeys[i]), hov = _hover == HChip + i;
            var rr = RR(x, y, w, 28 * K, 14 * K, 14 * K);
            if (pressed) { Col(_tp, 0.2f); ctx.FillRoundedRectangle(rr, _brush!); Col(_tp, 0.35f); ctx.DrawRoundedRectangle(Inset(rr, 0.5f * K), _brush!, 1 * K); }
            else { Col(_fg, hov ? 0.12f : 0.07f); ctx.FillRoundedRectangle(rr, _brush!); }
            float ca = pressed ? 0.95f : hov ? 0.85f : 0.6f;
            Col(_fg, ca);
            var icon = i == 0 ? _cloud! : i == 1 ? _ring! : _spark!;
            DrawIcon(ctx, icon, x + 10 * K, y + 7 * K, 14 * K, 1.7f);
            ctx.DrawText(L.T(ChipLabels[i]), _fChip!, new Rect(x + 30 * K, y, _chipW[i] + 4 * K, 28 * K), _brush!);
            x += w + 8 * K;
        }
    }

    private void DrawTile(ID2D1DeviceContext ctx, int i, float ox, float oy, float K)
    {
        var tr = TileRect(i);
        float x = ox + tr.X * K, y = oy + tr.Y * K, w = tr.Z * K, h = tr.W * K, rad = 14 * K;
        string id = Soundscape.Ids[i];
        bool on = Soundscape.IsOn(id), live = on && _playing;
        float hv = _tileHover[i];
        var rr = RR(x, y, w, h, rad, rad);

        Col(_fg, 0.06f + 0.04f * hv);
        ctx.FillRoundedRectangle(rr, _brush!);

        // Die Stufe füllt die Kachel von links; die Kante ist, wo man sie hinzieht.
        float fw = w * Math.Clamp(_fillW[i], 0, 1);
        if (fw > 0.5f && _grad != null)
        {
            _grad.StartPoint = new Vector2(x, y);
            _grad.EndPoint = new Vector2(x + fw, y);
            _grad.Opacity = _alpha * _fillOp[i];
            ctx.PushAxisAlignedClip(new Rect(x, y, fw, h), AntialiasMode.PerPrimitive);
            ctx.FillRoundedRectangle(rr, _grad);
            ctx.PopAxisAlignedClip();
            float ew = 1.5f * K;
            ctx.PushAxisAlignedClip(new Rect(x + fw - ew, y, ew, h), AntialiasMode.PerPrimitive);
            Col(_tp, 0.65f * _fillOp[i]);
            ctx.FillRoundedRectangle(rr, _brush!);
            ctx.PopAxisAlignedClip();
        }

        if (live) Col(_tp, 0.4f); else if (on) Col(_tp, 0.22f); else Col(_fg, 0.06f);
        ctx.DrawRoundedRectangle(Inset(rr, 0.5f * K), _brush!, 1 * K);

        float ta = live ? 0.96f : on ? Math.Max(0.78f, 0.58f + 0.22f * hv) : 0.58f + 0.22f * hv;
        if (live) Col(_tp, 1f); else Col(_fg, ta);
        DrawIcon(ctx, _soundIcons![i], x + 11 * K, y + 8 * K, 19 * K, 1.6f);
        Col(_fg, ta);
        ctx.DrawText(Soundscape.Name(i), _fName!, new Rect(x + 11 * K, y + h - 8 * K - 16 * K, w - 50 * K, 16 * K), _brush!);

        // Die eigenen Einstellungen, rechts.
        var tc = new Vector2(x + w - 22 * K, y + h / 2);
        bool th = _hover == HTweak + i;
        if (th) { Col(_fg, 0.1f); ctx.FillEllipse(new Ellipse(tc, 15 * K, 15 * K), _brush!); }
        Col(_fg, th ? 1f : 0.36f + 0.34f * hv);
        DrawIcon(ctx, _sliders!, tc.X - 8.5f * K, tc.Y - 8.5f * K, 17 * K, 1.6f);
    }

    private void DrawTune(ID2D1DeviceContext ctx, UiContext c, float ox, float oy, float K)
    {
        float y = oy + Head * K;
        for (int i = 0; i < _rowCount; i++)
        {
            var row = _rows[i];
            double v = Get(c, row);
            if (row.Kind == RowKind.Switch)
            {
                Col(_fg, 0.84f);
                ctx.DrawText(L.T(row.Label), _fLabel!, new Rect(ox + 16 * K, y, 230 * K, 28 * K), _brush!);
                bool sw = v > 0.5;
                float sx = ox + 264 * K, sy = y + 3 * K;
                var tr = RR(sx, sy, 36 * K, 22 * K, 11 * K, 11 * K);
                if (sw) Col(_tp, 0.8f); else Col(_fg, 0.16f);
                ctx.FillRoundedRectangle(tr, _brush!);
                float kx = sx + (2 + 9 + (sw ? 14 : 0)) * K, ky = sy + 11 * K;
                Col(Vector3.Zero, 0.18f);
                ctx.FillEllipse(new Ellipse(new Vector2(kx, ky + 1 * K), 9.5f * K, 9.5f * K), _brush!);
                Col(Vector3.One, 1f);
                ctx.FillEllipse(new Ellipse(new Vector2(kx, ky), 9 * K, 9 * K), _brush!);
                y += (28 + 10) * K;
                continue;
            }
            Col(_fg, 0.84f);
            ctx.DrawText(L.T(row.Label), _fLabel!, new Rect(ox + 16 * K, y, 160 * K, 24 * K), _brush!);
            Col(_fg, 0.55f);
            ctx.DrawText(Format(row, v), _fNum!, new Rect(ox + 120 * K, y, (274 - 120) * K, 24 * K), _brush!);
            // Zurücksetzen: auf dem Standard gibt es nichts zurückzusetzen.
            bool atDef = Math.Abs(v - Default(row)) < 1e-6, rh = _hover == HReset + i && !atDef;
            var rc = new Vector2(ox + 292 * K, y + 12 * K);
            if (rh) { Col(_fg, 0.1f); ctx.FillEllipse(new Ellipse(rc, 12 * K, 12 * K), _brush!); }
            Col(_fg, atDef ? 0.62f * 0.22f : rh ? 1f : 0.62f);
            DrawIcon(ctx, _undo!, rc.X - 7 * K, rc.Y - 7 * K, 14 * K, 1.9f);
            float t = (float)Math.Clamp((v - row.From) / (row.To - row.From), 0, 1);
            DrawSlider(ctx, ox + 16 * K, ox + 300 * K, y + 36 * K, t, _drag == HBar + i, K);
            y += (48 + 10) * K;
        }
    }

    private void DrawSlider(ID2D1DeviceContext ctx, float x0, float x1, float cy, float t, bool active, float K)
    {
        Col(_fg, 0.14f);
        ctx.FillRoundedRectangle(RR(x0, cy - 2 * K, x1 - x0, 4 * K, 2 * K, 2 * K), _brush!);
        float kx = x0 + (x1 - x0) * t;
        if (kx - x0 > 0.5f)
        {
            Col(_fg, 0.82f);
            ctx.FillRoundedRectangle(RR(x0, cy - 2 * K, kx - x0, 4 * K, 2 * K, 2 * K), _brush!);
        }
        float r = 7 * K * (active ? 1.15f : 1f);
        Col(Vector3.Zero, 0.2f);
        ctx.FillEllipse(new Ellipse(new Vector2(kx, cy + 1 * K), r + 1 * K, r + 1 * K), _brush!);
        Col(Vector3.One, 1f);
        ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), r, r), _brush!);
    }

    private void DrawIcon(ID2D1DeviceContext ctx, Icon icon, float x, float y, float size, float stroke)
    {
        ctx.Transform = Matrix3x2.CreateScale(size / 24) * Matrix3x2.CreateTranslation(x, y) * _t0;
        if (icon.Path != null) ctx.DrawGeometry(icon.Path, _brush!, stroke, _round);
        foreach (var cc in icon.Circles) ctx.DrawEllipse(new Ellipse(new Vector2(cc.X, cc.Y), cc.Z, cc.Z), _brush!, stroke, _round);
        ctx.Transform = _t0;
    }

    private static Matrix3x2 Morph(float scale, float deg) =>
        Matrix3x2.CreateTranslation(-12, -12) * Matrix3x2.CreateRotation(deg * MathF.PI / 180) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(12, 12);

    private static RoundedRectangle Inset(RoundedRectangle r, float d) =>
        RR(r.Rect.Left + d, r.Rect.Top + d, r.Rect.Right - r.Rect.Left - 2 * d, r.Rect.Bottom - r.Rect.Top - 2 * d, Math.Max(0, r.RadiusX - d), Math.Max(0, r.RadiusY - d));

    private static RoundedRectangle RR(float x, float y, float w, float h, float rx, float ry) =>
        new(new System.Drawing.RectangleF(x, y, w, h), rx, ry);

    private void Col(Vector3 rgb, float a) => _brush!.Color = new Color4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a * _alpha, 0, 1));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Ease(float cur, float target, float k) => Math.Abs(target - cur) < 1e-4f ? target : cur + (target - cur) * k;
    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    // =================================================================================================
    // Ressourcen
    // =================================================================================================

    private void EnsureDevice(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            DisposeDevice();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        }
        if (_factoryPtr != c.D2D.NativePointer || _soundIcons == null)
        {
            DisposeGeometry();
            _factoryPtr = c.D2D.NativePointer;
            BuildGeometry(c.D2D);
        }
    }

    private void EnsureGradient(ID2D1DeviceContext ctx)
    {
        if (_grad != null && _gradTp == _tp) return;
        _grad?.Dispose(); _grad = null;
        _stops?.Dispose(); _stops = null;
        _gradTp = _tp;
        _stops = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop { Position = 0, Color = new Color4(_tp.X, _tp.Y, _tp.Z, 0.1f) },
            new GradientStop { Position = 1, Color = new Color4(_tp.X, _tp.Y, _tp.Z, 0.26f) },
        });
        _grad = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = Vector2.Zero, EndPoint = new Vector2(1, 0) }, _stops);
    }

    private void EnsureFormats(UiContext c)
    {
        if (_fmtK == c.K && _fTitle != null) return;
        DisposeFormats();
        _fmtK = c.K;
        float K = c.K;
        _fTitle = Fmt0(c, 14.5f * K, 600, TextAlignment.Leading);
        _fMeta = Fmt0(c, 12 * K, 550, TextAlignment.Trailing);
        _fValue = Fmt0(c, 13.5f * K, 600, TextAlignment.Leading);
        _fName = Fmt0(c, 12 * K, 600, TextAlignment.Leading);
        _fNum = Fmt0(c, 12 * K, 550, TextAlignment.Trailing);
        _fChip = Fmt0(c, 12 * K, 600, TextAlignment.Leading);
        _fLabel = Fmt0(c, 12.5f * K, 600, TextAlignment.Leading);
        for (int i = 0; i < 3; i++) _chipW[i] = Measure(c, L.T(ChipLabels[i]), _fChip) / K;
        for (int i = 0; i < 6; i++) _nameW[i] = Measure(c, Soundscape.Name(i), _fTitle) / K;
        _valueDirty = true;
    }

    private static IDWriteTextFormat Fmt0(UiContext c, float size, int weight, TextAlignment align)
    {
        // Eigene Kopie: die Formate von UiFonts gehören allen Teilen und dürfen nicht verändert werden
        // (sonst landen Texte anderer Karten neben dem Bildschirm).
        var shared = c.Fonts?.Format(c, size, weight);
        var f = shared != null
            ? c.DWrite.CreateTextFormat(shared.FontFamilyName, shared.FontCollection, shared.FontWeight, shared.FontStyle, shared.FontStretch, shared.FontSize, "de-de")
            : c.DWrite.CreateTextFormat("Segoe UI Variable Text", (FontWeight)weight, FontStyle.Normal, FontStretch.Normal, size);
        f.TextAlignment = align;
        f.ParagraphAlignment = ParagraphAlignment.Center;
        f.WordWrapping = WordWrapping.NoWrap;
        return f;
    }

    private static float Measure(UiContext c, string s, IDWriteTextFormat f)
    {
        using var l = c.DWrite.CreateTextLayout(s, f, 4096, 100);
        return l.Metrics.WidthIncludingTrailingWhitespace;
    }

    // Symbole wie im Original (SVG, 24er Raster).
    private const string Cloud = "M7.2 18h9.9a3.9 3.9 0 0 0 .5-7.8 5.4 5.4 0 0 0-10.4-.9 4.4 4.4 0 0 0 0 8.7z";
    private static readonly string[] SoundPaths =
    {
        "M7 14.2h9.6a3.6 3.6 0 0 0 .5-7.15 5.1 5.1 0 0 0-9.8-.85A4 4 0 0 0 7 14.2z M8.6 17.4l-.9 2.4M12.6 17.4l-.9 2.4M16.6 17.4l-.9 2.4",
        "M3 8.2c2-1.5 4-1.5 6 0s4 1.5 6 0 4-1.5 6 0M3 12.6c2-1.5 4-1.5 6 0s4 1.5 6 0 4-1.5 6 0M3 17c2-1.5 4-1.5 6 0s4 1.5 6 0 4-1.5 6 0",
        "M3 9.2h10.6a2.9 2.9 0 1 0-2.9-2.9M3 13h14.6a2.9 2.9 0 1 1-2.9 2.9M3 16.8h6.4",
        "M2.6 17.6c2.4 0 3.4-1.9 5.8-1.9s3.4 1.9 5.8 1.9 3.4-1.9 5.8-1.9 M3.2 13.4c1.3-4.6 6.6-6.9 10.6-4.4-2.6.3-3.6 2.5-2.1 4 1.5 1.6 4.4 1 5.6-.9",
        "M12 20.8c-3.5 0-5.9-2.4-5.9-5.6 0-3.4 2.6-5 3.6-8.4 2.2 1.6 3 3.6 2.8 5.4 1-.6 1.8-1.8 2-3.2 1.8 1.8 3.4 4 3.4 6.4 0 3-2.4 5.4-5.9 5.4z",
        "M2.5 12h2.2l1.6-3.6 2.1 8.4 2.2-11.6 2.2 13.6 2.1-9.8 1.9 5.6 1.4-2.6h3.3",
    };

    private sealed class Icon
    {
        public ID2D1PathGeometry? Path;
        public Vector3[] Circles = Array.Empty<Vector3>();
        public void Dispose() { Path?.Dispose(); Path = null; }
    }

    private void BuildGeometry(ID2D1Factory1 f)
    {
        _round = f.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round, MiterLimit = 10,
        });
        _soundIcons = SoundPaths.Select(p => new Icon { Path = Svg.Parse(f, p, false) }).ToArray();
        _cloud = new Icon { Path = Svg.Parse(f, Cloud, false) };
        _ring = new Icon { Path = Svg.Parse(f, "M12 7.6V12l2.8 1.8", false), Circles = new[] { new Vector3(12, 12, 8) } };
        _spark = new Icon { Path = Svg.Parse(f, "M11 4.5l1.5 4.2 4.2 1.5-4.2 1.5L11 15.9l-1.5-4.2-4.2-1.5 4.2-1.5z M17.6 14.6l.7 1.7 1.7.7-1.7.7-.7 1.7-.7-1.7-1.7-.7 1.7-.7z", false) };
        _sliders = new Icon { Path = Svg.Parse(f, "M4 8h9M17 8h3M4 16h3M11 16h9", false), Circles = new[] { new Vector3(15, 8, 2.2f), new Vector3(9, 16, 2.2f) } };
        _back = new Icon { Path = Svg.Parse(f, "M14.5 6.5 9 12l5.5 5.5", false) };
        _undo = new Icon { Path = Svg.Parse(f, "M4 12a8 8 0 1 0 2.35-5.65M4 4v4.5h4.5", false) };
        _play = new Icon { Path = Svg.Parse(f, "M8 5.6v12.8a1.1 1.1 0 0 0 1.7.93l10-6.4a1.1 1.1 0 0 0 0-1.86l-10-6.4A1.1 1.1 0 0 0 8 5.6z", true) };
    }

    private void DisposeGeometry()
    {
        if (_soundIcons != null) foreach (var i in _soundIcons) i.Dispose();
        _soundIcons = null;
        _cloud?.Dispose(); _ring?.Dispose(); _spark?.Dispose(); _sliders?.Dispose(); _back?.Dispose(); _undo?.Dispose(); _play?.Dispose();
        _cloud = _ring = _spark = _sliders = _back = _undo = _play = null;
        _round?.Dispose(); _round = null;
    }

    private void DisposeDevice()
    {
        _brush?.Dispose(); _brush = null;
        _grad?.Dispose(); _grad = null;
        _stops?.Dispose(); _stops = null;
        _gradTp = new Vector3(-1);
    }

    private void DisposeFormats()
    {
        _fTitle?.Dispose(); _fMeta?.Dispose(); _fValue?.Dispose(); _fName?.Dispose(); _fNum?.Dispose(); _fChip?.Dispose(); _fLabel?.Dispose();
        _fTitle = _fMeta = _fValue = _fName = _fNum = _fChip = _fLabel = null;
    }

    public override void Dispose()
    {
        DisposeDevice();
        DisposeGeometry();
        DisposeFormats();
    }

    // =================================================================================================
    // Kleiner SVG-Pfad-Leser (M L H V C S A Z, absolut und relativ)
    // =================================================================================================

    private static class Svg
    {
        public static ID2D1PathGeometry Parse(ID2D1Factory1 f, string d, bool filled)
        {
            var g = f.CreatePathGeometry();
            using var s = g.Open();
            s.SetFillMode(FillMode.Winding);
            int i = 0;
            char cmd = ' ';
            Vector2 cur = default, start = default, lastC = default;
            bool open = false;
            char prev = ' ';
            var begin = filled ? FigureBegin.Filled : FigureBegin.Hollow;
            while (true)
            {
                Skip(d, ref i);
                if (i >= d.Length) break;
                char ch = d[i];
                if (char.IsLetter(ch)) { cmd = ch; i++; }
                else if (cmd == 'M') cmd = 'L';
                else if (cmd == 'm') cmd = 'l';
                bool rel = char.IsLower(cmd);
                Vector2 o = rel ? cur : Vector2.Zero;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        if (open) s.EndFigure(FigureEnd.Open);
                        cur = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        start = cur;
                        s.BeginFigure(cur, begin);
                        open = true;
                        break;
                    case 'L':
                        cur = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        s.AddLine(cur);
                        break;
                    case 'H':
                        cur = new Vector2((rel ? cur.X : 0) + Num(d, ref i), cur.Y);
                        s.AddLine(cur);
                        break;
                    case 'V':
                        cur = new Vector2(cur.X, (rel ? cur.Y : 0) + Num(d, ref i));
                        s.AddLine(cur);
                        break;
                    case 'C':
                    {
                        var p1 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        var p2 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        var p3 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        s.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                        lastC = p2; cur = p3;
                        break;
                    }
                    case 'S':
                    {
                        char pu = char.ToUpperInvariant(prev);
                        var p1 = pu == 'C' || pu == 'S' ? 2 * cur - lastC : cur;
                        var p2 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        var p3 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        s.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                        lastC = p2; cur = p3;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Num(d, ref i), ry = Num(d, ref i), rot = Num(d, ref i);
                        bool large = Num(d, ref i) != 0, sweep = Num(d, ref i) != 0;
                        var p = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        s.AddArc(new ArcSegment
                        {
                            Point = p, Size = new Size(rx, ry), RotationAngle = rot,
                            SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                            ArcSize = large ? ArcSize.Large : ArcSize.Small,
                        });
                        cur = p;
                        break;
                    }
                    case 'Z':
                        if (open) { s.EndFigure(FigureEnd.Closed); open = false; }
                        cur = start;
                        break;
                    default:
                        i++;
                        break;
                }
                prev = cmd;
            }
            if (open) s.EndFigure(FigureEnd.Open);
            s.Close();
            return g;
        }

        private static void Skip(string d, ref int i)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',' || d[i] == '\n' || d[i] == '\t')) i++;
        }

        private static float Num(string d, ref int i)
        {
            Skip(d, ref i);
            int s = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false;
            while (i < d.Length)
            {
                char ch = d[i];
                if (char.IsDigit(ch)) { i++; continue; }
                if (ch == '.' && !dot) { dot = true; i++; continue; }
                if ((ch == 'e' || ch == 'E') && i + 1 < d.Length && (char.IsDigit(d[i + 1]) || d[i + 1] == '-')) { i += 2; continue; }
                break;
            }
            return s == i ? 0 : float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}

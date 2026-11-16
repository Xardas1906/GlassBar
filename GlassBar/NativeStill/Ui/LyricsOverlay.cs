using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DTextAntialias = Vortice.Direct2D1.TextAntialiasMode;
using MRect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Liedtext (app.js „Lyrics“): getaktete Zeilen von LRCLIB, die der Position des Players folgen.
/// Auf der Bühne stehen sie mittig zwischen Uhr und Pillen-Reihe, in der Musikkarte klein und linksbündig
/// (<see cref="DrawInCard(ID2D1DeviceContext, UiContext, MRect, float)"/>). Die Zeilen laufen wie ein Förderband:
/// die nächste wartet klein und blass darunter, steigt zu ihrer Zeit auf und wird hell, die alte treibt nach oben
/// und löst sich auf. Eine Lücke zählt mit drei Punkten herunter. Beim Drüberfahren erscheint eine kleine Kapsel,
/// mit der sich der Text je Lied um halbe Sekunden früher oder später stellen lässt (wird je Lied gemerkt).
/// </summary>
public sealed class LyricsOverlay : IOverlay, IDisposable
{
    private const double Lead = 0.25;              // Wechsel einen Hauch früher, damit er auf der Stimme landet
    private const string ShiftKey = "lyrics.shift"; // StillState: je Lied die Verschiebung

    public LyricsOverlay()
    {
        // Ruhewerte: Schaltflächen ungedrückt, Wert voll sichtbar
        _press.Snap(1);
        _bump.Snap(1);
    }

    /// <summary>Liegt über dem Bild, unter den Pillen (Bühne z-index 2).</summary>
    public int Z => 20;

    /// <summary>Die Quelle (Stand, Fehler) – z. B. für die Einstellungen (lyricsStatus/lyricsError).</summary>
    public LyricsService Service { get; } = new();
    public LyricsStatus Status => Service.Status;

    /// <summary>Wie weit die Uhr nach oben ausweicht, solange Liedtext auf der Bühne steht (--lift, px).</summary>
    public float Lift => (float)_lift.X;

    /// <summary>Höhe der höchsten Pille unten (layout.edgeH('bottom')), von der Oberfläche gesetzt; 0 = unbekannt.</summary>
    public float BottomRowHeight;

    /// <summary>Verschiebung des aktuellen Lieds in Sekunden (negativ = früher).</summary>
    public double Shift => _shiftOff;
    /// <summary>Anzeige der Verschiebung wie in der Kapsel („Pünktlich“, „0.5 s früher“ …).</summary>
    public string ShiftText => FmtShift(_shiftOff);

    /// <summary>Steht der Liedtext gerade in der Musikkarte (Einstellung „card“ und das Lied hat getakteten Text)?</summary>
    public bool InCard(UiContext c) => c.Settings.Str("lyrics") == "card" && Visible(c);
    /// <summary>Höhe der Liedtext-Fläche in der Karte (cardLyricsH × K), ohne Platz für die Kapsel.</summary>
    public float CardLyricsHeight(UiContext c) => (float)(58 * c.Settings.Num("lyricsSize")) * c.K;
    /// <summary>Zusätzlicher Platz unter dem Liedtext in der Karte, solange die Kapsel offen ist (40 × K).</summary>
    public float CardNudgeRoom(UiContext c) => _shiftOn ? 40 * c.K : 0;

    public bool Animating { get; private set; }

    // ---------- Zustand ----------

    private MediaState? _media;
    private string _title = "", _artist = "";
    private string _key = "";
    private int _seenVersion = -1;
    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private string? _mode;
    private bool _inCard;
    private bool _shown;
    private int _index = -2;
    private Line? _past, _cur, _next;
    private readonly List<Line> _all = new(8);
    private float _blockH;
    private double _now;
    private float _k = 1;

    // Was die Zeilen formt: ändert sich etwas davon, werden die Zeilen neu gesetzt
    private double _size = -1, _bright = -1;
    private string _weight = "", _font = "";
    private float _shapeK = -1;
    private int _shapeW = -1;
    private IDWriteTextFormat? _fmt;
    private float _fmtSize;
    private int _fmtWeight;
    private UiFonts? _fmtFonts;

    // Bühne
    private readonly Spring _base = new(1, 0.6);
    private readonly Spring _lift = new(1, 1.1);
    private bool _baseSet;
    private float _stageX;
    private Anim _boxOp, _boxBlur;

    // Karte
    private double _cardSeenAt = -1e9;
    private float _cardOl;

    // Kapsel (Timing)
    private double _shiftOff;
    private bool _shiftOn;
    private double _shiftHideAt;
    private Anim _nudgeOp, _nudgeTr, _bump, _press;
    private int _pressed = -1;
    private MRect _capRect, _btnL, _btnVal, _btnR;
    private MRect _curHit, _nextHit;
    private Vector2 _lastPointer = new(float.NaN);
    private IDWriteTextLayout? _valLayout;
    private string _valText = "";
    private float _valK = -1;
    private UiFonts? _valFonts;

    // Geräteressourcen
    private IntPtr _ctxPtr;
    private ID2D1DeviceContext? _off;          // zweiter Kontext auf demselben Gerät: Zeilen in Bitmaps setzen
    private ID2D1SolidColorBrush? _offBrush;
    private ID2D1SolidColorBrush? _brush;
    private GaussianBlur? _blur;
    private ID2D1Image? _blurOut;
    private ColorMatrix? _tint;
    private ID2D1Image? _tintOut;
    private Shadow? _glow, _drop;
    private ID2D1Image? _glowOut, _dropOut;
    private ID2D1StrokeStyle? _round;

    // ---------- Update ----------

    public void Update(UiContext c, double dt)
    {
        _now = c.Time;
        _k = c.K;
        var s = c.Settings;
        string mode = s.Str("lyrics");
        string timing = s.Str("lyricsTiming");
        bool inCard = mode == "card";

        // Lied verfolgen (der Zustand wird als Ganzes ersetzt; nur bei neuem Titel/Künstler neu suchen)
        var m = c.Media;
        if (!ReferenceEquals(m, _media))
        {
            _media = m;
            if (m.Title.Length > 0 && (m.Title != _title || m.Artist != _artist))
            {
                _title = m.Title;
                _artist = m.Artist;
                LoadLyrics(c, false);
            }
        }

        // Einstellung „lyrics“ geändert: Platz wechseln, ggf. jetzt erst suchen
        if (mode != _mode)
        {
            bool first = _mode == null;
            _mode = mode;
            if (!first && mode != "off" && Service.Status == LyricsStatus.Idle && _key.Length > 0) LoadLyrics(c, true);
            if (inCard != _inCard)
            {
                // Der Kasten zieht um: alte Zeilen gehen sofort, ein frisches Paar folgt
                DropAll();
                _inCard = inCard;
            }
            _index = -2;
        }

        // Neues Ergebnis der Suche
        if (Service.Version != _seenVersion)
        {
            _seenVersion = Service.Version;
            _lyrics = Service.Key == _key ? Service.Lines : Array.Empty<LyricLine>();
            _index = -2;
        }

        ReadShape(c);

        bool show = Visible(c) && (!inCard || _now - _cardSeenAt < 0.3);
        if (show != _shown)
        {
            _shown = show;
            if (!show)
            {
                ClearLines();
                _index = -2;
            }
        }
        if (_shiftOn && (!show || timing != "hover")) ShowShift(false);

        // Kasten: blendet über 0,7 s ein (mit Unschärfe), pausiert halb
        bool paused = m.Paused && !m.Playing;
        _boxOp.Set(show ? (paused ? 0.5f : 1f) : 0f, _now, 0.7f, Curve.Ease);
        _boxBlur.Set(show ? 0f : 8 * _k, _now, 0.7f, Curve.Ease);

        // Liedtext auf der Bühne beschwert die untere Hälfte: die Uhr steigt ein wenig
        _lift.Target = show && !inCard ? (48 + 24 * _size) * _k : 0;
        _lift.Step(dt);

        if (!inCard) PositionStage(c, dt);

        if (show)
        {
            double pos = m.CurrentPosition + Lead - _shiftOff;
            int idx = -1;
            for (int i = 0; i < _lyrics.Count && _lyrics[i].T <= pos; i++) idx = i;
            if (idx != _index) ShowLine(c, idx);
            if (_cur is { Dots: true }) Countdown(_cur, pos, m.Playing);
        }

        UpdateHover(c, show, timing, inCard);

        // Ausgediente Zeilen aufräumen
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            var l = _all[i];
            if (_now >= l.RemoveAt) { l.Dispose(); _all.RemoveAt(i); }
        }

        Animating = ComputeAnimating(m.Playing);
    }

    private bool Visible(UiContext c)
    {
        var m = c.Media;
        return c.Settings.Str("lyrics") != "off" && Service.Status == LyricsStatus.Synced && Service.Key == _key
            && m.Duration > 0 && m.Title.Length > 0 && (m.Playing || m.Paused);
    }

    private void ReadShape(UiContext c)
    {
        var s = c.Settings;
        double size = s.Num("lyricsSize"), bright = s.Num("lyricsBright");
        string weight = s.Str("lyricsWeight"), font = s.Str("font");
        if (size != _size || weight != _weight || font != _font || c.K != _shapeK || c.Width != _shapeW)
        {
            // Neue Größe/Stärke/Schrift: die Zeilen neu setzen
            _size = size; _weight = weight; _font = font; _shapeK = c.K; _shapeW = c.Width;
            _fmt = null;
            _index = -2;
        }
        if (bright != _bright)
        {
            _bright = bright;
            if (_cur != null) LayoutLines();
        }
    }

    private void LoadLyrics(UiContext c, bool force)
    {
        var m = c.Media;
        string key = LyricsService.MakeKey(m);
        if (!force && key == _key) return;
        _key = key;
        _shiftOff = ShiftRead(key);
        _valText = "";
        _lyrics = Array.Empty<LyricLine>();
        _index = -2;
        Service.Load(m, c.Settings.Str("lyrics") != "off", () => _media ?? m);
    }

    // ---------- Zeilen (Förderband) ----------

    private void ShowLine(UiContext c, int idx)
    {
        bool sequential = _index != -2 && idx == _index + 1 && _next != null;
        _index = idx;
        string? nextText = LineText(idx + 1);
        if (sequential)
        {
            Retire(_past, 0.9);
            _past = _cur;
            _cur = _next;
            _next = nextText != null ? MakeLine(c, nextText) : null;
        }
        else
        {
            // Sprung, neues Lied oder erstes Erscheinen: auflösen und ein frisches Paar setzen
            Retire(_past, 0.7); Retire(_cur, 0.7); Retire(_next, 0.7);
            _past = null;
            _cur = MakeLine(c, idx >= 0 ? LineText(idx) ?? "" : "");
            _next = nextText != null ? MakeLine(c, nextText) : null;
        }
        LayoutLines();
    }

    private string? LineText(int i) => i >= 0 && i < _lyrics.Count ? _lyrics[i].Text : null;

    private void LayoutLines()
    {
        float S = _inCard ? 0.84f : 0.7f;
        float gap = (_inCard ? 5 : 12) * _k * (float)_size;
        float hN = _next != null ? _next.H * S : 0;
        float hC = _cur?.H ?? 0;
        float yCur = hN > 0 ? -(hN + gap) : 0;
        // Die Helligkeit stellt die aktuelle Zeile ein; die kommende bleibt anteilig blasser
        float B = (float)_bright;
        if (_next != null) SetRole(_next, 0, S, MathF.Round(B * 0.45f, 3), 0, false);
        if (_cur != null) SetRole(_cur, yCur, 1, B, 0, true);
        if (_past != null) SetRole(_past, yCur - hC - gap, 0.92f, 0, 6, false);
        _blockH = hC + (hN > 0 ? gap + hN : 0);
    }

    private void SetRole(Line l, float y, float scale, float opacity, float blur, bool current)
    {
        if (l.Fresh)
        {
            // Kommt von knapp darunter, blass, und setzt sich dann in seine Rolle
            l.Fresh = false;
            l.Y.Snap(y + 18 * _k);
            l.S.Snap(scale * 0.96f);
            l.Op.Snap(0);
            l.Blur.Snap(6);
            l.Glow.Snap(0);
        }
        l.Y.Set(y, _now, 0.95f, Curve.EaseOut);
        l.S.Set(scale, _now, 0.95f, Curve.EaseOut);
        l.Op.Set(opacity, _now, 0.75f, Curve.Ease);
        l.Blur.Set(blur, _now, 0.75f, Curve.Ease);
        l.Glow.Set(current ? 1 : 0, _now, 0.75f, Curve.Ease);
    }

    private void Retire(Line? l, double seconds)
    {
        if (l == null) return;
        l.Op.Set(0, _now, 0.75f, Curve.Ease);
        l.Blur.Set(6, _now, 0.75f, Curve.Ease);
        l.Glow.Set(0, _now, 0.75f, Curve.Ease);
        l.RemoveAt = Math.Min(l.RemoveAt, _now + seconds);
    }

    private void ClearLines()
    {
        Retire(_past, 0.7); Retire(_cur, 0.7); Retire(_next, 0.7);
        _past = _cur = _next = null;
        _blockH = 0;
    }

    private void DropAll()
    {
        foreach (var l in _all) l.Dispose();
        _all.Clear();
        _past = _cur = _next = null;
        _blockH = 0;
    }

    private Line MakeLine(UiContext c, string text)
    {
        float fontPx = (_inCard ? 15 : 30) * _k * (float)_size;
        float lh = (_inCard ? 1.3f : 1.26f) * fontPx;
        var l = new Line { Text = text, FontPx = fontPx, LineH = lh, Fresh = true };
        if (text.Length == 0)
        {
            l.Dots = true;
            l.H = lh;
        }
        else
        {
            float maxW = _inCard ? 364 * _k : MathF.Min(0.74f * c.Width, 1000 * _k);
            var fmt = Format(c, fontPx);
            var layout = c.DWrite.CreateTextLayout(text, fmt, maxW, 100000);
            layout.WordWrapping = WordWrapping.Wrap;
            layout.TextAlignment = _inCard ? TextAlignment.Leading : TextAlignment.Center;
            layout.ParagraphAlignment = ParagraphAlignment.Near;
            // Zeilenhöhe wie CSS line-height: Schrifthöhe mittig in der Zeile
            var lm = new LineMetrics[Math.Max(1, (int)layout.Metrics.LineCount)];
            layout.GetLineMetrics(lm, out uint lineCount);
            if (lineCount > 0)
            {
                float asc = lm[0].Baseline, total = lm[0].Height;
                layout.SetLineSpacing(LineSpacingMethod.Uniform, lh, (lh - total) / 2 + asc);
            }
            // text-wrap: balance – so schmal wie möglich bei gleicher Zeilenzahl
            int n = Math.Max(1, (int)layout.Metrics.LineCount);
            float w = maxW;
            if (n > 1)
            {
                float lo = maxW / (n + 1), hi = maxW;
                for (int i = 0; i < 10; i++)
                {
                    float mid = (lo + hi) / 2;
                    layout.MaxWidth = mid;
                    if (layout.Metrics.LineCount <= n) hi = mid; else lo = mid;
                }
                w = MathF.Ceiling(hi);
                layout.MaxWidth = w;
            }
            var tm = layout.Metrics;
            l.Layout = layout;
            l.W = w;
            l.H = Math.Max(1, (int)tm.LineCount) * lh;
            l.TextLeft = tm.Left;
            l.TextWidth = tm.Width;
            l.Pad = MathF.Ceiling(6 + 0.35f * fontPx);
        }
        _all.Add(l);
        return l;
    }

    private IDWriteTextFormat Format(UiContext c, float fontPx)
    {
        int weight = _weight switch { "regular" => 400, "medium" => 500, "bold" => 650, _ => 300 };
        if (_fmt == null || _fmtSize != fontPx || _fmtWeight != weight || !ReferenceEquals(_fmtFonts, c.Fonts))
        {
            // Das Format gehört UiFonts (wird dort verwaltet); hier nur gemerkt
            _fmt = c.Fonts.Format(c, fontPx, weight);
            _fmtSize = fontPx;
            _fmtWeight = weight;
            _fmtFonts = c.Fonts;
        }
        return _fmt;
    }

    /// <summary>Eine Lücke zählt zur nächsten Zeile herunter: die Punkte leuchten nacheinander auf, atmen
    /// langsam und schwellen im letzten Moment gemeinsam an.</summary>
    private void Countdown(Line l, double pos, bool playing)
    {
        int i = _index;
        bool hasNext = i + 1 < _lyrics.Count;
        double t0 = i >= 0 ? _lyrics[i].T : 0;
        double nextT = hasNext ? _lyrics[i + 1].T : 0;
        double p = hasNext ? Math.Clamp((pos - t0) / Math.Max(0.5, nextT - t0), 0, 1) : 0;
        double swell = hasNext ? 0.2 * Smooth(0.7, 0.05, nextT - pos) : 0;
        double breathe = playing ? 0.05 * Math.Sin(_now * Math.PI * 2 / 2.6) : 0;
        for (int j = 0; j < 3; j++) l.DotF[j] = (float)Smooth(j / 3.0, (j + 1) / 3.0, p);
        l.DotScale = (float)(1 + swell + breathe * (1 - swell * 5));
    }

    // ---------- Bühne ----------

    /// <summary>Die Zeilen stehen mittig zwischen Uhr und Pillen-Reihe und weichen aus, wenn es eng wird.</summary>
    private void PositionStage(UiContext c, double dt)
    {
        float k = _k;
        float bottom = 0;
        if (c.ClockRect.Height > 0) bottom = Math.Max(bottom, c.ClockRect.Bottom);
        if (c.DateRect.Height > 0) bottom = Math.Max(bottom, c.DateRect.Bottom);
        float focusBottom = bottom > 0 ? bottom : c.Height * 0.45f;
        float pillTop = BottomRowHeight > 0 ? c.Height - 88 * k - BottomRowHeight : c.Height - 70 * k;
        // Die Kapsel hängt unter der Grundlinie; wird es eng, machen die Zeilen Platz
        float floor = pillTop - (_shiftOn ? 58 : 22) * k;
        float baseline = Math.Min((focusBottom + pillTop) / 2 + _blockH / 2, floor);
        _base.Target = baseline;
        if (!_baseSet) { _base.Snap(baseline); _baseSet = true; }
        _base.Step(dt);
        // Waagerecht: halb zwischen einer verschobenen Uhr und der mittigen Pille
        float clockX = c.ClockRect.Width > 0 ? (c.ClockRect.Left + c.ClockRect.Right) / 2 - c.Width / 2f : 0;
        _stageX = c.Width / 2f + clockX * 0.5f;
    }

    // ---------- Kapsel (Timing) ----------

    private void UpdateHover(UiContext c, bool show, string timing, bool inCard)
    {
        var p = c.Pointer;
        bool moved = c.PointerInside && p != _lastPointer;
        _lastPointer = p;
        bool onCap = _shiftOn && c.PointerInside && Inside(_capRect, p, 0);
        if (moved && show && timing == "hover" && !(inCard && _cardOl < 0.9f))
        {
            float pad = 18 * _k;
            bool over = (_shiftOn && Inside(_capRect, p, pad)) || Inside(_curHit, p, pad) || Inside(_nextHit, p, pad);
            if (over || _shiftOn)
            {
                if (over) ShowShift(true);
                _shiftHideAt = _now + (over ? 3.5 : 0.6);
            }
        }
        // Ein ruhender Zeiger lässt sie nach ein paar Sekunden gehen – außer er ruht auf der Kapsel
        if (_shiftOn && _now >= _shiftHideAt && !onCap) ShowShift(false);
        if (!c.PointerInside && _pressed >= 0) { _pressed = -1; _press.Set(1, _now, 0.35f, Curve.Spring); }
    }

    private void ShowShift(bool on)
    {
        if (on == _shiftOn) return;
        _shiftOn = on;
        _nudgeOp.Set(on ? 1 : 0, _now, 0.35f, Curve.Ease);
        _nudgeTr.Set(on ? 1 : 0, _now, 0.5f, Curve.Spring);
    }

    /// <summary>Verschiebung setzen (halbe Sekunden, ±15 s), je Lied gemerkt.</summary>
    public void SetShift(double v)
    {
        double off = Math.Clamp(Math.Round(v * 2) / 2, -15, 15);
        if (off == _shiftOff || _key.Length == 0) return;
        _shiftOff = off;
        ShiftWrite(_key, off);
        _bump.Snap(0);
        _bump.Set(1, _now, 0.26f, Curve.CssEaseOut);
    }

    private static string FmtShift(double v) => v == 0
        ? "Pünktlich"
        : $"{Math.Abs(v).ToString("0.##", CultureInfo.InvariantCulture)} s {(v < 0 ? "früher" : "später")}";

    public sealed class ShiftEntry
    {
        public double Off { get; set; }
        public long At { get; set; }
    }

    private static double ShiftRead(string key)
    {
        var d = StillState.Get<Dictionary<string, ShiftEntry>?>(ShiftKey, null);
        return d != null && d.TryGetValue(key, out var e) ? e.Off : 0;
    }

    private static void ShiftWrite(string key, double off)
    {
        var d = StillState.Get<Dictionary<string, ShiftEntry>?>(ShiftKey, null) ?? new();
        if (off != 0) d[key] = new ShiftEntry { Off = off, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        else d.Remove(key);
        if (d.Count > 200)
        {
            foreach (var old in d.OrderBy(kv => kv.Value.At).Take(d.Count - 200).Select(kv => kv.Key).ToList()) d.Remove(old);
        }
        StillState.Set(ShiftKey, d);
    }

    // ---------- Maus ----------

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        bool capOn = _shiftOn && _nudgeOp.At(_now) > 0.3f;
        if (!capOn || !Inside(_capRect, e.Pos, 0))
        {
            if (e.Kind == PointerKind.Up && _pressed >= 0) { _pressed = -1; _press.Set(1, _now, 0.35f, Curve.Spring); }
            return false;
        }
        _shiftHideAt = Math.Max(_shiftHideAt, _now + 0.6);
        if (e.Kind == PointerKind.Down && e.Button == 0)
        {
            if (Inside(_btnL, e.Pos, 0)) { _pressed = 0; SetShift(_shiftOff - 0.5); }
            else if (Inside(_btnR, e.Pos, 0)) { _pressed = 2; SetShift(_shiftOff + 0.5); }
            else if (Inside(_btnVal, e.Pos, 0) && _shiftOff != 0) { _pressed = 1; SetShift(0); }
            if (_pressed >= 0) _press.Set(0.86f, _now, 0.12f, Curve.Ease);
        }
        else if (e.Kind == PointerKind.Up && _pressed >= 0)
        {
            _pressed = -1;
            _press.Set(1, _now, 0.35f, Curve.Spring);
        }
        return true;
    }

    public bool HitTest(UiContext c, Vector2 p) => _shiftOn && Inside(_capRect, p, 0);

    private static bool Inside(MRect r, Vector2 p, float pad) =>
        r.Width > 0 && p.X > r.Left - pad && p.X < r.Right + pad && p.Y > r.Top - pad && p.Y < r.Bottom + pad;

    // ---------- Zeichnen ----------

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        _curHit = _nextHit = default;
        if (_inCard) return;   // in der Karte zeichnet die Musikkarte (DrawInCard)
        float alpha = _boxOp.At(_now) * (1 - Math.Clamp(c.StageBusy, 0, 1));
        if (alpha < 0.002f || _all.Count == 0) { _capRect = default; return; }
        EnsureDevice(ctx, c);
        var prev = ctx.Transform;
        try
        {
            float oy = (float)_base.X;
            float blur = _boxBlur.At(_now);
            for (int i = 0; i < _all.Count; i++) DrawLine(ctx, c, _all[i], _stageX, oy, true, alpha, blur, prev);
            DrawNudge(ctx, c, true, _stageX, oy + 14 * _k, alpha, prev);
        }
        finally
        {
            ctx.Transform = prev;
        }
    }

    /// <summary>Liedtext in der Musikkarte: Zeilen auf einer Grundlinie am unteren Rand von <paramref name="r"/>
    /// (links bündig, ohne Leuchten), die Kapsel darunter.</summary>
    public void DrawInCard(ID2D1DeviceContext ctx, UiContext c, MRect r) => DrawInCard(ctx, c, r, 1f);

    /// <summary>Wie oben, mit der Deckung der Liedtext-Fläche beim Auf- und Zugehen der Karte (isl.ol).</summary>
    public void DrawInCard(ID2D1DeviceContext ctx, UiContext c, MRect r, float opacity)
    {
        _cardSeenAt = c.Time;
        _cardOl = opacity;
        if (!_inCard) return;
        _curHit = _nextHit = default;
        if (opacity < 0.002f || _all.Count == 0) { _capRect = default; return; }
        EnsureDevice(ctx, c);
        var prev = ctx.Transform;
        try
        {
            for (int i = 0; i < _all.Count; i++) DrawLine(ctx, c, _all[i], r.Left, r.Bottom, false, opacity, 0, prev);
            DrawNudge(ctx, c, false, r.Left, r.Bottom + 10 * _k, opacity, prev);
        }
        finally
        {
            ctx.Transform = prev;
        }
    }

    private void DrawLine(ID2D1DeviceContext ctx, UiContext c, Line l, float ox, float oy, bool stage, float alphaMul,
        float extraBlur, Matrix3x2 prev)
    {
        float alpha = l.Op.At(_now) * alphaMul;
        float y = l.Y.At(_now), s = l.S.At(_now);
        float boxW = l.Dots ? 0 : l.W;
        float left = stage ? ox - boxW / 2 : ox;
        float top = oy - l.H;

        // Für das Drüberfahren: wo der Text steht (aktuelle und kommende Zeile)
        if ((l == _cur || l == _next) && !l.Dots)
        {
            float x0 = ox + (left + l.TextLeft - ox) * s, x1 = ox + (left + l.TextLeft + l.TextWidth - ox) * s;
            float y0 = oy + y - l.H * s;
            var hit = new MRect(x0, y0, x1 - x0, l.H * s);
            if (l == _cur) _curHit = hit; else _nextHit = hit;
        }
        else if (l == _cur && l.Dots)
        {
            float w = 2 * l.FontPx, x0 = stage ? ox - w / 2 : ox;
            _curHit = new MRect(x0, oy + y - l.H, w, l.H);
        }

        if (alpha < 0.002f) return;
        // translateY(y) scale(s) um die untere Mitte (Bühne) bzw. unten links (Karte)
        ctx.Transform = Matrix3x2.CreateTranslation(-ox, -oy) * Matrix3x2.CreateScale(s)
            * Matrix3x2.CreateTranslation(ox, oy + y) * prev;

        if (l.Dots)
        {
            DrawDots(ctx, c, l, ox, oy, stage, alpha);
            return;
        }

        EnsureBitmap(l);
        if (l.Bmp == null) return;
        var at = new Vector2(left - l.Pad, top - l.Pad);
        var fg = c.Fg;

        // Leuchten der aktuellen Zeile folgt der Helligkeit (nur auf der Bühne)
        float glow = stage ? l.Glow.At(_now) * alpha : 0;
        if (glow > 0.003f)
        {
            var a = c.Accent;
            if (!c.LightTheme)
            {
                _drop!.SetInput(0, l.Bmp, true);
                _drop.BlurStandardDeviation = 8 * _k;
                _drop.Color = new Vector4(0, 0, 0, 0.22f * glow);
                ctx.DrawImage(_dropOut!, at + new Vector2(0, 2 * _k), null, InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            _glow!.SetInput(0, l.Bmp, true);
            _glow.BlurStandardDeviation = (c.LightTheme ? 11 : 13) * _k;
            _glow.Color = new Vector4(a.X, a.Y, a.Z, (float)_bright * (c.LightTheme ? 0.14f : 0.22f) * glow);
            ctx.DrawImage(_glowOut!, at, null, InterpolationMode.Linear, CompositeMode.SourceOver);
        }

        float blur = l.Blur.At(_now);
        blur = MathF.Sqrt(blur * blur + extraBlur * extraBlur);
        if (blur > 0.05f)
        {
            _blur!.SetInput(0, l.Bmp, true);
            _blur.StandardDeviation = blur;
            _tint!.SetInput(0, _blurOut!, true);
        }
        else
        {
            _tint!.SetInput(0, l.Bmp, true);
        }
        // Weiß → Vordergrundfarbe, Deckung × alpha
        _tint.Matrix = new Matrix5x4(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, alpha, fg.X, fg.Y, fg.Z, 0);
        ctx.DrawImage(_tintOut!, at, null, InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    /// <summary>Die drei Punkte einer Lücke (.ly-dots): .28em groß, .34em Abstand, jeder leuchtet mit seinem Anteil.</summary>
    private void DrawDots(ID2D1DeviceContext ctx, UiContext c, Line l, float ox, float oy, bool stage, float alpha)
    {
        float em = l.FontPx, d = 0.28f * em, gap = 0.34f * em;
        float groupW = 3 * d + 2 * gap;
        float cy = oy - l.H / 2;
        float gx = stage ? ox - groupW / 2 : ox;
        // Gruppe skaliert um ihre Mitte (Bühne) bzw. ihren linken Rand (Karte)
        float sx = stage ? ox : gx;
        float gs = l.DotScale;
        var fg = c.Fg;
        var a = c.Accent;
        for (int j = 0; j < 3; j++)
        {
            float f = l.DotF[j];
            float cx = gx + d / 2 + j * (d + gap);
            cx = sx + (cx - sx) * gs;
            float r = d / 2 * (0.78f + 0.3f * f) * gs;
            if (f > 0.01f)
            {
                // Schein (box-shadow .5em in Akzentfarbe)
                _brush!.Color = new Color4(a.X, a.Y, a.Z, 0.6f * f * 0.22f * alpha);
                ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r + 0.25f * em * f, r + 0.25f * em * f), _brush);
                _brush.Color = new Color4(a.X, a.Y, a.Z, 0.6f * f * 0.3f * alpha);
                ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r + 0.1f * em * f, r + 0.1f * em * f), _brush);
            }
            _brush!.Color = new Color4(fg.X, fg.Y, fg.Z, (0.28f + 0.72f * f) * alpha);
            ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), _brush);
        }
    }

    /// <summary>Die Kapsel: − / Wert / +; auf der Bühne Glas, in der Karte eine stille Füllung.</summary>
    private void DrawNudge(ID2D1DeviceContext ctx, UiContext c, bool stage, float anchorX, float top, float alphaMul, Matrix3x2 prev)
    {
        float k = _k;
        float op = _nudgeOp.At(_now);
        EnsureValueLayout(c);
        float valTextW = _valLayout!.Metrics.WidthIncludingTrailingWhitespace;
        float h = 30 * k, btn = 24 * k, padX = 3 * k, gapX = 2 * k;
        float valW = MathF.Max(94 * k, valTextW + 16 * k);
        float w = 2 * padX + 2 * btn + 2 * gapX + valW;
        float left = stage ? anchorX - w / 2 : anchorX;

        // Ruhelage (für Treffer)
        _capRect = op > 0.01f ? new MRect(left, top, w, h) : default;
        float by = top + (h - btn) / 2;
        _btnL = new MRect(left + padX, by, btn, btn);
        _btnVal = new MRect(_btnL.Right + gapX, by, valW, btn);
        _btnR = new MRect(_btnVal.Right + gapX, by, btn, btn);

        float alpha = op * alphaMul;
        if (alpha < 0.002f) return;

        // Einblenden: von etwas darüber und kleiner (Feder)
        float t = _nudgeTr.At(_now);
        float scale = stage ? 0.94f + 0.06f * t : 0.96f + 0.04f * t;
        float dy = (stage ? -6 : -4) * k * (1 - t);
        var origin = stage ? new Vector2(left + w / 2, top + h / 2) : new Vector2(left, top + h / 2);
        ctx.Transform = Matrix3x2.CreateScale(scale, origin) * Matrix3x2.CreateTranslation(0, dy) * prev;

        var fg = c.Fg;
        var cap = new MRect(left, top, w, h);
        if (stage)
        {
            c.Glass.Panel(ctx, c, cap, h / 2, alpha);
            // Heller Verlauf und feiner Rand wie .ly-nudge
            _brush!.Color = c.LightTheme ? new Color4(1, 1, 1, 0.5f * alpha) : new Color4(1, 1, 1, 0.1f * alpha);
            ctx.FillRoundedRectangle(Rounded(cap, h / 2, h / 2), _brush);
            _brush.Color = c.LightTheme ? new Color4(1, 1, 1, 0.7f * alpha) : new Color4(1, 1, 1, 0.09f * alpha);
            ctx.DrawRoundedRectangle(Rounded(Inset(cap, 0.5f), h / 2 - 0.5f, h / 2 - 0.5f), _brush, 1);
        }
        else
        {
            _brush!.Color = new Color4(fg.X, fg.Y, fg.Z, 0.07f * alpha);
            ctx.FillRoundedRectangle(Rounded(cap, h / 2, h / 2), _brush);
            _brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.05f * alpha);
            ctx.DrawRoundedRectangle(Rounded(Inset(cap, 0.5f), h / 2 - 0.5f, h / 2 - 0.5f), _brush, 1);
        }

        var p = c.Pointer;
        bool inside = c.PointerInside;
        bool shifted = _shiftOff != 0;
        var capT = ctx.Transform;
        DrawButton(ctx, c, _btnL, -1, inside && Inside(_btnL, p, 0), _pressed == 0, alpha, capT);
        DrawButton(ctx, c, _btnR, 1, inside && Inside(_btnR, p, 0), _pressed == 2, alpha, capT);

        // Wert: verschoben in Akzentfarbe, ein Klick stellt ihn zurück
        if (shifted && inside && Inside(_btnVal, p, 0))
        {
            _brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.1f * alpha);
            ctx.FillRoundedRectangle(Rounded(_btnVal, btn / 2, btn / 2), _brush);
        }
        float bump = _bump.At(_now);
        float valAlpha = (0.35f + 0.65f * bump) * alpha;
        var col = shifted ? c.Accent : fg;
        _brush.Color = new Color4(col.X, col.Y, col.Z, (shifted ? 1f : 0.72f) * valAlpha);
        var vm = _valLayout.Metrics;
        var vp = new Vector2(_btnVal.Left + (valW - valTextW) / 2, _btnVal.Top + (btn - vm.Height) / 2 + 2 * (1 - bump));
        ctx.DrawTextLayout(vp, _valLayout, _brush, DrawTextOptions.None);
        ctx.Transform = prev;
    }

    private void DrawButton(ID2D1DeviceContext ctx, UiContext c, MRect r, int dir, bool hover, bool pressed, float alpha, Matrix3x2 capT)
    {
        var fg = c.Fg;
        float s = pressed || (_press.Active(_now) && _pressed < 0) ? _press.At(_now) : 1;
        var center = new Vector2((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        ctx.Transform = Matrix3x2.CreateScale(s, center) * capT;
        if (hover)
        {
            _brush!.Color = new Color4(fg.X, fg.Y, fg.Z, 0.1f * alpha);
            ctx.FillEllipse(new Ellipse(center, r.Width / 2, r.Height / 2), _brush);
        }
        // Pfeil wie im SVG (24er-Raster, 14 px groß, Strich 2)
        float u = 14 * _k / 24;
        var o = center - new Vector2(12 * u, 12 * u);
        _brush!.Color = new Color4(fg.X, fg.Y, fg.Z, (hover ? 1f : 0.72f) * alpha);
        Vector2 a, b, e;
        if (dir < 0) { a = new(14.5f, 6.5f); b = new(9f, 12f); e = new(14.5f, 17.5f); }
        else { a = new(9.5f, 6.5f); b = new(15f, 12f); e = new(9.5f, 17.5f); }
        ctx.DrawLine(o + a * u, o + b * u, _brush, 2 * u, _round);
        ctx.DrawLine(o + b * u, o + e * u, _brush, 2 * u, _round);
        ctx.Transform = capT;
    }

    private static RoundedRectangle Rounded(MRect r, float rx, float ry) =>
        new(new System.Drawing.RectangleF(r.Left, r.Top, r.Width, r.Height), rx, ry);

    private static MRect Inset(MRect r, float d) => new(r.Left + d, r.Top + d, r.Width - 2 * d, r.Height - 2 * d);

    private void EnsureValueLayout(UiContext c)
    {
        string text = FmtShift(_shiftOff);
        if (_valLayout != null && text == _valText && _valK == _k && ReferenceEquals(_valFonts, c.Fonts)) return;
        _valLayout?.Dispose();
        _valText = text;
        _valK = _k;
        _valFonts = c.Fonts;
        var fmt = c.Fonts.Format(c, 12 * _k, 550);
        _valLayout = c.DWrite.CreateTextLayout(text, fmt, 4096, 4096);
        _valLayout.WordWrapping = WordWrapping.NoWrap;
    }

    // ---------- Geräteressourcen ----------

    private void EnsureDevice(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        using (var device = ctx.Device)
        {
            _off = device.CreateDeviceContext(DeviceContextOptions.None);
        }
        _off.TextAntialiasMode = D2DTextAntialias.Grayscale;
        _offBrush = _off.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

        _blur = new GaussianBlur(ctx) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
        _blurOut = _blur.Output;
        _tint = new ColorMatrix(ctx);
        _tintOut = _tint.Output;
        _glow = new Shadow(ctx);
        _glowOut = _glow.Output;
        _drop = new Shadow(ctx);
        _dropOut = _drop.Output;
        _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });
    }

    /// <summary>Text der Zeile einmal weiß in eine eigene Bitmap setzen; gefärbt, weichgezeichnet und
    /// abgeblendet wird beim Zeichnen.</summary>
    private void EnsureBitmap(Line l)
    {
        if (l.Bmp != null && l.BmpCtx == _ctxPtr) return;
        l.Bmp?.Dispose();
        l.Bmp = null;
        if (l.Layout == null || _off == null) return;
        int w = (int)MathF.Ceiling(l.W + 2 * l.Pad), h = (int)MathF.Ceiling(l.H + 2 * l.Pad);
        var bmp = _off.CreateBitmap(new SizeI(Math.Max(1, w), Math.Max(1, h)), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target));
        _off.Target = bmp;
        _off.BeginDraw();
        _off.Clear(new Color4(0, 0, 0, 0));
        _off.DrawTextLayout(new Vector2(l.Pad, l.Pad), l.Layout, _offBrush!, DrawTextOptions.None);
        _off.EndDraw();
        _off.Target = null;
        l.Bmp = bmp;
        l.BmpCtx = _ctxPtr;
    }

    private void DisposeDevice()
    {
        foreach (var l in _all) { l.Bmp?.Dispose(); l.Bmp = null; }
        _round?.Dispose(); _round = null;
        _dropOut?.Dispose(); _dropOut = null;
        _drop?.Dispose(); _drop = null;
        _glowOut?.Dispose(); _glowOut = null;
        _glow?.Dispose(); _glow = null;
        _tintOut?.Dispose(); _tintOut = null;
        _tint?.Dispose(); _tint = null;
        _blurOut?.Dispose(); _blurOut = null;
        _blur?.Dispose(); _blur = null;
        _offBrush?.Dispose(); _offBrush = null;
        _off?.Dispose(); _off = null;
        _brush?.Dispose(); _brush = null;
        _ctxPtr = IntPtr.Zero;
    }

    public void Dispose()
    {
        DropAll();
        DisposeDevice();
        _valLayout?.Dispose();
        _valLayout = null;
    }

    // ---------- Bewegung ----------

    private bool ComputeAnimating(bool playing)
    {
        if (_boxOp.Active(_now) || _boxBlur.Active(_now) || _base.Moving || _lift.Moving) return true;
        if (_nudgeOp.Active(_now) || _nudgeTr.Active(_now) || _bump.Active(_now) || _press.Active(_now)) return true;
        if (_shiftOn) return true;   // Schaltflächen reagieren auf den Zeiger
        foreach (var l in _all)
        {
            if (l.RemoveAt < double.MaxValue) return true;
            if (l.Y.Active(_now) || l.S.Active(_now) || l.Op.Active(_now) || l.Blur.Active(_now) || l.Glow.Active(_now)) return true;
            if (l.Dots && playing) return true;
        }
        return false;
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private enum Curve { Ease, EaseOut, Spring, CssEaseOut }

    private static readonly CubicBezier EaseCurve = new(0.25, 0.1, 0.25, 1);
    private static readonly CubicBezier EaseOutCurve = new(0.16, 1, 0.3, 1);       // --ease-out
    private static readonly CubicBezier SpringCurve = new(0.3, 1.3, 0.5, 1);       // --spring
    private static readonly CubicBezier CssEaseOutCurve = new(0, 0, 0.58, 1);      // ease-out

    /// <summary>Ein CSS-Übergang: von der aktuellen Stelle zum neuen Ziel in fester Dauer.</summary>
    private struct Anim
    {
        private float _from, _to;
        private double _t0;
        private float _dur;
        private Curve _curve;

        public readonly float At(double t)
        {
            if (_dur <= 0) return _to;
            double x = (t - _t0) / _dur;
            if (x >= 1) return _to;
            if (x <= 0) return _from;
            var e = _curve switch
            {
                Curve.EaseOut => EaseOutCurve.Ease(x),
                Curve.Spring => SpringCurve.Ease(x),
                Curve.CssEaseOut => CssEaseOutCurve.Ease(x),
                _ => EaseCurve.Ease(x),
            };
            return _from + (_to - _from) * (float)e;
        }

        public readonly bool Active(double t) => _dur > 0 && t - _t0 < _dur && _from != _to;

        public void Set(float to, double t, float dur, Curve curve)
        {
            if (to == _to) return;
            _from = At(t);
            _to = to;
            _t0 = t;
            _dur = dur;
            _curve = curve;
        }

        public void Snap(float v) { _from = _to = v; _dur = 0; }
    }

    /// <summary>Feder mit Dämpfung und Ansprechzeit (app.js Spring).</summary>
    private sealed class Spring(double damping, double response)
    {
        private readonly double _k = Math.Pow(2 * Math.PI / response, 2);
        private readonly double _c = 2 * damping * (2 * Math.PI / response);
        public double X, V, Target;

        public bool Moving => Math.Abs(X - Target) > 0.05 || Math.Abs(V) > 0.05;

        public double Step(double dt)
        {
            if (!Moving) { X = Target; V = 0; return X; }
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += (-_k * (X - Target) - _c * V) * h;
                X += V * h;
            }
            return X;
        }

        public void Snap(double x) { X = Target = x; V = 0; }
    }

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2).</summary>
    private readonly struct CubicBezier
    {
        private readonly double _ax, _bx, _cx, _ay, _by, _cy;

        public CubicBezier(double x1, double y1, double x2, double y2)
        {
            _cx = 3 * x1; _bx = 3 * (x2 - x1) - _cx; _ax = 1 - _cx - _bx;
            _cy = 3 * y1; _by = 3 * (y2 - y1) - _cy; _ay = 1 - _cy - _by;
        }

        private double X(double t) => ((_ax * t + _bx) * t + _cx) * t;
        private double Y(double t) => ((_ay * t + _by) * t + _cy) * t;
        private double DX(double t) => (3 * _ax * t + 2 * _bx) * t + _cx;

        public double Ease(double x)
        {
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double e = X(t) - x;
                if (Math.Abs(e) < 1e-6) return Y(t);
                double d = DX(t);
                if (Math.Abs(d) < 1e-6) break;
                t -= e / d;
                if (t < 0 || t > 1) break;
            }
            double lo = 0, hi = 1;
            t = x;
            for (int i = 0; i < 30; i++)
            {
                double v = X(t);
                if (Math.Abs(v - x) < 1e-6) break;
                if (v < x) lo = t; else hi = t;
                t = (lo + hi) / 2;
            }
            return Y(t);
        }
    }

    /// <summary>Eine Zeile auf dem Förderband.</summary>
    private sealed class Line : IDisposable
    {
        public string Text = "";
        public bool Dots;
        public IDWriteTextLayout? Layout;
        public float W, H, FontPx, LineH, Pad, TextLeft, TextWidth;
        public ID2D1Bitmap1? Bmp;
        public IntPtr BmpCtx;
        public Anim Y, S, Op, Blur, Glow;
        public bool Fresh;
        public double RemoveAt = double.MaxValue;
        public readonly float[] DotF = new float[3];
        public float DotScale = 1;

        public void Dispose()
        {
            Bmp?.Dispose(); Bmp = null;
            Layout?.Dispose(); Layout = null;
        }
    }
}

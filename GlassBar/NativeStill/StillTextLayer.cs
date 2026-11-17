using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DTextAntialias = Vortice.Direct2D1.TextAntialiasMode;
using BlendEffect = Vortice.Direct2D1.Effects.Blend;

namespace GlassBar.NativeStill;

/// <summary>Was die Textebene pro Bild braucht.</summary>
public sealed class TextFrame
{
    public DateTime Now;
    public StillValues Settings = new();
    /// <summary>Akzentfarbe der Palette (0..1).</summary>
    public Vector3 Accent;
    /// <summary>Helles Theme: dunkle Schrift (fg 24 25 31) statt hell (fg 246 246 250).</summary>
    public bool LightTheme;
    /// <summary>Wetter: Temperaturtext wie "5°" und ein Glyph (Segoe Fluent Icons / MDL2), null = kein Wetter.</summary>
    public string? WeatherText;
    public string? WeatherGlyph;
    /// <summary>Optional: Ortsname (für weatherPos = "above").</summary>
    public string? WeatherPlace;
    /// <summary>0..1: Etwas anderes (Wecker, Training) beansprucht die Bühne – Uhr und Datum treten so weit zurück.</summary>
    public float StageBusy;
}

/// <summary>
/// Zeichnet Datum, Wetter und Uhr wie im Original (style.css .stage/.focus-block/.date-line/.wx-row/.clock, app.js
/// renderClock/roll) mit Direct2D/DirectWrite auf das fertige Bild.
/// <paramref name="field"/> ist das Lichtfeld in voller Größe (für „Mit dem Hintergrund verschmelzen“ = clockBlend,
/// Overlay-Mischung über den D2D-Blend-Effekt). Läuft im Render-Thread.
/// </summary>
/// <remarks>
/// Die Uhr entsteht in einer eigenen kleinen Ebene (nur Deckung, in Schriftfarbe). Daraus werden beide Zifferblätter
/// des Originals: das gemischte (Overlay mit dem Lichtfeld, über die Deckung der Ebene maskiert) und darüber das
/// schlichte mit Schatten und Deckkraft 1 − clockBlend. Die Ebene wird nur neu gezeichnet, wenn sich etwas ändert.
/// </remarks>
public sealed class StillTextLayer : IDisposable
{
    // ---------- Werte aus dem Original ----------

    /// <summary>Rollen einer Ziffer (app.js ROLL_*): Hub in em, Unschärfe in px, Zeiten in ms.</summary>
    private readonly record struct RollSpec(double Lift, double Blur, double Out, double In, double Gap, double Stagger);

    private static readonly RollSpec RollMinute = new(0.22, 10, 650, 1100, 140, 80);
    private static readonly RollSpec RollSecFull = new(0.18, 6, 360, 620, 60, 60);
    private static readonly RollSpec RollSecSmall = new(0.4, 3, 320, 520, 40, 50);
    private const double MaxBlur = 10;

    // Gehen: cubic-bezier(.55, 0, .7, .3), Ankommen: cubic-bezier(.16, 1, .3, 1)
    private static readonly CubicBezier EaseLeave = new(.55, 0, .7, .3);
    private static readonly CubicBezier EaseArrive = new(.16, 1, .3, 1);

    /// <summary>Schrift je Schlüssel (app.js FONTS): eigene Uhrstärke, Laufweite, Schrift für Uhr und Text.</summary>
    private readonly record struct FontInfo(int Weight, float Track, string DisplayKey, string TextKey);

    private static readonly Dictionary<string, FontInfo> Fonts = new()
    {
        ["dmsans"] = new(200, -0.05f, "dmsans", "dmsans"),
        ["manrope"] = new(200, -0.05f, "manrope", "manrope"),
        ["poppins"] = new(200, -0.04f, "poppins", "poppins"),
        ["inter"] = new(200, -0.05f, "inter", "inter"),
        ["geist"] = new(200, -0.05f, "geist", "geist"),
        ["outfit"] = new(200, -0.04f, "outfit", "outfit"),
        ["serif"] = new(400, -0.02f, "serif", "inter"),
        ["system"] = new(300, -0.035f, FontStore.SystemDisplay, FontStore.SystemText),
    };

    private static readonly Dictionary<string, int> ClockWeights = new()
    {
        ["thin"] = 200, ["light"] = 300, ["regular"] = 400, ["medium"] = 500, ["bold"] = 650,
    };

    private const float ColonAir = 0.055f;      // .colon --colon-air
    private const float ClockLine = 1.04f;      // .clock line-height
    private const int ScratchCount = 16;        // gleichzeitig unscharfe Ziffern (4 + 2 Stellen, je alt und neu, mit Reserve)
    private static readonly Color4 Transparent = new(0, 0, 0, 0);

    // ---------- Fabriken und Schriften ----------

    private readonly IDWriteFactory _dw;
    private readonly FontStore _fonts;
    private readonly IDWriteTypography _tnum;
    private readonly string _iconFamily;
    private int _fontVersion = -1;

    // ---------- Geräteabhängiges (an den Gerätekontext gebunden) ----------

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private Shadow? _shadow;
    private ID2D1Image? _shadowOut;
    private Flood? _flood;
    private AffineTransform2D? _shift;
    private BlendEffect? _blend;
    private AlphaMask? _mask;
    private ID2D1Image? _maskOut;
    private nint _fieldPtr;
    private ID2D1Bitmap1? _layer, _solid;
    private int _layerW, _layerH;
    private readonly ID2D1Bitmap1?[] _scratch = new ID2D1Bitmap1?[ScratchCount];
    private readonly GaussianBlur?[] _blurFx = new GaussianBlur?[ScratchCount];
    private readonly ID2D1Image?[] _blurOut = new ID2D1Image?[ScratchCount];
    private int _scratchW, _scratchH, _scratchPad;

    // ---------- Einstellungen (zuletzt gesehen) ----------

    private int _width, _height;
    private string _fontKey = "";
    private string _weightKey = "";
    private double _clockScale = -1, _uiScale = -1;
    private bool _clock24 = true;
    private string _seconds = "";
    private bool _layoutDirty = true;

    // ---------- Maße und Textbausteine ----------

    private float _k, _size;
    private FontInfo _info;
    private GlyphSet? _main, _small;
    private Face? _textFace;
    private IDWriteTextLayout? _am, _pm;
    private float _amW, _pmW, _sufSize, _sufAsc, _sufDesc, _sufBase;
    private IDWriteTextFormat? _dateFmt, _rowFmt, _dateIconFmt, _rowIconFmt;
    private readonly TextLine _dateLine = new(), _rowLine = new();

    // Positionen innerhalb des Zifferblatts (links = 0)
    private readonly float[] _hmX = new float[5];
    private readonly float[] _secX = new float[3];
    private float _suffixX, _faceW, _faceWMax;

    // ---------- Uhrzustand ----------

    private readonly Slot?[] _hmSlots = new Slot?[5];
    private readonly Slot?[] _secSlots = new Slot?[3];
    private readonly char[] _hmNow = new char[5];
    private readonly char[] _secNow = new char[3];
    private char _suffix = ' ';              // 'A', 'P' oder ' ' (24 h)
    private bool _built;
    private double _time;
    private bool _rolling, _clockDirty = true;
    private Vector3 _fg = new(-1);
    private bool _light;
    private float _fracX = -1, _fracY = -1;
    private int _originX, _originY;

    // Versatz der Uhr (clockX/clockY), weich nachgeführt wie im Original
    private readonly Spring _offX = new(1, 0.7), _offY = new(1, 0.7);
    private bool _offSet;

    // Zeilentext (nur bei Änderung neu aufgebaut)
    private DateTime _dateDay = DateTime.MinValue;
    private string _dateStr = "";

    public StillTextLayer(ID2D1Factory1 d2d, IDWriteFactory dwrite)
    {
        _dw = dwrite;
        _fonts = new FontStore(dwrite);
        _tnum = dwrite.CreateTypography();
        _tnum.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
        // Die Wettersymbole von GlassBar (WeatherService.Symbol) stammen aus „Segoe UI Symbol“
        _iconFamily = "Segoe UI Symbol";
    }

    /// <summary>Wo die Uhr / die Datumszeile im letzten Bild standen (Pixel).</summary>
    public Vortice.Mathematics.Rect ClockRect { get; private set; }
    public Vortice.Mathematics.Rect DateRect { get; private set; }

    /// <summary>Braucht gerade Animation (Ziffern rollen)? Dann wird öfter gezeichnet.</summary>
    public bool Animating => _rolling || _offX.Moving || _offY.Moving;

    /// <summary>
    /// Auf <paramref name="ctx"/> zeichnen (Ziel ist bereits gesetzt, BeginDraw ist schon aufgerufen).
    /// width/height in Pixeln, dt = Sekunden seit dem letzten Bild (für Animationen).
    /// </summary>
    public void Draw(ID2D1DeviceContext ctx, ID2D1Bitmap1 field, int width, int height, TextFrame f, double dt)
    {
        if (width <= 0 || height <= 0) return;
        dt = Math.Clamp(dt, 0, 1);
        _time += dt;
        var s = f.Settings;

        EnsureDevice(ctx);
        ReadSettings(s, width, height);
        if (_fonts.Version != _fontVersion) { _fontVersion = _fonts.Version; _layoutDirty = true; }
        if (_layoutDirty) RebuildLayout(ctx);

        bool showClock = s.Bool("showClock");
        bool showDate = s.Bool("showDate");
        double blend = Math.Clamp(s.Num("clockBlend"), 0, 1);
        bool hasWx = s.Bool("weather") && !string.IsNullOrEmpty(f.WeatherText);
        bool above = hasWx && s.Str("weatherPos") == "above";
        bool beside = hasWx && !above;

        var fg = f.LightTheme ? new Vector3(24 / 255f, 25 / 255f, 31 / 255f) : new Vector3(246 / 255f, 246 / 255f, 250 / 255f);
        if (fg != _fg || f.LightTheme != _light)
        {
            _fg = fg;
            _light = f.LightTheme;
            _flood!.Color = new Vector4(fg, 1);
            _clockDirty = true;
        }

        UpdateClock(f.Now);
        UpdateLines(f, showDate, beside, above);

        // Versatz: bis zu 30 % des Bildschirms in jede Richtung (updateParallax)
        _offX.Target = s.Num("clockX") * 0.3 * width;
        _offY.Target = s.Num("clockY") * 0.3 * height;
        if (!_offSet) { _offX.Snap(_offX.Target); _offY.Snap(_offY.Target); _offSet = true; }
        double ox = _offX.Step(dt), oy = _offY.Step(dt);

        // Spalte: [Wetterzeile] [Datumszeile] [Uhr], als Ganzes mittig, dann um (offX, −3vh + offY) verschoben
        float k = _k;
        float rowH = above ? _rowLine.Height : 0;
        float dateH = _dateLine.Runs.Count > 0 ? _dateLine.Height : 0;
        float clockH = showClock ? ClockLine * _size : 0;
        float total = (above ? rowH + 7 * k : 0) + dateH + 2 * k + clockH;
        double cx = width / 2.0 + ox;
        double top = height / 2.0 - total / 2.0 - 0.03 * height + oy;
        double rowTop = top;
        double dateTop = top + (above ? rowH + 7 * k : 0);
        double clockTop = dateTop + dateH + 2 * k;

        // Für die Oberfläche: wo Uhr und Datumszeile stehen
        float clockW = Math.Max(_faceW, _size * 2.2f);
        ClockRect = new Vortice.Mathematics.Rect((float)(cx - clockW / 2), (float)clockTop, clockW, showClock ? clockH : 0);
        float dateW = Math.Max(_dateLine.Width, 1);
        DateRect = new Vortice.Mathematics.Rect((float)(cx - dateW / 2), (float)(above ? rowTop : dateTop), dateW,
            (float)((above ? rowH + 7 * k : 0) + dateH));
        if (f.StageBusy >= 0.995f) { _rolling = false; return; }

        var prevTarget = ctx.Target;
        var prevTransform = ctx.Transform;
        var prevAa = ctx.TextAntialiasMode;
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.TextAntialiasMode = D2DTextAntialias.Grayscale;

            if (showClock) DrawClock(ctx, prevTarget, field, cx, clockTop, blend);
            else _rolling = false;

            if (above) DrawLine(ctx, _rowLine, cx, rowTop, 0.55f);
            if (dateH > 0) DrawLine(ctx, _dateLine, cx, dateTop, 0.62f);
        }
        finally
        {
            ctx.Target = prevTarget;
            ctx.Transform = prevTransform;
            ctx.TextAntialiasMode = prevAa;
            prevTarget?.Dispose();
        }
    }

    // ---------- Einstellungen und Aufbau ----------

    private void ReadSettings(StillValues s, int width, int height)
    {
        string font = s.Str("font");
        if (!Fonts.ContainsKey(font)) font = "manrope";
        string weight = s.Str("clockWeight");
        double scale = s.Num("clockScale"), ui = s.Num("uiScale");
        bool c24 = s.Bool("clock24");
        string secs = s.Str("seconds");
        if (secs != "small" && secs != "full") secs = "off";

        if (width != _width || height != _height || font != _fontKey || weight != _weightKey
            || scale != _clockScale || ui != _uiScale || secs != _seconds)
        {
            _layoutDirty = true;
        }
        if (c24 != _clock24 || secs != _seconds) _built = false;   // anderer Aufbau: neu, ohne Rollen
        _width = width; _height = height; _fontKey = font; _weightKey = weight;
        _clockScale = scale; _uiScale = ui; _clock24 = c24; _seconds = secs;
    }

    private void RebuildLayout(ID2D1DeviceContext ctx)
    {
        _layoutDirty = false;
        _clockDirty = true;
        DisposeLayout();

        _info = Fonts[_fontKey];
        _k = (float)(Math.Min(_height / 1080.0, _width / 1200.0) * Math.Max(0.1, _uiScale));
        _size = (float)(188 * _k * Math.Max(0.05, _clockScale));

        int w = ClockWeights.TryGetValue(_weightKey, out var cw) ? cw : _info.Weight;
        var clockFace = _fonts.Resolve(_info.DisplayKey, w);
        _main = new GlyphSet(_dw, clockFace, _size, _info.Track, _tnum);
        if (_seconds == "small")
        {
            // .secs.small: .3em, Stärke + 100, ohne Laufweite (die Doppelpunkt-Ränder rechnen trotzdem mit --clock-track)
            var smallFace = _fonts.Resolve(_info.DisplayKey, w + 100);
            _small = new GlyphSet(_dw, smallFace, _size * 0.3f, 0, _tnum);
        }

        // AM/PM: Textschrift, 600, .13em, Laufweite .1em
        _sufSize = _size * 0.13f;
        var sufFace = _fonts.Resolve(_info.TextKey, 600);
        using (var sufFmt = MakeFormat(sufFace, _sufSize))
        {
            _am = MakeText("AM", sufFmt, 0.1f * _sufSize, false);
            _pm = MakeText("PM", sufFmt, 0.1f * _sufSize, false);
        }
        _amW = _am.Metrics.WidthIncludingTrailingWhitespace;
        _pmW = _pm.Metrics.WidthIncludingTrailingWhitespace;
        _sufAsc = sufFace.Asc * _sufSize;
        _sufDesc = sufFace.Desc * _sufSize;
        _sufBase = FirstBaseline(_am);

        // Datum und Wetter: Textschrift 500
        _textFace = _fonts.Resolve(_info.TextKey, 500);
        _dateFmt = MakeFormat(_textFace, 19 * _k);
        _rowFmt = MakeFormat(_textFace, 16.5f * _k);
        _dateIconFmt = MakeIconFormat(19 * _k * 1.12f);
        _rowIconFmt = MakeIconFormat(16.5f * _k * 1.12f);
        _dateLine.Invalidate();
        _rowLine.Invalidate();

        _faceWMax = ComputeFace(Math.Max(_amW, _pmW), !_clock24);
        EnsureScratch(ctx);
    }

    private IDWriteTextFormat MakeFormat(Face face, float size)
    {
        var fmt = _dw.CreateTextFormat(face.Family, face.Collection, face.Weight, FontStyle.Normal, FontStretch.Normal, size, "de-de");
        fmt.WordWrapping = WordWrapping.NoWrap;
        return fmt;
    }

    private IDWriteTextFormat MakeIconFormat(float size)
    {
        var fmt = _dw.CreateTextFormat(_iconFamily, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, size, "de-de");
        fmt.WordWrapping = WordWrapping.NoWrap;
        return fmt;
    }

    private IDWriteTextLayout MakeText(string text, IDWriteTextFormat fmt, float spacing, bool tabular)
    {
        var l = _dw.CreateTextLayout(text, fmt, 8192, 8192);
        var range = new TextRange(0, (uint)text.Length);
        if (spacing != 0)
        {
            using var l1 = l.QueryInterfaceOrNull<IDWriteTextLayout1>();
            l1?.SetCharacterSpacing(0, spacing, 0, range);
        }
        if (tabular) l.SetTypography(_tnum, range);
        return l;
    }

    private static float FirstBaseline(IDWriteTextLayout l)
    {
        var lines = l.LineMetrics;
        return lines.Length > 0 ? lines[0].Baseline : 0;
    }

    /// <summary>
    /// Waagrechte Lage der Teile im Zifferblatt (Flex-Reihe von .face): Ziffern in festen Fächern, Doppelpunkt mit
    /// den Rändern aus .colon, Sekunden, Suffix. Gibt die Gesamtbreite zurück.
    /// </summary>
    private float ComputeFace(float suffixW, bool hasSuffix)
    {
        var m = _main!;
        float x = 0;
        for (int i = 0; i < 5; i++)
        {
            if (i == 2)
            {
                x += (ColonAir - _info.Track) * m.Size;
                _hmX[i] = x;
                x += m.ColonAdv + ColonAir * m.Size;
            }
            else
            {
                _hmX[i] = x;
                x += m.SlotW;
            }
        }
        if (_seconds != "off")
        {
            var g = _small ?? m;
            if (_small != null) x += 0.1f * g.Size;
            x += (ColonAir - _info.Track) * g.Size;
            _secX[0] = x;
            x += g.ColonAdv + ColonAir * g.Size;
            _secX[1] = x; x += g.SlotW;
            _secX[2] = x; x += g.SlotW;
        }
        if (hasSuffix)
        {
            x += 0.3f * _sufSize;
            _suffixX = x;
            x += suffixW;
        }
        return x;
    }

    // ---------- Uhr: Zustand ----------

    private void UpdateClock(DateTime now)
    {
        int h = now.Hour;
        char suffix = ' ';
        if (!_clock24)
        {
            suffix = h < 12 ? 'A' : 'P';
            h %= 12;
            if (h == 0) h = 12;
        }
        int m = now.Minute, sec = now.Second;
        _hmNow[0] = (char)('0' + h / 10); _hmNow[1] = (char)('0' + h % 10); _hmNow[2] = ':';
        _hmNow[3] = (char)('0' + m / 10); _hmNow[4] = (char)('0' + m % 10);
        _secNow[0] = ':'; _secNow[1] = (char)('0' + sec / 10); _secNow[2] = (char)('0' + sec % 10);

        if (!_built)
        {
            // Neu aufbauen (renderClock(true)): alles steht sofort
            Build(_hmSlots, _hmNow);
            Build(_secSlots, _secNow);
            _built = true;
            _clockDirty = true;
        }
        else
        {
            if (Changed(_hmSlots, _hmNow)) Roll(_hmSlots, _hmNow, RollMinute);
            if (_seconds != "off" && Changed(_secSlots, _secNow))
                Roll(_secSlots, _secNow, _seconds == "full" ? RollSecFull : RollSecSmall);
        }
        if (suffix != _suffix) { _suffix = suffix; _clockDirty = true; }

        // Fertige Ziffern aufräumen, laufende merken
        bool rolling = Settle(_hmSlots);
        if (_seconds != "off") rolling |= Settle(_secSlots);
        if (rolling || _rolling) _clockDirty = true;   // auch das letzte Bild einer Rolle zeichnen
        _rolling = rolling;
    }

    private static void Build(Slot?[] slots, char[] str)
    {
        for (int i = 0; i < str.Length; i++)
        {
            if (str[i] == ':') { slots[i] = null; continue; }
            var slot = slots[i] ??= new Slot();
            slot.Live.Clear();
            slot.Live.Add(new Glyph { Ch = str[i] });
            slot.Ch = str[i];
        }
    }

    private static bool Changed(Slot?[] slots, char[] str)
    {
        for (int i = 0; i < str.Length; i++)
            if (slots[i] is { } s && s.Ch != str[i]) return true;
        return false;
    }

    /// <summary>
    /// Geänderte Ziffern gehen nach oben durch eine Unschärfe, die neuen kommen von unten, die rechte zuerst
    /// (app.js roll). Zeiten hier in Sekunden auf der eigenen Uhr <see cref="_time"/>.
    /// </summary>
    private void Roll(Slot?[] slots, char[] str, in RollSpec o)
    {
        int order = 0;
        for (int i = str.Length - 1; i >= 0; i--)
        {
            var slot = slots[i];
            char ch = str[i];
            if (slot == null || slot.Ch == ch) continue;
            double delay = order++ * o.Stagger / 1000.0;
            if (slot.Live.Count > 0)
            {
                var old = slot.Live[^1];
                old.Leaving = true;
                old.Moving = true;
                old.Start = _time + delay;
                old.Spec = o;
            }
            slot.Live.Add(new Glyph { Ch = ch, Moving = true, Start = _time + delay + o.Gap / 1000.0, Spec = o });
            slot.Ch = ch;
            // Nur die neuesten zwei behalten
            while (slot.Live.Count > 2) slot.Live.RemoveAt(0);
        }
    }

    private bool Settle(Slot?[] slots)
    {
        bool any = false;
        foreach (var slot in slots)
        {
            if (slot == null) continue;
            for (int j = slot.Live.Count - 1; j >= 0; j--)
            {
                var g = slot.Live[j];
                if (!g.Moving) continue;
                if (g.Leaving)
                {
                    if (_time - g.Start >= g.Spec.Out / 1000.0) { slot.Live.RemoveAt(j); continue; }
                }
                else if (_time - g.Start >= g.Spec.In / 1000.0)
                {
                    g.Moving = false;
                    continue;
                }
                any = true;
            }
        }
        return any;
    }

    /// <summary>Zustand einer Ziffer jetzt: Versatz in em (positiv = nach unten), Deckkraft, Unschärfe in px.</summary>
    private void GlyphState(Glyph g, out float dy, out float alpha, out float blur)
    {
        dy = 0; alpha = 1; blur = 0;
        if (!g.Moving) return;
        var o = g.Spec;
        if (g.Leaving)
        {
            double p = (_time - g.Start) / (o.Out / 1000.0);
            if (p <= 0) return;
            double e = EaseLeave.Ease(Math.Min(1, p));
            dy = (float)(-o.Lift * e);
            alpha = (float)(1 - e);
            blur = (float)(o.Blur * e);
        }
        else
        {
            // fill: backwards – vor dem Start im Anfangszustand
            double p = (_time - g.Start) / (o.In / 1000.0);
            double e = p <= 0 ? 0 : EaseArrive.Ease(Math.Min(1, p));
            dy = (float)(o.Lift * (1 - e));
            alpha = (float)e;
            blur = (float)(o.Blur * (1 - e));
        }
    }

    // ---------- Uhr: Zeichnen ----------

    private void DrawClock(ID2D1DeviceContext ctx, ID2D1Image? target, ID2D1Bitmap1 field, double cx, double clockTop, double blend)
    {
        bool hasSuffix = _suffix != ' ';
        _faceW = ComputeFace(_suffix == 'P' ? _pmW : _amW, hasSuffix);
        double faceLeft = cx - _faceW / 2.0;

        // Rand der Ebene: Schatten (3σ + Versatz) und Hub/Unschärfe der rollenden Ziffern
        float shadowPad = 3 * 20 * _k + 2 * _k;
        int padX = (int)Math.Ceiling(Math.Max(shadowPad, 3 * MaxBlur) + 2);
        int padY = (int)Math.Ceiling(Math.Max(shadowPad, 0.22 * _size + 3 * MaxBlur) + 2);
        int needW = (int)Math.Ceiling(_faceWMax) + 2 * padX + 2;
        int needH = (int)Math.Ceiling(ClockLine * _size) + 2 * padY + 2;
        if (_layer == null || needW != _layerW || needH != _layerH) CreateLayer(ctx, needW, needH);

        int originX = (int)Math.Floor(faceLeft) - padX;
        int originY = (int)Math.Floor(clockTop) - padY;
        float fracX = (float)(faceLeft - Math.Floor(faceLeft));
        float fracY = (float)(clockTop - Math.Floor(clockTop));
        if (Math.Abs(fracX - _fracX) > 0.01f || Math.Abs(fracY - _fracY) > 0.01f) { _fracX = fracX; _fracY = fracY; _clockDirty = true; }
        if (originX != _originX || originY != _originY)
        {
            _originX = originX; _originY = originY;
            _shift!.TransformMatrix = Matrix3x2.CreateTranslation(-originX, -originY);
        }
        if (field.NativePointer != _fieldPtr)
        {
            _fieldPtr = field.NativePointer;
            _shift!.SetInput(0, field, true);
        }

        if (_clockDirty)
        {
            _clockDirty = false;
            RenderLayer(ctx, padX + fracX, padY + fracY);

            // Schlichtes Zifferblatt: Schatten (0 2k 40k schwarz .2, nicht im hellen Theme) und Schrift mit .94
            ctx.Target = _solid;
            ctx.Clear(Transparent);
            if (!_light)
            {
                _shadow!.BlurStandardDeviation = 20 * _k;
                ctx.DrawImage(_shadowOut!, new Vector2(0, 2 * _k), null, InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            ctx.DrawBitmap(_layer!, null, 0.94f, InterpolationMode.NearestNeighbor, null, null);
        }

        ctx.Target = target;
        var dest = new RawRectF(originX, originY, originX + _layerW, originY + _layerH);
        // Gemischtes Zifferblatt: Overlay von voller Schriftfarbe mit dem Licht, in der Form der Ziffern
        if (blend > 0)
            ctx.DrawImage(_maskOut!, new Vector2(originX, originY), new RawRectF(0, 0, _layerW, _layerH),
                InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        // Darüber das schlichte, das mit steigendem clockBlend verblasst
        if (blend < 1)
            ctx.DrawBitmap(_solid!, dest, (float)(1 - blend), InterpolationMode.NearestNeighbor, null, null);
    }

    /// <summary>Ziffern, Doppelpunkte und Suffix in die Ebene (Schriftfarbe, Deckung = Form × Deckkraft).</summary>
    private void RenderLayer(ID2D1DeviceContext ctx, float left, float top)
    {
        var m = _main!;
        ctx.Target = _layer;
        ctx.Clear(Transparent);
        int scratch = 0;

        // Grundlinie: halber Durchschuss über der Schrift (line-height 1.04), alle Teile auf derselben Grundlinie
        float baseline = top + (ClockLine * m.Size - (m.Asc + m.Desc)) / 2 + m.Asc;

        for (int i = 0; i < 5; i++)
        {
            if (i == 2) { DrawStatic(ctx, m, m.Colon, left + _hmX[i], baseline - 0.07f * m.Size, 0.4f); continue; }
            DrawSlot(ctx, m, _hmSlots[i], left + _hmX[i], baseline, 1f, ref scratch);
        }

        if (_seconds != "off")
        {
            var g = _small ?? m;
            float group = _small != null ? 0.55f : 1f;
            float colon = _small != null ? 0.6f : 0.4f;
            DrawStatic(ctx, g, g.Colon, left + _secX[0], baseline - 0.07f * g.Size, group * colon);
            DrawSlot(ctx, g, _secSlots[1], left + _secX[1], baseline, group, ref scratch);
            DrawSlot(ctx, g, _secSlots[2], left + _secX[2], baseline, group, ref scratch);
        }

        if (_suffix != ' ')
        {
            // align-self: flex-start, margin-top .62em, eigene Zeilenhöhe 1.04
            float sufTop = top + 0.62f * _sufSize;
            float sufBase = sufTop + (ClockLine * _sufSize - (_sufAsc + _sufDesc)) / 2 + _sufAsc;
            SetBrush(0.5f);
            ctx.DrawTextLayout(new Vector2(left + _suffixX, sufBase - _sufBase), _suffix == 'P' ? _pm! : _am!, _brush!,
                DrawTextOptions.NoSnap);
        }
    }

    private void DrawStatic(ID2D1DeviceContext ctx, GlyphSet g, IDWriteTextLayout layout, float x, float baseline, float alpha)
    {
        SetBrush(alpha);
        ctx.DrawTextLayout(new Vector2(x, baseline - g.Baseline), layout, _brush!, DrawTextOptions.NoSnap);
    }

    private void DrawSlot(ID2D1DeviceContext ctx, GlyphSet g, Slot? slot, float slotX, float baseline, float group, ref int scratch)
    {
        if (slot == null) return;
        foreach (var glyph in slot.Live)
        {
            GlyphState(glyph, out float dy, out float alpha, out float blur);
            alpha *= group;
            if (alpha < 0.002f) continue;
            int d = glyph.Ch - '0';
            if ((uint)d > 9) continue;
            var layout = g.Digits[d];
            // Ziffer mittig im Fach (.slot .d: inset 0, text-align center, Laufweite hinter jeder Ziffer)
            float x = slotX + (g.SlotW - (g.Adv[d] + g.Track)) / 2;
            float y = baseline + dy * g.Size - g.Baseline;
            SetBrush(alpha);

            if (blur < 0.15f || scratch >= ScratchCount)
            {
                ctx.DrawTextLayout(new Vector2(x, y), layout, _brush!, DrawTextOptions.NoSnap);
                continue;
            }

            // Unscharf: Ziffer allein in eine kleine Fläche, durch den Weichzeichner in die Ebene
            int i = scratch++;
            var bmp = GetScratch(ctx, i);
            ctx.Target = bmp;
            ctx.Clear(Transparent);
            ctx.DrawTextLayout(new Vector2(_scratchPad, _scratchPad), layout, _brush!, DrawTextOptions.NoSnap);
            ctx.Target = _layer;
            var fx = _blurFx[i]!;
            fx.SetInput(0, bmp, true);
            fx.StandardDeviation = blur;
            ctx.DrawImage(_blurOut[i]!, new Vector2(x - _scratchPad, y - _scratchPad), null, InterpolationMode.Linear,
                CompositeMode.SourceOver);
        }
    }

    private void SetBrush(float alpha) => _brush!.Color = new Color4(_fg.X, _fg.Y, _fg.Z, alpha);

    // ---------- Datum und Wetter ----------

    private void UpdateLines(TextFrame f, bool showDate, bool beside, bool above)
    {
        if (f.Now.Date != _dateDay)
        {
            _dateDay = f.Now.Date;
            _dateStr = f.Now.ToString(L.En ? "dddd, MMMM d" : "dddd, d. MMMM", L.Culture);   // toLocaleDateString(weekday long, day, month long)
            _dateLine.Invalidate();
        }

        string? glyph = string.IsNullOrEmpty(f.WeatherGlyph) ? null : f.WeatherGlyph;
        float k = _k;

        // Datumszeile: 19k, 500, Laufweite .01em; daneben das Wetter („Datum · ☁ 5°“)
        if (_dateLine.NeedsBuild(showDate, beside, false, beside ? f.WeatherText : null, beside ? glyph : null, null, _dateStr))
        {
            float fs = 19 * k, sp = 0.01f * fs;
            var line = _dateLine;
            line.Clear();
            line.FontSize = fs;
            float x = 0;
            if (showDate) x = line.AddText(MakeText(_dateStr, _dateFmt!, sp, false), x);
            if (beside)
            {
                if (showDate) { x += 0.55f * fs; x = line.AddText(MakeText("·", _dateFmt!, sp, false), x); x += 0.55f * fs; }
                if (glyph != null) { x = line.AddIcon(MakeText(glyph, _dateIconFmt!, 0, false), x, 1.25f * fs); x += 0.32f * fs; }
                x = line.AddText(MakeText(f.WeatherText!, _dateFmt!, sp, true), x);
            }
            line.Width = x;
            line.Height = (beside && glyph != null ? 1.25f : 1.2f) * fs;
            line.Asc = _textFace!.Asc * fs;
            line.Desc = _textFace.Desc * fs;
        }

        // Wetterzeile darüber: 16.5k, mit Ort nach „·“
        if (above && _rowLine.NeedsBuild(false, false, true, f.WeatherText, glyph, f.WeatherPlace, null))
        {
            float fs = 16.5f * k, sp = 0.01f * fs;
            var line = _rowLine;
            line.Clear();
            line.FontSize = fs;
            float x = 0;
            if (glyph != null) { x = line.AddIcon(MakeText(glyph, _rowIconFmt!, 0, false), x, 1.25f * fs); x += 0.32f * fs; }
            x = line.AddText(MakeText(f.WeatherText!, _rowFmt!, sp, true), x);
            if (!string.IsNullOrEmpty(f.WeatherPlace))
            {
                x += 0.32f * fs + 0.5f * fs;
                x = line.AddText(MakeText("·", _rowFmt!, sp, false), x);
                x += 0.5f * fs;
                x = line.AddText(MakeText(f.WeatherPlace, _rowFmt!, sp, false), x);
            }
            line.Width = x;
            line.Height = 1.25f * fs;
            line.Asc = _textFace!.Asc * fs;
            line.Desc = _textFace.Desc * fs;
        }
    }

    private void DrawLine(ID2D1DeviceContext ctx, TextLine line, double cx, double top, float alpha)
    {
        if (line.Runs.Count == 0) return;
        SetBrush(alpha);
        float left = (float)(cx - line.Width / 2);
        float fs = line.FontSize;
        // Textteile: Zeilenhöhe 1.2, in der Zeile senkrecht mittig (align-items: center)
        float boxTop = (float)top + (line.Height - 1.2f * fs) / 2;
        float baseline = boxTop + (1.2f * fs - (line.Asc + line.Desc)) / 2 + line.Asc;
        float mid = (float)top + line.Height / 2;
        foreach (var r in line.Runs)
        {
            if (r.Icon)
            {
                // Glyph mittig im 1.25em-Kasten (nach seiner Tinte)
                float x = left + r.X + r.W / 2 - (r.InkX + r.InkW / 2);
                float y = mid - (r.InkY + r.InkH / 2);
                ctx.DrawTextLayout(new Vector2(x, y), r.Layout, _brush!, DrawTextOptions.None);
            }
            else
            {
                ctx.DrawTextLayout(new Vector2(left + r.X, baseline - r.Baseline), r.Layout, _brush!, DrawTextOptions.None);
            }
        }
    }

    // ---------- Geräteressourcen ----------

    private void EnsureDevice(ID2D1DeviceContext ctx)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

        _shadow = new Shadow(ctx) { Color = new Vector4(0, 0, 0, 0.2f), BlurStandardDeviation = 20 };
        _shadowOut = _shadow.Output;

        // Overlay: Ziel = Lichtfeld (in Ebenen-Koordinaten verschoben), Quelle = volle Schriftfarbe;
        // danach mit der Deckung der Ebene maskiert
        _flood = new Flood(ctx) { Color = new Vector4(1, 1, 1, 1) };
        _shift = new AffineTransform2D(ctx);
        _blend = new BlendEffect(ctx) { Mode = BlendMode.Overlay };
        _blend.SetInputEffect(0, _shift, true);
        _blend.SetInputEffect(1, _flood, true);
        _mask = new AlphaMask(ctx);
        _mask.SetInputEffect(0, _blend, true);
        _maskOut = _mask.Output;

        _fg = new Vector3(-1);
        _fieldPtr = 0;
        _originX = int.MinValue;
        _layoutDirty = true;
    }

    private ID2D1Bitmap1 CreateTargetBitmap(ID2D1DeviceContext ctx, int w, int h) =>
        ctx.CreateBitmap(new SizeI(Math.Max(1, w), Math.Max(1, h)), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target));

    private void CreateLayer(ID2D1DeviceContext ctx, int w, int h)
    {
        _layer?.Dispose();
        _solid?.Dispose();
        _layer = CreateTargetBitmap(ctx, w, h);
        _solid = CreateTargetBitmap(ctx, w, h);
        _layerW = w;
        _layerH = h;
        _shadow!.SetInput(0, _layer, true);
        _mask!.SetInput(1, _layer, true);
        _clockDirty = true;
    }

    private void EnsureScratch(ID2D1DeviceContext ctx)
    {
        var m = _main!;
        int pad = (int)Math.Ceiling(3 * MaxBlur) + 2;
        float maxAdv = 0;
        foreach (var a in m.Adv) maxAdv = Math.Max(maxAdv, a);
        int w = (int)Math.Ceiling(maxAdv + Math.Abs(m.Track) + m.Size * 0.1f) + 2 * pad;
        int h = (int)Math.Ceiling(m.LayoutHeight) + 2 * pad;
        if (w == _scratchW && h == _scratchH) return;
        for (int i = 0; i < ScratchCount; i++) { _scratch[i]?.Dispose(); _scratch[i] = null; }
        _scratchW = w;
        _scratchH = h;
        _scratchPad = pad;
    }

    private ID2D1Bitmap1 GetScratch(ID2D1DeviceContext ctx, int i)
    {
        if (_blurFx[i] == null)
        {
            _blurFx[i] = new GaussianBlur(ctx) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
            _blurOut[i] = _blurFx[i]!.Output;
        }
        return _scratch[i] ??= CreateTargetBitmap(ctx, _scratchW, _scratchH);
    }

    // ---------- Aufräumen ----------

    private void DisposeLayout()
    {
        _main?.Dispose(); _main = null;
        _small?.Dispose(); _small = null;
        _am?.Dispose(); _am = null;
        _pm?.Dispose(); _pm = null;
        _dateFmt?.Dispose(); _dateFmt = null;
        _rowFmt?.Dispose(); _rowFmt = null;
        _dateIconFmt?.Dispose(); _dateIconFmt = null;
        _rowIconFmt?.Dispose(); _rowIconFmt = null;
        _dateLine.Clear();
        _rowLine.Clear();
    }

    private void DisposeDevice()
    {
        for (int i = 0; i < ScratchCount; i++)
        {
            _scratch[i]?.Dispose(); _scratch[i] = null;
            _blurOut[i]?.Dispose(); _blurOut[i] = null;
            _blurFx[i]?.Dispose(); _blurFx[i] = null;
        }
        _scratchW = _scratchH = 0;
        _layer?.Dispose(); _layer = null;
        _solid?.Dispose(); _solid = null;
        _layerW = _layerH = 0;
        _shadowOut?.Dispose(); _shadowOut = null;
        _shadow?.Dispose(); _shadow = null;
        _maskOut?.Dispose(); _maskOut = null;
        _mask?.Dispose(); _mask = null;
        _blend?.Dispose(); _blend = null;
        _shift?.Dispose(); _shift = null;
        _flood?.Dispose(); _flood = null;
        _brush?.Dispose(); _brush = null;
        _ctxPtr = 0;
    }

    public void Dispose()
    {
        DisposeLayout();
        DisposeDevice();
        _tnum.Dispose();
        _fonts.Dispose();
    }

    // ---------- Hilfstypen ----------

    private sealed class Glyph
    {
        public char Ch;
        public bool Leaving;
        public bool Moving;
        public double Start;
        public RollSpec Spec;
    }

    /// <summary>Ein Ziffernfach: die aktuelle Ziffer und, während des Rollens, die gehende.</summary>
    private sealed class Slot
    {
        public char Ch;
        public readonly List<Glyph> Live = new(4);
    }

    /// <summary>Ziffern 0–9 und Doppelpunkt einer Größe/Stärke, fertig gesetzt und vermessen (Tabellenziffern).</summary>
    private sealed class GlyphSet : IDisposable
    {
        public readonly IDWriteTextFormat Format;
        public readonly IDWriteTextLayout[] Digits = new IDWriteTextLayout[10];
        public readonly float[] Adv = new float[10];
        public readonly IDWriteTextLayout Colon;
        public readonly float ColonAdv, Size, Track, SlotW, Asc, Desc, Baseline, LayoutHeight;

        public GlyphSet(IDWriteFactory dw, Face face, float size, float trackEm, IDWriteTypography tnum)
        {
            Size = size;
            Track = trackEm * size;
            Format = dw.CreateTextFormat(face.Family, face.Collection, face.Weight, FontStyle.Normal, FontStretch.Normal, size, "de-de");
            Format.WordWrapping = WordWrapping.NoWrap;
            for (int d = 0; d < 10; d++)
            {
                var l = dw.CreateTextLayout(((char)('0' + d)).ToString(), Format, 8192, 8192);
                l.SetTypography(tnum, new TextRange(0, 1));
                Digits[d] = l;
                Adv[d] = l.Metrics.WidthIncludingTrailingWhitespace;
            }
            Colon = dw.CreateTextLayout(":", Format, 8192, 8192);
            ColonAdv = Colon.Metrics.WidthIncludingTrailingWhitespace;
            // Fach = Breite der „0“ plus Laufweite (die versteckte .ghost hält die Breite)
            SlotW = Adv[0] + Track;
            Asc = face.Asc * size;
            Desc = face.Desc * size;
            Baseline = FirstBaseline(Digits[0]);
            LayoutHeight = Digits[0].Metrics.Height;
        }

        public void Dispose()
        {
            foreach (var d in Digits) d?.Dispose();
            Colon.Dispose();
            Format.Dispose();
        }
    }

    private struct Run
    {
        public IDWriteTextLayout Layout;
        public float X, W, Baseline;
        public bool Icon;
        public float InkX, InkY, InkW, InkH;
    }

    /// <summary>Eine Textzeile aus Teilen (Text oder Glyph), mit Breite und Höhe; baut nur bei Änderung neu.</summary>
    private sealed class TextLine
    {
        public readonly List<Run> Runs = new();
        public float Width, Height, FontSize, Asc, Desc;
        private bool _valid, _a, _b, _c;
        private string? _s1, _s2, _s3, _s4;

        public void Invalidate() => _valid = false;

        public bool NeedsBuild(bool a, bool b, bool c, string? s1, string? s2, string? s3, string? s4)
        {
            if (_valid && a == _a && b == _b && c == _c && s1 == _s1 && s2 == _s2 && s3 == _s3 && s4 == _s4) return false;
            _valid = true; _a = a; _b = b; _c = c; _s1 = s1; _s2 = s2; _s3 = s3; _s4 = s4;
            return true;
        }

        public float AddText(IDWriteTextLayout l, float x)
        {
            float w = l.Metrics.WidthIncludingTrailingWhitespace;
            Runs.Add(new Run { Layout = l, X = x, W = w, Baseline = FirstBaseline(l) });
            return x + w;
        }

        public float AddIcon(IDWriteTextLayout l, float x, float box)
        {
            // Tinte des Glyphs aus den Überhängen (Layoutkasten 8192 × 8192)
            var o = l.OverhangMetrics;
            float inkX = -o.Left, inkY = -o.Top, inkR = 8192 + o.Right, inkB = 8192 + o.Bottom;
            if (inkR <= inkX || inkB <= inkY)
            {
                var m = l.Metrics;
                inkX = 0; inkY = 0; inkR = m.WidthIncludingTrailingWhitespace; inkB = m.Height;
            }
            Runs.Add(new Run { Layout = l, X = x, W = box, Icon = true, InkX = inkX, InkY = inkY, InkW = inkR - inkX, InkH = inkB - inkY });
            return x + box;
        }

        public void Clear()
        {
            foreach (var r in Runs) r.Layout.Dispose();
            Runs.Clear();
            Width = 0;
        }
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

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2): zu x (Zeit) das t suchen, y zurückgeben.</summary>
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
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            // Newton, bei Bedarf Halbierung
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double e = X(t) - x;
                if (Math.Abs(e) < 1e-7) return Y(t);
                double d = DX(t);
                if (Math.Abs(d) < 1e-6) break;
                t -= e / d;
                if (t < 0 || t > 1) break;
            }
            double lo = 0, hi = 1;
            t = x;
            for (int i = 0; i < 40; i++)
            {
                double v = X(t);
                if (Math.Abs(v - x) < 1e-7) break;
                if (v < x) lo = t; else hi = t;
                t = (lo + hi) / 2;
            }
            return Y(t);
        }
    }

    /// <summary>Eine geladene Schrift: Familienname, Sammlung (null = System), Stärke, Ober-/Unterlänge je em.</summary>
    private sealed record Face(string Family, IDWriteFontCollection? Collection, FontWeight Weight, float Asc, float Desc);

    /// <summary>
    /// Die Schriften aus %AppData%\GlassBar\Fonts, je Stärke eine Datei ({key}-{weight}.ttf, die Vorschau legt
    /// {key}.ttf mit ihrer Grundstärke ab). Fehlt eine Stärke, wird sie im Hintergrund bei Google Fonts geholt
    /// (ohne Browser-Kennung kommen statische TTF in Hunderterstufen); bis dahin gilt die nächste vorhandene
    /// Stärke oder Segoe UI Variable Display. Nie blockierend: fertige Downloads erhöhen nur <see cref="Version"/>.
    /// </summary>
    private sealed class FontStore : IDisposable
    {
        public const string SystemDisplay = "#system-display";
        public const string SystemText = "#system-text";

        private readonly record struct Spec(string Query, int Min, int Max, int Base, bool Fixed);

        // Abfrage und Stärkebereich wie in app.js geladen (css der FONTS-Tabelle)
        private static readonly Dictionary<string, Spec> Specs = new()
        {
            ["dmsans"] = new("DM+Sans", 200, 700, 200, false),
            ["manrope"] = new("Manrope", 200, 700, 200, false),
            ["poppins"] = new("Poppins", 200, 600, 200, false),
            ["inter"] = new("Inter", 200, 700, 200, false),
            ["geist"] = new("Geist", 100, 800, 200, false),
            ["outfit"] = new("Outfit", 100, 700, 200, false),
            ["serif"] = new("Instrument+Serif", 400, 400, 400, true),
        };

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly string Folder = Path.Combine(Config.Folder, "Fonts");

        private readonly IDWriteFactory _dw;
        private readonly IDWriteFactory5? _dw5;
        private readonly IDWriteFontCollection _system;
        private readonly Dictionary<string, Face?> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDWriteFontCollection> _collections = new();
        private readonly ConcurrentDictionary<string, DateTime> _attempts = new();
        private int _version;

        public FontStore(IDWriteFactory dw)
        {
            _dw = dw;
            _dw5 = dw.QueryInterfaceOrNull<IDWriteFactory5>();
            _system = dw.GetSystemFontCollection(false);
        }

        public int Version => Volatile.Read(ref _version);

        public bool HasSystemFamily(string family) => _system.FindFamilyName(family, out _);

        /// <summary>Schrift für einen Schlüssel (auch "inter" oder die Systemschlüssel) in der gewünschten Stärke.</summary>
        public Face Resolve(string key, int weight)
        {
            if (key == SystemDisplay) return SystemFace("Segoe UI Variable Display", weight);
            if (key == SystemText) return SystemFace("Segoe UI Variable Text", weight);
            if (!Specs.TryGetValue(key, out var spec)) return SystemFace("Segoe UI Variable Display", weight);

            int w = StaticWeight(spec, weight);
            if (FileFor(key, spec, w) is { } path && Load(path) is { } face)
                return spec.Fixed ? face with { Weight = (FontWeight)weight } : face;   // feste Schnitte: fett simulieren wie der Browser

            StartDownload(key, spec, w);

            // Bis dahin: die nächste vorhandene Stärke derselben Schrift (höchstens 200 daneben)
            Face? best = null;
            int bestD = int.MaxValue;
            for (int c = 100; c <= 900; c += 100)
            {
                int d = Math.Abs(c - w);
                if (d == 0 || d > 200 || d >= bestD) continue;
                if (FileFor(key, spec, c) is { } p && Load(p) is { } f) { best = f; bestD = d; }
            }
            return best ?? SystemFace("Segoe UI Variable Display", weight);
        }

        /// <summary>Die Stärke, die es als Datei gibt: auf Hunderter gerundet (genau zwischen zwei: die leichtere), im Bereich.</summary>
        private static int StaticWeight(Spec spec, int weight)
        {
            if (spec.Fixed) return spec.Base;
            int w = (int)Math.Ceiling((weight - 50) / 100.0) * 100;
            return Math.Clamp(w, spec.Min, spec.Max);
        }

        private static string? FileFor(string key, Spec spec, int w)
        {
            var p = Path.Combine(Folder, $"{key}-{w}.ttf");
            if (File.Exists(p)) return p;
            if (w == spec.Base)
            {
                var legacy = Path.Combine(Folder, key + ".ttf");   // von der Vorschau in den Einstellungen
                if (File.Exists(legacy)) return legacy;
            }
            return null;
        }

        private Face SystemFace(string family, int weight)
        {
            if (!_system.FindFamilyName(family, out _)) family = "Segoe UI";
            var (asc, desc) = MetricsOf(_system, family, (FontWeight)weight);
            return new Face(family, null, (FontWeight)weight, asc, desc);
        }

        private static (float Asc, float Desc) MetricsOf(IDWriteFontCollection coll, string family, FontWeight weight)
        {
            try
            {
                if (!coll.FindFamilyName(family, out uint idx)) return (0.95f, 0.25f);
                using var fam = coll.GetFontFamily(idx);
                using var font = fam.GetFirstMatchingFont(weight, FontStretch.Normal, FontStyle.Normal);
                var m = font.Metrics;
                float em = Math.Max((ushort)1, m.DesignUnitsPerEm);
                return (m.Ascent / em, m.Descent / em);
            }
            catch { return (0.95f, 0.25f); }
        }

        /// <summary>Eine Datei als eigene kleine Sammlung laden (Schriftsatz aus genau dieser Datei).</summary>
        private Face? Load(string path)
        {
            if (_files.TryGetValue(path, out var cached)) return cached;
            Face? face = null;
            if (_dw5 != null)
            {
                IDWriteFontCollection1? coll = null;
                try
                {
                    using var file = _dw.CreateFontFileReference(path, null);
                    using var builder = _dw5.CreateFontSetBuilder();
                    builder.AddFontFile(file);
                    using var set = builder.CreateFontSet();
                    coll = _dw5.CreateFontCollectionFromFontSet(set);
                    if (coll.FontFamilyCount > 0)
                    {
                        using var fam = coll.GetFontFamily(0);
                        using var names = fam.FamilyNames;
                        string name = names.GetString(names.FindLocaleName("en-us", out uint li) ? li : 0);
                        using var font = fam.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
                        var m = font.Metrics;
                        float em = Math.Max((ushort)1, m.DesignUnitsPerEm);
                        face = new Face(name, coll, font.Weight, m.Ascent / em, m.Descent / em);
                        _collections.Add(coll);
                        coll = null;
                    }
                }
                catch { face = null; }
                finally { coll?.Dispose(); }
            }
            _files[path] = face;
            return face;
        }

        private void StartDownload(string key, Spec spec, int w)
        {
            string id = $"{key}-{w}";
            var now = DateTime.UtcNow;
            // Läuft schon oder ging eben schief: frühestens nach fünf Minuten wieder
            if (_attempts.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(5)) return;
            _attempts[id] = now;
            string query = spec.Fixed ? spec.Query : $"{spec.Query}:wght@{w}";
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    // Ohne Browser-Kennung liefert Google Fonts TTF-Dateien
                    var css = await Http.GetStringAsync($"https://fonts.googleapis.com/css2?family={query}").ConfigureAwait(false);
                    var m = Regex.Match(css, @"url\((https://[^)]+\.ttf)\)");
                    if (!m.Success) return;
                    var bytes = await Http.GetByteArrayAsync(m.Groups[1].Value).ConfigureAwait(false);
                    var file = Path.Combine(Folder, id + ".ttf");
                    var part = file + ".part";
                    await File.WriteAllBytesAsync(part, bytes).ConfigureAwait(false);
                    File.Move(part, file, true);
                    Interlocked.Increment(ref _version);
                }
                catch (Exception ex) { Log.Error("Schrift laden", ex); }
            });
        }

        public void Dispose()
        {
            foreach (var c in _collections) c.Dispose();
            _collections.Clear();
            _files.Clear();
            _system.Dispose();
            _dw5?.Dispose();
        }
    }
}

using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui;

// =====================================================================================================
// Gemeinsame Schnittstellen der Oberfläche des nativen „Still“ (Pillen, Karten, Überlagerungen).
// Die Typen in dieser Datei sind FEST – alle Teile werden dagegen geschrieben. Nur die als GERÜST
// markierten Klassen (StillUi, GlassPainter, UiFonts) werden noch ausgefüllt.
// Alle Koordinaten in Pixeln des Hintergrundfensters (Hauptbildschirm, Ursprung oben links).
// =====================================================================================================

/// <summary>Zustand der Musik (vom UI-Thread gefüllt, im Render-Thread gelesen; als Ganzes ersetzt, nie verändert).</summary>
public sealed record MediaState
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string App { get; init; } = "";
    /// <summary>Cover als BGRA32 (vormultipliziert) oder null.</summary>
    public byte[]? CoverBgra { get; init; }
    public int CoverWidth { get; init; }
    public int CoverHeight { get; init; }
    /// <summary>Ändert sich bei jedem neuen Cover (zum Neu-Anlegen der Bitmap).</summary>
    public int CoverVersion { get; init; }
    public bool Playing { get; init; }
    public bool Paused { get; init; }
    /// <summary>Der Player erlaubt das Springen im Lied.</summary>
    public bool CanSeek { get; init; }
    /// <summary>Ändert sich, sobald die Wiedergabe-Seite einen Sprung übernommen oder verworfen hat.</summary>
    public int SeekGeneration { get; init; }
    /// <summary>Position in Sekunden zum Zeitpunkt <see cref="Stamp"/>; Dauer 0 = unbekannt.</summary>
    public double Position { get; init; }
    public double Duration { get; init; }
    public DateTime Stamp { get; init; } = DateTime.Now;
    public bool HasMedia => Title.Length > 0;
    /// <summary>Aktuelle Position (läuft beim Abspielen weiter).</summary>
    public double CurrentPosition =>
        Duration > 0 ? Math.Min(Duration, Position + (Playing ? (DateTime.Now - Stamp).TotalSeconds : 0)) : Position;
}

/// <summary>Steuerung der Wiedergabe (Aufrufe dürfen aus dem Render-Thread kommen).</summary>
public sealed class MediaControls
{
    public Action PlayPause { get; init; } = () => { };
    public Action Next { get; init; } = () => { };
    public Action Previous { get; init; } = () => { };
    /// <summary>Im Lied springen (Sekunden ab Liedanfang).</summary>
    public Action<double> Seek { get; init; } = _ => { };
    /// <summary>Lautstärke des abspielenden Programms ändern (+1/-1 Raste); der Rückruf bekommt die neue Lautstärke (0–1).</summary>
    public Action<int, Action<float>> Volume { get; init; } = (_, _) => { };
}

public enum PointerKind { Move, Down, Up, Wheel, Leave }

/// <summary>Maus: Pos in Pixeln, Button 0 = links, 1 = rechts; Wheel in Rasten (±120 = eine Raste).</summary>
public readonly record struct PointerEvent(PointerKind Kind, Vector2 Pos, int Button = 0, int Wheel = 0);

/// <summary>Alles, was Pillen und Überlagerungen pro Bild brauchen. Wird vom Renderer jedes Bild aktualisiert.</summary>
public sealed class UiContext
{
    public StillValues Settings = new();
    public StillPalette Palette = null!;
    /// <summary>Einheit wie im Original: min(H/1080, W/1200) × uiScale (1 px bei 1080p).</summary>
    public float K;
    /// <summary>--pk: wie K, aber ohne uiScale.</summary>
    public float PK;
    public int Width, Height;
    /// <summary>Sekunden seit Start (monoton).</summary>
    public double Time;
    public DateTime Now;
    /// <summary>Vordergrundfarbe 0..1 (hell auf dunklem Theme, dunkel auf hellem).</summary>
    public Vector3 Fg;
    public bool LightTheme;
    public Vector3 Accent;

    public MediaState Media = new();
    public MediaControls Controls = new();
    /// <summary>Vier Bänder 0..1 wie audio.bands im Original (tief → hoch), plus Pegel und Beat.</summary>
    public float[] Bands = new float[4];
    public float Energy, Kick;
    /// <summary>Rohspektrum (128 Werte, 64 je Kanal) oder null.</summary>
    public float[]? Spectrum;

    public ID2D1Factory1 D2D = null!;
    public IDWriteFactory DWrite = null!;
    /// <summary>Lichtfeld in voller Größe (für Glas: weichgezeichnet hinter Karten).</summary>
    public ID2D1Bitmap1 Field = null!;
    public UiFonts Fonts = null!;
    public GlassPainter Glass = null!;

    /// <summary>Wo die Uhr gerade steht (für Klick auf die Uhr, Wecker-Ziffernblatt, Liedtext-Platz).</summary>
    public Vortice.Mathematics.Rect ClockRect;
    /// <summary>Wo Datum/Wetter-Zeile steht.</summary>
    public Vortice.Mathematics.Rect DateRect;

    /// <summary>Mausposition (zuletzt gesehen) und ob sie über dem Hintergrund ist.</summary>
    public Vector2 Pointer;
    public bool PointerInside;

    /// <summary>Licht aufblühen lassen (wie flowBloom), z. B. bei einer erledigten Aufgabe: x, y, Stärke.</summary>
    public Action<float, float, float> Bloom = (_, _, _) => { };
    /// <summary>Anordnen-Modus aktiv?</summary>
    public bool Editing;
    /// <summary>Etwas (Wecker, Training …) fordert gerade die ganze Bühne: Uhr/Datum blenden dann aus (0..1).</summary>
    public float StageBusy;

    /// <summary>Für kurze Hinweise unten (z. B. „Gut gemacht.“). Wird von der Oberfläche angezeigt.</summary>
    public Action<string> Toast = _ => { };
}

/// <summary>
/// Eine Pille in einer der zwei Reihen (oben/unten, wie im Dock zentriert). Klein = Pille, beim Drüberfahren
/// (oder Klick) öffnet sie sich zur Karte. Hintergrund (Glas) zeichnet die Oberfläche; die Pille zeichnet ihren
/// Inhalt in <see cref="Bounds"/>, abhängig von <see cref="Open"/> (0 = Pille, 1 = Karte).
/// </summary>
public abstract class Pill
{
    /// <summary>"music", "water", "timer", "workout", "prayer", "sound".</summary>
    public abstract string Id { get; }
    /// <summary>Soll die Pille gerade da sein (Einstellung an und es gibt etwas zu zeigen)?</summary>
    public abstract bool Shown(UiContext c);
    /// <summary>Größe als Pille / als geöffnete Karte, in Pixeln (mit c.K skalieren).</summary>
    public abstract Vector2 CompactSize(UiContext c);
    public abstract Vector2 CardSize(UiContext c);

    // Von der Oberfläche gesetzt:
    public Vortice.Mathematics.Rect Bounds;
    public float Open;
    public bool Hovered;
    /// <summary>Reihe: 0 = unten, 1 = oben (gespeichert vom Anordnen-Modus).</summary>
    public int Row;

    /// <summary>Möchte sich kurz von selbst öffnen (z. B. neues Lied, Erinnerung): Dauer in Sekunden, 0 = nein.</summary>
    public double AnnounceFor;
    /// <summary>Hervorheben (Erinnerung fällig).</summary>
    public virtual bool Alert => false;

    public virtual void Update(UiContext c, double dt) { }
    public abstract void Draw(ID2D1DeviceContext ctx, UiContext c);
    /// <summary>Maus über der Pille/Karte; true = verarbeitet.</summary>
    public virtual bool OnPointer(UiContext c, PointerEvent e) => false;
    /// <summary>Gerade in Bewegung (dann wird jedes Bild gezeichnet).</summary>
    public virtual bool Animating => false;
    public virtual void Dispose() { }
}

/// <summary>Freie Ebene über dem Bild (Liedtext, Wecker-Ziffernblatt, Training-Bühne, Tipps, Anordnen-Modus).</summary>
public interface IOverlay
{
    /// <summary>Zeichenreihenfolge (klein = weiter hinten). Pillen liegen bei 50.</summary>
    int Z { get; }
    void Update(UiContext c, double dt);
    void Draw(ID2D1DeviceContext ctx, UiContext c);
    /// <summary>true = verarbeitet (darunter liegende Ebenen bekommen das Ereignis nicht).</summary>
    bool OnPointer(UiContext c, PointerEvent e);
    /// <summary>Liegt an dieser Stelle etwas Bedienbares? (für die Maus-Weiterleitung)</summary>
    bool HitTest(UiContext c, Vector2 p);
    bool Animating { get; }
}

/// <summary>Glas-Optik der Karten und Pillen (style.css .glass): weichgezeichnetes Licht dahinter,
/// Tönung, feiner Rand, Schatten. Effekte und Pinsel werden je Gerätekontext einmal angelegt und wiederverwendet.</summary>
public sealed class GlassPainter : IDisposable
{
    // Je Fläche im Bild eine eigene Effektkette (D2D wertet Effekte erst beim Zeichnen aus)
    private const int PoolSize = 16;

    private sealed class Chain : IDisposable
    {
        public Border Border = null!;
        public Crop Crop = null!;
        public GaussianBlur Blur = null!;
        public AffineTransform2D Shift = null!;
        public ID2D1ImageBrush Brush = null!;
        public Flood ShadowFlood = null!;
        public Crop ShadowCrop = null!;
        public GaussianBlur ShadowBlur = null!;
        public ID2D1Image ShadowOut = null!;
        public IntPtr Field;

        public void Dispose()
        {
            ShadowOut?.Dispose();
            Brush?.Dispose();
            Shift?.Dispose(); Blur?.Dispose(); Crop?.Dispose(); Border?.Dispose();
            ShadowBlur?.Dispose(); ShadowCrop?.Dispose(); ShadowFlood?.Dispose();
        }
    }

    private readonly Chain?[] _pool = new Chain?[PoolSize];
    private int _next;
    private double _frameTime = double.NaN;
    private IntPtr _ctxPtr;
    private ID2D1SolidColorBrush? _solid;
    private ID2D1LinearGradientBrush? _tintDark, _tintLight, _sheetDark, _sheetLight, _rimDark, _rimLight;
    private ID2D1RadialGradientBrush? _sheen;

    /// <summary>Die normale Glasfläche (Pillen, Karten).</summary>
    public void Panel(ID2D1DeviceContext ctx, UiContext c, Vortice.Mathematics.Rect r, float radius, float opacity = 1f) =>
        Paint(ctx, c, r, radius, opacity, false);

    /// <summary>Die dunklere, stärker weichgezeichnete Fläche (.settings.glass, .ed-sheet, .ed-toast).</summary>
    public void Sheet(ID2D1DeviceContext ctx, UiContext c, Vortice.Mathematics.Rect r, float radius, float opacity = 1f) =>
        Paint(ctx, c, r, radius, opacity, true);

    private void Paint(ID2D1DeviceContext ctx, UiContext c, Vortice.Mathematics.Rect r, float radius, float opacity, bool sheet)
    {
        if (r.Width < 1 || r.Height < 1 || opacity <= 0.003f) return;
        EnsureDevice(ctx);
        if (c.Time != _frameTime) { _frameTime = c.Time; _next = 0; }
        var ch = _pool[_next % PoolSize] ??= MakeChain(ctx);
        _next++;

        float k = Math.Max(0.1f, c.K), pk = Math.Max(0.1f, c.PK);
        bool light = c.LightTheme;
        float cs = (float)Math.Max(0, c.Settings.Num("cardShadow"));
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.Width, r.Height), radius, radius);
        var solid = _solid!;

        // 1) Weicher Schatten: Fläche (eingezogen, nach unten versetzt) weichgezeichnet
        float offY, blur, spread, alpha;
        Vector3 shadowRgb = light ? new Vector3(40 / 255f, 40 / 255f, 70 / 255f) : Vector3.Zero;
        if (sheet) { offY = 30 * pk; blur = 70 * pk; spread = (light ? -18 : -16) * pk; alpha = light ? 0.35f : 0.6f; }
        else { offY = 18 * k; blur = (light ? 44 : 48) * k; spread = (light ? -14 : -12) * k; alpha = (light ? 0.3f : 0.55f) * cs; }
        alpha *= opacity;
        if (alpha > 0.003f)
        {
            float l = r.Left - spread, t = r.Top - spread + offY, rt = r.Right + spread, b = r.Bottom + spread + offY;
            if (rt > l && b > t)
            {
                ch.ShadowFlood.Color = new Vector4(shadowRgb, alpha);
                ch.ShadowCrop.Rectangle = new Vector4(l, t, rt, b);
                ch.ShadowBlur.StandardDeviation = blur / 2;
                ctx.DrawImage(ch.ShadowOut, null, null, InterpolationMode.Linear, CompositeMode.SourceOver);
            }
        }
        // Kleiner, dichter Schatten direkt unter der Kante
        float small = (sheet ? (light ? 0.06f : 0.2f) : (light ? 0.06f : 0.18f) * Math.Min(1, cs)) * opacity;
        if (small > 0.003f)
        {
            solid.Color = new Color4(0, 0, 0, small);
            var sr = new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y + 1 * k, r.Width, r.Height), radius, radius);
            ctx.FillRoundedRectangle(ref sr, solid);
        }

        // 2) Das Licht dahinter, weichgezeichnet (backdrop-filter: blur)
        var field = c.Field;
        if (field != null && field.NativePointer != IntPtr.Zero)
        {
            if (ch.Field != field.NativePointer)
            {
                ch.Border.SetInput(0, field, true);
                ch.Field = field.NativePointer;
            }
            float sigma = sheet ? 40 * pk : 22 * k;
            float pad = 3 * sigma + 2;
            ch.Crop.Rectangle = new Vector4(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad);
            ch.Blur.StandardDeviation = sigma;
            ch.Shift.TransformMatrix = Matrix3x2.CreateTranslation(-r.Left, -r.Top);
            ch.Brush.SourceRectangle = new System.Drawing.RectangleF(0, 0, r.Width, r.Height);
            ch.Brush.Transform = Matrix3x2.CreateTranslation(r.Left, r.Top);
            ch.Brush.Opacity = opacity;
            ctx.FillRoundedRectangle(ref rr, ch.Brush);
        }

        // 3) Tönung von oben nach unten
        var tint = sheet ? (light ? _sheetLight! : _sheetDark!) : (light ? _tintLight! : _tintDark!);
        tint.StartPoint = new Vector2(r.Left, r.Top);
        tint.EndPoint = new Vector2(r.Left, r.Bottom);
        tint.Opacity = opacity;
        ctx.FillRoundedRectangle(ref rr, tint);

        // Kartenfarben: ein Hauch der Akzentfarbe im Glas
        if (!sheet && c.Settings.Bool("cardColors"))
        {
            solid.Color = new Color4(c.Accent.X, c.Accent.Y, c.Accent.Z, 0.07f * opacity);
            ctx.FillRoundedRectangle(ref rr, solid);
        }

        // 4) Glanz oben links (radial-gradient(120% 160% at 18% -30%))
        var sheen = _sheen!;
        sheen.Center = new Vector2(r.Left + 0.18f * r.Width, r.Top - 0.3f * r.Height);
        sheen.RadiusX = 1.2f * r.Width;
        sheen.RadiusY = 1.6f * r.Height;
        sheen.Opacity = opacity;
        ctx.FillRoundedRectangle(ref rr, sheen);

        // 5) Feiner Rand: hell, wo das Licht auftrifft (linear-gradient(160deg))
        var rim = light ? _rimLight! : _rimDark!;
        const float sin160 = 0.34202f, cos160 = -0.93969f;
        var dir = new Vector2(sin160, -cos160);
        float len = Math.Abs(r.Width * sin160) + Math.Abs(r.Height * cos160);
        var mid = new Vector2(r.Left + r.Width / 2, r.Top + r.Height / 2);
        rim.StartPoint = mid - dir * (len / 2);
        rim.EndPoint = mid + dir * (len / 2);
        rim.Opacity = opacity;
        var edge = new RoundedRectangle(new System.Drawing.RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1),
            Math.Max(0, radius - 0.5f), Math.Max(0, radius - 0.5f));
        ctx.DrawRoundedRectangle(edge, rim, 1f);
    }

    private Chain MakeChain(ID2D1DeviceContext ctx)
    {
        var ch = new Chain
        {
            Border = new Border(ctx) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp },
            Crop = new Crop(ctx),
            Blur = new GaussianBlur(ctx) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Speed },
            Shift = new AffineTransform2D(ctx),
            ShadowFlood = new Flood(ctx) { Color = new Vector4(0, 0, 0, 0.5f) },
            ShadowCrop = new Crop(ctx),
            ShadowBlur = new GaussianBlur(ctx) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Speed },
        };
        ch.Crop.SetInputEffect(0, ch.Border, true);
        ch.Blur.SetInputEffect(0, ch.Crop, true);
        ch.Shift.SetInputEffect(0, ch.Blur, true);
        ch.ShadowCrop.SetInputEffect(0, ch.ShadowFlood, true);
        ch.ShadowBlur.SetInputEffect(0, ch.ShadowCrop, true);
        ch.ShadowOut = ch.ShadowBlur.Output;
        using (var img = ch.Shift.Output)
            ch.Brush = ctx.CreateImageBrush(img,
                new ImageBrushProperties(new System.Drawing.RectangleF(0, 0, 1, 1), ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.Linear),
                null);
        return ch;
    }

    private void EnsureDevice(ID2D1DeviceContext ctx)
    {
        if (_ctxPtr == ctx.NativePointer && _solid != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _solid = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _tintDark = Linear(ctx, (0, W(0.155f)), (0.55f, W(0.07f)), (1, W(0.09f)));
        _tintLight = Linear(ctx, (0, W(0.58f)), (1, W(0.34f)));
        _sheetDark = Linear(ctx, (0, new Color4(32 / 255f, 34 / 255f, 46 / 255f, 0.5f)), (1, new Color4(16 / 255f, 17 / 255f, 24 / 255f, 0.6f)));
        _sheetLight = Linear(ctx, (0, W(0.66f)), (1, new Color4(244 / 255f, 244 / 255f, 248 / 255f, 0.74f)));
        _rimDark = Linear(ctx, (0, W(0.42f)), (0.32f, W(0.08f)), (0.62f, W(0.03f)), (1, W(0.16f)));
        _rimLight = Linear(ctx, (0, W(0.95f)), (0.4f, W(0.3f)), (1, W(0.65f)));
        using (var stops = ctx.CreateGradientStopCollection(new[]
               {
                   new GradientStop { Position = 0, Color = W(0.14f) },
                   new GradientStop { Position = 0.55f, Color = W(0) },
               }))
            _sheen = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
    }

    private static Color4 W(float a) => new(1, 1, 1, a);

    private static ID2D1LinearGradientBrush Linear(ID2D1DeviceContext ctx, params (float pos, Color4 color)[] s)
    {
        var stops = new GradientStop[s.Length];
        for (int i = 0; i < s.Length; i++) stops[i] = new GradientStop { Position = s[i].pos, Color = s[i].color };
        using var coll = ctx.CreateGradientStopCollection(stops);
        return ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(0, 1)), coll);
    }

    private void DisposeDevice()
    {
        for (int i = 0; i < PoolSize; i++) { _pool[i]?.Dispose(); _pool[i] = null; }
        _solid?.Dispose(); _solid = null;
        _tintDark?.Dispose(); _tintLight?.Dispose(); _sheetDark?.Dispose(); _sheetLight?.Dispose();
        _rimDark?.Dispose(); _rimLight?.Dispose(); _sheen?.Dispose();
        _tintDark = _tintLight = _sheetDark = _sheetLight = _rimDark = _rimLight = null;
        _sheen = null;
        _ctxPtr = IntPtr.Zero;
    }

    public void Dispose() => DisposeDevice();
}

/// <summary>Schriften für die Oberfläche (dieselben Dateien wie die Uhr, %AppData%\GlassBar\Fonts).
/// Die zurückgegebenen Formate gehören dem Cache: aufheben ist erlaubt, freigeben nicht.</summary>
public sealed class UiFonts : IDisposable
{
    /// <summary>Schrift je Schlüssel (app.js FONTS): Uhrschrift und Textschrift.</summary>
    private static readonly Dictionary<string, (string Display, string Text)> Map = new()
    {
        ["dmsans"] = ("dmsans", "dmsans"),
        ["manrope"] = ("manrope", "manrope"),
        ["poppins"] = ("poppins", "poppins"),
        ["inter"] = ("inter", "inter"),
        ["geist"] = ("geist", "geist"),
        ["outfit"] = ("outfit", "outfit"),
        ["serif"] = ("serif", "inter"),
        ["system"] = (FontStore.SystemDisplay, FontStore.SystemText),
    };

    private readonly IDWriteFactory _dw;
    private readonly FontStore _store;
    private readonly Dictionary<(int Size, int Weight, bool Display), IDWriteTextFormat> _cache = new();
    private readonly Dictionary<int, IDWriteTextFormat> _icons = new();
    // Nicht mehr aktuelle Formate: können noch in Pillen stecken, werden erst am Ende freigegeben
    private readonly List<IDWriteTextFormat> _retired = new();
    private string _font = "";
    private int _version = -1;
    private string? _iconFamily;
    private readonly object _lock = new();

    public UiFonts(IDWriteFactory dwrite)
    {
        _dw = dwrite;
        _store = new FontStore(dwrite);
    }

    /// <summary>Textformat in der Textschrift der gewählten Schrift (display = Uhrschrift).</summary>
    public IDWriteTextFormat Format(UiContext c, float sizePx, int weight = 500, bool display = false)
    {
        lock (_lock)
        {
            string font = c.Settings.Str("font");
            if (!Map.ContainsKey(font)) font = "manrope";
            int v = _store.Version;
            if (font != _font || v != _version)
            {
                foreach (var f in _cache.Values) _retired.Add(f);
                _cache.Clear();
                _font = font;
                _version = v;
            }
            weight = Math.Clamp(weight, 1, 999);
            int sizeKey = Math.Max(1, (int)MathF.Round(sizePx * 4));
            var key = (sizeKey, weight, display);
            if (_cache.TryGetValue(key, out var fmt) && fmt.NativePointer != IntPtr.Zero) return Neutral(fmt);
            if (_cache.Count > 400)
            {
                foreach (var f in _cache.Values) _retired.Add(f);
                _cache.Clear();
            }
            var info = Map[font];
            var face = _store.Resolve(display ? info.Display : info.Text, weight);
            fmt = _dw.CreateTextFormat(face.Family, face.Collection, face.Weight, FontStyle.Normal, FontStretch.Normal,
                sizeKey / 4f, "de-de");
            _cache[key] = fmt;
            return fmt;
        }
    }

    /// <summary>
    /// Geteilte Formate immer in Grundstellung ausgeben: verstellt ein Teil die Ausrichtung, landeten sonst die
    /// Texte aller anderen (in großen Layout-Boxen) neben dem Bildschirm.
    /// </summary>
    private static IDWriteTextFormat Neutral(IDWriteTextFormat f)
    {
        if (f.TextAlignment != TextAlignment.Leading) f.TextAlignment = TextAlignment.Leading;
        if (f.ParagraphAlignment != ParagraphAlignment.Near) f.ParagraphAlignment = ParagraphAlignment.Near;
        return f;
    }

    /// <summary>Symbolschrift (Segoe Fluent Icons, sonst Segoe MDL2 Assets).</summary>
    public IDWriteTextFormat Icons(UiContext c, float sizePx)
    {
        lock (_lock)
        {
            int sizeKey = Math.Max(1, (int)MathF.Round(sizePx * 4));
            if (_icons.TryGetValue(sizeKey, out var fmt) && fmt.NativePointer != IntPtr.Zero) return fmt;
            _iconFamily ??= _store.HasSystemFamily("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                : _store.HasSystemFamily("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "Segoe UI Symbol";
            fmt = _dw.CreateTextFormat(_iconFamily, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, sizeKey / 4f, "de-de");
            fmt.WordWrapping = WordWrapping.NoWrap;
            _icons[sizeKey] = fmt;
            return fmt;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var f in _cache.Values) f.Dispose();
            foreach (var f in _icons.Values) f.Dispose();
            foreach (var f in _retired) f.Dispose();
            _cache.Clear(); _icons.Clear(); _retired.Clear();
            _store.Dispose();
        }
    }

    /// <summary>Eine geladene Schrift: Familienname, Sammlung (null = System), Stärke.</summary>
    private sealed record Face(string Family, IDWriteFontCollection? Collection, FontWeight Weight);

    /// <summary>
    /// Wie in der Textebene: je Stärke eine Datei ({key}-{weight}.ttf, die Vorschau legt {key}.ttf mit ihrer
    /// Grundstärke ab). Fehlt eine Stärke, wird sie im Hintergrund bei Google Fonts geholt; bis dahin gilt die
    /// nächste vorhandene Stärke oder Segoe UI Variable Text. Nie blockierend.
    /// </summary>
    private sealed class FontStore : IDisposable
    {
        public const string SystemDisplay = "#system-display";
        public const string SystemText = "#system-text";

        private readonly record struct Spec(string Query, int Min, int Max, int Base, bool Fixed);

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

        private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly string Folder = Path.Combine(Config.Folder, "Fonts");

        private readonly IDWriteFactory _dw;
        private readonly IDWriteFactory5? _dw5;
        private readonly IDWriteFontCollection _system;
        private readonly Dictionary<string, Face?> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDWriteFontCollection> _collections = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _attempts = new();
        private int _version;

        public FontStore(IDWriteFactory dw)
        {
            _dw = dw;
            _dw5 = dw.QueryInterfaceOrNull<IDWriteFactory5>();
            _system = dw.GetSystemFontCollection(false);
        }

        public int Version => Volatile.Read(ref _version);

        public bool HasSystemFamily(string family) => _system.FindFamilyName(family, out _);

        public Face Resolve(string key, int weight)
        {
            if (key == SystemDisplay) return SystemFace("Segoe UI Variable Display", weight);
            if (key == SystemText || !Specs.TryGetValue(key, out var spec)) return SystemFace("Segoe UI Variable Text", weight);

            int w = StaticWeight(spec, weight);
            if (FileFor(key, spec, w) is { } path && Load(path) is { } face)
                return spec.Fixed ? face with { Weight = (FontWeight)weight } : face;

            StartDownload(key, spec, w);

            Face? best = null;
            int bestD = int.MaxValue;
            for (int c = 100; c <= 900; c += 100)
            {
                int d = Math.Abs(c - w);
                if (d == 0 || d > 200 || d >= bestD) continue;
                if (FileFor(key, spec, c) is { } p && Load(p) is { } f) { best = f; bestD = d; }
            }
            return best ?? SystemFace("Segoe UI Variable Text", weight);
        }

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
                var legacy = Path.Combine(Folder, key + ".ttf");
                if (File.Exists(legacy)) return legacy;
            }
            return null;
        }

        private Face SystemFace(string family, int weight)
        {
            if (!_system.FindFamilyName(family, out _)) family = "Segoe UI";
            return new Face(family, null, (FontWeight)weight);
        }

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
                        face = new Face(name, coll, font.Weight);
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
            if (_attempts.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(5)) return;
            _attempts[id] = now;
            string query = spec.Fixed ? spec.Query : $"{spec.Query}:wght@{w}";
            _ = Task.Run(async () =>
            {
                try
                {
                    var file = Path.Combine(Folder, id + ".ttf");
                    if (File.Exists(file)) { Interlocked.Increment(ref _version); return; }   // die Textebene war schneller
                    Directory.CreateDirectory(Folder);
                    // Ohne Browser-Kennung liefert Google Fonts TTF-Dateien
                    var css = await Http.GetStringAsync($"https://fonts.googleapis.com/css2?family={query}").ConfigureAwait(false);
                    var m = System.Text.RegularExpressions.Regex.Match(css, @"url\((https://[^)]+\.ttf)\)");
                    if (!m.Success) return;
                    var bytes = await Http.GetByteArrayAsync(m.Groups[1].Value).ConfigureAwait(false);
                    var part = file + ".ui.part";
                    await File.WriteAllBytesAsync(part, bytes).ConfigureAwait(false);
                    File.Move(part, file, true);
                    Interlocked.Increment(ref _version);
                }
                catch (Exception ex) { Log.Error("Schrift laden (Pillen)", ex); }
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

/// <summary>
/// Die Oberfläche (layout.js, pills.js): zwei Pillen-Reihen oben und unten, jede wie ein Dock mittig. Nur Pillen,
/// die gerade da sind, nehmen Platz ein; eine Karte öffnet sich aus der Mitte ihrer Pille, und die Nachbarn
/// gleiten zur Seite (Federn). Dazu die Überlagerungen (nach Z sortiert, Pillen bei 50) und die Maus-Weiterleitung.
/// </summary>
public sealed class StillUi : IDisposable
{
    public readonly List<Pill> Pills = new();
    public readonly List<IOverlay> Overlays = new();

    /// <summary>Feder mit Dämpfung und Ansprechzeit (app.js Spring).</summary>
    public sealed class Spring(double x, double damping, double response, double eps = 0.001)
    {
        private readonly double _k = Math.Pow(2 * Math.PI / response, 2);
        private readonly double _c = 2 * damping * (2 * Math.PI / response);
        public double X = x, V, Target = x;
        public bool Moving => Math.Abs(X - Target) > eps || Math.Abs(V) > eps * 4;

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

        public void Snap(double v) { X = Target = v; V = 0; }
        /// <summary>Von x aus auf 0 zurückfedern (kick in layout.js).</summary>
        public void Kick(double v) { X = v; V = 0; Target = 0; }
    }

    // Abstände in Einheiten (1 px bei 1080p), wie layout.js
    private const float GapU = 14, TopU = 30, BottomU = 88, PillHU = 40, GhostWU = 112, MarginU = 40;
    private static readonly string[] HomeTop = { "sound", "prayer", "workout" }, HomeBottom = { "water", "music", "timer" };
    private const string RowsKey = "layout.rows";

    /// <summary>Welche Einstellung ein Element ein- und ausschaltet (layout.js INFO).</summary>
    public static string? SettingKey(string id) => id switch
    {
        "music" => "showPill", "water" => "hydrate", "timer" => "pomo", "prayer" => "prayer",
        "sound" => "sounds", "workout" => "workout", "weather" => "weather", _ => null,
    };

    private sealed class Slot(Pill p)
    {
        public readonly Pill P = p;
        public string Id = p.Id;
        public readonly Spring E = new(0, 0.8, 0.4);
        public readonly Spring Wc = new(84, 1, 0.4, 0.05), Cw = new(300, 1, 0.42, 0.05), Ch = new(150, 1, 0.42, 0.05);
        public readonly Spring Pres = new(0, 1, 0.3), Off = new(0, 0.86, 0.38, 0.05), Gy = new(0, 0.86, 0.4, 0.05);
        public float X, Tx, LastW, W, H;
        public double? Glide;
        public bool Fresh = true, Shown, SizeFresh = true;
        public float Vis, Halo, Lift;
        public double AlertSince = -1;
        public bool HoverRaw, Hovered, ClickOpen;
        public double HoverOutAt;
        public int Edge;   // 0 unten, 1 oben
    }

    private sealed class Opening
    {
        public int Edge, At;
        public float W, X;
        public readonly Spring P = new(0, 1, 0.3);
    }

    private readonly struct Item(Slot? s, Opening? g, float w, float p)
    {
        public readonly Slot? S = s;
        public readonly Opening? G = g;
        public readonly float W = w, P = p;
    }

    private readonly List<Slot> _slots = new();
    private readonly List<string> _top = new(), _bottom = new();
    private bool _rowsLoaded, _moved;
    private string _pillPos = "";
    private readonly Dictionary<string, bool> _was = new();
    private readonly List<Opening> _gaps = new();
    private readonly List<Item> _items = new();
    private readonly List<float> _xs = new();
    private IOverlay[] _sorted = Array.Empty<IOverlay>();
    private int _overlayCount = -1;
    private EditOverlay? _edit;
    private TipsOverlay? _tips;
    private ToastOverlay? _toast;
    private Action<string>? _toastShow;
    private object? _hover, _capture;
    private float _dim;
    private bool _editing;
    private UiContext? _c;

    // Getragene Pille (Anordnen-Modus)
    private Slot? _carried;
    private float _x0c, _dy0, _dx, _dy;
    private int _edge0;
    private (int Edge, int Index, bool Full)? _target;
    private Opening? _live;

    private IntPtr _ctxPtr;
    private ID2D1SolidColorBrush? _brush;

    public void Add(Pill p) => Pills.Add(p);
    public void Add(IOverlay o) { Overlays.Add(o); Wire(o); }

    /// <summary>Der Anordnen-Modus, falls angemeldet.</summary>
    public EditOverlay? Edit => _edit;
    /// <summary>Die Tipps, falls angemeldet.</summary>
    public TipsOverlay? Tips => _tips;

    private void Wire(IOverlay o)
    {
        if (o is EditOverlay e) { _edit = e; e.Ui ??= this; }
        if (o is TipsOverlay t) { _tips = t; t.Ui ??= this; }
        if (o is ToastOverlay s) { _toast = s; _toastShow = s.Show; }
    }

    // ---------- Jedes Bild ----------

    public void Update(UiContext c, double dt)
    {
        _c = c;
        SyncLists();
        if (_edit != null) c.Editing = _edit.Active;
        if (_toastShow != null) c.Toast = _toastShow;
        _editing = c.Editing;
        float k = c.K;
        LoadRows(c);
        Settle(c);

        double ease = 1 - Math.Exp(-dt * 7);
        _dim += ((_editing ? 1f : 0f) - _dim) * (float)ease;
        if (Math.Abs(_dim - (_editing ? 1 : 0)) < 0.002f) _dim = _editing ? 1 : 0;

        bool hoverExpand = c.Settings.Bool("hoverExpand");
        bool expanded = c.Settings.Str("pillStyle") == "expanded";

        // Größe und Öffnen je Pille
        foreach (var s in _slots)
        {
            var p = s.P;
            s.Shown = p.Shown(c);
            s.Edge = RowOf(s.Id);
            p.Row = s.Edge;

            // Kurze Gnadenzeit beim Verlassen, damit ein Streifen der Kante nichts zuschnappen lässt
            if (!s.HoverRaw && s.Hovered && c.Time >= s.HoverOutAt) { s.Hovered = false; s.ClickOpen = false; }
            if (!s.Shown) { s.Hovered = s.HoverRaw = s.ClickOpen = false; }
            p.Hovered = s.Hovered;

            // Ankündigungen laufen hier für alle Pillen ab (Pillen setzen sie nur, zählen aber nicht selbst)
            if (p.AnnounceFor > 0) p.AnnounceFor = Math.Max(0, p.AnnounceFor - dt);
            double target = 0;
            if (s.Shown && !_editing)
            {
                if (s.Id == "music" && expanded) target = 1;
                else if (hoverExpand ? s.Hovered : s.ClickOpen) target = 1;
                else if (p.AnnounceFor > 0) target = 1;
            }
            s.E.Target = target;
            double e = s.E.Step(dt);
            float ec = (float)Math.Clamp(e, 0, 1);

            var cs = p.CompactSize(c);
            var cd = p.CardSize(c);
            s.Wc.Target = cs.X; s.Cw.Target = cd.X; s.Ch.Target = cd.Y;
            if (s.SizeFresh || !s.Shown || s.Vis < 0.01f) { s.Wc.Snap(cs.X); s.SizeFresh = false; }
            if (target < 0.5 && e < 0.01) { s.Cw.Snap(cd.X); s.Ch.Snap(cd.Y); }
            float wc = (float)s.Wc.Step(dt), cw = (float)s.Cw.Step(dt), ch = (float)s.Ch.Step(dt);
            s.W = Lerp(wc, cw, (float)e);
            s.H = Math.Max(1, Lerp(cs.Y, ch, (float)e));
            p.Open = ec;

            // Erscheinen und Verschwinden (.is-hidden: Deckkraft, Weg vom Rand, Größe)
            float vt = s.Shown ? 1 : 0;
            s.Vis += (vt - s.Vis) * (float)(1 - Math.Exp(-dt * 6.5));
            if (Math.Abs(s.Vis - vt) < 0.003f) s.Vis = vt;

            // Hinweis: ein Ring in der Pille, der dreimal atmet
            bool alert = s.Shown && p.Alert;
            if (alert && s.AlertSince < 0) s.AlertSince = c.Time;
            if (!alert) s.AlertSince = -1;
            float ht = alert ? 1 : 0;
            s.Halo += (ht - s.Halo) * (float)(1 - Math.Exp(-dt * 5));
            if (Math.Abs(s.Halo - ht) < 0.003f) s.Halo = ht;

            float lt = s == _carried ? 1 : 0;
            s.Lift += (lt - s.Lift) * (float)(1 - Math.Exp(-dt * 12));
            if (Math.Abs(s.Lift - lt) < 0.003f) s.Lift = lt;
        }

        LayoutRows(c, dt);

        // Ort setzen – vor Update und Draw der Pille
        foreach (var s in _slots)
        {
            float gy = (float)s.Gy.Step(dt);
            float x = s.X, dy = gy;
            int edge = s.Edge;
            if (s == _carried) { x = _x0c + _dx; dy = _dy0 + _dy; edge = _edge0; }
            s.P.Bounds = new Vortice.Mathematics.Rect(x - s.W / 2, TopOf(c, edge, s.H) + dy, s.W, s.H);
        }

        // Platz der Reihen vom Rand aus (edgeH in layout.js, plus Abstand zum Rand)
        float rowB = 0, rowT = 0;
        foreach (var s in _slots)
        {
            if (!s.Shown || !On(c, s.Id)) continue;
            if (s.Edge == 1) rowT = Math.Max(rowT, s.H); else rowB = Math.Max(rowB, s.H);
        }
        BottomRowHeight = rowB > 0 ? rowB + BottomU * k : 0;
        TopRowHeight = rowT > 0 ? rowT + TopU * k : 0;

        foreach (var s in _slots) s.P.Update(c, dt);
        foreach (var o in _sorted) o.Update(c, dt);
    }

    /// <summary>Platz, den die untere Pillen-Reihe vom unteren Rand aus einnimmt (Pixel, mit Randabstand; 0 = leer).</summary>
    public float BottomRowHeight { get; private set; }
    /// <summary>Platz, den die obere Pillen-Reihe vom oberen Rand aus einnimmt (Pixel, mit Randabstand; 0 = leer).</summary>
    public float TopRowHeight { get; private set; }

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        _c = c;
        EnsureDevice(ctx);
        int i = 0;
        for (; i < _sorted.Length && _sorted[i].Z < 50; i++) _sorted[i].Draw(ctx, c);

        // Im Anordnen-Modus wird das Licht etwas gedämpft
        if (_dim > 0.002f)
        {
            _brush!.Color = c.LightTheme ? new Color4(1, 1, 1, 0.24f * _dim) : new Color4(0, 0, 0, 0.22f * _dim);
            ctx.FillRectangle(new Vortice.RawRectF(0, 0, c.Width, c.Height), _brush);
        }

        foreach (var s in _slots) if (s != _carried) DrawPill(ctx, c, s);
        if (_carried != null) DrawPill(ctx, c, _carried);

        for (; i < _sorted.Length; i++) _sorted[i].Draw(ctx, c);
    }

    private void DrawPill(ID2D1DeviceContext ctx, UiContext c, Slot s)
    {
        if (s.Vis < 0.003f || !s.Shown && s.Vis < 0.003f) return;
        var p = s.P;
        var b = p.Bounds;
        float k = c.K;
        var prev = ctx.Transform;
        var centre = new Vector2(b.X + b.Width / 2, b.Y + b.Height / 2);
        float scale = (0.8f + 0.2f * s.Vis) * (1 + 0.07f * s.Lift);
        float dy = (1 - s.Vis) * 14 * k * (s.Edge == 1 ? -1 : 1) + FloatOffset(c, s);
        bool moved = scale != 1 || dy != 0;
        if (moved) ctx.Transform = Matrix3x2.CreateScale(scale, centre) * Matrix3x2.CreateTranslation(0, dy) * prev;
        bool layer = s.Vis < 0.997f;
        if (layer)
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(-1e7f, -1e7f, 1e7f, 1e7f),
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = s.Vis,
            }, null!);

        float radius = Radius(c, b.Height, p.Open);
        c.Glass.Panel(ctx, c, b, radius, 1);
        p.Draw(ctx, c);

        if (s.Halo > 0.003f)
        {
            float a = s.Halo;
            if (s.AlertSince >= 0)
            {
                double t = c.Time - s.AlertSince;
                if (t < 7.8) a *= (float)(1 - 0.7 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 2.6)));
            }
            var acc = c.Accent;
            _brush!.Color = new Color4(acc.X, acc.Y, acc.Z, 0.85f * a);
            float w = 1.5f * k;
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(b.X + w / 2, b.Y + w / 2, b.Width - w, b.Height - w),
                Math.Max(0, radius - w / 2), Math.Max(0, radius - w / 2));
            ctx.DrawRoundedRectangle(rr, _brush, w);
            // Weicher Schein nach innen (inset 0 0 18k)
            for (int i = 1; i <= 3; i++)
            {
                float inset = (2.5f + 4 * i) * k;
                if (b.Width < 2 * inset || b.Height < 2 * inset) break;
                _brush.Color = new Color4(acc.X, acc.Y, acc.Z, 0.09f * a / i);
                var ri = new RoundedRectangle(new System.Drawing.RectangleF(b.X + inset, b.Y + inset, b.Width - 2 * inset, b.Height - 2 * inset),
                    Math.Max(0, radius - inset), Math.Max(0, radius - inset));
                ctx.DrawRoundedRectangle(ri, _brush, 5 * k);
            }
        }

        if (layer) ctx.PopLayer();
        if (moved) ctx.Transform = prev;
    }

    /// <summary>Eckenradius einer Pille/Karte: min(H/2, lerp(20, 30, Open) × K).</summary>
    public static float Radius(UiContext c, float height, float open) => Math.Min(height / 2, Lerp(20, 30, Math.Clamp(open, 0, 1)) * c.K);

    /// <summary>Im Anordnen-Modus schwebt alles leicht und etwas versetzt (ed-float, 4,2 s).</summary>
    private float FloatOffset(UiContext c, Slot s)
    {
        if (_dim <= 0.002f || s == _carried) return 0;
        double delay = s.Id switch { "water" => -1.1, "timer" => -2.3, "prayer" => -3.2, _ => 0 };
        double ph = (c.Time - delay) / 4.2;
        return (float)(-3 * c.K * (0.5 - 0.5 * Math.Cos(2 * Math.PI * ph))) * _dim;
    }

    // ---------- Maus ----------

    /// <summary>Ein Element hält die Maus seit dem Drücken fest (nur im Render-Thread lesen).</summary>
    public bool HasCapture => _capture != null;

    /// <summary>Maus-Ereignis verteilen; true = von der Oberfläche verarbeitet.</summary>
    public bool OnPointer(UiContext c, PointerEvent e)
    {
        _c = c;
        if (e.Kind == PointerKind.Leave)
        {
            if (_capture == null) SetHover(c, null, e.Pos);
            return false;
        }

        if (_capture != null)
        {
            var cap = _capture;
            if (e.Kind == PointerKind.Up) _capture = null;
            if (cap is IOverlay co) co.OnPointer(c, e);
            else if (cap is Slot cs) DeliverPill(c, cs, e);
            if (_capture == null) SetHover(c, TopAt(c, e.Pos), e.Pos);
            return true;
        }

        var top = TopAt(c, e.Pos);
        SetHover(c, top, e.Pos);

        // Ein Klick woanders schließt per Klick geöffnete Karten
        if (e.Kind == PointerKind.Down)
            foreach (var s in _slots) if (s != top) s.ClickOpen = false;

        bool handled = false;
        int i = _sorted.Length - 1;
        for (; i >= 0 && _sorted[i].Z >= 50 && !handled; i--)
        {
            var o = _sorted[i];
            if (!o.HitTest(c, e.Pos)) continue;
            if (o.OnPointer(c, e)) { handled = true; if (e.Kind == PointerKind.Down) _capture = o; }
        }
        if (!handled && PillAt(e.Pos) is { } slot)
        {
            handled = DeliverPill(c, slot, e);
            if (e.Kind == PointerKind.Down) _capture = slot;
        }
        for (; i >= 0 && !handled; i--)
        {
            var o = _sorted[i];
            if (o.Z >= 50 || !o.HitTest(c, e.Pos)) continue;
            if (o.OnPointer(c, e)) { handled = true; if (e.Kind == PointerKind.Down) _capture = o; }
        }
        return handled || top != null;
    }

    private bool DeliverPill(UiContext c, Slot s, PointerEvent e)
    {
        bool used = s.P.OnPointer(c, e);
        // Ohne Öffnen beim Drüberfahren: ein Klick öffnet bzw. schließt die Karte
        if (!used && e.Kind == PointerKind.Down && e.Button == 0 && !_editing && !c.Settings.Bool("hoverExpand"))
            s.ClickOpen = !(s.ClickOpen && s.P.Open > 0.5f);
        return true;
    }

    private object? TopAt(UiContext c, Vector2 p)
    {
        int i = _sorted.Length - 1;
        for (; i >= 0 && _sorted[i].Z >= 50; i--) if (_sorted[i].HitTest(c, p)) return _sorted[i];
        if (PillAt(p) is { } s) return s;
        for (; i >= 0; i--) if (_sorted[i].HitTest(c, p)) return _sorted[i];
        return null;
    }

    private Slot? PillAt(Vector2 p)
    {
        if (_carried != null && Hit(_carried, p)) return _carried;
        for (int i = _slots.Count - 1; i >= 0; i--) if (Hit(_slots[i], p)) return _slots[i];
        return null;
    }

    private static bool Hit(Slot s, Vector2 p) => s.Shown && s.Vis > 0.5f && s.P.Bounds.Contains(p);

    private void SetHover(UiContext c, object? target, Vector2 pos)
    {
        if (ReferenceEquals(target, _hover)) return;
        var old = _hover;
        _hover = target;
        if (old is Slot os)
        {
            os.HoverRaw = false;
            os.HoverOutAt = c.Time + 0.12;
            os.P.OnPointer(c, new PointerEvent(PointerKind.Leave, pos));
        }
        else if (old is IOverlay oo) oo.OnPointer(c, new PointerEvent(PointerKind.Leave, pos));
        if (target is Slot ns) { ns.HoverRaw = true; ns.Hovered = true; ns.P.Hovered = true; }
    }

    /// <summary>Liegt an der Stelle eine Pille/Karte/Ebene? (Klicks dort gehören dem Hintergrund, nicht dem Desktop)</summary>
    public bool HitTest(UiContext c, Vector2 p)
    {
        foreach (var o in _sorted) if (o.HitTest(c, p)) return true;
        return PillAt(p) != null;
    }

    public bool Animating
    {
        get
        {
            if (_dim > 0 && _dim < 1 || _editing || _carried != null || _gaps.Count > 0) return true;
            double now = _c?.Time ?? 0;
            foreach (var s in _slots)
            {
                if (s.E.Moving || s.Wc.Moving || s.Cw.Moving || s.Ch.Moving || s.Pres.Moving || s.Off.Moving || s.Gy.Moving) return true;
                if (s.Vis is > 0 and < 1 || s.Halo is > 0 and < 1 || s.Lift is > 0 and < 1 || s.Glide != null) return true;
                if (s.AlertSince >= 0 && now - s.AlertSince < 8) return true;
                if (!s.HoverRaw && s.Hovered) return true;   // Gnadenzeit läuft
                if (s.P.AnnounceFor > 0 || s.Shown && s.P.Animating) return true;
            }
            foreach (var o in _sorted) if (o.Animating) return true;
            return false;
        }
    }

    // ---------- Reihen (layout.js) ----------

    private void SyncLists()
    {
        if (_overlayCount != Overlays.Count || _sorted.Length != Overlays.Count)
        {
            _overlayCount = Overlays.Count;
            var arr = Overlays.ToArray();
            // stabil nach Z sortieren
            _sorted = arr.Select((o, idx) => (o, idx)).OrderBy(t => t.o.Z).ThenBy(t => t.idx).Select(t => t.o).ToArray();
            foreach (var o in _sorted) Wire(o);
        }
        bool same = _slots.Count == Pills.Count;
        for (int i = 0; same && i < Pills.Count; i++) same = ReferenceEquals(_slots[i].P, Pills[i]);
        if (same) return;
        var old = _slots.ToList();
        _slots.Clear();
        foreach (var p in Pills) _slots.Add(old.Find(s => s.P == p) ?? new Slot(p));
        if (_carried != null && !_slots.Contains(_carried)) _carried = null;
        _rowsLoaded = _rowsLoaded && old.Count == _slots.Count;
    }

    private Slot? SlotOf(string id)
    {
        foreach (var s in _slots) if (s.Id == id) return s;
        return null;
    }

    private void LoadRows(UiContext c)
    {
        string pos = c.Settings.Str("pillPos");
        if (_rowsLoaded && (pos == _pillPos || _moved)) return;
        bool first = !_rowsLoaded;
        _rowsLoaded = true;
        _pillPos = pos;
        _top.Clear(); _bottom.Clear();
        if (first)
        {
            var saved = StillState.Get<Dictionary<string, List<string>>?>(RowsKey, null);
            if (saved != null && saved.TryGetValue("top", out var t) && saved.TryGetValue("bottom", out var b))
            {
                _top.AddRange(t); _bottom.AddRange(b);
                _moved = true;
            }
        }
        if (!_moved)
        {
            // Unberührt folgen die Reihen der Einstellung „pillPos“ (oben = getauscht)
            bool swap = pos == "top";
            _top.AddRange(swap ? HomeBottom : HomeTop);
            _bottom.AddRange(swap ? HomeTop : HomeBottom);
        }
        foreach (var s in _slots) if (!s.Fresh) s.Glide = s.X;
        Settle(c, force: true);
    }

    private void Save()
    {
        if (_moved)
            StillState.Set<Dictionary<string, List<string>>?>(RowsKey, new() { ["top"] = _top.ToList(), ["bottom"] = _bottom.ToList() });
        else
            StillState.Set<Dictionary<string, List<string>>?>(RowsKey, null);
    }

    private int RowOf(string id) => _top.Contains(id) ? 1 : 0;
    private List<string> RowList(int edge) => edge == 1 ? _top : _bottom;
    private static int HomeOf(string id) => Array.IndexOf(HomeTop, id) >= 0 ? 1 : 0;

    private bool On(UiContext c, string id) => SettingKey(id) is not { } key || c.Settings.Bool(key);

    /// <summary>Jede Pille in genau einer Reihe; eine eingeschaltete Pille in einer vollen Reihe versucht die andere.</summary>
    private void Settle(UiContext c, bool force = false)
    {
        bool changed = false, any = force;
        foreach (var s in _slots)
            if (!_was.TryGetValue(s.Id, out var w) || w != On(c, s.Id)) { any = true; break; }
        if (!any) return;

        var seen = new HashSet<string>();
        foreach (var row in new[] { _top, _bottom })
            for (int i = row.Count - 1; i >= 0; i--)
                if (seen.Contains(row[i])) { row.RemoveAt(i); changed = true; }
                else seen.Add(row[i]);
        foreach (var s in _slots)
            if (seen.Add(s.Id)) { RowList(HomeOf(s.Id)).Add(s.Id); changed = true; }
        foreach (var s in _slots)
        {
            bool on = On(c, s.Id);
            if (on && _was.TryGetValue(s.Id, out var was) && !was)
            {
                int e = RowOf(s.Id), o = 1 - e;
                var other = RowList(o).ToList();
                other.Add(s.Id);
                if (!Fits(c, RowList(e)) && Fits(c, other))
                {
                    RowList(e).Remove(s.Id);
                    RowList(o).Add(s.Id);
                    changed = true;
                }
            }
        }
        foreach (var s in _slots) _was[s.Id] = On(c, s.Id);
        if (changed && !force) { _moved = true; Save(); }
    }

    private float CompactOf(UiContext c, Slot s) => s.Shown || !_editing ? s.P.CompactSize(c).X : GhostWU * c.K;

    /// <summary>Platz für diese Pillen in einer Reihe: nebeneinander geschlossen, plus die breiteste Karte offen.</summary>
    private bool Fits(UiContext c, List<string> ids)
    {
        float k = c.K, sum = 0, grow = 0;
        int n = 0;
        foreach (var id in ids)
        {
            var s = SlotOf(id);
            if (s == null || !On(c, id)) continue;
            float cw = CompactOf(c, s);
            sum += cw;
            grow = Math.Max(grow, s.P.CardSize(c).X - cw);
            n++;
        }
        return n == 0 || sum + GapU * k * (n - 1) + grow <= c.Width - 2 * MarginU * k;
    }

    /// <summary>Breite jetzt (eine Karte macht sie breiter); eine verschwundene behält ihre letzte, solange ihr Platz sich schließt.</summary>
    private float WidthOf(UiContext c, Slot s)
    {
        if (s.Shown && s.W > 0) s.LastW = s.W;
        else if (_editing && On(c, s.Id)) s.LastW = GhostWU * c.K;
        return s.LastW;
    }

    private float HeightOf(UiContext c, Slot s) => s.Shown ? s.H : PillHU * c.K;

    private bool Showing(UiContext c, Slot s) => (s.Shown || _editing && On(c, s.Id)) && s != _carried;

    private static float TopOf(UiContext c, int edge, float hh) => edge == 1 ? TopU * c.K : c.Height - BottomU * c.K - hh;

    private void Centres(UiContext c)
    {
        float g = GapU * c.K, total = 0, any = 0;
        foreach (var it in _items) { total += (it.W + g) * it.P; any += it.P; }
        total -= g * Math.Min(1, any);
        float x = c.Width / 2f - total / 2;
        _xs.Clear();
        foreach (var it in _items)
        {
            _xs.Add(x + it.W * it.P / 2);
            x += (it.W + g) * it.P;
        }
    }

    private void LayoutRows(UiContext c, double dt)
    {
        for (int i = _gaps.Count - 1; i >= 0; i--)
        {
            var g = _gaps[i];
            g.P.Step(dt);
            if (g.P.Target == 0 && g.P.X < 0.004) _gaps.RemoveAt(i);
        }
        for (int edge = 0; edge < 2; edge++)
        {
            _items.Clear();
            foreach (var id in RowList(edge))
            {
                var s = SlotOf(id);
                if (s == null) continue;
                s.Pres.Target = Showing(c, s) ? 1 : 0;
                if (s.Fresh) s.Pres.Snap(s.Pres.Target);
                _items.Add(new Item(s, null, WidthOf(c, s), (float)s.Pres.Step(dt)));
            }
            // Öffnungen kommen hinter so viele Pillen (ohne die getragene), wie ihr Index sagt
            int plainCount = _items.Count;
            for (int pass = 0; pass < _gaps.Count; pass++)
            {
                Opening? g = null;
                int best = int.MinValue;
                foreach (var cand in _gaps)
                    if (cand.Edge == edge && cand.At > best && !Inserted(cand)) { g = cand; best = cand.At; }
                if (g == null) break;
                int pos = 0, n = 0;
                while (pos < plainCount && n < g.At) { if (_items[pos].S != _carried) n++; pos++; }
                _items.Insert(pos, new Item(null, g, g.W, (float)g.P.X));
                plainCount++;
            }
            Centres(c);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.G != null) { it.G.X = _xs[i]; continue; }
                var s = it.S!;
                s.Tx = _xs[i];
                if (s.Fresh) { s.Off.Snap(0); s.Fresh = false; }
                if (s.Glide is { } gl) { s.Off.Kick(gl - s.Tx); s.Glide = null; }
                s.X = s.Tx + (float)s.Off.Step(dt);
            }
        }
    }

    private bool Inserted(Opening g)
    {
        foreach (var it in _items) if (it.G == g) return true;
        return false;
    }

    // ---------- Für den Anordnen-Modus ----------

    /// <summary>Ist die Pille eingeschaltet (Einstellung an)?</summary>
    public bool IsOn(UiContext c, Pill p) => On(c, p.Id);

    /// <summary>Im Anordnen-Modus: eingeschaltet, aber gerade nichts zu zeigen (wird als Platzhalter gezeichnet).</summary>
    public bool IsGhost(UiContext c, Pill p) => _editing && On(c, p.Id) && !p.Shown(c);

    /// <summary>Wo die Pille gerade auf dem Bildschirm ist (mit Gleiten, Tragen und Schweben); null = nicht da.</summary>
    public Vortice.Mathematics.Rect? VisualRect(UiContext c, Pill p)
    {
        var s = _slots.Find(x => x.P == p);
        if (s == null) return null;
        float w = WidthOf(c, s), hh = HeightOf(c, s);
        if (w <= 0) return null;
        float x = s.X, dy = (float)s.Gy.X;
        int edge = s.Edge;
        if (s == _carried) { x = _x0c + _dx; dy = _dy0 + _dy; edge = _edge0; }
        else dy += FloatOffset(c, s);
        return new Vortice.Mathematics.Rect(x - w / 2, TopOf(c, edge, hh) + dy, w, hh);
    }

    public Pill? Carried => _carried?.P;

    /// <summary>Eine Pille aufnehmen (sie bleibt unter der Maus, ihre Reihe schließt sich).</summary>
    public void CarryStart(Pill p)
    {
        var s = _slots.Find(x => x.P == p);
        if (s == null) return;
        _carried = s;
        _x0c = s.X; _dy0 = (float)s.Gy.X; _edge0 = s.Edge;
        _dx = _dy = 0;
        _target = null;
        _live = null;
    }

    /// <summary>Getragene Pille bewegen: delta seit dem Aufnehmen, pointer = Mausposition.</summary>
    public void CarryMove(UiContext c, Vector2 delta, Vector2 pointer)
    {
        if (_carried == null) return;
        _dx = delta.X; _dy = delta.Y;
        Aim(c, PickSlot(c, pointer));
    }

    /// <summary>Loslassen: landet in der Öffnung (falls es eine gibt) und gleitet an ihren Platz.</summary>
    public void CarryEnd()
    {
        var s = _carried;
        var c = _c;
        if (s == null || c == null) { _carried = null; return; }
        var before = VisualRect(c, s.P) ?? new Vortice.Mathematics.Rect(s.X, 0, 0, 0);
        float bx = before.X + before.Width / 2;
        if (_target is { Full: false } t && _live != null)
        {
            _top.Remove(s.Id); _bottom.Remove(s.Id);
            var row = RowList(t.Edge);
            row.Insert(Math.Min(t.Index, row.Count), s.Id);
            s.Pres.X = _live.P.X; s.Pres.V = 0;
            _gaps.Remove(_live);
            _moved = true;
            Save();
        }
        foreach (var g in _gaps) g.P.Target = 0;
        _live = null;
        _target = null;
        _carried = null;
        s.Edge = RowOf(s.Id);
        s.P.Row = s.Edge;
        s.Glide = bx;
        s.X = bx;
        s.Gy.Kick(before.Y - TopOf(c, s.Edge, HeightOf(c, s)));
    }

    /// <summary>Wo die getragene Pille landen würde (oder: Reihe voll).</summary>
    public bool CarrySlot(UiContext c, out Vortice.Mathematics.Rect r, out bool full)
    {
        r = default; full = false;
        if (_carried == null || _target is not { } t) return false;
        float k = c.K, hh = PillHU * k;
        if (!t.Full && _live != null)
        {
            float w = _live.W * (float)_live.P.X;
            r = new Vortice.Mathematics.Rect(_live.X - w / 2, TopOf(c, _live.Edge, hh), w, hh);
            return true;
        }
        if (!t.Full) return false;
        full = true;
        float end = float.NegativeInfinity;
        foreach (var id in RowList(t.Edge))
            if (SlotOf(id) is { } s && Showing(c, s) && VisualRect(c, s.P) is { } vr) end = Math.Max(end, vr.Right);
        if (float.IsNegativeInfinity(end)) end = c.Width / 2f;
        r = new Vortice.Mathematics.Rect(end + GapU * k, TopOf(c, t.Edge, hh), 64 * k, hh);
        return true;
    }

    /// <summary>Oberes oder unteres Drittel wählt die Reihe, die Stelle entlang der Reihe den Platz.</summary>
    private (int Edge, int Index, bool Full)? PickSlot(UiContext c, Vector2 p)
    {
        float vh = c.Height;
        int edge = p.Y < vh * 0.36f ? 1 : p.Y > vh * 0.64f ? 0 : -1;
        if (edge < 0) return null;
        var list = new List<Slot>();
        foreach (var id in RowList(edge)) if (SlotOf(id) is { } s && s != _carried) list.Add(s);
        var ids = list.Select(x => x.Id).ToList();
        ids.Add(_carried!.Id);
        if (!Fits(c, ids)) return (edge, 0, true);
        _items.Clear();
        foreach (var s in list) _items.Add(new Item(s, null, WidthOf(c, s), (float)s.Pres.X));
        Centres(c);
        int index = 0;
        for (int i = 0; i < list.Count; i++) if (list[i].Pres.X > 0.5 && _xs[i] < p.X) index = i + 1;
        return (edge, index, false);
    }

    private void Aim(UiContext c, (int Edge, int Index, bool Full)? t)
    {
        _target = t;
        var want = t is { Full: false } w ? w : ((int, int, bool)?)null;
        _live = null;
        foreach (var g in _gaps)
        {
            if (want is { } ww && g.Edge == ww.Item1 && g.At == ww.Item2) _live = g;
            else g.P.Target = 0;
        }
        if (want is { } nw && _live == null && _carried != null)
        {
            _live = new Opening { Edge = nw.Item1, At = nw.Item2, W = CompactOf(c, _carried) };
            _gaps.Add(_live);
        }
        if (_live != null) _live.P.Target = 1;
    }

    /// <summary>Pillen zurück an ihre Plätze (Layout zurücksetzen).</summary>
    public void ResetRows()
    {
        var c = _c;
        _top.Clear(); _top.AddRange(HomeTop);
        _bottom.Clear(); _bottom.AddRange(HomeBottom);
        foreach (var s in _slots)
        {
            int to = RowOf(s.Id);
            if (to != s.Edge && s.Shown) { s.Pres.Snap(0); s.Vis = 0; s.Off.Snap(0); s.Gy.Snap(0); }
            else s.Glide = s.X;
        }
        foreach (var s in _slots) if (!_top.Contains(s.Id) && !_bottom.Contains(s.Id)) RowList(HomeOf(s.Id)).Add(s.Id);
        _moved = true;
        Save();
    }

    // ---------- Gerät ----------

    private void EnsureDevice(ID2D1DeviceContext ctx)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        _brush?.Dispose();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Dispose()
    {
        foreach (var p in Pills) p.Dispose();
        foreach (var o in Overlays) (o as IDisposable)?.Dispose();
        _brush?.Dispose();
        _brush = null;
    }
}

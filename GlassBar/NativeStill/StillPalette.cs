using System.Numerics;

namespace GlassBar.NativeStill;

/// <summary>
/// Farben des Lichtfelds: Theme-Palette, optional zur Albumfarbe hin getönt, mit Wetterstimmung.
/// Port aus app.js (THEMES, harmonize, extractColors, rebuildTarget, stepPalette, stepMood).
/// Alle Farben 0..1 (sRGB-Werte, genau so, wie der Shader des Originals sie bekommt).
/// Step() und die Eigenschaften laufen im Render-Thread; SetTheme/SetAlbum/SetMood dürfen aus jedem Thread kommen.
/// </summary>
public sealed class StillPalette
{
    // ---------- Themes ----------

    private sealed record Theme(bool Light, string Base, string C0, string C1, string C2, string C3, string Accent);

    // Dieselben Farben wie im Original (THEMES in app.js)
    private static readonly Dictionary<string, Theme> Themes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ink"] = new(false, "#07080d", "#30358a", "#1b5f6e", "#5b2c70", "#141a35", "#a9b4ff"),
        ["dusk"] = new(false, "#0b070e", "#86385a", "#3b2c80", "#a8603a", "#22142c", "#ffb3c7"),
        ["moss"] = new(false, "#050907", "#1e5b49", "#4f6e2a", "#1a4260", "#0f1f19", "#a6e8c8"),
        ["ember"] = new(false, "#0a0605", "#8f3c1f", "#6e2436", "#a7782b", "#22110b", "#ffc49a"),
        ["graphite"] = new(false, "#070708", "#3c3d44", "#2a2e35", "#48444f", "#151517", "#e6e7ee"),
        ["paper"] = new(true, "#ebe8e1", "#c3cff3", "#f3d1c1", "#cde6da", "#efe7f6", "#4a55c8"),
    };

    // Reihenfolge der Palettenfarben (PAL_KEYS): base, c0, c1, c2, c3, accent
    private const int PalCount = 6;
    private const int KBase = 0, KC0 = 1, KC1 = 2, KC2 = 3, KC3 = 4, KAccent = 5;
    private static readonly string[] PalKeys = ["base", "c0", "c1", "c2", "c3", "accent"];

    /// <summary>Namen der Palettenfarben in der Reihenfolge des Originals.</summary>
    public static IReadOnlyList<string> PaletteKeys => PalKeys;

    /// <summary>Alle Theme-Schlüssel.</summary>
    public static IReadOnlyCollection<string> ThemeKeys => Themes.Keys;

    // ---------- Ausgabe (Render-Thread) ----------

    // Aktuelle (geglättete) Feldpalette nach Stimmung – das, was der Shader bekommt (fieldPal im Original)
    public Vector3 Base { get; private set; } = new(0.027f, 0.031f, 0.051f);
    public Vector3 C0 { get; private set; } = new(0.188f, 0.208f, 0.541f);
    public Vector3 C1 { get; private set; } = new(0.106f, 0.373f, 0.431f);
    public Vector3 C2 { get; private set; } = new(0.357f, 0.173f, 0.439f);
    public Vector3 C3 { get; private set; } = new(0.078f, 0.102f, 0.208f);
    /// <summary>Akzentfarbe (für Texte/Karten).</summary>
    public Vector3 Accent { get; private set; } = new(0.663f, 0.706f, 1f);
    /// <summary>0 = dunkles Theme, 1 = helles Theme (geglättet, lightCur im Original).</summary>
    public float Light { get; private set; }
    /// <summary>Ist das gewählte Theme hell (paper)? Dann dunkle Schrift.</summary>
    public bool IsLightTheme { get; private set; }
    /// <summary>Geschwindigkeitsfaktor der Stimmung (mood.speed), für die Bewegung des Felds.</summary>
    public float MoodSpeed { get; private set; } = 1f;

    // ---------- Zielzustand (unter _lock, aus jedem Thread) ----------

    private readonly object _lock = new();
    private string _themeKey = "ink";
    private Vector3[]? _albumColors;          // drei Farben aus dem Cover oder null
    private bool _follow = true;
    private float _strength = 0.8f;
    private bool _visible;
    private Mood _moodGoal = Mood.Neutral;
    private Vector3[] _palTarget;              // fertig gemischtes Ziel (rebuildTarget)
    private float _lightTarget;
    private bool _targetLight;

    // ---------- Laufzustand (nur Render-Thread) ----------

    private readonly Vector3[] _palCur = new Vector3[PalCount];
    private readonly Vector3[] _fieldPal = new Vector3[PalCount];
    private float _lightCur;
    private Mood _mood = Mood.Neutral;

    private struct Mood(float warm, float sat, float light, float speed)
    {
        public float Warm = warm, Sat = sat, Light = light, Speed = speed;
        public static Mood Neutral => new(0f, 1f, 1f, 1f);
    }

    public StillPalette()
    {
        // Startet direkt im Ziel – kein Einblenden aus Schwarz beim Start.
        _palTarget = BuildTarget();
        _lightTarget = _targetLight ? 1f : 0f;
        Array.Copy(_palTarget, _palCur, PalCount);
        _lightCur = _lightTarget;
        ApplyMood(_targetLight);
        Publish(_targetLight);
    }

    /// <summary>Theme-Schlüssel: ink, dusk, moss, ember, graphite, paper.</summary>
    public void SetTheme(string themeKey)
    {
        lock (_lock)
        {
            _themeKey = Themes.ContainsKey(themeKey ?? "") ? themeKey! : "ink";
            RebuildTargetLocked();
        }
    }

    /// <summary>
    /// Albumcover (Pixel BGRA32, Breite, Höhe) oder null. follow = Einstellung followAlbum, strength = tintStrength.
    /// visible = Musik-Karte sichtbar (wantVisible im Original); nur dann wird getönt.
    /// </summary>
    public void SetAlbum(byte[]? bgra, int width, int height, bool follow, double strength, bool visible)
    {
        // Das Auslesen der Farben kostet etwas – darum außerhalb der Sperre.
        Vector3[]? cols = bgra != null ? ExtractColors(bgra, width, height) : null;
        lock (_lock)
        {
            _albumColors = cols;
            _follow = follow;
            _strength = (float)Math.Clamp(double.IsFinite(strength) ? strength : 0.8, 0, 1);
            _visible = visible;
            RebuildTargetLocked();
        }
    }

    /// <summary>Wetterstimmung (warm -1..1, sat, light, speed; neutral = 0,1,1,1). Null = neutral.</summary>
    public void SetMood(float warm, float sat, float light, float speed)
    {
        lock (_lock) _moodGoal = new Mood(warm, sat, light, speed);
    }

    /// <summary>Stimmung zurück auf neutral (setMood(null) im Original).</summary>
    public void ClearMood()
    {
        lock (_lock) _moodGoal = Mood.Neutral;
    }

    /// <summary>Einen Schritt glätten (dt in Sekunden).</summary>
    public void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0) dt = 0;

        Span<Vector3> target = stackalloc Vector3[PalCount];
        float lightTarget;
        bool isLight;
        Mood goal;
        lock (_lock)
        {
            _palTarget.AsSpan().CopyTo(target);
            lightTarget = _lightTarget;
            isLight = _targetLight;
            goal = _moodGoal;
        }

        // stepPalette: Lieder gehen langsam ineinander über
        float a = (float)(1 - Math.Exp(-dt / 1.4));
        for (int k = 0; k < PalCount; k++)
            _palCur[k] += (target[k] - _palCur[k]) * a;
        _lightCur += (lightTarget - _lightCur) * a;

        // stepMood: das Wetter lehnt sich über etwa eine Minute hinein
        float m = (float)(1 - Math.Exp(-dt / 25));
        _mood.Warm += (goal.Warm - _mood.Warm) * m;
        _mood.Sat += (goal.Sat - _mood.Sat) * m;
        _mood.Light += (goal.Light - _mood.Light) * m;
        _mood.Speed += (goal.Speed - _mood.Speed) * m;

        ApplyMood(isLight);
        Publish(isLight);
    }

    // Stimmung auf alles außer den Akzent legen (zweiter Teil von stepMood)
    private void ApplyMood(bool isLight)
    {
        // Helle Themes nehmen nur 30 % der Abdunklung mit.
        float light = isLight ? 1 - (1 - _mood.Light) * 0.3f : _mood.Light;
        for (int k = 0; k < PalCount; k++)
        {
            var c = _palCur[k];
            if (k == KAccent)
            {
                _fieldPal[k] = c;
                continue;
            }
            float lum = 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;
            var o = new Vector3(lum) + (c - new Vector3(lum)) * _mood.Sat;
            o.X *= 1 + 0.12f * _mood.Warm;
            o.Z *= 1 - 0.12f * _mood.Warm;
            _fieldPal[k] = Vector3.Clamp(o * light, Vector3.Zero, Vector3.One);
        }
    }

    private void Publish(bool isLight)
    {
        Base = _fieldPal[KBase];
        C0 = _fieldPal[KC0];
        C1 = _fieldPal[KC1];
        C2 = _fieldPal[KC2];
        C3 = _fieldPal[KC3];
        Accent = _fieldPal[KAccent];
        Light = _lightCur;
        IsLightTheme = isLight;
        MoodSpeed = _mood.Speed;
    }

    // ---------- Ziel ----------

    private void RebuildTargetLocked()
    {
        _palTarget = BuildTarget();
        _lightTarget = _targetLight ? 1f : 0f;
    }

    // rebuildTarget: tintStrength mischt von der Theme-Palette (0) zur Cover-Palette (1)
    private Vector3[] BuildTarget()
    {
        var theme = Themes.TryGetValue(_themeKey, out var t) ? t : Themes["ink"];
        _targetLight = theme.Light;
        var pal = ThemePalette(theme);
        if (_follow && _visible && _albumColors != null)
        {
            var album = Harmonize(_albumColors, theme.Light);
            for (int k = 0; k < PalCount; k++)
                pal[k] += (album[k] - pal[k]) * _strength;
        }
        return pal;
    }

    private static Vector3[] ThemePalette(Theme t) =>
        [Hex(t.Base), Hex(t.C0), Hex(t.C1), Hex(t.C2), Hex(t.C3), Hex(t.Accent)];

    // ---------- Wetter ----------

    /// <summary>
    /// Stimmung zum Wettercode (WMO) wie in weather.js (MOODS); nachts etwas dunkler und kühler.
    /// Null = neutral (kein gültiger Code, z. B. negativ). Unbekannte Codes gelten wie im Original als bewölkt.
    /// </summary>
    public static (float warm, float sat, float light, float speed)? MoodForWeather(int wmoCode, bool isDay)
    {
        if (wmoCode < 0) return null;
        (float warm, float sat, float light, float speed) m = wmoCode switch
        {
            0 => (0.8f, 1.05f, 1f, 1f),                                   // klar
            1 or 2 => (0.4f, 1f, 1f, 1f),                                  // teilweise bewölkt
            45 or 48 => (0f, 0.75f, 0.97f, 0.85f),                         // Nebel
            51 or 53 or 55 or 56 or 57 => (-0.6f, 0.88f, 0.93f, 0.85f),   // Nieselregen
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => (-0.8f, 0.85f, 0.9f, 0.8f), // Regen
            71 or 73 or 75 or 77 or 85 or 86 => (-0.5f, 0.7f, 1.03f, 0.75f),            // Schnee
            95 or 96 or 99 => (-0.6f, 0.8f, 0.82f, 0.9f),                 // Gewitter
            _ => (-0.1f, 0.85f, 0.95f, 0.92f),                             // bewölkt (auch Code 3)
        };
        if (!isDay)
        {
            m.light *= 0.92f;
            m.warm = Math.Min(m.warm, 0f) - 0.2f;
        }
        return m;
    }

    // ---------- Farbrechnung ----------

    private static Vector3 Hex(string h)
    {
        h = h.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        int n = Convert.ToInt32(h, 16);
        return new Vector3((n >> 16 & 255) / 255f, (n >> 8 & 255) / 255f, (n & 255) / 255f);
    }

    private static (float h, float s, float l) Rgb2Hsl(Vector3 c)
    {
        float r = c.X, g = c.Y, b = c.Z;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        float d = max - min;
        float s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Vector3 Hsl2Rgb(float h, float s, float l)
    {
        h = ((h % 1) + 1) % 1;
        if (s == 0) return new Vector3(l);
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        float F(float t)
        {
            t = ((t % 1) + 1) % 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < 1f / 2) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }
        return new Vector3(F(h + 1f / 3), F(h), F(h - 1f / 3));
    }

    // Grün und Gelb wirken bei gleicher HSL-Helligkeit viel heller als Blau – darum zusätzlich nach Luma deckeln.
    private static Vector3 CapLuma(Vector3 c, float maxY)
    {
        float y = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        return y > maxY ? c * (maxY / y) : c;
    }

    // Die Farbtöne des Covers behalten, aber zähmen: tief und ruhig auf dunklen Themes, sanftes Pastell auf dem hellen.
    private static Vector3[] Harmonize(Vector3[] cols, bool light)
    {
        var (h0, s0, _) = Rgb2Hsl(cols[0]);
        var (h1, s1, _) = Rgb2Hsl(cols[1]);
        var (h2, s2, _) = Rgb2Hsl(cols[2]);
        static float Cl(float v, float lo, float hi) => Math.Clamp(v, lo, hi);

        var o = new Vector3[PalCount];
        if (!light)
        {
            o[KBase] = Hsl2Rgb(h0, Cl(s0 * 0.6f, 0.18f, 0.42f), 0.05f);
            o[KC3] = Hsl2Rgb(h0, Cl(s0 * 0.7f, 0.2f, 0.5f), 0.13f);
            o[KC2] = CapLuma(Hsl2Rgb(h2, Cl(s2, 0.32f, 0.7f), 0.31f), 0.29f);
            o[KC1] = CapLuma(Hsl2Rgb(h1, Cl(s1, 0.36f, 0.74f), 0.37f), 0.33f);
            o[KC0] = CapLuma(Hsl2Rgb(h0, Cl(s0, 0.4f, 0.8f), 0.44f), 0.4f);
            o[KAccent] = Hsl2Rgb(h0, Cl(s0, 0.45f, 0.85f), 0.76f);
        }
        else
        {
            o[KBase] = Hsl2Rgb(h0, Cl(s0 * 0.3f, 0.08f, 0.2f), 0.925f);
            o[KC3] = Hsl2Rgb(h0, Cl(s0 * 0.4f, 0.12f, 0.3f), 0.90f);
            o[KC2] = Hsl2Rgb(h2, Cl(s2 * 0.7f, 0.25f, 0.55f), 0.85f);
            o[KC1] = Hsl2Rgb(h1, Cl(s1 * 0.7f, 0.25f, 0.55f), 0.83f);
            o[KC0] = Hsl2Rgb(h0, Cl(s0 * 0.75f, 0.3f, 0.6f), 0.80f);
            o[KAccent] = Hsl2Rgb(h0, Cl(s0, 0.4f, 0.75f), 0.42f);
        }
        return o;
    }

    // ---------- Farben aus dem Cover ----------

    private sealed class Bin(int i)
    {
        public readonly int I = i;
        public double W, R, G, B;
    }

    /// <summary>
    /// Die drei kräftigsten, deutlich verschiedenen Farbtöne des Covers, nach Buntheit gewichtet –
    /// so kann ein kleines leuchtendes Detail eine große graue Fläche schlagen. Null bei einfarbigem Cover.
    /// </summary>
    private static Vector3[]? ExtractColors(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4) return null;

        // Wie im Original erst auf 48×48 verkleinern (dort per Canvas, hier per Flächenmittel).
        const int N = 48;
        const int BINS = 18;
        var bins = new Bin[BINS];
        for (int i = 0; i < BINS; i++) bins[i] = new Bin(i);
        double total = 0;

        for (int y = 0; y < N; y++)
        {
            int y0 = y * height / N, y1 = Math.Max(y0 + 1, (y + 1) * height / N);
            for (int x = 0; x < N; x++)
            {
                int x0 = x * width / N, x1 = Math.Max(x0 + 1, (x + 1) * width / N);
                double sb = 0, sg = 0, sr = 0, sa = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * width * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int p = row + xx * 4;
                        double al = bgra[p + 3];
                        // vormultipliziert mitteln, damit durchsichtige Ränder nicht abfärben
                        sb += bgra[p] * al; sg += bgra[p + 1] * al; sr += bgra[p + 2] * al; sa += al;
                    }
                }
                if (sa <= 0) continue; // ganz durchsichtig: im Canvas schwarz, also ohne Buntheit

                // wie getImageData: wieder auf ganze 8-Bit-Werte
                float r = (float)Math.Round(sr / sa) / 255f;
                float g = (float)Math.Round(sg / sa) / 255f;
                float b = (float)Math.Round(sb / sa) / 255f;
                float chroma = MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
                if (chroma < 0.06f) continue;
                var (h, _, l) = Rgb2Hsl(new Vector3(r, g, b));
                double w = chroma * chroma * (l > 0.1f && l < 0.92f ? 1 : 0.2);
                var bin = bins[(int)Math.Floor(h * BINS) % BINS];
                bin.W += w; bin.R += r * w; bin.G += g * w; bin.B += b * w;
                total += w;
            }
        }
        if (total < N * N * 0.004) return null; // praktisch einfarbiges Cover

        var ranked = bins.Where(b => b.W > 0).OrderByDescending(b => b.W).ToList();
        var picked = new List<Bin>(3);
        foreach (var b in ranked)
        {
            if (b.W < ranked[0].W * 0.06) break;
            bool near = picked.Any(p => { int d = Math.Abs(p.I - b.I); return Math.Min(d, BINS - d) < 2; });
            if (!near) picked.Add(b);
            if (picked.Count == 3) break;
        }

        var cols = picked.Select(b => new Vector3((float)(b.R / b.W), (float)(b.G / b.W), (float)(b.B / b.W))).ToList();
        while (cols.Count < 3)
        {
            // zu wenige Farbtöne: Nachbarn des stärksten dazuerfinden
            var (h, s, l) = Rgb2Hsl(cols[0]);
            cols.Add(Hsl2Rgb(h + (cols.Count == 1 ? 0.07f : -0.08f), s, l));
        }
        return [.. cols];
    }
}

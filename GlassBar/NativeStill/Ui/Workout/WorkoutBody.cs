using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui.Workout;

/// <summary>Welche Muskeln eine Auswahl von Übungen trainiert: Primary leuchtet voll, Secondary sanft.</summary>
public sealed class Works
{
    public readonly HashSet<string> Primary = new(), Secondary = new();
    public readonly List<string> PrimaryList = new();
    public string Key = "";
}

/// <summary>
/// Die Körperkarte (workout-body.js): eine durchscheinende, athletische Figur, vorn und hinten. Jeder Muskel ist
/// schwach da, die trainierten leuchten auf, mit sichtbaren Fasern. Formen sind Punktlisten auf der rechten
/// Körperhälfte (x vom Mittelstrich nach außen, y nach unten, auf einem Blatt 200 × 440), zu Kurven geglättet
/// und gespiegelt. Die Geometrien werden einmal je D2D-Fabrik gebaut (Fasern schon geweitet und
/// zugeschnitten), Pinsel einmal je Gerätekontext – pro Bild entsteht nichts Neues.
/// </summary>
public sealed class WorkoutBody : IDisposable
{
    private const float CX = 100;
    /// <summary>Sichtbarer Ausschnitt des Blatts (viewBox 16 0 168 440).</summary>
    public const float ViewX = 16, ViewW = 168, ViewH = 440;

    private static readonly float[,] Outline =
    {
        { 0, 7 }, { 9, 9 }, { 15, 17 }, { 16.5f, 29 }, { 14, 41 }, { 11, 50 }, { 12, 57 }, { 22, 63 }, { 38, 67 }, { 50, 74 }, { 57, 88 }, { 59, 104 },
        { 60, 122 }, { 61, 142 }, { 60, 160 }, { 66, 180 }, { 68, 204 }, { 66, 226 }, { 71, 238 }, { 73, 252 }, { 68, 263 }, { 61, 258 }, { 59, 244 },
        { 55, 228 }, { 50, 204 }, { 45, 178 }, { 42, 160 }, { 40, 138 }, { 37, 113 }, { 38, 128 }, { 35, 150 }, { 28.5f, 178 }, { 30.5f, 200 }, { 37, 222 },
        { 43, 250 }, { 43, 282 }, { 35, 316 }, { 31.5f, 335 }, { 37, 358 }, { 34, 386 }, { 23, 410 }, { 27, 423 }, { 24, 431 }, { 12, 431 }, { 9.5f, 420 },
        { 11, 406 }, { 7.5f, 372 }, { 10.5f, 340 }, { 10.5f, 320 }, { 6, 284 }, { 3, 248 }, { 0, 232 },
    };

    private sealed record Part(float[] Pts, float[] From, float[] To, int N, float Bow);
    private sealed record Muscle(string Side, string Label, Part[] Parts);

    private static Part P(float[] pts, float[] from, float[] to, int n, float bow = 0) => new(pts, from, to, n, bow);

    private static readonly Dictionary<string, Muscle> Muscles = new()
    {
        /* Vorn */
        ["chest"] = new("front", "Brust", [P([1.5f, 81, 14, 74, 30, 76, 41, 85, 38, 102, 24, 113, 9, 113, 1.5f, 104], [1, 76, 1, 118], [42, 84, 42, 92], 15, 2)]),
        ["frontDelts"] = new("front", "Schultern", [P([33, 69, 46, 72, 55, 86, 57, 104, 51, 117, 44, 109, 39, 88], [31, 68, 55, 78], [50, 120, 53, 120], 10, 1)]),
        ["upperTraps"] = new("front", "Trapezmuskel", [P([10, 57, 15, 60, 31, 67, 17, 72, 9, 67], [9, 54, 12, 72], [33, 66, 24, 74], 6, 0)]),
        ["biceps"] = new("front", "Bizeps", [P([41, 117, 50, 114, 58, 130, 58, 150, 52, 160, 44, 158, 41, 140], [41, 112, 54, 112], [46, 162, 50, 162], 9, 1.5f)]),
        ["forearms"] = new("both", "Unterarme", [P([45, 166, 58, 162, 65, 182, 66, 205, 62, 224, 56, 226, 50, 200], [44, 160, 62, 160], [56, 228, 62, 228], 9, 1)]),
        ["abs"] = new("front", "Bauch", [
            P([2, 118, 13, 117, 14, 132, 2, 134], [1, 116, 15, 116], [1, 136, 15, 136], 5),
            P([2, 137, 14, 135, 15, 150, 2, 152], [1, 134, 16, 134], [1, 154, 16, 154], 5),
            P([2, 155, 15, 153, 15, 170, 2, 172], [1, 152, 16, 152], [1, 174, 16, 174], 5),
            P([2, 175, 15, 173, 14, 196, 7, 212, 2, 216], [1, 172, 16, 172], [1, 218, 8, 218], 5),
        ]),
        ["obliques"] = new("front", "Seitliche Bauchmuskeln", [P([18, 120, 34, 118, 35, 142, 29, 176, 30.5f, 196, 18, 206, 17, 170], [34, 114, 36, 170], [14, 150, 14, 210], 10, 0)]),
        ["quads"] = new("front", "Oberschenkelvorderseite", [
            P([27, 228, 38, 226, 42, 256, 40, 290, 31, 316, 27, 296, 27, 258], [30, 224, 40, 224], [28, 318, 31, 318], 7, 2),
            P([15, 226, 25, 226, 26, 260, 26, 298, 21, 316, 15, 298, 14, 260], [15, 222, 26, 222], [20, 318, 22, 318], 7, 0),
            P([9, 280, 14, 284, 20, 312, 15, 326, 11, 320, 8, 300], [8, 276, 16, 280], [13, 328, 17, 328], 5, -1),
        ]),
        ["adductors"] = new("front", "Innere Oberschenkel", [P([3.5f, 240, 12, 226, 12.5f, 256, 8, 276, 5, 262], [3, 226, 13, 224], [6, 280, 9, 280], 5, 0)]),
        ["shins"] = new("front", "Schienbeine", [P([17, 342, 29, 340, 35, 360, 30, 392, 21, 406, 18, 378], [18, 338, 32, 338], [21, 408, 24, 408], 7, 1)]),

        /* Hinten */
        ["traps"] = new("back", "Oberer Rücken", [
            P([1.5f, 58, 11, 59, 31, 69, 23, 84, 11, 104, 3, 128, 1.5f, 128], [1, 56, 1, 130], [33, 68, 26, 86], 15, 0),
            P([25, 87, 37, 84, 39, 98, 29, 109, 16, 106], [14, 104, 24, 84], [39, 92, 39, 100], 7, 0),
        ]),
        ["rearDelts"] = new("back", "Hintere Schultern", [P([33, 69, 46, 72, 55, 86, 57, 104, 51, 115, 43, 104, 38, 84], [31, 68, 55, 78], [50, 118, 53, 118], 10, 1)]),
        ["lats"] = new("back", "Latissimus", [P([13, 110, 27, 112, 38, 106, 37, 130, 31, 160, 16, 180, 10, 150], [8, 130, 12, 186], [39, 104, 39, 112], 13, 2)]),
        ["triceps"] = new("back", "Trizeps", [
            P([41, 113, 50, 111, 50, 134, 47, 158, 42, 142], [40, 110, 50, 110], [46, 160, 48, 160], 5, -1),
            P([52, 111, 59, 126, 59, 148, 53, 160, 49, 158, 52, 134], [51, 108, 60, 120], [49, 160, 53, 162], 5, 1),
        ]),
        ["lowerBack"] = new("back", "Unterer Rücken", [P([2, 136, 9, 136, 12, 176, 14, 198, 3, 208], [1, 132, 9, 132], [2, 210, 14, 204], 6, 0)]),
        ["glutes"] = new("back", "Gesäß", [P([2, 214, 18, 202, 35, 210, 40, 236, 33, 255, 14, 259, 3, 246], [1, 204, 1, 250], [42, 228, 30, 262], 14, 3)]),
        ["hamstrings"] = new("back", "Beinbeuger", [
            P([10, 264, 24, 261, 24, 300, 19, 322, 12, 314, 9, 290], [10, 258, 24, 258], [15, 324, 19, 324], 7, -1),
            P([27, 261, 41, 258, 41, 286, 33, 318, 27, 322, 26, 292], [27, 256, 42, 256], [28, 324, 32, 322], 7, 1.5f),
        ]),
        ["calves"] = new("back", "Waden", [
            P([9, 344, 21, 337, 22, 372, 14, 388, 9, 372], [9, 336, 22, 334], [15, 392, 18, 392], 6, -1),
            P([24, 337, 35, 346, 36, 366, 30, 388, 24, 372], [23, 334, 37, 344], [25, 392, 28, 392], 6, 1),
        ]),
    };

    // Was jede Leiter trainiert: primary leuchtet voll, secondary sanft.
    private static readonly Dictionary<string, (string[] P, string[] S)> WorksOf = new()
    {
        ["push"] = (["chest"], ["frontDelts", "triceps", "abs"]),
        ["wide"] = (["chest"], ["frontDelts", "triceps"]),
        ["close"] = (["triceps"], ["chest", "frontDelts"]),
        ["pike"] = (["frontDelts", "rearDelts"], ["triceps", "traps", "upperTraps"]),
        ["pull"] = (["traps", "rearDelts"], ["lats", "lowerBack"]),
        ["row"] = (["lats", "traps"], ["biceps", "rearDelts", "forearms"]),
        ["curl"] = (["biceps"], ["forearms"]),
        ["squat"] = (["quads", "glutes"], ["hamstrings", "calves", "adductors"]),
        ["hinge"] = (["glutes", "hamstrings"], ["lowerBack"]),
        ["calf"] = (["calves"], ["shins"]),
        ["abs"] = (["abs"], ["obliques"]),
        ["plank"] = (["abs"], ["obliques", "frontDelts", "lowerBack"]),
        ["side"] = (["obliques"], ["abs", "glutes"]),
        ["cJack"] = (["calves", "frontDelts", "rearDelts"], ["quads", "glutes"]),
        ["cBurpee"] = (["quads", "chest", "glutes"], ["frontDelts", "triceps", "abs", "calves"]),
        ["cClimb"] = (["abs", "frontDelts"], ["quads", "chest", "obliques"]),
        ["cLunge"] = (["quads", "glutes"], ["hamstrings", "calves", "adductors"]),
    };

    /// <summary>Welche Muskeln diese Übungen trainieren (Primary schlägt Secondary).</summary>
    public static Works MusclesFor(IEnumerable<string> moves)
    {
        var w = new Works();
        foreach (var id in moves)
        {
            if (!WorksOf.TryGetValue(id, out var x)) continue;
            foreach (var m in x.P) if (w.Primary.Add(m)) w.PrimaryList.Add(m);
            foreach (var m in x.S) w.Secondary.Add(m);
        }
        w.Secondary.ExceptWith(w.Primary);
        w.Key = string.Join(",", w.PrimaryList) + "|" + string.Join(",", w.Secondary.OrderBy(s => s, StringComparer.Ordinal));
        return w;
    }

    public static string Label(string muscle) => Muscles.TryGetValue(muscle, out var m) ? L.T(m.Label) : muscle;

    /* ---------- Geometrie (je Fabrik) ---------- */

    private sealed class Shape
    {
        public string Muscle = "";
        public ID2D1Geometry Fill = null!;
        public ID2D1Geometry Fibers = null!;
        public float Top, Bottom;
    }

    private nint _factoryPtr;
    private ID2D1PathGeometry? _body;
    private readonly List<Shape>[] _shapes = [new(), new()]; // 0 = vorn, 1 = hinten

    // Maske für den Lichtstreifen: alle Primärmuskeln einer Ansicht.
    private string _maskKey = "";
    private readonly ID2D1GeometryGroup?[] _mask = new ID2D1GeometryGroup?[2];
    private readonly float[] _maskTop = new float[2], _maskBottom = new float[2];

    private static Vector2 Right(float x, float y) => new(CX + x, y);
    private static Vector2 Left(float x, float y) => new(CX - x, y);

    private static ID2D1PathGeometry Smooth(ID2D1Factory f, Vector2[] p)
    {
        var g = f.CreatePathGeometry();
        using var s = g.Open();
        int n = p.Length;
        s.BeginFigure(p[0], FigureBegin.Filled);
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
            s.AddBezier(new BezierSegment { Point1 = p1 + (p2 - p0) / 6f, Point2 = p2 - (p3 - p1) / 6f, Point3 = p2 });
        }
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return g;
    }

    private static ID2D1PathGeometry Combine(ID2D1Factory f, ID2D1Geometry a, ID2D1Geometry b, CombineMode mode)
    {
        var g = f.CreatePathGeometry();
        using var s = g.Open();
        a.CombineWithGeometry(b, mode, s);
        s.Close();
        return g;
    }

    // Die Fasern eines Teils: für jede eine leicht gebogene Linie, geweitet zu einer Fläche.
    private static ID2D1PathGeometry Fibers(ID2D1Factory f, Part p, bool right)
    {
        Vector2 Place(Vector2 v) => right ? Right(v.X, v.Y) : Left(v.X, v.Y);
        using var lines = f.CreatePathGeometry();
        using (var s = lines.Open())
        {
            for (int i = 0; i < p.N; i++)
            {
                float t = p.N > 1 ? i / (float)(p.N - 1) : 0.5f;
                var a = new Vector2(p.From[0] + (p.From[2] - p.From[0]) * t, p.From[1] + (p.From[3] - p.From[1]) * t);
                var b = new Vector2(p.To[0] + (p.To[2] - p.To[0]) * t, p.To[1] + (p.To[3] - p.To[1]) * t);
                var mid = (a + b) / 2;
                var d = b - a;
                float l = d.Length();
                if (l == 0) l = 1;
                var c = new Vector2(mid.X - d.Y / l * p.Bow * 2, mid.Y + d.X / l * p.Bow * 2);
                s.BeginFigure(Place(a), FigureBegin.Hollow);
                s.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = Place(c), Point2 = Place(b) });
                s.EndFigure(FigureEnd.Open);
            }
            s.Close();
        }
        var wide = f.CreatePathGeometry();
        using (var s = wide.Open())
        {
            lines.Widen(0.55f, s);
            s.Close();
        }
        return wide;
    }

    private void EnsureGeometry(ID2D1Factory f)
    {
        if (_factoryPtr == f.NativePointer && _body != null) return;
        DisposeGeometry();
        _factoryPtr = f.NativePointer;

        int n = Outline.GetLength(0);
        var pts = new List<Vector2>();
        for (int i = 0; i < n; i++) pts.Add(Right(Outline[i, 0], Outline[i, 1]));
        for (int i = n - 2; i >= 1; i--) pts.Add(Left(Outline[i, 0], Outline[i, 1]));
        _body = Smooth(f, pts.ToArray());

        foreach (var (name, m) in Muscles)
            foreach (var part in m.Parts)
                foreach (bool right in new[] { true, false })
                {
                    var pp = new Vector2[part.Pts.Length / 2];
                    float top = float.MaxValue, bottom = float.MinValue;
                    for (int i = 0; i < pp.Length; i++)
                    {
                        float x = part.Pts[2 * i], y = part.Pts[2 * i + 1];
                        pp[i] = right ? Right(x, y) : Left(x, y);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                    using var raw = Smooth(f, pp);
                    var fill = Combine(f, raw, _body, CombineMode.Intersect);
                    using var fib = Fibers(f, part, right);
                    var fibers = Combine(f, fib, fill, CombineMode.Intersect);
                    var shape = new Shape { Muscle = name, Fill = fill, Fibers = fibers, Top = top, Bottom = bottom };
                    if (m.Side != "back") _shapes[0].Add(shape);
                    if (m.Side != "front")
                    {
                        if (m.Side == "both")
                        {
                            // Eigene Kopie für hinten, damit jede Geometrie genau einmal freigegeben wird.
                            var fill2 = Combine(f, raw, _body, CombineMode.Intersect);
                            var fibers2 = Combine(f, fib, fill2, CombineMode.Intersect);
                            _shapes[1].Add(new Shape { Muscle = name, Fill = fill2, Fibers = fibers2, Top = top, Bottom = bottom });
                        }
                        else _shapes[1].Add(shape);
                    }
                }
    }

    private void EnsureMask(ID2D1Factory f, Works w)
    {
        if (_maskKey == w.Key && (_mask[0] != null || _mask[1] != null || w.Primary.Count == 0)) return;
        for (int v = 0; v < 2; v++) { _mask[v]?.Dispose(); _mask[v] = null; }
        _maskKey = w.Key;
        for (int v = 0; v < 2; v++)
        {
            var list = new List<ID2D1Geometry>();
            float top = float.MaxValue, bottom = float.MinValue;
            foreach (var s in _shapes[v])
            {
                if (!w.Primary.Contains(s.Muscle)) continue;
                list.Add(s.Fill);
                top = Math.Min(top, s.Top);
                bottom = Math.Max(bottom, s.Bottom);
            }
            if (list.Count == 0) continue;
            _mask[v] = f.CreateGeometryGroup(FillMode.Winding, list.ToArray());
            _maskTop[v] = top;
            _maskBottom[v] = bottom;
        }
    }

    /* ---------- Pinsel (je Gerätekontext) ---------- */

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1LinearGradientBrush? _bodyFill, _bodyStroke, _band;
    private Vector3 _brushFg = new(-1);

    private void EnsureBrushes(ID2D1DeviceContext ctx, Vector3 fg)
    {
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            DisposeBrushes();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            _band = Gradient(ctx, new Vector3(1, 1, 1), [(0, 0), (0.5f, 0.95f), (1, 0)], Vector2.Zero, new Vector2(70, 100));
        }
        if (_brushFg != fg)
        {
            _brushFg = fg;
            _bodyFill?.Dispose();
            _bodyStroke?.Dispose();
            // Wie im SVG: Verläufe über den Umriss (objectBoundingBox ≈ 27..173 × 7..431).
            _bodyFill = Gradient(ctx, fg, [(0, 0.2f), (0.55f, 0.09f), (1, 0.14f)], new Vector2(100, 7), new Vector2(100, 431));
            _bodyStroke = Gradient(ctx, fg, [(0, 0.6f), (0.5f, 0.16f), (1, 0.36f)], new Vector2(27, 7), new Vector2(173, 431));
        }
    }

    private static ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext ctx, Vector3 c, (float At, float A)[] stops, Vector2 a, Vector2 b)
    {
        var gs = new GradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++) gs[i] = new GradientStop { Position = stops[i].At, Color = new Color4(c.X, c.Y, c.Z, stops[i].A) };
        using var coll = ctx.CreateGradientStopCollection(gs);
        return ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = a, EndPoint = b }, coll);
    }

    private ID2D1SolidColorBrush Solid(Vector3 c, float a)
    {
        _brush!.Color = new Color4(c.X, c.Y, c.Z, a);
        return _brush;
    }

    /* ---------- Zeichnen ---------- */

    /// <summary>Breite des Paars (vorn + hinten) bei gegebener Höhe.</summary>
    public static float PairWidth(float height, float gap) => 2 * height * ViewW / ViewH + gap;

    /// <summary>
    /// Vorn und hinten nebeneinander, oben links bei (x, y), Höhe height. fibers = Fasern zeigen (zu klein auf der
    /// Karte), sweep = Phase 0..1 des Lichtstreifens oder &lt; 0 für keinen, alpha = Deckkraft des Ganzen.
    /// </summary>
    public void DrawPair(ID2D1DeviceContext ctx, UiContext c, float x, float y, float height, float gap, Works w, bool fibers, float sweep, float alpha)
    {
        if (alpha <= 0.002f) return;
        EnsureGeometry(c.D2D);
        EnsureBrushes(ctx, c.Fg);
        EnsureMask(c.D2D, w);
        float s = height / ViewH;
        float sideW = ViewW * s;
        var saved = ctx.Transform;
        for (int v = 0; v < 2; v++)
        {
            float ox = x + v * (sideW + gap);
            ctx.Transform = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(ox - ViewX * s, y) * saved;
            DrawSide(ctx, c, v, w, fibers, sweep, alpha);
        }
        ctx.Transform = saved;
    }

    private void DrawSide(ID2D1DeviceContext ctx, UiContext c, int v, Works w, bool fibers, float sweep, float alpha)
    {
        var fg = c.Fg;
        var fiber = c.LightTheme ? new Vector3(232, 236, 245) / 255f : new Vector3(40, 52, 84) / 255f;
        _bodyFill!.Opacity = alpha;
        ctx.FillGeometry(_body!, _bodyFill);
        var shapes = _shapes[v];
        bool working = sweep >= 0;

        // Ruhend, dann sanft (secondary), dann die leuchtenden (primary) obenauf.
        for (int level = 0; level < 3; level++)
        {
            foreach (var sh in shapes)
            {
                int lv = w.Primary.Contains(sh.Muscle) ? 2 : w.Secondary.Contains(sh.Muscle) ? 1 : 0;
                if (lv != level) continue;
                switch (lv)
                {
                    case 0:
                        ctx.FillGeometry(sh.Fill, Solid(fg, 0.07f * alpha));
                        if (fibers) ctx.FillGeometry(sh.Fibers, Solid(fg, 0.045f * alpha));
                        ctx.DrawGeometry(sh.Fill, Solid(fg, 0.13f * alpha), 0.5f);
                        break;
                    case 1:
                        ctx.FillGeometry(sh.Fill, Solid(fg, 0.3f * alpha));
                        if (fibers) ctx.FillGeometry(sh.Fibers, Solid(fiber, 0.16f * alpha));
                        ctx.DrawGeometry(sh.Fill, Solid(fg, 0.3f * alpha), 0.5f);
                        break;
                    default:
                        // Ein weicher Schein um die trainierten Muskeln (statt drop-shadow).
                        ctx.DrawGeometry(sh.Fill, Solid(fg, 0.06f * alpha), 7f);
                        ctx.DrawGeometry(sh.Fill, Solid(fg, 0.09f * alpha), 3.5f);
                        ctx.FillGeometry(sh.Fill, Solid(fg, (working ? 0.48f : 0.9f) * alpha));
                        if (fibers) ctx.FillGeometry(sh.Fibers, Solid(fiber, 0.3f * alpha));
                        ctx.DrawGeometry(sh.Fill, Solid(fiber, 0.5f * alpha), 0.7f);
                        break;
                }
            }
        }
        _bodyStroke!.Opacity = alpha;
        ctx.DrawGeometry(_body!, _bodyStroke, 1f);

        // Der Lichtstreifen: von knapp über den Muskeln bis knapp darunter, egal wie groß sie sind.
        if (working && _mask[v] is { } mask)
        {
            float top = _maskTop[v], bottom = _maskBottom[v];
            float band = Math.Max(44, (bottom - top) * 0.7f), travel = bottom - top + band;
            float e = sweep < 0.5f ? 2 * sweep * sweep : 1 - MathF.Pow(-2 * sweep + 2, 2) / 2;
            float y0 = top - band + travel * e;
            _band!.StartPoint = new Vector2(0, y0);
            _band.EndPoint = new Vector2(70, y0 + band);
            _band.Opacity = alpha;
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Rect(0, 0, 200, ViewH),
                GeometricMask = mask,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                LayerOptions = LayerOptions1.None,
            }, null!);
            ctx.FillRectangle(new Rect(0, y0, 200, band), _band);
            ctx.PopLayer();
            if (fibers)
                foreach (var sh in shapes)
                    if (w.Primary.Contains(sh.Muscle)) ctx.FillGeometry(sh.Fibers, Solid(fiber, 0.3f * alpha));
        }
    }

    private void DisposeGeometry()
    {
        for (int v = 0; v < 2; v++) { _mask[v]?.Dispose(); _mask[v] = null; }
        _maskKey = "";
        var seen = new HashSet<ID2D1Geometry>(ReferenceEqualityComparer.Instance);
        foreach (var list in _shapes)
        {
            foreach (var s in list)
            {
                if (seen.Add(s.Fill)) s.Fill.Dispose();
                if (seen.Add(s.Fibers)) s.Fibers.Dispose();
            }
            list.Clear();
        }
        _body?.Dispose();
        _body = null;
        _factoryPtr = 0;
    }

    private void DisposeBrushes()
    {
        _brush?.Dispose(); _brush = null;
        _bodyFill?.Dispose(); _bodyFill = null;
        _bodyStroke?.Dispose(); _bodyStroke = null;
        _band?.Dispose(); _band = null;
        _brushFg = new Vector3(-1);
        _ctxPtr = 0;
    }

    public void Dispose()
    {
        DisposeBrushes();
        DisposeGeometry();
    }
}

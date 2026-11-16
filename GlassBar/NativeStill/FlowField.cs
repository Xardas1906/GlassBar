namespace GlassBar.NativeStill;

/// <summary>
/// Die „Wasser“-Strömung der Maus (Port aus app.js: flow, flowResize, flowSplat, flowStep, flowBloom, updateFlow
/// samt Maus-/Klick-Behandlung): ein kleines Raster (RGBA float, r/g = Schub in Bildschirmanteilen, y nach unten,
/// b = Leuchten, a ungenutzt), das der Shader als Textur liest. Läuft komplett im Render-Thread; Pointer/Click dürfen
/// aus jedem Thread kommen (werden intern gepuffert und erst in <see cref="Step"/> verarbeitet).
/// </summary>
public sealed class FlowField
{
    // 80 Zellen in der Breite, wie im Original.
    private const int FlowCols = 80;
    // Höchstens so viele Ereignisse pro Bild puffern; danach werden Mausbewegungen zusammengefasst.
    private const int QueueCapacity = 512;

    private enum EventKind : byte { Move, Bloom }

    private struct FlowEvent
    {
        public EventKind Kind;
        public float X, Y, Scale;
    }

    /// <summary>Spalten/Zeilen des Rasters.</summary>
    public int Cols { get; private set; } = 2;
    public int Rows { get; private set; } = 2;
    /// <summary>Cols*Rows*4 Werte (r, g, b, a) zeilenweise von oben nach unten.</summary>
    public float[] Data { get; private set; } = new float[16];
    /// <summary>Gerade in Bewegung (der Shader soll die Strömung nutzen).</summary>
    public bool Active { get; private set; }

    // Zweiter Puffer für den Weichzeichner (wird mit Data getauscht).
    private float[] _tmp = new float[16];
    private int _width = 1, _height = 1;
    private double _acc;
    private bool _hasLast;
    private float _lastX, _lastY;
    private bool _dirty = true;

    // Doppelt gepufferte Ereignisliste: Schreiber füllen _incoming, Step tauscht und arbeitet _working ab.
    private readonly object _queueLock = new();
    private FlowEvent[] _incoming = new FlowEvent[QueueCapacity];
    private FlowEvent[] _working = new FlowEvent[QueueCapacity];
    private int _incomingCount;

    /// <summary>Bildschirmgröße in Pixeln (legt das Raster fest).</summary>
    public void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        int cols = FlowCols;
        int rows = Math.Max(8, (int)Math.Floor(FlowCols * (double)Math.Max(0, height) / Math.Max(1, width) + 0.5));
        if (cols != Cols || rows != Rows || Data.Length != cols * rows * 4)
        {
            Cols = cols;
            Rows = rows;
            Data = new float[cols * rows * 4];
            _tmp = new float[cols * rows * 4];
        }
        else
        {
            Array.Clear(Data);
            Array.Clear(_tmp);
        }
        Active = false;
        _hasLast = false;
        _dirty = true;
    }

    /// <summary>Mausbewegung in Pixeln relativ zum Hintergrundfenster.</summary>
    public void Pointer(float x, float y)
    {
        lock (_queueLock)
        {
            if (_incomingCount < QueueCapacity)
            {
                _incoming[_incomingCount++] = new FlowEvent { Kind = EventKind.Move, X = x, Y = y };
            }
            else if (_incoming[QueueCapacity - 1].Kind == EventKind.Move)
            {
                // Puffer voll: die letzte Bewegung einfach weiterziehen.
                _incoming[QueueCapacity - 1].X = x;
                _incoming[QueueCapacity - 1].Y = y;
            }
        }
    }

    /// <summary>Klick (Einstellung waterClick): ein Ring/Aufblühen an der Stelle.</summary>
    public void Click(float x, float y) => Enqueue(EventKind.Bloom, x, y, 1f, clickOnly: true);

    /// <summary>
    /// Aufblühen an einer Stelle (wie flowBloom im Original, z. B. 1.6 für eine erledigte Aufgabe).
    /// Hängt nicht an waterClick, nur an water.
    /// </summary>
    public void Bloom(float x, float y, float scale) => Enqueue(EventKind.Bloom, x, y, scale, clickOnly: false);

    private void Enqueue(EventKind kind, float x, float y, float scale, bool clickOnly)
    {
        lock (_queueLock)
        {
            if (_incomingCount >= QueueCapacity) return;
            // Klicks merken sich über ein negatives Maß, dass waterClick gilt (wird in Step geprüft).
            _incoming[_incomingCount++] = new FlowEvent { Kind = kind, X = x, Y = y, Scale = clickOnly ? -scale : scale };
        }
    }

    /// <summary>Einen Schritt simulieren. Liefert true, wenn sich Data geändert hat (neu hochladen).</summary>
    public bool Step(double dt, StillValues settings)
    {
        int count;
        lock (_queueLock)
        {
            (_incoming, _working) = (_working, _incoming);
            count = _incomingCount;
            _incomingCount = 0;
        }

        bool water = settings.Bool("water");
        if (water && count > 0)
        {
            double strength = settings.Num("waterStrength");
            double size = settings.Num("waterSize");
            double shine = settings.Num("waterShine");
            bool clickOn = settings.Bool("waterClick");
            for (int k = 0; k < count; k++)
            {
                ref readonly var e = ref _working[k];
                if (e.Kind == EventKind.Move) Move(e.X, e.Y, strength, size, shine);
                else if (e.Scale > 0 || clickOn) BloomAt(e.X, e.Y, Math.Abs(e.Scale), strength, size, shine);
            }
        }

        UpdateFlow(dt, settings, water);

        bool changed = _dirty;
        _dirty = false;
        return changed;
    }

    // Bildschirmpixel in Rasterkoordinaten.
    private double ToGridX(double x) => x / _width * Cols;
    private double ToGridY(double y) => y / _height * Rows;

    private static double FlowRadius(double size) => 2.2 + size * 4.8; // in Zellen

    // Bewegung zieht das Licht den Weg entlang, mit einem leisen Schimmer dahinter.
    private void Move(float x, float y, double strength, double size, double shine)
    {
        double gx = ToGridX(x), gy = ToGridY(y);
        bool had = _hasLast;
        double lx = _lastX, ly = _lastY;
        _lastX = (float)gx;
        _lastY = (float)gy;
        _hasLast = true;
        if (!had) return;
        double dx = gx - lx, dy = gy - ly, dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.02) return;
        double s = strength * 1.4;
        double glow = Math.Min(0.6, dist * 0.22) * shine;
        double r = FlowRadius(size);
        int n = (int)Math.Min(8, Math.Max(1, Math.Ceiling(dist / (r * 0.5))));
        for (int k = 1; k <= n; k++)
        {
            Splat(lx + dx * k / n, ly + dy * k / n, r,
                dx / Cols * s / n, dy / Rows * s / n, glow / n, radial: false, strength);
        }
    }

    // Ein Aufblühen von Licht, das von einem Punkt nach außen schwillt.
    private void BloomAt(float x, float y, double scale, double strength, double size, double shine)
    {
        Splat(ToGridX(x), ToGridY(y), FlowRadius(size) * 1.5 * scale, (0.03 + 0.04 * strength) * scale, 0,
            (0.7 + 0.8 * shine) * scale, radial: true, strength);
    }

    // Ein weicher Gauß-Fleck aus Schub (in Bildschirmanteilen) und Leuchten. Mit radial zeigt der Schub
    // vom Mittelpunkt weg, das liest sich als Aufblühen.
    private void Splat(double gx, double gy, double radius, double px, double py, double glow, bool radial, double strength)
    {
        int cols = Cols, rows = Rows;
        var f = Data;
        double r2 = radius * radius, reach = radius * 2.2;
        double maxPush = 0.06 + 0.09 * strength;
        int x0 = (int)Math.Max(0, Math.Floor(gx - reach)), x1 = (int)Math.Min(cols - 1, Math.Ceiling(gx + reach));
        int y0 = (int)Math.Max(0, Math.Floor(gy - reach)), y1 = (int)Math.Min(rows - 1, Math.Ceiling(gy + reach));
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - gx, dy = y + 0.5 - gy, d2 = dx * dx + dy * dy;
                double w = Math.Exp(-d2 / r2);
                if (w < 0.004) continue;
                int i = (y * cols + x) * 4;
                double fx = f[i], fy = f[i + 1];
                if (radial)
                {
                    double d = Math.Sqrt(d2);
                    if (d == 0) d = 1;
                    fx += dx / d * px * w;
                    fy += dy / d * px * w;
                }
                else
                {
                    fx += px * w;
                    fy += py * w;
                }
                // Den Schub sanft halten, egal wie lange die Maus sich bewegt.
                double m = Math.Sqrt(fx * fx + fy * fy);
                if (m > maxPush)
                {
                    fx *= maxPush / m;
                    fy *= maxPush / m;
                }
                f[i] = (float)fx;
                f[i + 1] = (float)fy;
                f[i + 2] = (float)Math.Min(1.2, f[i + 2] + glow * w);
            }
        }
        Active = true;
    }

    // Ein wenig weichzeichnen und verblassen: verschobenes Licht verteilt sich, wird weicher und kehrt heim.
    private double FlowStep(float keepPush, float keepGlow)
    {
        int cols = Cols, rows = Rows;
        float[] a = Data, b = _tmp;
        const float soft = 0.22f;
        double energy = 0;
        for (int y = 0; y < rows; y++)
        {
            int row = y * cols;
            int up = (y > 0 ? y - 1 : y) * cols, dn = (y < rows - 1 ? y + 1 : y) * cols;
            for (int x = 0; x < cols; x++)
            {
                int l = x > 0 ? x - 1 : x, r = x < cols - 1 ? x + 1 : x;
                int i = (row + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float v = a[i + c];
                    float avg = (a[(row + l) * 4 + c] + a[(row + r) * 4 + c] + a[(up + x) * 4 + c] + a[(dn + x) * 4 + c]) * 0.25f;
                    float nv = (v + (avg - v) * soft) * (c < 2 ? keepPush : keepGlow);
                    b[i + c] = nv;
                    energy += nv < 0 ? -nv : nv;
                }
            }
        }
        Data = b;
        _tmp = a;
        return energy;
    }

    private void UpdateFlow(double dt, StillValues settings, bool water)
    {
        if (!Active) return;
        if (!water)
        {
            Array.Clear(Data);
            Active = false;
            _dirty = true;
            return;
        }
        // Feste Schritte halten die Ruhezeit bei jeder Bildrate gleich. Im Standard halbieren sich Schub und
        // Leuchten etwa alle 0,4 s: langsam genug für Flüssiges, schnell genug für baldige Ruhe.
        double decay = settings.Num("waterDecay");
        float keepPush = (float)(0.955 + (0.99 - 0.955) * decay);
        float keepGlow = (float)(0.95 + (0.988 - 0.95) * decay);
        _acc = Math.Min(_acc + Math.Max(0, dt) * 60, 6);
        double energy = double.PositiveInfinity;
        int steps = 0;
        while (_acc >= 1)
        {
            energy = FlowStep(keepPush, keepGlow);
            _acc -= 1;
            steps++;
        }
        if (steps > 0) _dirty = true;
        if (steps > 0 && energy < 1e-4 * Cols * Rows)
        {
            Array.Clear(Data);
            Active = false;
        }
    }
}

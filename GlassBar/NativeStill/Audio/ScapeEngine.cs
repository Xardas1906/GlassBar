namespace GlassBar.NativeStill.Audio;

// =====================================================================================================
// Die Klangwelten selbst (Port von soundscape.js, scapeEngine): Regen, Bach, Wind, Wellen, Feuer und
// braunes Rauschen, live erzeugt – keine Aufnahmen, nichts wiederholt sich. Jeder Klang ist nach seiner
// Entstehung gebaut (Tropfen als angeregte Resonanzen, Blasen nach van den Doel, Feuer nach Farnell …).
// Läuft ausschließlich im Audio-Thread; von außen kommen nur Pegel (Target) und Abstimmungen (Post).
// Übernommen ist jeweils die Abstimmung „still“, die das Original verwendet.
// =====================================================================================================

/// <summary>Abstimmung aus den erweiterten Einstellungen (wird im UI-Thread berechnet, im Audio-Thread übernommen).</summary>
internal sealed record ScapeTunes(
    double RainBed, double RainAir, double RainMuffle, double RainGain, bool RainDynamic,
    double StreamRate, double StreamGlugRate, double StreamRush, double StreamDeep, double StreamBurst, double StreamDistance, double StreamGain,
    double WindGusts, double WindRumbleF, double WindDistance, double WindGain,
    double WavesSizeMin, double WavesSizeMax, double WavesGapMin, double WavesGapMax, double WavesSpray, double WavesRumble, double WavesSea, double WavesDistance, double WavesGain,
    double BrownDark, double BrownGain);

internal sealed class ScapeEngine
{
    public const int Block = 128;
    public const int Count = 6;
    private const double TAU = Math.PI * 2;

    /// <summary>Verstärkung je Klang bei voller Stufe (gemessen, alle bei etwa −18 LUFS).</summary>
    private static readonly double[] Norm = { 0.582, 0.977, 1.245, 1.778, 1.189, 0.537 };

    public readonly double Sr;
    private uint _seed;

    /// <summary>Zielpegel je Klang 0..1 (rain, stream, wind, waves, fire, brown); wird sanft angesteuert.</summary>
    public readonly double[] Target = new double[Count];
    private readonly double[] _cur = new double[Count];
    private readonly double[] _tl = new double[Block], _tr = new double[Block];
    private ScapeTunes? _pending;

    private readonly Rain _rain;
    private readonly Stream _stream;
    private readonly Wind _wind;
    private readonly Waves _waves;
    private readonly Fire _fire;
    private readonly BrownNoise _brown;
    private readonly Layer[] _layers;

    public ScapeEngine(int sampleRate)
    {
        Sr = sampleRate;
        _seed = (uint)Random.Shared.Next(1, int.MaxValue);
        _rain = new Rain(this);
        _stream = new Stream(this);
        _wind = new Wind(this);
        _waves = new Waves(this);
        _fire = new Fire(this);
        _brown = new BrownNoise(this);
        _layers = new Layer[] { _rain, _stream, _wind, _waves, _fire, _brown };
    }

    /// <summary>Wie weit die dynamische Intensität den Regen gerade trägt (−0,7 … 1).</summary>
    public double Surge => _rain.SurgeValue;

    /// <summary>Neue Abstimmung (aus jedem Thread); gilt ab dem nächsten Block.</summary>
    public void Post(ScapeTunes t) => Volatile.Write(ref _pending, t);

    /// <summary>Pegel sofort auf null (nur wenn der Audio-Thread steht).</summary>
    public void ResetLevels() => Array.Clear(_cur);

    // ---------- Zufall ----------

    public double Rnd()
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return _seed / 4294967296.0;
    }
    public double Wn() => Rnd() * 2 - 1;
    public double Range(double a, double b) => a + (b - a) * Rnd();
    public double Logr(double a, double b) => a * Math.Pow(b / a, Rnd());
    public static double Db(double d) => Math.Pow(10, d / 20);
    public static double PanL(double p) => Math.Cos((p + 1) * Math.PI / 4);
    public static double PanR(double p) => Math.Sin((p + 1) * Math.PI / 4);
    /// <summary>Samples bis zum nächsten Ereignis eines zufälligen (Poisson-)Stroms mit <paramref name="rate"/> pro Sekunde.</summary>
    public double Wait(double rate) => rate > 1e-6 ? (-Math.Log(1 - Rnd()) / rate) * Sr : 1e12;

    // ---------- Mischen ----------

    private static double Soft(double x) =>
        x > 0.75 ? 0.75 + 0.25 * Math.Tanh((x - 0.75) / 0.25)
        : x < -0.75 ? -0.75 + 0.25 * Math.Tanh((x + 0.75) / 0.25) : x;

    /// <summary>Einen Block (n ≤ 128) in L/R schreiben (überschreibt). Rückgabe: klang etwas?</summary>
    public bool Render(double[] L, double[] R, int n)
    {
        var t = Interlocked.Exchange(ref _pending, null);
        if (t != null) Apply(t);

        Array.Clear(L, 0, n);
        Array.Clear(R, 0, n);
        bool any = false;
        double ease = 1 - Math.Exp(-n / (0.4 * Sr));
        for (int k = 0; k < Count; k++)
        {
            double target = Math.Clamp(Target[k], 0, 1), from = _cur[k];
            double to = from + (target - from) * ease;
            if (Math.Abs(to - target) < 1e-4) to = target;
            _cur[k] = to;
            if (from < 1e-4 && to < 1e-4) continue;
            any = true;
            Array.Clear(_tl, 0, n);
            Array.Clear(_tr, 0, n);
            _layers[k].Run(_tl, _tr, n);
            // Die Stufe geht im Quadrat ein: die Mitte des Reglers klingt wie die Mitte.
            double g0 = from * from * Norm[k], g1 = to * to * Norm[k], dg = (g1 - g0) / n, g = g0;
            for (int i = 0; i < n; i++, g += dg) { L[i] += _tl[i] * g; R[i] += _tr[i] * g; }
        }
        if (any) for (int i = 0; i < n; i++) { L[i] = Soft(L[i]); R[i] = Soft(R[i]); }
        return any;
    }

    private void Apply(ScapeTunes t)
    {
        _rain.Tune(t);
        _stream.Tune(t);
        _wind.Tune(t);
        _waves.Tune(t);
        _brown.Tune(t);
    }

    // =================================================================================================
    // Bausteine
    // =================================================================================================

    /// <summary>RBJ-Biquad: Tiefpass, Hochpass oder Bandpass (0 dB Spitze).</summary>
    internal sealed class BQ
    {
        public const int LP = 0, HP = 1, BP = 2;
        private readonly int _type;
        private readonly double _sr;
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public BQ(ScapeEngine e, int type, double f, double q) { _sr = e.Sr; _type = type; Tune(f, q); }

        public void Tune(double f, double q)
        {
            double w = TAU * Math.Min(f, _sr * 0.45) / _sr, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            if (_type == LP) { _b1 = (1 - cs) / a0; _b0 = _b2 = _b1 / 2; }
            else if (_type == HP) { _b1 = -(1 + cs) / a0; _b0 = _b2 = -_b1 / 2; }
            else { _b0 = al / a0; _b1 = 0; _b2 = -_b0; }
            _a1 = -2 * cs / a0;
            _a2 = (1 - al) / a0;
        }

        public double Run(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    /// <summary>Rosa Rauschen (Paul Kellets verfeinerter Filter).</summary>
    internal sealed class Pink(ScapeEngine e)
    {
        private double _b0, _b1, _b2, _b3, _b4, _b5, _b6;
        public double Run()
        {
            double w = e.Wn();
            _b0 = 0.99886 * _b0 + w * 0.0555179;
            _b1 = 0.99332 * _b1 + w * 0.0750759;
            _b2 = 0.969 * _b2 + w * 0.153852;
            _b3 = 0.8665 * _b3 + w * 0.3104856;
            _b4 = 0.55 * _b4 + w * 0.5329522;
            _b5 = -0.7616 * _b5 - w * 0.016898;
            double y = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + w * 0.5362;
            _b6 = w * 0.115926;
            return y * 0.11;
        }
    }

    /// <summary>Braunes Rauschen: ein sanft auslaufender Zufallsweg.</summary>
    internal sealed class Brown(ScapeEngine e)
    {
        private double _y;
        public double Run() { _y = (_y + 0.02 * e.Wn()) / 1.02; return _y * 3.5; }
    }

    /// <summary>Ein weicher Zufallswert 0..1, der etwa <see cref="Rate"/>-mal pro Sekunde wandert.</summary>
    internal sealed class Drift
    {
        private readonly ScapeEngine _e;
        public double Rate;
        private double _a, _b, _t;
        public Drift(ScapeEngine e, double rate) { _e = e; Rate = rate; _a = e.Rnd(); _b = e.Rnd(); _t = e.Rnd(); }
        public double Step(double dt)
        {
            _t += dt * Rate;
            while (_t >= 1) { _t -= 1; _a = _b; _b = _e.Rnd(); }
            double s = _t * _t * (3 - 2 * _t);
            return _a + (_b - _a) * s;
        }
    }

    /// <summary>Aufschläge, zu viele und zu klein zum Unterscheiden: zufällige Klicks in ein paar Resonanzen.</summary>
    internal sealed class Patter
    {
        private readonly ScapeEngine _e;
        public readonly BQ[] F;
        public Patter(ScapeEngine e, double[] freqs, double[] qs)
        {
            _e = e;
            F = new BQ[freqs.Length];
            for (int i = 0; i < F.Length; i++) F[i] = new BQ(e, BQ.BP, freqs[i], qs[i]);
        }
        public double Run(double p)
        {
            int n = F.Length, hit = -1;
            double a = 0;
            if (_e.Rnd() < p) { hit = (int)(_e.Rnd() * n); double m = _e.Rnd(); a = (_e.Rnd() < 0.5 ? -1 : 1) * m * m; }
            double y = 0;
            for (int j = 0; j < n; j++) y += F[j].Run(j == hit ? a : 0);
            return y;
        }
    }

    // ---------- Stimmen: kurze Ereignisse mit eigenem Leben ----------

    /// <summary>Ein Rauschstoß, der in einer Resonanz nachklingt: Tropfen auf einem Blatt, ein Knacken, ein Kiesel.</summary>
    internal sealed class Tick
    {
        public double B0, A1, A2, Z1, Z2, Env, K, Gl, Gr;
        public int N, At;
        public void Start(ScapeEngine e, int at, double f, double q, double decay, double amp, double p)
        {
            double w = TAU * Math.Min(f, e.Sr * 0.45) / e.Sr, al = Math.Sin(w) / (2 * q), a0 = 1 + al;
            B0 = al / a0; A1 = -2 * Math.Cos(w) / a0; A2 = (1 - al) / a0;
            Z1 = Z2 = 0;
            Env = amp;
            K = Math.Exp(-1 / (decay * e.Sr));
            // der Stoß, dann das Ausklingen der Resonanz
            N = (int)Math.Ceiling((7 * decay + (7 * q) / (Math.PI * f)) * e.Sr);
            Gl = PanL(p); Gr = PanR(p);
            At = at;
        }
    }

    /// <summary>
    /// Eine Blase (van den Doel 2005; Moss et al. 2010): klingt bei Minnaerts Frequenz f0 ≈ 3 / Radius,
    /// verklingt mit β = 0,043 f0 + 0,0014 f0^1,5 pro Sekunde und steigt beim Auftauchen: f(t) = f0 (1 + ξβt).
    /// </summary>
    internal sealed class Bubble
    {
        public double Ph, W, Dw, Amp, K, Gl, Gr;
        public int N, At;
        public void Start(ScapeEngine e, int at, double f0, double amp, double xi, double p)
        {
            double sr = e.Sr;
            f0 = Math.Min(f0, sr * 0.42);
            double beta = 0.043 * f0 + 0.0014 * Math.Pow(f0, 1.5);
            Ph = 0;
            W = TAU * f0 / sr;
            Dw = (TAU * f0 * xi * beta) / (sr * sr);
            Amp = amp;
            K = Math.Exp(-beta / sr);
            N = (int)Math.Ceiling((6.9 / beta) * sr);
            Gl = PanL(p); Gr = PanR(p);
            At = at;
        }
    }

    internal sealed class Pool<T> where T : class, new()
    {
        public readonly T[] Act;
        public int Count;
        private readonly T[] _free;
        private int _freeN;
        public Pool(int max)
        {
            Act = new T[max];
            _free = new T[max];
            for (int i = 0; i < max; i++) _free[i] = new T();
            _freeN = max;
        }
        public T? Get()
        {
            if (_freeN == 0) return null;
            var v = _free[--_freeN];
            Act[Count++] = v;
            return v;
        }
        public void Done(int j)
        {
            var v = Act[j];
            Act[j] = Act[--Count];
            Act[Count] = null!;
            _free[_freeN++] = v;
        }
    }

    // Stimmen können später im Block beginnen, oder in einem späteren (vorausgeplante Folgen).
    internal void RunTicks(Pool<Tick> P, double[] L, double[] R, int n)
    {
        for (int j = P.Count - 1; j >= 0; j--)
        {
            var v = P.Act[j];
            if (v.At >= n) { v.At -= n; continue; }
            double b0 = v.B0, a1 = v.A1, a2 = v.A2, k = v.K, gl = v.Gl, gr = v.Gr;
            double env = v.Env, z1 = v.Z1, z2 = v.Z2;
            int s = v.At, e = Math.Min(n, s + v.N);
            for (int i = s; i < e; i++)
            {
                double x = Wn() * env;
                env *= k;
                double y = b0 * x + z1;
                z1 = -a1 * y + z2;
                z2 = -b0 * x - a2 * y;
                L[i] += y * gl;
                R[i] += y * gr;
            }
            v.N -= e - s; v.At = 0; v.Env = env; v.Z1 = z1; v.Z2 = z2;
            if (v.N <= 0) P.Done(j);
        }
    }

    internal static void RunBubbles(Pool<Bubble> P, double[] L, double[] R, int n)
    {
        for (int j = P.Count - 1; j >= 0; j--)
        {
            var v = P.Act[j];
            if (v.At >= n) { v.At -= n; continue; }
            double k = v.K, dw = v.Dw, gl = v.Gl, gr = v.Gr;
            double ph = v.Ph, w = v.W, amp = v.Amp;
            int s = v.At, e = Math.Min(n, s + v.N);
            for (int i = s; i < e; i++)
            {
                double y = Math.Sin(ph) * amp;
                ph += w; w += dw; amp *= k;
                L[i] += y * gl;
                R[i] += y * gr;
            }
            v.N -= e - s; v.At = 0; v.Ph = ph; v.W = w; v.Amp = amp;
            if (v.N <= 0) P.Done(j);
        }
    }

    // Ereignisse eines Poisson-Stroms: t = Begin(...); while (t < n) { … At(t) …; t += Wait(rate); } next = t - n;
    internal double Begin(double next, double rate) => next > 1e11 ? Wait(rate) : next;
    internal static int At(double t) => Math.Max(0, (int)Math.Floor(t));

    /// <summary>Weiter weg (ein Tiefpass über den ganzen Klang) und gegen die anderen Klänge ausgeglichen.</summary>
    internal static void Finish(double[] L, double[] R, int n, double distance, double gain, BQ fL, BQ fR)
    {
        if (distance > 0) for (int i = 0; i < n; i++) { L[i] = fL.Run(L[i]) * gain; R[i] = fR.Run(R[i]) * gain; }
        else if (gain != 1) for (int i = 0; i < n; i++) { L[i] *= gain; R[i] *= gain; }
    }

    internal abstract class Layer(ScapeEngine e)
    {
        protected readonly ScapeEngine E = e;
        public abstract void Run(double[] L, double[] R, int n);
    }

    // =================================================================================================
    // Regen
    // =================================================================================================

    /* Ferne Gischt (bed, air), dichtes Prasseln in mittlerer Entfernung, nahe Tropfen (Tonhöhe, Klang,
       Abklingen, Lautstärke-Streuung), Plinks in Pfützen, Tropfen ganz nah und eine Wand dazwischen (muffle). */
    internal sealed class Rain : Layer
    {
        private const double Swell = 0.25, BedF = 1200, BedQ = 0.45, AirF = 7000;
        private static readonly double[] PatF = { 1200, 1884, 2916 }, PatQ = { 2.85, 3.05, 2.85 };
        private const double PatRate = 2810, PatterG = 1.75;
        private const double DropRate = 79, DropF0 = 800, DropF1 = 4750, DropQ0 = 0.7, DropQ1 = 5, DropDecay0 = 0.0015, DropDecay1 = 0.006, DropDb0 = 3, DropDb1 = 29, Drops = 4.45;
        private const double DropBubble = 0, BubR0 = 0.9, BubR1 = 1.8, PlinkRate = 0, PlinkR0 = 0.9, PlinkR1 = 4, Plinks = 0.4;
        private const double DripG = 0.74;

        private sealed class Drip { public double F, Every, Jit0, Jit1, P, T; }

        private readonly Pink _pL, _pR;
        private readonly BQ _bedL, _bedR, _airL, _airR, _muL, _muR;
        private readonly Patter _patL, _patR;
        private readonly Drift _swellD, _gust;
        private readonly Pool<Tick> _ticks = new(96);
        private readonly Pool<Bubble> _bubbles = new(48);
        private readonly Drip[] _drips;
        private double _nxDrop, _nxPlink;
        private double _i = 0.8, _w = 1;

        private double _bed = 0.9, _air = 0.05, _muffle = 5474, _gain = 1.514, _mf;
        private bool _dynamic = true;

        // Dynamische Intensität
        private enum Stage { Rest, Rise, Hold, Fall }
        private Stage _stage = Stage.Rest;
        private double _s, _t, _from, _to, _dur = 1;
        public double SurgeValue => _s;

        public Rain(ScapeEngine e) : base(e)
        {
            _pL = new Pink(e); _pR = new Pink(e);
            _bedL = new BQ(e, BQ.BP, 2300, 0.45); _bedR = new BQ(e, BQ.BP, 2700, 0.45);
            _airL = new BQ(e, BQ.HP, 7000, 0.6); _airR = new BQ(e, BQ.HP, 7400, 0.6);
            _muL = new BQ(e, BQ.LP, 2600, 0.6); _muR = new BQ(e, BQ.LP, 2600, 0.6);
            _swellD = new Drift(e, 0.06);
            _gust = new Drift(e, 0.4);
            // Die erste Änderung frühestens nach einer Minute.
            _t = e.Range(60, 240);
            _bedL.Tune(BedF * 0.92, BedQ); _bedR.Tune(BedF * 1.08, BedQ);
            _airL.Tune(AirF, 0.6); _airR.Tune(AirF * 1.06, 0.6);
            _patL = new Patter(e, PatF, PatQ);
            _patR = new Patter(e, new[] { PatF[0] * 1.08, PatF[1] * 1.08, PatF[2] * 1.08 }, PatQ);
            _drips = new[]
            {
                new Drip { F = 1690, Every = 3, Jit0 = 0.91, Jit1 = 1.2, P = -0.55 },
                new Drip { F = 1318, Every = 4.35, Jit0 = 0.91, Jit1 = 1.2, P = 0.5 },
            };
            for (int i = 0; i < _drips.Length; i++) _drips[i].T = e.Sr * (0.7 + 1.3 * i + e.Rnd());
            SetMuffle(_muffle);
        }

        private void SetMuffle(double m)
        {
            _muffle = m;
            if (m > 0) { _muL.Tune(m, 0.6); _muR.Tune(m * 1.05, 0.6); }
            _mf = m;
        }

        public void Tune(ScapeTunes t)
        {
            _bed = t.RainBed; _air = t.RainAir; _gain = t.RainGain; _dynamic = t.RainDynamic;
            SetMuffle(t.RainMuffle);
        }

        /* Alle 2½–8 Minuten legt der Regen zu (häufiger) oder lässt eine Weile nach: schnell herein
           (4–12 s; nachlassend 10–25 s), hält ½–2½ Minuten und kehrt in 20–60 s zur Einstellung zurück. */
        private void Ramp(Stage stage, double to, double dur) { _stage = stage; _from = _s; _to = to; _dur = _t = dur; }

        private double Surge(double dt)
        {
            if (!_dynamic && (_stage == Stage.Rise || _stage == Stage.Hold)) Ramp(Stage.Fall, 0, 20);
            _t -= dt;
            if (_t <= 0)
            {
                if (_stage == Stage.Rest)
                {
                    if (_dynamic)
                    {
                        bool up = E.Rnd() < 0.6;
                        Ramp(Stage.Rise, (up ? 1 : -0.7) * E.Range(0.45, 1), up ? E.Range(4, 12) : E.Range(10, 25));
                    }
                    else _t = 30;
                }
                else if (_stage == Stage.Rise) { _stage = Stage.Hold; _t = E.Range(30, 150); }
                else if (_stage == Stage.Hold) Ramp(Stage.Fall, 0, E.Range(20, 60));
                else { _stage = Stage.Rest; _t = E.Range(150, 480); _s = 0; }
            }
            if (_stage == Stage.Rise || _stage == Stage.Fall)
            {
                double x = Math.Clamp(1 - _t / _dur, 0, 1), ee = x * x * (3 - 2 * x);
                _s = _from + (_to - _from) * ee;
            }
            return _s;
        }

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr, I0 = _i;
            double I1 = Math.Clamp(1 - Swell + Swell * _swellD.Step(dt) * (0.8 + 0.4 * _gust.Step(dt)), 0, 1);
            _i = I1;
            // Ein Anschwellen bewegt alles gemeinsam; bei Starkregen lässt die Wand etwas mehr durch.
            double s = Surge(dt), W0 = _w, W1 = Math.Max(0.3, 1 + 0.6 * s);
            _w = W1;
            if (_muffle > 0)
            {
                double f = _muffle * (1 + 0.12 * s);
                if (Math.Abs(f - _mf) > 1) { _muL.Tune(f, 0.6); _muR.Tune(f * 1.05, 0.6); _mf = f; }
            }

            // Nahe Tropfen auf Blättern, Stein und Erde, jeder in seiner eigenen Tonhöhe.
            double rate = DropRate * I1 * I1 * Math.Max(0.2, 1 + 0.75 * s);
            double t = E.Begin(_nxDrop, rate);
            while (t < n)
            {
                var v = _ticks.Get();
                if (v != null)
                {
                    double amp = Db(-E.Range(DropDb0, DropDb1)) * Drops, pan = E.Range(-0.95, 0.95);
                    v.Start(E, At(t), E.Logr(DropF0, DropF1), E.Range(DropQ0, DropQ1), E.Logr(DropDecay0, DropDecay1), amp, pan);
                    if (E.Rnd() < DropBubble)
                    {
                        var b = _bubbles.Get();
                        b?.Start(E, At(t), 3000 / E.Range(BubR0, BubR1), amp * E.Range(0.3, 0.7), E.Range(0.08, 0.3), pan);
                    }
                }
                t += E.Wait(rate);
            }
            _nxDrop = t - n;
            // Pfützen: ein Plink, der beim Auftauchen der Blase steigt.
            rate = PlinkRate * I1;
            t = E.Begin(_nxPlink, rate);
            while (t < n)
            {
                _bubbles.Get()?.Start(E, At(t), 3000 / E.Range(PlinkR0, PlinkR1), Db(-E.Range(4, 26)) * Plinks, E.Range(0.1, 0.28), E.Range(-0.8, 0.8));
                t += E.Wait(rate);
            }
            _nxPlink = t - n;
            // Tropfen ganz nah, jeder in seinem Takt (ab und zu bleibt einer aus).
            foreach (var d in _drips)
            {
                if (d.T < n)
                {
                    int at = At(d.T);
                    if (E.Rnd() > 0.08)
                    {
                        _bubbles.Get()?.Start(E, at, d.F * E.Range(0.94, 1.06), 0.5 * DripG, E.Range(0.15, 0.25), d.P);
                        _ticks.Get()?.Start(E, at, 3200, 1.5, 0.0007, 0.22 * DripG, d.P);
                    }
                    d.T += d.Every * E.Range(d.Jit0, d.Jit1) * (1 - 0.3 * Math.Max(0, s)) * E.Sr;
                }
                d.T -= n;
            }

            double pp = ((PatRate * (I0 + I1)) / 2 / E.Sr) * Math.Max(0.25, 1 + 0.8 * s);
            double dI = (I1 - I0) / n, dW = (W1 - W0) / n, I = I0, W = W0;
            for (int i = 0; i < n; i++, I += dI, W += dW)
            {
                double bed = _bed * (0.55 + 0.45 * I) * W, air = _air * I * W;
                double l = _bedL.Run(_pL.Run()) * bed + _airL.Run(E.Wn()) * air + _patL.Run(pp) * PatterG;
                double r = _bedR.Run(_pR.Run()) * bed + _airR.Run(E.Wn()) * air + _patR.Run(pp) * PatterG;
                // (das tiefe Grollen ist in „still“ aus)
                L[i] += l;
                R[i] += r;
            }
            E.RunTicks(_ticks, L, R, n);
            RunBubbles(_bubbles, L, R, n);
            // Durch eine Wand, und gegen die anderen Klänge ausgeglichen.
            Finish(L, R, n, _muffle, _gain, _muL, _muR);
        }
    }

    // =================================================================================================
    // Bach
    // =================================================================================================

    /* Blasen, die beim Auftauchen klingen, in Büscheln an drei Stellen über den Bach verteilt, über dem
       Rauschen des Wassers; ab und zu ein tiefes Gluckern. Gischt und Gurgeln sind in „still“ aus. */
    internal sealed class Stream : Layer
    {
        private const double Spread = 1.1, Flow = 0.12, Cluster = 1, RMin = 0.5, RMax = 5.505, Rise = 0.3, Babble = 1;
        private const double GlugR = 10, Glug = 1, RushF = 1100, RushQ = 0.5, DeepF = 280;
        private static readonly double[] SpotX = { -0.55, 0.05, 0.62 };

        private sealed class Spot { public double P, Jit; public Drift Babble = null!, Flow = null!; public double Next; }

        private readonly Pool<Bubble> _bubbles = new(200);
        private readonly Pink _pL, _pR;
        private readonly Brown _bL, _bR;
        private readonly BQ _rushL, _rushR, _deepL, _deepR, _farL, _farR;
        private readonly Spot[] _spots;
        private double _nxGlug;

        private double _rate = 80, _glugRate = 0.65, _rush = 0.5, _deep = 0.25, _burst = 3.4, _distance = 6744, _gain = 1.265;

        public Stream(ScapeEngine e) : base(e)
        {
            _pL = new Pink(e); _pR = new Pink(e); _bL = new Brown(e); _bR = new Brown(e);
            _rushL = new BQ(e, BQ.BP, RushF * 0.935, RushQ); _rushR = new BQ(e, BQ.BP, RushF * 1.065, RushQ);
            _deepL = new BQ(e, BQ.LP, DeepF * 0.96, 0.6); _deepR = new BQ(e, BQ.LP, DeepF * 1.04, 0.6);
            _farL = new BQ(e, BQ.LP, 4000, 0.6); _farR = new BQ(e, BQ.LP, 4000, 0.6);
            // Die Stellen, an denen das Wasser über Steine bricht, jede plätschert in ihren eigenen Schüben.
            _spots = new Spot[SpotX.Length];
            for (int i = 0; i < _spots.Length; i++)
            {
                double jit = e.Range(0.8, 1.2);
                _spots[i] = new Spot { P = SpotX[i] * Spread, Jit = jit, Babble = new Drift(e, _burst * jit), Flow = new Drift(e, Flow) };
            }
            SetDistance(_distance);
        }

        private void SetDistance(double d)
        {
            _distance = d;
            if (d > 0) { _farL.Tune(d, 0.6); _farR.Tune(d * 1.05, 0.6); }
        }

        public void Tune(ScapeTunes t)
        {
            _rate = t.StreamRate; _glugRate = t.StreamGlugRate; _rush = t.StreamRush; _deep = t.StreamDeep; _gain = t.StreamGain;
            if (t.StreamBurst != _burst) { _burst = t.StreamBurst; foreach (var s in _spots) s.Babble.Rate = _burst * s.Jit; }
            SetDistance(t.StreamDistance);
        }

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr, c = Cluster, inv0 = 1 / RMin, inv1 = 1 / RMax;
            foreach (var s in _spots)
            {
                double b = s.Babble.Step(dt), f = s.Flow.Step(dt);
                double rate = _rate * (1 - c + c * (0.2 + 1.6 * b * b)) * (0.75 + 0.5 * f);
                double t = E.Begin(s.Next, rate);
                while (t < n)
                {
                    var v = _bubbles.Get();
                    if (v != null)
                    {
                        // Radien vom kleinsten zum größten, die meisten klein; meist sanft angeregt (1–10 % des Radius).
                        double r = 1 / (inv0 - E.Rnd() * (inv0 - inv1));
                        double eps = 0.01 * Math.Pow(10, E.Rnd() * E.Rnd());
                        v.Start(E, At(t), 3000 / r, eps * r * 2.2 * Babble, E.Range(Rise * 0.19, Rise), s.P + E.Range(-0.22, 0.22) * Spread);
                    }
                    t += E.Wait(rate);
                }
                s.Next = t - n;
            }
            // Ab und zu ein tiefes Gluckern aus einer größeren Luftblase.
            double tg = E.Begin(_nxGlug, _glugRate);
            while (tg < n)
            {
                _bubbles.Get()?.Start(E, At(tg), 3000 / E.Range(GlugR * 0.6, GlugR), E.Range(0.15, 0.35) * Glug, E.Range(0.08, 0.16), E.Range(-0.6, 0.6) * Spread);
                tg += E.Wait(_glugRate);
            }
            _nxGlug = tg - n;
            for (int i = 0; i < n; i++)
            {
                L[i] += _rushL.Run(_pL.Run()) * _rush + _deepL.Run(_bL.Run()) * _deep;
                R[i] += _rushR.Run(_pR.Run()) * _rush + _deepR.Run(_bR.Run()) * _deep;
            }
            RunBubbles(_bubbles, L, R, n);
            // Weiter weg verblassen zuerst die feinen Höhen.
            Finish(L, R, n, _distance, _gain, _farL, _farR);
        }
    }

    // =================================================================================================
    // Wind
    // =================================================================================================

    /* Böen, die von einer Seite zur anderen ziehen, ein tiefes Wummern und ein leises Pfeifen, wenn es
       am stärksten weht. Immer über 7 kHz abgerundet. Blätterrascheln ist in „still“ aus. */
    internal sealed class Wind : Layer
    {
        private const double Calm = 0.1, Tide = 0.55, TideRate = 0.03, GustRate = 0.2;
        private const double Body = 1, BodyF = 160, BodyRise = 620, BodyQ = 0.75, Air = 0.86, Rumble = 2;
        private const double Whistle = 2, WhistleF = 380, WhistleFrom = 0.5, Cross = 0.43;

        private readonly Drift _slow, _gustD, _turb, _wob;
        private readonly float[] _hist = new float[1024];
        private int _h;
        private double _v = 0.4, _vR = 0.4;
        private readonly Pink _pL, _pR;
        private readonly Brown _bL, _bR;
        private readonly BQ _bodyL, _bodyR, _airL, _airR, _rumL, _rumR, _wh1, _wh2, _farL, _farR;
        private bool _hasG;
        private double _ga0, _ga1, _ga2, _ga3, _gaR0, _gaR1, _gaR2;

        private double _gusts = 0.25, _distance = 7000, _gain = 1.4;

        public Wind(ScapeEngine e) : base(e)
        {
            _slow = new Drift(e, TideRate);
            _gustD = new Drift(e, GustRate);
            _turb = new Drift(e, 6);
            _wob = new Drift(e, 1.2);
            _pL = new Pink(e); _pR = new Pink(e); _bL = new Brown(e); _bR = new Brown(e);
            _bodyL = new BQ(e, BQ.BP, 400, 0.75); _bodyR = new BQ(e, BQ.BP, 400, 0.75);
            _airL = new BQ(e, BQ.BP, 1000, 1.4); _airR = new BQ(e, BQ.BP, 1000, 1.4);
            _rumL = new BQ(e, BQ.LP, 85, 0.7); _rumR = new BQ(e, BQ.LP, 95, 0.7);
            _wh1 = new BQ(e, BQ.BP, 700, 45); _wh2 = new BQ(e, BQ.BP, 1070, 40);
            _farL = new BQ(e, BQ.LP, 4000, 0.6); _farR = new BQ(e, BQ.LP, 4000, 0.6);
            SetRumble(90);
            SetDistance(_distance);
        }

        private void SetRumble(double f) { _rumL.Tune(f * 0.94, 0.7); _rumR.Tune(f * 1.06, 0.7); }
        private void SetDistance(double d)
        {
            _distance = d;
            if (d > 0) { _farL.Tune(d, 0.6); _farR.Tune(d * 1.05, 0.6); }
        }

        public void Tune(ScapeTunes t)
        {
            _gusts = t.WindGusts; _gain = t.WindGain;
            SetRumble(t.WindRumbleF);
            SetDistance(t.WindDistance);
        }

        private static void Gains(double v, double tb, out double body, out double air, out double rum, out double wh)
        {
            body = 0.1 + 0.9 * Math.Pow(v, 1.7);
            air = Math.Pow(v, 2.2);
            rum = v * v * (0.6 + 0.6 * tb);
            double x = Math.Max(0, v - WhistleFrom);
            wh = x * x * 4;
        }

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr;
            // Die Stärke des Windes: eine langsame Gezeit und Böen darauf.
            double gd = _gustD.Step(dt);
            double v1 = Math.Clamp(Calm + Tide * _slow.Step(dt) + _gusts * Math.Pow(gd, 2.5), 0, 1);
            _hist[_h & 1023] = (float)v1;
            _h++;
            // Jede Böe erreicht rechts einen Moment später als links: sie zieht vor einem vorbei.
            int lag = Math.Min(1000, (int)Math.Round((Cross * E.Sr) / n));
            double vR1 = _h > lag ? _hist[(_h - lag) & 1023] : v1;
            double tb = _turb.Step(dt), wobble = 1 + 0.02 * (_wob.Step(dt) - 0.5);
            _bodyL.Tune(BodyF + BodyRise * v1, BodyQ); _bodyR.Tune(BodyF + BodyRise * vR1, BodyQ);
            _airL.Tune((BodyF + BodyRise * v1) * 2.6, 1.4); _airR.Tune((BodyF + BodyRise * vR1) * 2.6, 1.4);
            double fw = (WhistleF + 900 * v1) * wobble;
            _wh1.Tune(fw, 45);
            _wh2.Tune(fw * 1.53, 40);
            if (!_hasG)
            {
                Gains(_v, tb, out _ga0, out _ga1, out _ga2, out _ga3);
                Gains(_vR, tb, out _gaR0, out _gaR1, out _gaR2, out _);
                _hasG = true;
            }
            Gains(v1, tb, out double b0, out double b1, out double b2, out double b3);
            Gains(vR1, tb, out double bR0, out double bR1, out double bR2, out _);
            double a0 = _ga0, a1 = _ga1, a2 = _ga2, a3 = _ga3, aR0 = _gaR0, aR1 = _gaR1, aR2 = _gaR2;
            _ga0 = b0; _ga1 = b1; _ga2 = b2; _ga3 = b3; _gaR0 = bR0; _gaR1 = bR1; _gaR2 = bR2;
            _v = v1; _vR = vR1;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double gb = a0 + (b0 - a0) * t, ga = a1 + (b1 - a1) * t, gr = a2 + (b2 - a2) * t, gw = a3 + (b3 - a3) * t;
                double gbR = aR0 + (bR0 - aR0) * t, gaR = aR1 + (bR1 - aR1) * t, grR = aR2 + (bR2 - aR2) * t;
                double w = E.Wn(), wh = (_wh1.Run(w) + 0.6 * _wh2.Run(w)) * gw * Whistle;
                L[i] += _bodyL.Run(_pL.Run()) * gb * Body + _airL.Run(E.Wn()) * ga * Air + _rumL.Run(_bL.Run()) * gr * Rumble + wh * 0.8;
                R[i] += _bodyR.Run(_pR.Run()) * gbR * Body + _airR.Run(E.Wn()) * gaR * Air + _rumR.Run(_bR.Run()) * grR * Rumble + wh * 0.55;
            }
            Finish(L, R, n, _distance, _gain, _farL, _farR);
        }
    }

    // =================================================================================================
    // Wellen
    // =================================================================================================

    internal sealed class WavesParams
    {
        public double GapMin = 6, GapMax = 10, SizeMin = 0.6, SizeMax = 1, Build = 3.6, FoamLen = 7, Width = 1.1;
        public double Rumble = 0.8, Crash = 0, CrashDecay = 0.9, Bright = 4000, Foam = 0, Fizz = 0, FizzRate = 0;
        public double Spray = 0.7, SprayF = 3200, SprayRise = 2.6, SprayFade = 5.5, SprayFlutter = 0.3, SprayRate = 0.8;
        public double Back = 0.45, Pebbles = 2, PebbleRate = 120, Sea = 0.45, SeaF = 300;
        public double Sets = 0.6, SetPeriod = 90, Variety = 0.6, Ripples = 3, Depth = 0.8, Space3d = 0.8, Distance = 5000, Gain = 1.096;
    }

    private struct WaveEnv { public double Rum, RumF, Crash, CrashF, Spray, Foam, Back, BackF, Pan; }

    /// <summary>Eine Welle: baut sich auf, bricht, schäumt den Strand hinauf und zieht zurück.</summary>
    internal sealed class Wave
    {
        private readonly ScapeEngine E;
        public bool On;
        private readonly Brown _bL, _bR;
        private readonly Pink _pL, _pR;
        private readonly BQ _rumL, _rumR, _crL, _crR, _foL, _foR, _fhL, _fhR, _bkL, _bkR, _spL, _spR, _zL, _zR;
        // Ort: ein paar Samples Verzögerung zu einem Ohr setzen die Welle auf diese Seite.
        private readonly double[] _dlL = new double[64], _dlR = new double[64];
        private int _wi;
        private readonly Drift _flut;
        private double _fl = 1;
        private double _nxFizz, _nxPeb;
        private double _t, _s, _p, _dir, _T1, _F, _sRise, _sFade, _z, _itd, _zg, _keep, _len;
        private bool _deep;
        private WavesParams _P = null!;
        private WaveEnv _e;

        public Wave(ScapeEngine e)
        {
            E = e;
            _bL = new Brown(e); _bR = new Brown(e); _pL = new Pink(e); _pR = new Pink(e);
            _rumL = new BQ(e, BQ.LP, 200, 0.7); _rumR = new BQ(e, BQ.LP, 200, 0.7);
            _crL = new BQ(e, BQ.LP, 6000, 0.6); _crR = new BQ(e, BQ.LP, 6000, 0.6);
            _foL = new BQ(e, BQ.BP, 2600, 0.5); _foR = new BQ(e, BQ.BP, 2900, 0.5);
            _fhL = new BQ(e, BQ.HP, 5000, 0.7); _fhR = new BQ(e, BQ.HP, 5400, 0.7);
            _bkL = new BQ(e, BQ.BP, 2600, 0.8); _bkR = new BQ(e, BQ.BP, 2600, 0.8);
            _spL = new BQ(e, BQ.HP, 5000, 0.7); _spR = new BQ(e, BQ.HP, 5000, 0.7);
            // Tiefe: eine fernere Welle ist dunkler (und leiser und schmaler).
            _zL = new BQ(e, BQ.LP, 16000, 0.6); _zR = new BQ(e, BQ.LP, 16000, 0.6);
            _flut = new Drift(e, 1.3);
        }

        private double Vr(double k) => _P.Variety != 0 ? 1 + k * _P.Variety * (2 * E.Rnd() - 1) : 1;

        // z: wie weit draußen diese Welle bricht (0 nah … 1 fern); small: eine kleine Welle zwischen den großen.
        public void Start(double s, double p, double dir, WavesParams P, double z, bool small)
        {
            On = true;
            _t = 0; _s = s; _p = p; _dir = dir; _P = P;
            double shortK = small ? 0.6 : 1;
            _T1 = Math.Max(0.6, E.Range(P.Build - 0.5, P.Build + 0.5) * Vr(0.35) * shortK);
            _F = Math.Max(1, E.Range(P.FoamLen - 1, P.FoamLen + 1) * Vr(0.4) * shortK);
            _sRise = Math.Max(0.2, P.SprayRise * Vr(0.4));
            _sFade = Math.Max(0.3, P.SprayFade * Vr(0.4));
            double sf = P.SprayF * Vr(0.25);
            _spL.Tune(sf * 0.963, 0.7); _spR.Tune(sf * 1.037, 0.7);
            _flut.Rate = P.SprayRate;
            _z = z;
            _itd = P.Space3d;
            _zg = 1 - 0.65 * _z;
            // Weiter draußen schmaler; mit Ortung ein gemeinsamer Kern, den beide Ohren hören.
            _keep = 1 - Math.Min(0.85, 0.8 * _z + 0.35 * _itd);
            double zf = 16000 * Math.Pow(0.1, _z);
            _zL.Tune(zf, 0.6); _zR.Tune(zf * 1.03, 0.6);
            _deep = _z > 0.001 || _itd > 0;
            _len = Math.Max(_T1 + _F + 3.4, _T1 + 0.4 * _sRise + _sFade + 0.2);
            _e = Env(0);
        }

        private WaveEnv Env(double t)
        {
            double T1 = _T1, s = _s, tc = t - T1;
            var P = _P;
            var r = new WaveEnv();
            r.Rum = (t < T1 ? Math.Pow(t / T1, 2) : Math.Exp(-tc / 0.45)) * s;
            r.Crash = tc < 0 ? 0 : (tc < 0.09 ? tc / 0.09 : Math.Exp(-(tc - 0.09) / P.CrashDecay)) * s;
            // Gischt: schwillt kurz vor dem Brechen an, legt sich danach langsam; nie ein Klatschen.
            double s0 = T1 - 0.6 * _sRise, s1 = T1 + 0.4 * _sRise, spray = 0;
            if (t > s0 && t <= s1) { double x = (t - s0) / _sRise; spray = x * x * (3 - 2 * x); }
            else if (t > s1 && t < s1 + _sFade) spray = 0.5 * (1 + Math.Cos((Math.PI * (t - s1)) / _sFade));
            r.Spray = spray * s;
            double tf = tc - 0.15;
            r.Foam = tf < 0 ? 0 : (tf < 0.5 ? Math.Pow(tf / 0.5, 0.7) : Math.Exp(-(tf - 0.5) / (_F * 0.42))) * s;
            double tb = t - (T1 + _F * 0.5);
            r.Back = tb < 0 || tb > 3.2 ? 0 : Math.Pow(Math.Sin((Math.PI * tb) / 3.2), 2) * 0.7 * s;
            r.RumF = 120 + 650 * Math.Min(1, t / T1);
            r.CrashF = 1400 + P.Bright * s * Math.Exp(-Math.Max(0, tc) / 1.2);
            r.BackF = 2600 - 1400 * Math.Clamp(tb / 3.2, 0, 1);
            r.Pan = _p + _dir * Math.Min(1, t / _len);
            return r;
        }

        public void Run(double[] L, double[] R, int n, Waves W)
        {
            var P = _P;
            var a = _e;
            _t += n / E.Sr;
            var b = Env(_t);
            _e = b;
            // Die Gischt kommt und geht ein wenig in ihrem Anschwellen.
            double fl0 = _fl, fl1 = 1 - P.SprayFlutter * (1 - _flut.Step(n / E.Sr));
            _fl = fl1;
            _rumL.Tune(b.RumF, 0.7); _rumR.Tune(b.RumF * 1.05, 0.7);
            _crL.Tune(b.CrashF, 0.6); _crR.Tune(b.CrashF * 1.04, 0.6);
            _bkL.Tune(b.BackF, 0.8); _bkR.Tune(b.BackF * 1.06, 0.8);
            // Schaum, dessen Blasen platzen; der Rücksog, der die Kiesel rollt (beides draußen leiser).
            double zg = _zg, near = 1 - _z;
            double rate = P.FizzRate * b.Foam * near;
            double t = E.Begin(_nxFizz, rate);
            while (t < n)
            {
                W.Bubbles.Get()?.Start(E, At(t), 3000 / E.Range(0.25, 1), Db(-E.Range(18, 38)) * P.Fizz * zg, 0.1, b.Pan + E.Range(-0.5, 0.5) * P.Width * _keep);
                t += E.Wait(rate);
            }
            _nxFizz = t - n;
            rate = P.PebbleRate * b.Back * near;
            t = E.Begin(_nxPeb, rate);
            while (t < n)
            {
                W.Ticks.Get()?.Start(E, At(t), E.Logr(1800, 5200), E.Range(2, 4), E.Logr(0.0006, 0.002), Db(-E.Range(10, 30)) * P.Pebbles * zg, b.Pan + E.Range(-0.4, 0.4) * P.Width * _keep);
                t += E.Wait(rate);
            }
            _nxPeb = t - n;
            // Ortung: das abgewandte Ohr hört die Welle bis zu 0,6 ms später.
            int dS = _itd != 0 ? Math.Min(30, (int)Math.Round(_itd * 30 * Math.Abs(b.Pan))) : 0;
            int dLs = b.Pan > 0 ? dS : 0, dRs = b.Pan < 0 ? dS : 0;
            bool deep = _deep;
            double keep = _keep;
            var bufL = _dlL; var bufR = _dlR;
            int wi = _wi;
            // Balance statt Panorama, damit die Welle beim Wandern ihre Breite behält.
            double bl0 = Math.Min(1, 1 - a.Pan), br0 = Math.Min(1, 1 + a.Pan), bl1 = Math.Min(1, 1 - b.Pan), br1 = Math.Min(1, 1 + b.Pan);
            bool doRum = a.Rum + b.Rum > 1e-4, doFo = (a.Foam + b.Foam) * P.Foam > 1e-4, doBk = a.Back + b.Back > 1e-4;
            bool doCr = (a.Crash + b.Crash) * P.Crash > 1e-4, doSp = (a.Spray + b.Spray) * P.Spray > 1e-4;
            double rm0 = a.Rum * P.Rumble, rm1 = b.Rum * P.Rumble, cr0 = a.Crash * P.Crash, cr1 = b.Crash * P.Crash;
            double fo0 = a.Foam * P.Foam, fo1 = b.Foam * P.Foam, bk0 = a.Back * P.Back, bk1 = b.Back * P.Back;
            // Bei Stufe 1 liegt die Gischt 3 dB unter dem früheren Brechen (gemessen).
            double sp0 = a.Spray * P.Spray * fl0 * 0.16, sp1 = b.Spray * P.Spray * fl1 * 0.16;
            for (int i = 0; i < n; i++)
            {
                double u = (double)i / n, l = 0, r = 0;
                if (doRum) { double g = rm0 + (rm1 - rm0) * u; l += _rumL.Run(_bL.Run()) * g; r += _rumR.Run(_bR.Run()) * g; }
                if (doCr) { double g = cr0 + (cr1 - cr0) * u; l += _crL.Run(_pL.Run()) * g; r += _crR.Run(_pR.Run()) * g; }
                if (doSp) { double g = sp0 + (sp1 - sp0) * u; l += _spL.Run(E.Wn()) * g; r += _spR.Run(E.Wn()) * g; }
                if (doFo)
                {
                    double g = fo0 + (fo1 - fo0) * u, wl = E.Wn(), wr = E.Wn();
                    l += (_foL.Run(wl) + 0.5 * _fhL.Run(wl)) * g;
                    r += (_foR.Run(wr) + 0.5 * _fhR.Run(wr)) * g;
                }
                if (doBk) { double g = bk0 + (bk1 - bk0) * u; l += _bkL.Run(_pL.Run()) * g; r += _bkR.Run(_pR.Run()) * g; }
                l *= bl0 + (bl1 - bl0) * u;
                r *= br0 + (br1 - br0) * u;
                if (deep)
                {
                    // Weiter draußen: dunkler, leiser, schmaler; dann mit der Verzögerung verortet.
                    l = _zL.Run(l); r = _zR.Run(r);
                    double m = (l + r) * 0.5;
                    l = (m + (l - m) * keep) * zg; r = (m + (r - m) * keep) * zg;
                    bufL[wi & 63] = l; bufR[wi & 63] = r;
                    l = bufL[(wi - dLs) & 63]; r = bufR[(wi - dRs) & 63];
                    wi++;
                }
                L[i] += l;
                R[i] += r;
            }
            _wi = wi & 0xffff;
            if (_t > _len) On = false;
        }
    }

    internal sealed class Waves : Layer
    {
        private readonly WavesParams _p = new();
        // Bis zu sechs zugleich: mit Tiefe überlagern sich nahe und ferne Wellen.
        private readonly Wave[] _voices;
        private double _next, _nextR;
        // Sätze: ein langsamer Wechsel zwischen Folgen größerer Wellen und ruhigeren Pausen.
        private readonly Drift _setD;
        public readonly Pool<Bubble> Bubbles = new(160);
        public readonly Pool<Tick> Ticks = new(120);
        // Die Brandung weiter draußen hört nie ganz auf.
        private readonly Brown _bL, _bR;
        private readonly Pink _pL, _pR;
        private readonly BQ _seaL, _seaR, _farL, _farR, _dL, _dR;
        private readonly Drift _swell;
        private double _sw = 0.5;

        public Waves(ScapeEngine e) : base(e)
        {
            _voices = new Wave[6];
            for (int i = 0; i < _voices.Length; i++) _voices[i] = new Wave(e);
            _next = e.Sr * 0.6;
            _nextR = e.Sr * 4;
            _setD = new Drift(e, 1 / 90.0);
            _bL = new Brown(e); _bR = new Brown(e); _pL = new Pink(e); _pR = new Pink(e);
            _seaL = new BQ(e, BQ.LP, 360, 0.6); _seaR = new BQ(e, BQ.LP, 380, 0.6);
            _farL = new BQ(e, BQ.BP, 950, 0.6); _farR = new BQ(e, BQ.BP, 1050, 0.6);
            _dL = new BQ(e, BQ.LP, 4000, 0.6); _dR = new BQ(e, BQ.LP, 4000, 0.6);
            _swell = new Drift(e, 0.1);
            Use();
        }

        private void Use()
        {
            _seaL.Tune(_p.SeaF * 0.973, 0.6); _seaR.Tune(_p.SeaF * 1.027, 0.6);
            if (_p.Distance > 0) { _dL.Tune(_p.Distance, 0.6); _dR.Tune(_p.Distance * 1.05, 0.6); }
            _setD.Rate = 1 / (_p.SetPeriod > 0 ? _p.SetPeriod : 90);
        }

        public void Tune(ScapeTunes t)
        {
            _p.SizeMin = t.WavesSizeMin; _p.SizeMax = t.WavesSizeMax; _p.GapMin = t.WavesGapMin; _p.GapMax = t.WavesGapMax;
            _p.Spray = t.WavesSpray; _p.Rumble = t.WavesRumble; _p.Sea = t.WavesSea; _p.Distance = t.WavesDistance; _p.Gain = t.WavesGain;
            Use();
        }

        private Wave? Free()
        {
            foreach (var w in _voices) if (!w.On) return w;
            return null;
        }

        // Tiefe: jede Welle bricht irgendwo zwischen dem nahen Ufer und der Weite, die Depth vorgibt.
        private double Far(double bias) => _p.Depth != 0 ? _p.Depth * Math.Pow(E.Rnd(), bias) : 0;

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr;
            var p = _p;
            // In einem Satz kommen die Wellen größer und dichter; in einer Pause kleiner und seltener.
            double setK = p.Sets != 0 ? (2 * _setD.Step(dt) - 1) * p.Sets : 0;
            _next -= n;
            if (_next <= 0)
            {
                Free()?.Start(E.Range(p.SizeMin, p.SizeMax) * (1 + 0.35 * setK), E.Range(-0.35, 0.35) * p.Width, E.Range(-0.3, 0.3) * p.Width, p, Far(0.9), false);
                _next = E.Range(p.GapMin, Math.Max(p.GapMin, p.GapMax)) * (1 - 0.35 * setK) * E.Sr;
            }
            // Kleine Wellen dazwischen, meist weiter draußen.
            if (p.Ripples != 0)
            {
                _nextR -= n;
                if (_nextR <= 0)
                {
                    Free()?.Start(E.Range(0.18, 0.35) * p.SizeMax, E.Range(-0.6, 0.6) * p.Width, E.Range(-0.3, 0.3) * p.Width, p, Far(0.5), true);
                    _nextR = E.Wait(p.Ripples / 60);
                }
            }
            double s0 = _sw, s1 = 0.65 + 0.35 * _swell.Step(dt);
            _sw = s1;
            double ds = (s1 - s0) / n, s = s0;
            for (int i = 0; i < n; i++, s += ds)
            {
                L[i] += (_seaL.Run(_bL.Run()) + 0.35 * _farL.Run(_pL.Run())) * s * p.Sea;
                R[i] += (_seaR.Run(_bR.Run()) + 0.35 * _farR.Run(_pR.Run())) * s * p.Sea;
            }
            foreach (var w in _voices) if (w.On) w.Run(L, R, n, this);
            RunBubbles(Bubbles, L, R, n);
            E.RunTicks(Ticks, L, R, n);
            Finish(L, R, n, p.Distance, p.Gain, _dL, _dR);
        }
    }

    // =================================================================================================
    // Feuer
    // =================================================================================================

    /* Farnells drei Teile (züngelnde Flammen, Zischen, Knistern), dazu ab und zu ein lauteres Knallen und
       hin und wieder ein Scheit, das sich setzt. In „still“ trägt das Knistern, Flammen und Zischen nur leise. */
    internal sealed class Fire : Layer
    {
        private const double Lap = 0.04, LapLow = 230, LapHigh = 670, Flicker = 3, Roar = 0.05, RoarF = 345, Hiss = 0.01;
        private const double Crackle = 1.6, CrackleRate = 2, CrackleBurst = 12, CrackleLo = 1200, CrackleHi = 9000, CrackleLen = 5, Doubles = 0.3;
        private const double PopG = 4, PopEvery = 14, SettleG = 1.8, SettleEvery = 60, Width = 0.22, Distance = 6134, Gain = 4;

        private sealed class LapSet
        {
            public BQ Bp = null!, H1 = null!, H2 = null!, C1 = null!, C2 = null!;
        }

        private readonly LapSet _lapL, _lapR;
        private readonly Brown _bL, _bR;
        private readonly BQ _roarL, _roarR, _hHP, _hLP, _farL, _farR;
        private readonly Drift _flick, _crk;
        private double _fl = 0.5, _hm;
        private readonly double _hc, _hs;
        private readonly Pool<Tick> _ticks = new(200);
        private double _nxCrackle, _nxPop, _nxSettle;

        public Fire(ScapeEngine e) : base(e)
        {
            // Züngeln (Farnell): Rauschen, das bei 30 Hz klingt, hart in die Begrenzung getrieben, dann gefärbt.
            LapSet MakeLap() => new()
            {
                Bp = new BQ(e, BQ.BP, 30, 5), H1 = new BQ(e, BQ.HP, 25, 0.7), H2 = new BQ(e, BQ.HP, 25, 0.7),
                C1 = new BQ(e, BQ.BP, LapLow, 0.2), C2 = new BQ(e, BQ.BP, LapHigh, 0.6),
            };
            _lapL = MakeLap(); _lapR = MakeLap();
            _bL = new Brown(e); _bR = new Brown(e);
            _roarL = new BQ(e, BQ.LP, RoarF * 0.95, 0.7); _roarR = new BQ(e, BQ.LP, RoarF * 1.05, 0.7);
            _flick = new Drift(e, Flicker);
            // Zischen (Farnell): hohes Rauschen, in Schüben geöffnet von langsamem Rauschen hoch vier.
            _hHP = new BQ(e, BQ.HP, 1600, 0.7); _hLP = new BQ(e, BQ.LP, 7000, 0.7);
            _hc = 1 - Math.Exp(-TAU / e.Sr);
            _hs = 0.57735 * Math.Sqrt(_hc / (2 - _hc));
            _farL = new BQ(e, BQ.LP, Distance, 0.6); _farR = new BQ(e, BQ.LP, Distance * 1.05, 0.6);
            _crk = new Drift(e, 0.6);
        }

        private void CrackleAt(int at, double scale, bool low)
        {
            var v = _ticks.Get();
            if (v == null) return;
            // Farnells Knackser liegen bei 1,5–16 kHz in 30 Stufen; die eines sich setzenden Scheits tiefer und weicher.
            double f = low ? E.Logr(700, 3500) : CrackleLo + ((CrackleHi - CrackleLo) * Math.Floor(E.Rnd() * 30)) / 29;
            double amp = Db(-E.Range(0, 30)) * scale, pan = E.Range(-1, 1) * Width;
            v.Start(E, at, f, E.Range(0.8, 2), E.Logr(0.0003, Math.Max(0.0004, CrackleLen / 1000)), amp, pan);
            // Holz knackt selten nur einmal: ein paar weitere folgen binnen Millisekunden.
            if (E.Rnd() < Doubles)
            {
                for (int i = 0, m = 1 + (int)(E.Rnd() * 3); i < m; i++)
                {
                    var u = _ticks.Get();
                    if (u == null) break;
                    u.Start(E, at + (int)Math.Floor(E.Range(0.002, 0.014) * E.Sr * (i + 1)), f * E.Range(0.8, 1.25), E.Range(0.8, 2), E.Logr(0.0003, 0.002), amp * E.Range(0.3, 0.8), pan + E.Range(-0.05, 0.05));
                }
            }
        }

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr, c = _crk.Step(dt);
            double rate = CrackleRate + CrackleBurst * c * c * c;
            double t = E.Begin(_nxCrackle, rate);
            while (t < n) { CrackleAt(At(t), Crackle, false); t += E.Wait(rate); }
            _nxCrackle = t - n;
            // Eine Harztasche platzt: ein lauteres, tieferes Knallen und ein Funkenregen.
            t = E.Begin(_nxPop, 1 / PopEvery);
            while (t < n)
            {
                int at = At(t);
                _ticks.Get()?.Start(E, at, E.Range(450, 1300), 2.5, E.Range(0.012, 0.03), 0.8 * PopG, E.Range(-0.7, 0.7) * Width);
                for (int i = 0, m = 3 + (int)(E.Rnd() * 6); i < m; i++) CrackleAt(at + (int)Math.Floor(E.Range(0.01, 0.25) * E.Sr), 0.7 * Crackle, false);
                t += E.Wait(1 / PopEvery);
            }
            _nxPop = t - n;
            // Hin und wieder setzt sich ein Scheit: ein weicher Schlag und bröselnde Glut.
            t = E.Begin(_nxSettle, 1 / SettleEvery);
            while (t < n)
            {
                int at = At(t);
                _ticks.Get()?.Start(E, at, 160, 0.7, 0.12, 0.7 * SettleG, E.Range(-0.6, 0.6) * Width);
                for (int i = 0, m = 18 + (int)(E.Rnd() * 14); i < m; i++) CrackleAt(at + (int)Math.Floor(E.Range(0.02, 1.2) * E.Sr), 0.45 * SettleG, true);
                t += E.Wait(1 / SettleEvery);
            }
            _nxSettle = t - n;

            double f0 = _fl, f1 = _flick.Step(dt);
            _fl = f1;
            double df = (f1 - f0) / n, hc = _hc, hs = _hs, fl = f0;
            var A = _lapL; var B = _lapR;
            for (int i = 0; i < n; i++, fl += df)
            {
                double lapG = (0.55 + 0.45 * fl) * Lap, roarG = (0.4 + 0.6 * fl) * Roar;
                _hm += hc * (E.Wn() - _hm);
                double u = _hm / hs, m = Math.Min(2, (u * u * u * u) / 30);
                double hiss = _hLP.Run(_hHP.Run(E.Wn())) * m * Hiss;
                double xl = A.H1.Run(A.Bp.Run(E.Wn()) * 100); xl = xl > 0.9 ? 0.9 : xl < -0.9 ? -0.9 : xl; xl = A.H2.Run(xl) * 0.6;
                double xr = B.H1.Run(B.Bp.Run(E.Wn()) * 100); xr = xr > 0.9 ? 0.9 : xr < -0.9 ? -0.9 : xr; xr = B.H2.Run(xr) * 0.6;
                double lapl = (A.C1.Run(xl) * 0.6 + A.C2.Run(xl) * 0.35 + xl * 0.05) * lapG;
                double lapr = (B.C1.Run(xr) * 0.6 + B.C2.Run(xr) * 0.35 + xr * 0.05) * lapG;
                L[i] += lapl + _roarL.Run(_bL.Run()) * roarG + hiss;
                R[i] += lapr + _roarR.Run(_bR.Run()) * roarG + hiss * 0.85;
            }
            E.RunTicks(_ticks, L, R, n);
            Finish(L, R, n, Distance, Gain, _farL, _farR);
        }
    }

    // =================================================================================================
    // Braunes Rauschen
    // =================================================================================================

    /* „still“ ist die rollende Fassung: ein langsames Heben und Senken wie ferne Brandung; die Weichheit
       folgt der Einstellung (rundet die Höhen ab, der verlorene Pegel wird zurückgegeben). */
    internal sealed class BrownNoise : Layer
    {
        private const double LowCut = 24, Move = 0.35, MoveRate = 0.08, Width = 1;
        private readonly Brown _bL, _bR;
        private readonly BQ _hL, _hR, _dL, _dR;
        private readonly Drift _moveD;
        private double _mv = 1, _dark = 0.3, _gain = 1.167;

        public BrownNoise(ScapeEngine e) : base(e)
        {
            _bL = new Brown(e); _bR = new Brown(e);
            _hL = new BQ(e, BQ.HP, LowCut, 0.7); _hR = new BQ(e, BQ.HP, LowCut, 0.7);
            _dL = new BQ(e, BQ.LP, 2000, 0.6); _dR = new BQ(e, BQ.LP, 2000, 0.6);
            _moveD = new Drift(e, MoveRate);
            SetDark(_dark);
        }

        private void SetDark(double dark)
        {
            _dark = dark;
            double f = 6000 * Math.Pow(150 / 6000.0, dark);
            _dL.Tune(f, 0.6); _dR.Tune(f, 0.6);
        }

        public void Tune(ScapeTunes t) { _gain = t.BrownGain; SetDark(t.BrownDark); }

        public override void Run(double[] L, double[] R, int n)
        {
            double dt = n / E.Sr, g = _gain;
            double m0 = _mv, m1 = 1 + Move * (2 * _moveD.Step(dt) - 1);
            _mv = m1;
            double dm = (m1 - m0) / n, m = m0;
            bool dark = _dark > 0.005;
            for (int i = 0; i < n; i++, m += dm)
            {
                double l = _hL.Run(_bL.Run()), r = _hR.Run(_bR.Run());
                if (dark) { l = _dL.Run(l); r = _dR.Run(r); }
                double mid = (l + r) * 0.5;
                L[i] += (mid + (l - mid) * Width) * g * m;
                R[i] += (mid + (r - mid) * Width) * g * m;
            }
        }
    }
}

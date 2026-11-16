using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GlassBar.NativeStill.Audio;

/// <summary>
/// Kurze, synthetisch erzeugte Klänge wie im Original (tones.js): Erinnerungen, Wecker, Timer, Gebet, Training.
/// Nichts ist aufgenommen – FM-Glocken, Synth-Marimba, E-Piano, warme Zupfer, ein runder Bass, ein Stereo-Echo und
/// ein kleiner Hall werden einmal (im Hintergrund) ausgerechnet und dann abgespielt. Ein klingelnder Wecker wächst
/// von leise zu voll, seine zweite Schicht (Bass, weitere Glocken) blüht dabei auf.
/// Abspielen über eine gemeinsame WASAPI-Ausgabe mit Mischer (Klänge überlagern sich), nie blockierend, aus jedem
/// Thread aufrufbar, wirft nie.
/// Namen: "glint", "beacon", "dewdrop", "bamboo" (Timer), "horizon", "silk", "rise", "harbor" (Wecker) sowie die
/// kleinen Glöckchen aus pills.js (chime): "water", "pomo-focus", "pomo-break", "task", "workout-soon",
/// "workout-start", "workout-stop", "workout-done" und frei "chime:880,1318.5".
/// </summary>
public static class Tones
{
    /// <summary>Klänge für Timer (wie TONES.timer).</summary>
    public static readonly IReadOnlyList<string> TimerSounds = ["glint", "beacon", "dewdrop", "bamboo"];
    /// <summary>Klänge für Wecker (wie TONES.alarm).</summary>
    public static readonly IReadOnlyList<string> AlarmSounds = ["horizon", "silk", "rise", "harbor"];

    /// <summary>Anzeigenamen wie im Original.</summary>
    public static string DisplayName(string name) => Synth.Defs.TryGetValue(name ?? "", out var d) ? d.Name : name ?? "";

    /// <summary>Kurzbeschreibung wie im Original (about).</summary>
    public static string About(string name) => Synth.Defs.TryGetValue(name ?? "", out var d) ? d.About : "";

    /// <summary>Gibt es diesen Klang?</summary>
    public static bool Exists(string name) => !string.IsNullOrEmpty(name) && (Synth.Defs.ContainsKey(name) || Chimes.TryGet(name, out _));

    /// <summary>Klang abspielen (Lautstärke 0..1). "off" oder unbekannt = nichts.</summary>
    public static void Play(string name, float volume = 1f)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || name == "off" || !(volume > 0)) return;
            float vol = Math.Min(1f, volume);
            if (Chimes.TryGet(name, out var notes))
            {
                Task.Run(() =>
                {
                    try
                    {
                        int sr = Output.SampleRate();
                        if (sr <= 0) return;
                        Output.Add(new BufferVoice(Chimes.Render(sr, notes), sr, vol), sr);
                    }
                    catch { }
                });
                return;
            }
            if (!Synth.Defs.ContainsKey(name)) return;
            Task.Run(() =>
            {
                try
                {
                    int sr = Output.SampleRate();
                    if (sr <= 0) return;
                    var buf = Cache.Once(name, sr);
                    if (buf != null) Output.Add(new BufferVoice(buf, sr, vol), sr);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>Wiederholenden Klang (Wecker) starten; Rückgabe stoppt ihn (mit kurzem Ausblenden).</summary>
    /// <remarks>Wie ring() im Original: beginnt bei 35 % und wird über 30 s voll, die zweite Schicht blüht im
    /// letzten Dreiviertel davon auf. Die kleinen Glöckchen (chime) wiederholen sich alle zwei Sekunden.</remarks>
    public static IDisposable Loop(string name, float volume = 1f)
    {
        var handle = new LoopHandle();
        try
        {
            if (string.IsNullOrEmpty(name) || name == "off" || !(volume > 0)) return handle;
            float vol = Math.Min(1f, volume);
            bool chime = Chimes.TryGet(name, out var notes);
            if (!chime && !Synth.Defs.ContainsKey(name)) return handle;
            Task.Run(() =>
            {
                try
                {
                    int sr = Output.SampleRate();
                    if (sr <= 0 || handle.Disposed) return;
                    RingVoice? voice;
                    if (chime)
                    {
                        var buf = Chimes.Render(sr, notes);
                        int period = 2 * sr * 2;
                        var loop = new float[Math.Max(period, buf.Length)];
                        Array.Copy(buf, loop, buf.Length);
                        voice = new RingVoice(new Stem([], loop), null, sr, vol, 1, 0);
                    }
                    else
                    {
                        var ring = Cache.Ring(name, sr);
                        if (ring == null) return;
                        voice = new RingVoice(ring.Core, ring.Bloom, sr, vol, 0.35, 30);
                    }
                    handle.Attach(voice);
                    if (!handle.Disposed) Output.Add(voice, sr);
                }
                catch { }
            });
        }
        catch { }
        return handle;
    }

    /// <summary>Einen Klang schon einmal ausrechnen (z. B. kurz vor einem Wecker), damit er pünktlich beginnt.</summary>
    public static void Prepare(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || !Synth.Defs.ContainsKey(name)) return;
            Task.Run(() =>
            {
                try
                {
                    int sr = Output.SampleRate();
                    if (sr > 0) Cache.Ring(name, sr);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>Nur zum Prüfen: den Klang einmal durch als verschränktes Stereo (float) ausrechnen.</summary>
    internal static float[]? RenderOnce(string name, int sr) =>
        Chimes.TryGet(name, out var n) ? Chimes.Render(sr, n) : Synth.Defs.ContainsKey(name) ? Synth.RenderOnce(Synth.Defs[name], sr) : null;

    // =================================================================================================
    // Abspielen
    // =================================================================================================

    private sealed class LoopHandle : IDisposable
    {
        private readonly object _lock = new();
        private RingVoice? _voice;
        public volatile bool Disposed;

        public void Attach(RingVoice v)
        {
            lock (_lock)
            {
                _voice = v;
                if (Disposed) v.Stop(0.3);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (Disposed) return;
                Disposed = true;
                _voice?.Stop(0.3);
            }
        }
    }

    /// <summary>Ein fertiger Klang einmal durch (verschränktes Stereo).</summary>
    private sealed class BufferVoice(float[] data, int sr, float gain) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sr, 2);

        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length & ~1, data.Length - _pos);
            if (n <= 0) return 0;
            for (int i = 0; i < n; i++) buffer[i] = data[_pos + i] * gain;
            _pos += n;
            return n;
        }
    }

    /// <summary>Anfang (einmal) und Schleife (dann immer wieder), verschränktes Stereo.</summary>
    private sealed record Stem(float[] Intro, float[] Loop);

    private sealed record RingSet(Stem Core, Stem? Bloom);

    /// <summary>Wie ring(): Meister-Lautstärke von <c>from</c> zu voll über <c>grow</c> s, Blüte ab einem Viertel davon.</summary>
    private sealed class RingVoice(Stem core, Stem? bloom, int sr, float volume, double from, double grow) : ISampleProvider
    {
        private long _frame;
        private volatile int _fadeFrames = -1;   // > 0: wird über so viele Bilder ausgeblendet, -1 = läuft
        private int _fadeLeft = -1;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sr, 2);

        public void Stop(double fade) => _fadeFrames = Math.Max(1, (int)(fade * sr));

        private static void Sample(Stem s, long frame, out float l, out float r)
        {
            int introFrames = s.Intro.Length / 2;
            int i;
            float[] src;
            if (frame < introFrames) { src = s.Intro; i = (int)frame * 2; }
            else { src = s.Loop; int lf = s.Loop.Length / 2; i = (int)((frame - introFrames) % lf) * 2; }
            l = src[i];
            r = src[i + 1];
        }

        public int Read(Span<float> buffer)
        {
            int frames = buffer.Length / 2;
            int fade = _fadeFrames;
            if (fade > 0 && _fadeLeft < 0) _fadeLeft = fade;
            for (int f = 0; f < frames; f++)
            {
                float fg = 1;
                if (fade > 0)
                {
                    if (_fadeLeft <= 0) return f * 2;
                    fg = _fadeLeft / (float)fade;
                    _fadeLeft--;
                }
                double t = _frame / (double)sr;
                double master = grow > 0 ? Math.Min(1, from + (1 - from) * t / grow) : 1;
                Sample(core, _frame, out float l, out float r);
                if (bloom != null)
                {
                    double bg = grow > 0 ? Math.Clamp((t - grow * 0.25) / (grow * 0.75), 0, 1) : 1;
                    if (bg > 0)
                    {
                        Sample(bloom, _frame, out float bl, out float br);
                        l += (float)(bl * bg);
                        r += (float)(br * bg);
                    }
                }
                float g = (float)master * volume * fg;
                buffer[2 * f] = l * g;
                buffer[2 * f + 1] = r * g;
                _frame++;
            }
            return frames * 2;
        }
    }

    /// <summary>Die eine gemeinsame Ausgabe. Wird beim ersten Klang geöffnet und nach einer Weile Stille geschlossen.</summary>
    private static class Output
    {
        private static readonly object Gate = new();
        private static WasapiPlayer? _out;
        private static MixingSampleProvider? _mixer;
        private static int _sr;
        private static int _active;
        private static long _lastActive;
        private static Timer? _idle;
        private static long _failedAt = -1;

        /// <summary>Abtastrate der Ausgabe (öffnet sie bei Bedarf); 0 = keine Ausgabe möglich.</summary>
        public static int SampleRate()
        {
            lock (Gate)
            {
                Ensure();
                return _mixer != null ? _sr : 0;
            }
        }

        public static void Add(ISampleProvider voice, int sr)
        {
            lock (Gate)
            {
                Ensure();
                if (_mixer == null || sr != _sr) return;
                try
                {
                    _mixer.AddMixerInput(voice);
                    Interlocked.Increment(ref _active);
                    _lastActive = Environment.TickCount64;
                }
                catch { }
            }
        }

        private static void Ensure()
        {
            if (_out != null && _mixer != null) return;
            // Nach einem Fehlschlag nicht bei jedem Klang erneut versuchen
            if (_failedAt >= 0 && Environment.TickCount64 - _failedAt < 5000) return;
            WasapiPlayer? o = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                o = new WasapiPlayerBuilder().WithDevice(dev).WithSharedMode().WithLatency(80).Build();
                var mix = o.DeviceMixFormat;
                int sr = mix != null && mix.SampleRate > 0 ? mix.SampleRate : 48000;
                var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sr, 2)) { ReadFully = true };
                mixer.MixerInputEnded += (_, _) =>
                {
                    Interlocked.Decrement(ref _active);
                    _lastActive = Environment.TickCount64;
                };
                // Gemeinsamer Modus: Kanalzahl passt die Audio-Engine selbst an
                o.Init(new SampleToWaveProvider(mixer));
                o.PlaybackStopped += OnStopped;
                o.Play();
                _out = o;
                _mixer = mixer;
                _sr = sr;
                _active = 0;
                _lastActive = Environment.TickCount64;
                _idle ??= new Timer(_ => CheckIdle(), null, 5000, 5000);
            }
            catch
            {
                try { o?.Dispose(); } catch { }
                _failedAt = Environment.TickCount64;
                _out = null;
                _mixer = null;
            }
        }

        private static void OnStopped(object? sender, StoppedEventArgs e)
        {
            // Gerät weg oder Fehler: beim nächsten Klang neu öffnen (nicht im Wiedergabe-Thread aufräumen)
            Task.Run(() =>
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(sender, _out)) return;
                    Close();
                }
            });
        }

        private static void CheckIdle()
        {
            lock (Gate)
            {
                if (_out == null) return;
                if (Volatile.Read(ref _active) <= 0 && Environment.TickCount64 - _lastActive > 20000) Close();
            }
        }

        private static void Close()
        {
            var o = _out;
            _out = null;
            _mixer = null;
            _active = 0;
            if (o == null) return;
            try { o.PlaybackStopped -= OnStopped; } catch { }
            try { o.Stop(); } catch { }
            try { o.Dispose(); } catch { }
        }
    }

    /// <summary>Ausgerechnete Klänge (einige MB je Stück), die zuletzt benutzten werden behalten.</summary>
    private static class Cache
    {
        private const int Keep = 6;
        private static readonly object Gate = new();
        private static readonly Dictionary<string, (Lazy<object?> Value, long Used)> Items = new();

        private static object? Get(string key, Func<object?> make)
        {
            Lazy<object?> lazy;
            lock (Gate)
            {
                if (Items.TryGetValue(key, out var e)) lazy = e.Value;
                else
                {
                    lazy = new Lazy<object?>(make, LazyThreadSafetyMode.ExecutionAndPublication);
                    if (Items.Count >= Keep)
                    {
                        string? oldest = null;
                        long t = long.MaxValue;
                        foreach (var kv in Items) if (kv.Value.Used < t) { t = kv.Value.Used; oldest = kv.Key; }
                        if (oldest != null) Items.Remove(oldest);
                    }
                }
                Items[key] = (lazy, Environment.TickCount64);
            }
            try { return lazy.Value; }
            catch
            {
                lock (Gate) Items.Remove(key);
                return null;
            }
        }

        public static float[]? Once(string id, int sr) =>
            Get($"{sr}|{id}|once", () => Synth.RenderOnce(Synth.Defs[id], sr)) as float[];

        public static RingSet? Ring(string id, int sr) =>
            Get($"{sr}|{id}|ring", () => Synth.RenderRing(Synth.Defs[id], sr)) as RingSet;
    }

    // =================================================================================================
    // Die kleinen Glöckchen aus pills.js (chime): Grundton und ein leiser heller Teilton, 0,17 s versetzt
    // =================================================================================================

    private static class Chimes
    {
        private static readonly Dictionary<string, double[]> Named = new()
        {
            ["water"] = [880, 1318.5],
            ["pomo-focus"] = [659.3, 880, 1318.5],
            ["pomo-break"] = [1318.5, 880, 659.3],
            ["task"] = [784, 1046.5, 1568],
            ["workout-soon"] = [659.3],
            ["workout-start"] = [880, 1174.7],
            ["workout-stop"] = [1174.7, 784],
            ["workout-done"] = [659.3, 880, 1318.5],
        };

        public static bool TryGet(string name, out double[] notes)
        {
            if (Named.TryGetValue(name, out notes!)) return true;
            notes = [];
            if (!name.StartsWith("chime:", StringComparison.Ordinal)) return false;
            var list = new List<double>();
            foreach (var part in name[6..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 20 && f < 20000) list.Add(f);
            notes = list.ToArray();
            return notes.Length > 0;
        }

        public static float[] Render(int sr, double[] notes)
        {
            const double t0 = 0.03;
            int frames = (int)Math.Ceiling((t0 + (notes.Length - 1) * 0.17 + 1.75) * sr);
            var mono = new double[frames];
            for (int n = 0; n < notes.Length; n++)
            {
                double t = t0 + n * 0.17;
                Partial(mono, sr, t, notes[n], 0.14);
                Partial(mono, sr, t, notes[n] * 2.01, 0.035);
            }
            var outp = new float[frames * 2];
            for (int i = 0; i < frames; i++) outp[2 * i] = outp[2 * i + 1] = (float)mono[i];
            return outp;
        }

        // exponentialRamp 0,0001 → Spitze in 15 ms, dann exponentiell zurück bis 1,6 s; Stopp bei 1,7 s
        private static void Partial(double[] o, int sr, double t, double f, double peak)
        {
            int i0 = (int)Math.Ceiling(t * sr), i1 = Math.Min(o.Length, (int)((t + 1.7) * sr));
            double w = 2 * Math.PI * f / sr;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / (double)sr - t;
                double g = tt < 0.015
                    ? 0.0001 * Math.Pow(peak / 0.0001, tt / 0.015)
                    : tt < 1.6 ? peak * Math.Pow(0.0001 / peak, (tt - 0.015) / 1.585) : 0.0001;
                o[i] += Math.Sin(w * (i - t * sr)) * g;
            }
        }
    }

    // =================================================================================================
    // Die Synthese (tones.js), offline in doppelter Genauigkeit
    // =================================================================================================

    private static class Synth
    {
        public delegate void PlayFn(Bus o, double t, double spb, bool core, bool bloom);

        public sealed record Def(string Id, string Name, string About, double Bpm, int Beats, double EchoBeats, double EchoFb,
            double EchoAmt, double Wet, double Gain, bool HasBloom, PlayFn Play);

        public static readonly Dictionary<string, Def> Defs = BuildDefs();

        /// <summary>Stereo-Sammelschiene (der „inp“-Knoten des Originals).</summary>
        public sealed class Bus(int sr, int frames)
        {
            public readonly int Sr = sr;
            public readonly double[] L = new double[frames], R = new double[frames];
            public int Frames => L.Length;
            public int Start(double t) => Math.Max(0, (int)Math.Ceiling(t * Sr));
            public int End(double t) => Math.Min(Frames, (int)Math.Ceiling(t * Sr));
        }

        // ---------- Noten ----------

        private static readonly int[] NoteIndex = [9, 11, 0, 2, 4, 5, 7];   // A B C D E F G

        /// <summary>'F#5' → Hz (A4 = 440).</summary>
        public static double Hz(string s)
        {
            int n = NoteIndex[s[0] - 'A'];
            int i = 1;
            if (s[i] == '#') { n++; i++; }
            else if (s[i] == 'b') { n--; i++; }
            int oct = s[i] - '0';
            int midi = 12 * (oct + 1) + n;
            return 440 * Math.Pow(2, (midi - 69) / 12.0);
        }

        // ---------- Bausteine ----------

        /// <summary>StereoPanner für ein Monosignal (gleiche Leistung); Mitte ohne Panner = voll auf beiden Seiten.</summary>
        private static (double L, double R) Pan(double pan)
        {
            if (pan == 0) return (1, 1);
            double x = (Math.Clamp(pan, -1, 1) + 1) / 2;
            return (Math.Cos(x * Math.PI / 2), Math.Sin(x * Math.PI / 2));
        }

        /// <summary>Biquad nach Web Audio (Q in dB bei Tief-/Hochpass).</summary>
        private struct Biquad
        {
            private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

            public void LowPass(double f, double qDb, int sr)
            {
                double w = 2 * Math.PI * Math.Min(f, sr / 2.0 * 0.999) / sr, cw = Math.Cos(w);
                double alpha = Math.Sin(w) / (2 * Math.Pow(10, qDb / 20));
                double a0 = 1 + alpha;
                _b0 = (1 - cw) / 2 / a0; _b1 = (1 - cw) / a0; _b2 = _b0;
                _a1 = -2 * cw / a0; _a2 = (1 - alpha) / a0;
            }

            public void HighPass(double f, double qDb, int sr)
            {
                double w = 2 * Math.PI * Math.Min(f, sr / 2.0 * 0.999) / sr, cw = Math.Cos(w);
                double alpha = Math.Sin(w) / (2 * Math.Pow(10, qDb / 20));
                double a0 = 1 + alpha;
                _b0 = (1 + cw) / 2 / a0; _b1 = -(1 + cw) / a0; _b2 = _b0;
                _a1 = -2 * cw / a0; _a2 = (1 - alpha) / a0;
            }

            public void HighShelf(double f, double gainDb, int sr)
            {
                double A = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * Math.Min(f, sr / 2.0 * 0.999) / sr, cw = Math.Cos(w);
                double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), sa = 2 * Math.Sqrt(A) * alpha;
                double a0 = (A + 1) - (A - 1) * cw + sa;
                _b0 = A * ((A + 1) + (A - 1) * cw + sa) / a0;
                _b1 = -2 * A * ((A - 1) + (A + 1) * cw) / a0;
                _b2 = A * ((A + 1) + (A - 1) * cw - sa) / a0;
                _a1 = 2 * ((A - 1) - (A + 1) * cw) / a0;
                _a2 = ((A + 1) - (A - 1) * cw - sa) / a0;
            }

            public double Step(double x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                return y;
            }
        }

        /// <summary>Bandbegrenzter Sägezahn (PolyBLEP), beginnt wie in Web Audio bei 0 und steigt.</summary>
        private static double Saw(double p, double dt)
        {
            double v = 2 * p - 1;
            if (p < dt) { double x = p / dt; v -= x + x - x * x - 1; }
            else if (p > 1 - dt) { double x = (p - 1) / dt; v -= x * x + x + x + 1; }
            return v;
        }

        private static double Tri(double p) => 1 - 4 * Math.Abs((p + 0.25) - Math.Floor(p + 0.25) - 0.5);

        /// <summary>Eine abklingende Sinusschwingung (mode).</summary>
        private static void Mode(Bus o, double t, double f, double amp, double tau, double attack, (double L, double R) pan)
        {
            if (f > o.Sr * 0.45 || amp <= 0) return;
            double a = attack > 0 ? attack : 0.003, sr = o.Sr;
            int i0 = o.Start(t), i1 = o.End(t + a + tau * 6 + 0.05);
            double w = 2 * Math.PI * f / sr, k = Math.Exp(-1 / (tau * sr)), env = -1;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / sr - t, g;
                if (tt < a) g = amp * tt / a;
                else { env = env < 0 ? amp * Math.Exp(-(tt - a) / tau) : env * k; g = env; }
                double s = Math.Sin(w * (i - t * sr)) * g;
                o.L[i] += s * pan.L;
                o.R[i] += s * pan.R;
            }
        }

        private readonly record struct FmP(double Ratio, double Index, double Bright, double Tau, double Pan = 0,
            double Cents = 0, double Attack = 0.003, double Floor = 0.06);

        /// <summary>Zwei-Operator-FM: Helligkeit und Pegel klingen je für sich ab – ein angeschlagener Ton.</summary>
        private static void Fm(Bus o, double t, double f, double v, FmP p)
        {
            var pan = Pan(p.Pan);
            double sr = o.Sr, a = p.Attack;
            int i0 = o.Start(t), i1 = o.End(t + a + p.Tau * 6 + 0.05);
            if (i0 >= i1) return;
            double det = Math.Pow(2, p.Cents / 1200), fm = f * p.Ratio, dev = p.Index * f * p.Ratio;
            double devFloor = dev * p.Floor, tt0 = i0 / sr - t;
            double devExtra = (dev - devFloor) * Math.Exp(-tt0 / p.Bright), kb = Math.Exp(-1 / (p.Bright * sr));
            double kt = Math.Exp(-1 / (p.Tau * sr)), env = -1;
            double wm = 2 * Math.PI * fm / sr, mPhase = 2 * Math.PI * fm * tt0, cPhase = 2 * Math.PI * f * det * tt0;
            double twoPiOverSr = 2 * Math.PI / sr;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / sr - t, g;
                if (tt < a) g = v * tt / a;
                else { env = env < 0 ? v * Math.Exp(-(tt - a) / p.Tau) : env * kt; g = env; }
                double s = Math.Sin(cPhase) * g;
                o.L[i] += s * pan.L;
                o.R[i] += s * pan.R;
                double inst = (f + (devFloor + devExtra) * Math.Sin(mPhase)) * det;
                cPhase += inst * twoPiOverSr;
                mPhase += wm;
                devExtra *= kb;
            }
        }

        // ---------- Instrumente ----------

        /// <summary>Weiche Synth-Marimba: runder Körper, oben ein kurzes hölzernes „Tok“.</summary>
        private static void Mallet(Bus o, double t, double f, double v, double pan)
        {
            double d = Math.Pow(523 / f, 0.4);
            Fm(o, t, f, 0.3 * v, new FmP(1, 1.4, 0.045, 0.42 * d, pan));
            Fm(o, t, f, 0.06 * v, new FmP(4, 0.5, 0.02, 0.06, pan));
        }

        /// <summary>Hohler Holzton, trockener und kürzer.</summary>
        private static void Wood(Bus o, double t, double f, double v, double pan)
        {
            Fm(o, t, f, 0.3 * v, new FmP(2, 1, 0.03, 0.2 * Math.Pow(587 / f, 0.4), pan));
            Fm(o, t, f, 0.04 * v, new FmP(5.4, 0.4, 0.012, 0.03, pan));
        }

        /// <summary>Gläserne FM-Glocke, zweimal und ein paar Cent auseinander, damit sie schimmert.</summary>
        private static void Bell(Bus o, double t, double f, double v, double pan)
        {
            double d = Math.Pow(1000 / f, 0.5);
            Fm(o, t, f, 0.14 * v, new FmP(3.5, 0.9, 0.35, 1.1 * d, pan, -4));
            Fm(o, t, f, 0.14 * v, new FmP(3.5, 0.9, 0.35, 1.1 * d, pan, 4));
        }

        /// <summary>E-Piano: warme FM-Zinke mit dem leisen metallischen Ticken des Hammers.</summary>
        private static void Ep(Bus o, double t, double f, double v, double pan)
        {
            double d = Math.Pow(440 / f, 0.3);
            Fm(o, t, f, 0.22 * v, new FmP(1, 0.9, 0.45, 1.3 * d, pan));
            Fm(o, t, f, 0.04 * v, new FmP(14, 0.25, 0.01, 0.035, pan));
        }

        /// <summary>Warmer Zupfer: zwei verstimmte Sägezähne durch einen Filter, der sich nach dem Anreißen schließt.</summary>
        private static void Pluck(Bus o, double t, double f, double v, double pan)
        {
            var pg = Pan(pan);
            double sr = o.Sr;
            int i0 = o.Start(t), i1 = o.End(t + 2);
            var lp = new Biquad();
            double f1 = f * Math.Pow(2, -9 / 1200.0), f2 = f * Math.Pow(2, 9 / 1200.0);
            double d1 = f1 / sr, d2 = f2 / sr, tt0 = i0 / sr - t;
            double p1 = Frac(0.5 + f1 * tt0), p2 = Frac(0.5 + f2 * tt0);
            double fStart = Math.Min(11000, f * 9), fEnd = f * 1.5, kt = Math.Exp(-1 / (0.3 * sr)), env = -1;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / sr - t;
                if (((i - i0) & 15) == 0) lp.LowPass(fEnd + (fStart - fEnd) * Math.Exp(-tt / 0.08), 1.5, o.Sr);
                double x = Saw(p1, d1) + Saw(p2, d2);
                p1 += d1; if (p1 >= 1) p1 -= 1;
                p2 += d2; if (p2 >= 1) p2 -= 1;
                double y = lp.Step(x), g;
                if (tt < 0.003) g = 0.11 * v * tt / 0.003;
                else { env = env < 0 ? 0.11 * v * Math.Exp(-(tt - 0.003) / 0.3) : env * kt; g = env; }
                y *= g;
                o.L[i] += y * pg.L;
                o.R[i] += y * pg.R;
            }
            Mode(o, t, f, 0.12 * v, 0.32, 0.003, pg);
        }

        private static double Frac(double x) => x - Math.Floor(x);

        /// <summary>Runder, weicher Sinuston mit etwas Oktave.</summary>
        private static void Blip(Bus o, double t, double f, double v, double pan, double tau)
        {
            var pg = Pan(pan);
            double sr = o.Sr, hold = 0.005 + tau * 0.5;
            int i0 = o.Start(t), i1 = o.End(t + tau * 7 + 0.1);
            double ph1 = 0, ph2 = 0, kt = Math.Exp(-1 / (tau * sr)), env = -1;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / sr - t, g;
                if (tt < 0.005) g = 0.28 * v * tt / 0.005;
                else if (tt < hold) g = 0.28 * v;
                else { env = env < 0 ? 0.28 * v * Math.Exp(-(tt - hold) / tau) : env * kt; g = env; }
                double glide = 1 + 0.02 * Math.Exp(-tt / 0.01);
                double s = (Math.Sin(ph1) + 0.12 * Math.Sin(ph2)) * g;
                ph1 += 2 * Math.PI * f * glide / sr;
                ph2 += 2 * Math.PI * 2 * f * glide / sr;
                o.L[i] += s * pg.L;
                o.R[i] += s * pg.R;
            }
        }

        /// <summary>Sub-Bass: runder tiefer Sinus, der schnell anschwillt und verklingt.</summary>
        private static void Sub(Bus o, double t, double f, double v, double len)
        {
            double sr = o.Sr, hold = 0.015 + len * 0.3, tau = len * 0.4;
            int i0 = o.Start(t), i1 = o.End(t + len * 3 + 0.1);
            double kt = Math.Exp(-1 / (tau * sr)), env = -1, w = 2 * Math.PI * f / sr;
            for (int i = i0; i < i1; i++)
            {
                double tt = i / sr - t, g;
                if (tt < 0.015) g = 0.3 * v * tt / 0.015;
                else if (tt < hold) g = 0.3 * v;
                else { env = env < 0 ? 0.3 * v * Math.Exp(-(tt - hold) / tau) : env * kt; g = env; }
                double ph = w * (i - t * sr);
                double s = (Math.Sin(ph) + 0.12 * Math.Sin(2 * ph)) * g;
                o.L[i] += s;
                o.R[i] += s;
            }
        }

        /// <summary>Weiche Fläche: Dreiecke mit einem Hauch Sägezahn, durch einen sich sanft öffnenden Filter.</summary>
        private static void Pad(Bus o, double t, string[] notes, double dur, double v)
        {
            double sr = o.Sr;
            int i0 = o.Start(t), i1 = o.End(t + dur * 2.2);
            int n = i1 - i0;
            if (n <= 0) return;
            var bl = new double[n];
            var br = new double[n];
            double norm = 1 / Math.Sqrt(notes.Length), tt0 = i0 / sr - t;
            for (int k = 0; k < notes.Length; k++)
            {
                double f = Hz(notes[k]);
                var pg = Pan(k % 2 == 1 ? 0.35 : -0.35);
                double fa = f * Math.Pow(2, 7 / 1200.0), fb = f * Math.Pow(2, -7 / 1200.0);
                double pt = Frac(f * tt0), pa = Frac(0.5 + fa * tt0), pb = Frac(0.5 + fb * tt0);
                double dt = f / sr, da = fa / sr, db = fb / sr;
                for (int j = 0; j < n; j++)
                {
                    double s = (0.05 * Tri(pt) + 0.016 * Saw(pa, da) + 0.016 * Saw(pb, db)) * norm;
                    pt += dt; if (pt >= 1) pt -= 1;
                    pa += da; if (pa >= 1) pa -= 1;
                    pb += db; if (pb >= 1) pb -= 1;
                    bl[j] += s * pg.L;
                    br[j] += s * pg.R;
                }
            }
            var fl = new Biquad();
            var fr = new Biquad();
            double rampEnd = dur * 0.6, kt = Math.Exp(-1 / (dur * 0.25 * sr)), env = -1;
            for (int j = 0; j < n; j++)
            {
                double tt = (i0 + j) / sr - t;
                if ((j & 15) == 0)
                {
                    double fc = tt < rampEnd ? 600 + 700 * tt / rampEnd : 1300;
                    fl.LowPass(fc, 0.4, o.Sr);
                    fr.LowPass(fc, 0.4, o.Sr);
                }
                double g;
                if (tt < dur * 0.3) g = v * tt / (dur * 0.3);
                else if (tt < dur * 0.8) g = v;
                else { env = env < 0 ? v * Math.Exp(-(tt - dur * 0.8) / (dur * 0.25)) : env * kt; g = env; }
                o.L[i0 + j] += fl.Step(bl[j]) * g;
                o.R[i0 + j] += fr.Step(br[j]) * g;
            }
        }

        // Harfe: gezupfte Saite (Karplus–Strong), je Ton einmal ausgerechnet
        private static readonly Dictionary<string, (double[] Buf, double F0)> Strings = new();
        private static readonly object StringsGate = new();

        private static (double[] Buf, double F0) StringBuf(int sr, double f, double tau)
        {
            int P = Math.Max(4, (int)Math.Round(sr / f + 0.5));
            double f0 = sr / (P - 0.5);
            string key = $"{sr}|{P}|{tau.ToString(CultureInfo.InvariantCulture)}";
            lock (StringsGate)
            {
                if (Strings.TryGetValue(key, out var hit)) return hit;
                if (Strings.Count > 64) Strings.Clear();
                int len = (int)Math.Round(sr * Math.Min(4.5, tau * 6));
                double rho = Math.Exp(-1 / (tau * f0));
                var line = new double[P];
                var rnd = new Random(P * 7919 + (int)(tau * 100));
                double lp = 0, mean = 0;
                for (int i = 0; i < P; i++)
                {
                    lp += (rnd.NextDouble() * 2 - 1 - lp) * 0.45;
                    double x = i / (double)P, tri = x < 0.25 ? x / 0.25 : (1 - x) / 0.75;
                    line[i] = 0.4 * lp + 0.8 * tri;
                    mean += line[i] / P;
                }
                for (int i = 0; i < P; i++) line[i] -= mean;
                var buf = new double[len];
                for (int i = 0, k = 0; i < len; i++)
                {
                    double a = line[k], b = line[(k + 1) % P];
                    buf[i] = a;
                    line[k] = rho * 0.5 * (a + b);
                    k = (k + 1) % P;
                }
                var r = (buf, f0);
                Strings[key] = r;
                return r;
            }
        }

        private static void Harp(Bus o, double t, double f, double v, double pan)
        {
            var pg = Pan(pan);
            double tau = Math.Round(0.8 * Math.Pow(440 / f, 0.5), 2, MidpointRounding.AwayFromZero);
            var (buf, f0) = StringBuf(o.Sr, f, tau);
            double rate = f / f0, sr = o.Sr;
            var lp = new Biquad();
            lp.LowPass(Math.Min(7000, f * 7), 1, o.Sr);
            int i0 = o.Start(t);
            for (int i = i0; i < o.Frames; i++)
            {
                double pos = (i / sr - t) * sr * rate;
                int k = (int)pos;
                if (k + 1 >= buf.Length) break;
                double x = buf[k] + (buf[k + 1] - buf[k]) * (pos - k);
                double y = lp.Step(x) * 0.2 * v;
                o.L[i] += y * pg.L;
                o.R[i] += y * pg.R;
            }
        }

        // ---------- Die Mischung: trocken, Ping-Pong-Echo, kleiner Hall, sanfter Kompressor, weiche Höhen ----------

        private static readonly Dictionary<int, (double[] L, double[] R, double Scale)> Halls = new();

        private static (double[] L, double[] R, double Scale) Hall(int sr)
        {
            lock (Halls)
            {
                if (Halls.TryGetValue(sr, out var h)) return h;
                int len = (int)Math.Round(sr * 2.8), pre = (int)Math.Round(sr * 0.016);
                var ch = new double[2][];
                var rnd = new Random(20240611);
                double power = 0;
                for (int c = 0; c < 2; c++)
                {
                    var d = ch[c] = new double[len];
                    double lp = 0;
                    for (int i = pre; i < len; i++)
                    {
                        double tt = (i - pre) / (double)sr;
                        lp += (rnd.NextDouble() * 2 - 1 - lp) * (0.08 + 0.8 * Math.Exp(-tt * 2.2));
                        d[i] = lp * Math.Exp(-tt / 0.55) * Math.Min(1, tt / 0.008);
                        power += d[i] * d[i];
                    }
                }
                // Normalisierung des ConvolverNode (wie im Browser)
                power = Math.Sqrt(power / (2.0 * len));
                if (!double.IsFinite(power) || power < 0.000125) power = 0.000125;
                double scale = 0.00125 / power * 44100.0 / sr;
                h = (ch[0], ch[1], scale);
                Halls[sr] = h;
                return h;
            }
        }

        private static float[] Master(Def def, Bus inp)
        {
            int n = inp.Frames, sr = inp.Sr;
            double spb = 60 / def.Bpm;

            // Ping-Pong-Echo (nur Mitte, ohne Bass): hp 300 → lp 4000 → links ↔ rechts
            var hp = new Biquad();
            hp.HighPass(300, 1, sr);
            var lpe = new Biquad();
            lpe.LowPass(4000, 1, sr);
            var m = new double[n];
            for (int i = 0; i < n; i++) m[i] = lpe.Step(hp.Step(0.5 * (inp.L[i] + inp.R[i])));
            int D = Math.Max(1, (int)Math.Round(def.EchoBeats * spb * sr));
            var dl = new double[n];
            var dr = new double[n];
            for (int i = D; i < n; i++)
            {
                dl[i] = m[i - D] + def.EchoFb * dr[i - D];
                dr[i] = def.EchoFb * dl[i - D];
            }
            for (int i = 0; i < n; i++) { dl[i] *= def.EchoAmt; dr[i] *= def.EchoAmt; }

            // Hall: (trocken + Echo) × wet durch den Raum
            var (hl, hr, scale) = Hall(sr);
            var wl = new double[n];
            var wr = new double[n];
            for (int i = 0; i < n; i++)
            {
                wl[i] = def.Wet * (inp.L[i] + dl[i]);
                wr[i] = def.Wet * (inp.R[i] + dr[i]);
            }
            var (cl, cr) = Convolve(wl, wr, hl, hr, n);

            // Höhen −3 dB ab 7 kHz, Kompressor, Pegel
            var sl = new Biquad();
            var sR = new Biquad();
            sl.HighShelf(7000, -3, sr);
            sR.HighShelf(7000, -3, sr);
            var outp = new float[n * 2];
            var comp = new Compressor(sr);
            for (int i = 0; i < n; i++)
            {
                double l = sl.Step(inp.L[i] + cl[i] * scale + dl[i]);
                double r = sR.Step(inp.R[i] + cr[i] * scale + dr[i]);
                double g = comp.Gain(l, r) * def.Gain;
                outp[2 * i] = (float)(l * g);
                outp[2 * i + 1] = (float)(r * g);
            }
            return outp;
        }

        /// <summary>Kompressor ähnlich dem DynamicsCompressorNode: −18 dB, Knie 12, 2,5:1, 6 ms / 350 ms, Ausgleichsverstärkung.</summary>
        private sealed class Compressor(int sr)
        {
            private const double T = -18, W = 12, Ratio = 2.5;
            private readonly double _att = 1 - Math.Exp(-1 / (0.006 * sr)), _rel = 1 - Math.Exp(-1 / (0.35 * sr));
            private readonly double _makeup = Math.Pow(1 / Db(Curve(0)), 0.6);
            private double _gDb;

            private static double Db(double db) => Math.Pow(10, db / 20);

            private static double Curve(double x)
            {
                if (x <= T) return x;
                if (x <= T + W) return x + (1 / Ratio - 1) * (x - T) * (x - T) / (2 * W);
                return T + W + (1 / Ratio - 1) * W / 2 + (x - T - W) / Ratio;
            }

            public double Gain(double l, double r)
            {
                double peak = Math.Max(Math.Abs(l), Math.Abs(r));
                double x = 20 * Math.Log10(peak + 1e-12);
                double want = Curve(x) - x;
                _gDb += (want - _gDb) * (want < _gDb ? _att : _rel);
                return Db(_gDb) * _makeup;
            }
        }

        /// <summary>Faltung beider Kanäle mit je eigenem Impuls (FFT, beide Kanäle in einem komplexen Durchgang).</summary>
        private static (double[] L, double[] R) Convolve(double[] xl, double[] xr, double[] hl, double[] hr, int n)
        {
            int size = 1;
            while (size < n + hl.Length) size <<= 1;
            var ar = new double[size];
            var ai = new double[size];
            var br = new double[size];
            var bi = new double[size];
            Array.Copy(xl, ar, Math.Min(n, xl.Length));
            Array.Copy(xr, ai, Math.Min(n, xr.Length));
            Array.Copy(hl, br, hl.Length);
            Array.Copy(hr, bi, hr.Length);
            Fft(ar, ai, false);
            Fft(br, bi, false);
            // Kanäle trennen, multiplizieren, wieder zusammenlegen: C = YL + i·YR
            for (int k = 0; k <= size / 2; k++)
            {
                int j = (size - k) & (size - 1);
                double aRe = ar[k], aIm = ai[k], cRe = ar[j], cIm = -ai[j];   // conj(A[N−k])
                double xlRe = (aRe + cRe) / 2, xlIm = (aIm + cIm) / 2;
                double xrRe = (aIm - cIm) / 2, xrIm = -(aRe - cRe) / 2;
                double bRe = br[k], bIm = bi[k], dRe = br[j], dIm = -bi[j];
                double hlRe = (bRe + dRe) / 2, hlIm = (bIm + dIm) / 2;
                double hrRe = (bIm - dIm) / 2, hrIm = -(bRe - dRe) / 2;
                double ylRe = xlRe * hlRe - xlIm * hlIm, ylIm = xlRe * hlIm + xlIm * hlRe;
                double yrRe = xrRe * hrRe - xrIm * hrIm, yrIm = xrRe * hrIm + xrIm * hrRe;
                // C[k] = YL + i·YR; für j: YL* und YR* (reelle Signale)
                ar[k] = ylRe - yrIm; ai[k] = ylIm + yrRe;
                if (j != k) { ar[j] = ylRe + yrIm; ai[j] = -ylIm + yrRe; }
            }
            Fft(ar, ai, true);
            var ol = new double[n];
            var or = new double[n];
            Array.Copy(ar, ol, n);
            Array.Copy(ai, or, n);
            return (ol, or);
        }

        private static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            var cosT = new double[n / 2];
            var sinT = new double[n / 2];
            double sign = inverse ? 1 : -1;
            for (int i = 0; i < n / 2; i++)
            {
                double a = 2 * Math.PI * i / n;
                cosT[i] = Math.Cos(a);
                sinT[i] = sign * Math.Sin(a);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1, step = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0, t = 0; k < half; k++, t += step)
                    {
                        int a = i + k, b = a + half;
                        double wr = cosT[t], wi = sinT[t];
                        double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
            if (inverse)
            {
                double s = 1.0 / n;
                for (int i = 0; i < n; i++) { re[i] *= s; im[i] *= s; }
            }
        }

        // ---------- Ausrechnen ----------

        /// <summary>Schleifenlänge: ganze 128er-Blöcke, wie im Original.</summary>
        private static int LoopFrames(Def d, int sr) => Math.Max(1, (int)Math.Round(d.Beats * 60.0 * sr / d.Bpm / 128)) * 128;

        private static int Passes(Def d) => d.Beats * 60 / d.Bpm < 3 ? 4 : 3;

        private static float[] Render(Def def, int sr, int passes, double tail, bool core, bool bloom)
        {
            double spb = 60 / def.Bpm;
            int N = LoopFrames(def, sr);
            double L = N / (double)sr;
            var bus = new Bus(sr, N * passes + (int)Math.Round(tail * sr));
            for (int i = 0; i < passes; i++) def.Play(bus, i * L, spb, core, bloom);
            return Master(def, bus);
        }

        /// <summary>Einmal durch, beide Schichten, Nachklang 3 s.</summary>
        public static float[] RenderOnce(Def def, int sr) => Render(def, sr, 1, 3, true, true);

        public static RingSet RenderRing(Def def, int sr)
        {
            Stem MakeStem(bool core, bool bloom)
            {
                int n = Passes(def), N = LoopFrames(def, sr);
                var buf = Render(def, sr, n, 0, core, bloom);
                var intro = new float[N * (n - 1) * 2];
                var loop = new float[N * 2];
                Array.Copy(buf, 0, intro, 0, intro.Length);
                Array.Copy(buf, intro.Length, loop, 0, loop.Length);
                return new Stem(intro, loop);
            }
            var coreStem = MakeStem(true, false);
            var bloomStem = def.HasBloom ? MakeStem(false, true) : null;
            return new RingSet(coreStem, bloomStem);
        }

        // ---------- Die Klänge ----------

        private delegate void Inst(Bus o, double t, double f, double v, double pan);

        private static void Seq(Bus o, double t, double spb, Inst inst, params (string N, double B, double V, double P)[] notes)
        {
            foreach (var (n, b, v, p) in notes) inst(o, t + b * spb, Hz(n), v, p);
        }

        private static Dictionary<string, Def> BuildDefs()
        {
            var list = new List<Def>
            {
                new("glint", "Glint", "Eine leichte Synth-Marimba-Figur, die funkelt und nachhallt.",
                    120, 4, 0.75, 0.28, 0.24, 0.3, 0.299, false, (o, t, spb, _, _) =>
                    {
                        Seq(o, t, spb, Mallet, ("E5", 0, 0.9, -0.2), ("B5", 0.5, 0.7, 0.2), ("G#5", 0.75, 0.6, -0.1), ("E6", 1, 0.85, 0.15),
                            ("B5", 1.5, 0.65, -0.2), ("C#6", 2, 0.8, 0.2), ("B5", 2.5, 0.65, -0.15), ("G#5", 2.75, 0.55, 0.1),
                            ("F#5", 3, 0.75, -0.1), ("G#5", 3.5, 0.6, 0.15));
                        Sub(o, t, Hz("E3"), 0.5, 0.6);
                    }),
                new("beacon", "Beacon", "Weiche, runde Töne in gleichmäßigem Muster, mit Echo links und rechts.",
                    120, 4, 0.75, 0.3, 0.26, 0.25, 0.279, false, (o, t, spb, _, _) =>
                    {
                        (string N, double B, double V, double Tau)[] notes =
                            [("A5", 0, 0.8, 0.06), ("A5", 0.25, 0.7, 0.06), ("C#6", 0.5, 0.9, 0.16), ("A5", 2, 0.8, 0.06), ("A5", 2.25, 0.7, 0.06), ("E6", 2.5, 0.9, 0.2)];
                        for (int i = 0; i < notes.Length; i++)
                        {
                            var (n, b, v, tau) = notes[i];
                            Blip(o, t + b * spb, Hz(n), v, i % 3 == 2 ? 0 : (i % 2 == 1 ? 0.15 : -0.15), tau);
                        }
                    }),
                new("dewdrop", "Dewdrop", "Glasglocken, die sanft fallen, mit langem Nachhall.",
                    100, 4, 0.75, 0.35, 0.3, 0.4, 0.335, false, (o, t, spb, _, _) =>
                        Seq(o, t, spb, Bell, ("B5", 0, 0.85, -0.3), ("E6", 0.5, 0.7, 0.2), ("G#6", 1, 0.75, -0.1), ("F#6", 2, 0.7, 0.3),
                            ("E6", 2.5, 0.6, -0.2), ("B5", 3, 0.6, 0.1))),
                new("bamboo", "Bamboo", "Eine hölzerne Melodie, leicht und trocken, über einem sanften tiefen Ton.",
                    112, 4, 0.5, 0.2, 0.16, 0.28, 0.355, false, (o, t, spb, _, _) =>
                    {
                        Seq(o, t, spb, Wood, ("D5", 0, 0.9, -0.15), ("F#5", 0.5, 0.7, 0.15), ("A5", 1, 0.8, -0.1), ("B5", 1.75, 0.75, 0.2),
                            ("A5", 2.25, 0.7, -0.15), ("F#5", 2.5, 0.65, 0.1), ("E5", 3, 0.75, -0.1), ("F#5", 3.5, 0.6, 0.15));
                        Sub(o, t, Hz("D3"), 0.45, 0.5);
                    }),
                new("horizon", "Horizon", "Eine helle, hoffnungsvolle Zupfmelodie über einem sanften Akkord; Bass und Glocken blühen auf, während sie wächst.",
                    104, 8, 0.75, 0.3, 0.2, 0.35, 0.275, true, (o, t, spb, core, bloom) =>
                    {
                        if (core)
                        {
                            Seq(o, t, spb, Pluck, ("A5", 0, 0.9, -0.1), ("F#5", 0.5, 0.7, 0.15), ("E5", 1.5, 0.75, -0.15), ("F#5", 2, 0.7, 0.1),
                                ("A5", 2.5, 0.8, -0.1), ("C#6", 3.5, 0.85, 0.15), ("B5", 4, 0.9, -0.1), ("A5", 4.5, 0.7, 0.15),
                                ("F#5", 5, 0.75, -0.15), ("D5", 6, 0.75, 0.1), ("E5", 6.5, 0.7, -0.1), ("F#5", 7, 0.75, 0.15));
                            Pad(o, t, ["D3", "A3", "E4", "F#4", "C#5"], 4 * spb, 0.5);
                            Pad(o, t + 4 * spb, ["G3", "D4", "F#4", "A4", "B4"], 4 * spb, 0.5);
                        }
                        if (bloom)
                        {
                            foreach (var (n, b, v) in new[] { ("D2", 0.0, 0.9), ("D2", 1.5, 0.55), ("D2", 2.5, 0.7), ("G2", 4.0, 0.9), ("G2", 5.5, 0.55), ("A2", 6.5, 0.7) })
                                Sub(o, t + b * spb, Hz(n), v, 0.6);
                            Seq(o, t, spb, Bell, ("A6", 0, 0.35, 0.3), ("C#7", 3.5, 0.3, -0.3), ("B6", 4, 0.35, 0.3), ("F#6", 7, 0.3, -0.3));
                        }
                    }),
                new("silk", "Silk", "Sanfte E-Piano-Akkorde, über die Glasglocken treiben.",
                    92, 8, 0.75, 0.32, 0.22, 0.45, 0.204, true, (o, t, spb, core, bloom) =>
                    {
                        if (core)
                        {
                            void Roll(string[] notes, double b, double v)
                            {
                                for (int i = 0; i < notes.Length; i++) Ep(o, t + b * spb + i * 0.025, Hz(notes[i]), v, (i - 2) * 0.15);
                            }
                            Roll(["F3", "C4", "E4", "G4", "A4"], 0, 0.8);
                            Roll(["E4", "G4", "A4"], 2.5, 0.5);
                            Roll(["D3", "A3", "C4", "E4", "F4"], 4, 0.8);
                            Roll(["C4", "E4", "F4"], 6.5, 0.5);
                            Seq(o, t, spb, Bell, ("C6", 1, 0.55, 0.3), ("A5", 1.5, 0.45, -0.2), ("E6", 3, 0.5, 0.2), ("D6", 5, 0.55, -0.3),
                                ("A5", 5.5, 0.45, 0.2), ("C6", 7, 0.5, -0.1));
                        }
                        if (bloom)
                        {
                            Sub(o, t, Hz("F2"), 0.85, 0.9);
                            Sub(o, t + 4 * spb, Hz("D2"), 0.85, 0.9);
                            Seq(o, t, spb, Bell, ("G6", 2.5, 0.3, -0.35), ("F6", 6.5, 0.3, 0.35));
                        }
                    }),
                new("rise", "Aufgang", "Eine Synth-Marimba, die Akkord für Akkord aufsteigt; ein pulsierender Bass und ein Flächenklang blühen auf.",
                    116, 8, 0.75, 0.25, 0.18, 0.3, 0.288, true, (o, t, spb, core, bloom) =>
                    {
                        string[][] chords = [["E5", "G#5", "B5", "E6"], ["E5", "G#5", "C#6", "E6"], ["E5", "A5", "C#6", "E6"], ["F#5", "B5", "D#6", "F#6"]];
                        if (core)
                        {
                            for (int k = 0; k < chords.Length; k++)
                                for (int i = 0; i < chords[k].Length; i++)
                                    Mallet(o, t + (k * 2 + i * 0.5) * spb, Hz(chords[k][i]), 0.65 + (i == 3 ? 0.1 : 0) + 0.05 * k, (i - 1.5) * 0.2);
                        }
                        if (bloom)
                        {
                            string[] bass = ["E2", "C#2", "A1", "B1"];
                            for (int k = 0; k < bass.Length; k++)
                                for (int e = 0; e < 4; e++) Sub(o, t + (k * 2 + e * 0.5) * spb, Hz(bass[k]), e != 0 ? 0.5 : 0.75, 0.22);
                            Pad(o, t, ["E3", "B3", "G#4"], 4 * spb, 0.35);
                            Pad(o, t + 4 * spb, ["A3", "E4", "C#5"], 4 * spb, 0.35);
                        }
                    }),
                new("harbor", "Harbor", "Eine verträumte Harfe, die durch drei Akkorde rollt, mit Echo; Flächenklang und Glocken blühen auf.",
                    120, 9, 1.5, 0.32, 0.22, 0.45, 0.822, true, (o, t, spb, core, bloom) =>
                    {
                        string[][] bars = [["C3", "G3", "E4", "B4", "G4", "E4"], ["A2", "E3", "C4", "B4", "G4", "E4"], ["F2", "C3", "A3", "E4", "B4", "A4"]];
                        if (core)
                        {
                            for (int k = 0; k < bars.Length; k++)
                                for (int i = 0; i < bars[k].Length; i++)
                                    Harp(o, t + (k * 3 + i * 0.5) * spb, Hz(bars[k][i]), i == 3 ? 0.95 : 0.75, (i - 2.5) * 0.15);
                        }
                        if (bloom)
                        {
                            Pad(o, t, ["C4", "G4", "E5"], 3 * spb, 0.3);
                            Pad(o, t + 3 * spb, ["A3", "E4", "C5"], 3 * spb, 0.3);
                            Pad(o, t + 6 * spb, ["F3", "C4", "A4"], 3 * spb, 0.3);
                            Seq(o, t, spb, Bell, ("E6", 0, 0.35, 0.3), ("B5", 3, 0.35, -0.3), ("A5", 6, 0.35, 0.3));
                        }
                    }),
            };
            return list.ToDictionary(d => d.Id);
        }
    }
}

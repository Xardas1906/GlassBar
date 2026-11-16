using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GlassBar.NativeStill.Audio;

// =====================================================================================================
// Klangwelten (Port von soundscape.js): Regen, Bach, Wind, Wellen, Feuer und braunes Rauschen, live erzeugt.
// Kette wie im Original: Klänge → Ein-/Ausblenden → Ducken (Musik) → Lautstärke → Lautsprecher, dazu ein
// kurzer, weicher Raum und ein Abgriff für die drei Balken der Pille.
// Ein gemeinsames Ausgabegerät (WASAPI, geteilt), nur geöffnet, solange etwas klingt. Alle öffentlichen
// Aufrufe sind threadsicher (UI-, Render- oder sonstiger Thread).
// =====================================================================================================

public static class Soundscape
{
    /// <summary>Die Klänge in der Reihenfolge der Kacheln.</summary>
    public static readonly string[] Ids = { "rain", "stream", "wind", "waves", "fire", "brown" };
    public static readonly string[] Names = { "Regen", "Bach", "Wind", "Wellen", "Feuer", "Braunes Rauschen" };
    /// <summary>Stufe, die ein Klang hat, bis man sie selbst setzt.</summary>
    public const double DefaultLevel = 0.75;
    private const string StoreKey = "sound.mix";

    /// <summary>Wetterlage wie im Original ("clear", "partly", "cloudy", "fog", "drizzle", "rain", "storm", "snow") oder null.
    /// Von außen zu setzen (z. B. <c>() =&gt; Soundscape.KindFromWmo(weather.Code)</c>); nötig für „Wetter“.</summary>
    public static Func<string?>? WeatherKind;
    /// <summary>Läuft gerade eine Fokus-Einheit des Pomodoro? Von außen zu setzen; nötig für „Fokus“.</summary>
    public static Func<bool>? FocusActive;

    private static readonly object Gate = new();
    private static readonly double[] Levels = new double[ScapeEngine.Count];
    private static readonly bool[] On = new bool[ScapeEngine.Count];
    private static readonly bool[] Mine = new bool[ScapeEngine.Count];
    private static string _matched = "";
    private static bool _playing, _byFocus, _music, _wasFocus, _loaded;
    private static int _solo = -1;
    private static double _lastDuck = 1;
    private static int _version;

    // Einstellungen (zuletzt aus Apply)
    private static double _vol = 0.8;
    private static string _duckMode = "quiet";
    private static bool _soundMatch, _soundFocus, _soundsOn = true;
    private static readonly double[] _tuneSig = new double[9];
    private static ScapeTunes? _tunes;

    // Ausgabe
    private static ScapeEngine? _engine;
    private static ScapeOut? _out;
    private static WasapiPlayer? _device;
    private static bool _opening;
    private static Timer? _closeTimer;

    private static readonly Dictionary<string, (int k, double v)[]> ForWeather = new()
    {
        ["rain"] = new[] { (0, 0.8) },
        ["drizzle"] = new[] { (0, 0.55) },
        ["storm"] = new[] { (0, 0.85), (2, 0.55) },
        ["snow"] = new[] { (2, 0.45), (4, 0.65) },
        ["fog"] = new[] { (3, 0.6) },
        ["cloudy"] = new[] { (2, 0.45), (1, 0.4) },
        ["partly"] = new[] { (1, 0.65) },
        ["clear"] = new[] { (1, 0.6), (2, 0.25) },
    };

    // ---------------------------------------------------------------------------------------------
    // Zustand lesen
    // ---------------------------------------------------------------------------------------------

    /// <summary>Spielt die Mischung gerade (Abspielen gedrückt)?</summary>
    public static bool Playing { get { lock (Gate) { Load(); return _playing; } } }
    /// <summary>Wurde sie von einer Fokus-Einheit gestartet?</summary>
    public static bool ByFocus { get { lock (Gate) return _byFocus; } }
    /// <summary>Der Klang, dessen Einstellungen offen sind (allein zu hören), oder null.</summary>
    public static string? Solo { get { lock (Gate) return _solo >= 0 ? Ids[_solo] : null; } }
    /// <summary>Für welche Wetterlage die Mischung gewählt wurde ("" = selbst gewählt).</summary>
    public static string Matched { get { lock (Gate) return _matched; } }
    /// <summary>Zählt bei jeder Änderung hoch (zum günstigen Neuzeichnen).</summary>
    public static int Version => Volatile.Read(ref _version);

    public static int IndexOf(string id) => Array.IndexOf(Ids, id);

    public static bool IsOn(string id) { int k = IndexOf(id); if (k < 0) return false; lock (Gate) { Load(); return On[k]; } }
    public static double Level(string id) { int k = IndexOf(id); if (k < 0) return 0; lock (Gate) { Load(); return Levels[k]; } }
    /// <summary>Ist dieser Klang gerade zu hören (in der laufenden Mischung oder allein)?</summary>
    public static bool IsPlaying(string id)
    {
        int k = IndexOf(id);
        if (k < 0) return false;
        lock (Gate) { Load(); return _solo >= 0 ? _solo == k : _playing && On[k]; }
    }
    public static bool AnyOn { get { lock (Gate) { Load(); return AnyOnLocked(); } } }

    /// <summary>Faktor des Duckens gerade (1 = voll, 0.3 = leiser, 0 = pausiert).</summary>
    public static double DuckNow { get { lock (Gate) return DuckNowLocked(); } }
    /// <summary>Faktor allein für die Mischung (ohne Sonderfall „allein“).</summary>
    public static double DuckForMusic { get { lock (Gate) return DuckFor(_music); } }

    /// <summary>Wie weit die dynamische Intensität den Regen gerade trägt (−0,7 … 1).</summary>
    public static double RainSurge => _engine?.Surge ?? 0;

    /// <summary>Drei Bänder (tief, mittel, hoch) für die Balken der Pille, je 0.22..1; ohne Klang die Ruhelage.</summary>
    public static float Meter(int band)
    {
        var o = _out;
        bool live;
        lock (Gate) live = (_playing || _solo >= 0) && o != null;
        if (!live) return band == 0 ? 0.42f : band == 1 ? 0.78f : 0.58f;
        return o!.Meter(band);
    }

    /// <summary>Wetter-Code (WMO, wie open-meteo) → Wetterlage wie im Original.</summary>
    public static string? KindFromWmo(int code) => code switch
    {
        0 => "clear",
        1 or 2 => "partly",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "rain",
        71 or 73 or 75 or 77 or 85 or 86 => "snow",
        95 or 96 or 99 => "storm",
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------
    // Bedienen
    // ---------------------------------------------------------------------------------------------

    /// <summary>Kachel angetippt: läuft nichts, startet sie mit diesem Klang; sonst schaltet sie ihn um.</summary>
    public static void Toggle(string id)
    {
        int k = IndexOf(id);
        if (k < 0) return;
        lock (Gate)
        {
            Load();
            _matched = "";
            if (!_playing) { On[k] = true; StartLocked(false); }
            else
            {
                On[k] = !On[k];
                if (!AnyOnLocked()) StopLocked();
                else SendLocked();
            }
            Changed();
            SaveLocked();
        }
    }

    /// <summary>Stufe über die Kachel ziehen (0..1): unter 4 % ist er aus; die Stufe gehört dann einem selbst.
    /// Gespeichert wird mit <see cref="Save"/> (am Ende des Ziehens).</summary>
    public static void SetLevel(string id, double v)
    {
        int k = IndexOf(id);
        if (k < 0) return;
        lock (Gate)
        {
            Load();
            _matched = "";
            bool on = v >= 0.04;
            Levels[k] = Math.Max(0.04, Math.Min(1, v));
            Mine[k] = true;
            On[k] = on;
            if (on && !_playing) StartLocked(false);
            else if (!AnyOnLocked() && _playing) StopLocked();
            else SendLocked();
            Changed();
        }
    }

    /// <summary>Stufe in den Einstellungen des Klangs; zurück auf den Standard gibt sie wieder dem Wetter.</summary>
    public static void SetOwnLevel(string id, double v)
    {
        int k = IndexOf(id);
        if (k < 0) return;
        lock (Gate)
        {
            Load();
            Levels[k] = Math.Max(0.04, Math.Min(1, v));
            Mine[k] = Math.Abs(v - DefaultLevel) > 1e-9;
            SendLocked();
            Changed();
        }
    }

    /// <summary>Abspielen (wie die Taste): die Wetter-Mischung, wenn das an ist, sonst die letzte (beim ersten Mal Regen).</summary>
    public static void Play(bool focus = false)
    {
        lock (Gate)
        {
            Load();
            ChooseMixLocked();
            StartLocked(focus);
            Changed();
        }
    }

    public static void Stop()
    {
        lock (Gate) { StopLocked(); Changed(); }
    }

    public static void PlayPause()
    {
        if (Playing) Stop();
        else Play();
    }

    /// <summary>Einen Klang allein hören (während seine Einstellungen offen sind); null = zurück zur Mischung oder Stille.</summary>
    public static void SetSolo(string? id)
    {
        int k = id == null ? -1 : IndexOf(id);
        lock (Gate)
        {
            Load();
            if (k == _solo) return;
            if (k >= 0)
            {
                _solo = k;
                _closeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                EnsureOutputLocked();
                SendLocked();
                DuckToLocked();
                _out?.SetFade(1, 0.4);
            }
            else
            {
                _solo = -1;
                DuckToLocked();
                if (_playing) SendLocked();
                else { _out?.SetFade(0, 0.45); ScheduleCloseLocked(); }
            }
            Changed();
        }
    }

    /// <summary>Läuft Musik? Dann je nach soundDuck leiser (quiet), pausiert (pause) oder unverändert (keep).</summary>
    public static void Duck(bool music)
    {
        lock (Gate)
        {
            if (music == _music) return;
            _music = music;
            DuckToLocked();
            Changed();
        }
    }

    /// <summary>Einstellungen übernehmen (Lautstärke, Ducken, Abstimmungen). Günstig, darf jedes Bild aufgerufen werden.</summary>
    public static void Apply(StillValues s)
    {
        double vol = Math.Clamp(s.Num("soundVol"), 0, 1);
        string duck = s.Str("soundDuck");
        bool match = s.Bool("soundMatch"), focus = s.Bool("soundFocus"), on = s.Bool("sounds");
        double ri = s.Num("rainIntensity"), rm = s.Num("rainMuffle"), rd = s.Bool("rainDynamic") ? 1 : 0;
        double si = s.Num("streamIntensity"), sd = s.Num("streamDistance"), wi = s.Num("windIntensity");
        double vi = s.Num("wavesIntensity"), vd = s.Num("wavesDistance"), bs = s.Num("brownSoft");
        lock (Gate)
        {
            if (vol != _vol) { _vol = vol; _out?.SetVolume(vol * vol, 0.06); }
            if (duck != _duckMode) { _duckMode = duck; DuckToLocked(); Changed(); }
            if (match != _soundMatch || focus != _soundFocus) { _soundMatch = match; _soundFocus = focus; Changed(); }
            Span<double> sig = stackalloc double[9] { ri, rm, rd, si, sd, wi, vi, vd, bs };
            bool same = _tunes != null;
            for (int i = 0; i < 9 && same; i++) same = sig[i] == _tuneSig[i];
            if (!same)
            {
                sig.CopyTo(_tuneSig);
                _tunes = MakeTunes(ri, rm, rd > 0, si, sd, wi, vi, vd, bs);
                _engine?.Post(_tunes);
            }
            if (on != _soundsOn)
            {
                _soundsOn = on;
                if (!on) { _solo = -1; if (_playing) StopLocked(); else if (_out != null) { _out.SetFade(0, 0.45); ScheduleCloseLocked(); } Changed(); }
            }
        }
    }

    /// <summary>Etwa viermal pro Sekunde: Fokus-Einheiten folgen, der Wetterlage folgen, Ducken nachführen.</summary>
    public static void Tick()
    {
        bool focus = false;
        try { focus = FocusActive?.Invoke() ?? false; } catch { }
        string? kind = null;
        try { kind = WeatherKind?.Invoke(); } catch { }
        lock (Gate)
        {
            Load();
            DuckToLocked();
            if (focus != _wasFocus)
            {
                _wasFocus = focus;
                if (_soundFocus && _soundsOn)
                {
                    if (focus && !_playing) { ChooseMixLocked(kind); StartLocked(true); Changed(); }
                    else if (!focus && _playing && _byFocus) { StopLocked(); Changed(); }
                }
            }
            // Auf das Wetter abgestimmt und das Wetter schlägt um: mitgehen.
            if (_playing && _matched.Length > 0 && _soundMatch && kind != null && kind != _matched && ForWeather.TryGetValue(kind, out var m))
            {
                UseMixLocked(m, kind);
                SaveLocked();
                SendLocked();
                Changed();
            }
        }
    }

    /// <summary>Stufen und Auswahl sichern (gebündelt über StillState).</summary>
    public static void Save() { lock (Gate) SaveLocked(); }

    /// <summary>Alles anhalten und das Gerät schließen (beim Beenden).</summary>
    public static void Shutdown()
    {
        WasapiPlayer? d;
        lock (Gate)
        {
            _playing = false; _solo = -1;
            d = _device; _device = null; _out = null;
            _closeTimer?.Dispose(); _closeTimer = null;
        }
        try { d?.Stop(); } catch { }
        try { d?.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------------------------------------
    // Innen (alles unter Gate)
    // ---------------------------------------------------------------------------------------------

    private static void Changed() => Interlocked.Increment(ref _version);
    private static bool AnyOnLocked() { foreach (var b in On) if (b) return true; return false; }

    private static double DuckFor(bool music) =>
        !music ? 1 : _duckMode switch { "quiet" => 0.3, "pause" => 0, _ => 1 };

    // Ein Klang, der gerade abgestimmt wird, wird für Musik genauso leiser wie die Mischung; pausiert die
    // Mischung für Musik, spielt er stattdessen leise.
    private static double DuckNowLocked() => _solo >= 0 ? Math.Max(0.3, DuckFor(_music)) : DuckFor(_music);

    // Schnell herunter, wenn Musik beginnt (oder ein Klang allein kommt); sanft wieder hinauf.
    private static void DuckToLocked()
    {
        double duck = DuckNowLocked();
        if (duck == _lastDuck) return;
        _out?.SetDuck(duck, duck < _lastDuck ? 0.5 : _solo >= 0 ? 0.25 : 1.2);
        _lastDuck = duck;
    }

    private static void FillLevels(double[] target)
    {
        for (int k = 0; k < ScapeEngine.Count; k++)
            target[k] = _solo >= 0 ? (k == _solo ? Levels[k] : 0) : _playing && On[k] ? Levels[k] : 0;
    }

    private static void SendLocked()
    {
        if (_engine != null && (_playing || _solo >= 0)) FillLevels(_engine.Target);
    }

    private static void StartLocked(bool focus)
    {
        _playing = true;
        _byFocus = focus;
        _closeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        EnsureOutputLocked();
        SendLocked();
        _out?.SetFade(1, 0.6);
    }

    private static void StopLocked()
    {
        _playing = false;
        _byFocus = false;
        // Ein Klang, der abgestimmt wird, spielt weiter; er hört auf, wenn seine Einstellungen schließen.
        if (_solo >= 0 || _out == null) return;
        _out.SetFade(0, 0.45);
        ScheduleCloseLocked();
    }

    private static void UseMixLocked((int k, double v)[] m, string kind)
    {
        for (int k = 0; k < ScapeEngine.Count; k++) On[k] = false;
        foreach (var (k, v) in m)
        {
            On[k] = true;
            if (!Mine[k]) Levels[k] = v;
        }
        _matched = kind;
    }

    private static void ChooseMixLocked(string? kind = null)
    {
        if (_soundMatch)
        {
            if (kind == null) { try { kind = WeatherKind?.Invoke(); } catch { } }
            if (kind != null && ForWeather.TryGetValue(kind, out var m)) { UseMixLocked(m, kind); SaveLocked(); return; }
        }
        if (!AnyOnLocked()) On[0] = true;
        SaveLocked();
    }

    // ---------- Speichern ----------

    private sealed class MixStore
    {
        public Dictionary<string, double> Levels { get; set; } = new();
        public Dictionary<string, bool> On { get; set; } = new();
        public string Matched { get; set; } = "";
        public Dictionary<string, bool> Mine { get; set; } = new();
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var st = StillState.Get<MixStore?>(StoreKey, null);
        for (int k = 0; k < ScapeEngine.Count; k++)
        {
            string id = Ids[k];
            Levels[k] = st != null && st.Levels.TryGetValue(id, out var l) && l >= 0.04 ? Math.Min(1, l) : DefaultLevel;
            On[k] = st != null && st.On.TryGetValue(id, out var o) && o;
            Mine[k] = st != null && st.Mine.TryGetValue(id, out var m) && m;
        }
        _matched = st?.Matched ?? "";
    }

    private static void SaveLocked()
    {
        var st = new MixStore { Matched = _matched };
        for (int k = 0; k < ScapeEngine.Count; k++)
        {
            st.Levels[Ids[k]] = Math.Round(Levels[k], 4);
            if (On[k]) st.On[Ids[k]] = true;
            if (Mine[k]) st.Mine[Ids[k]] = true;
        }
        StillState.Set(StoreKey, st);
    }

    // ---------- Abstimmung aus den erweiterten Einstellungen ----------

    private static double Far(double d) => d >= 7990 ? 0 : Math.Round(d);
    private static double FarGain(double b, double d) => Math.Pow(b / Math.Min(8000, d), 0.2);
    private static readonly (double x, double db)[] SoftDb = { (0, -0.9), (0.3, 0), (0.6, 1), (1, 5.5) };

    private static ScapeTunes MakeTunes(double rainI, double rainMuffle, bool rainDyn, double streamI, double streamD,
        double windI, double wavesI, double wavesD, double brownSoft)
    {
        // Regen: die Intensität setzt die ferne Gischt (0,35×–1,5×, das Zischen mit) und öffnet die Wand etwas;
        // eine dichtere Wand gibt etwa die Hälfte des Pegels zurück, den sie nimmt.
        double bed = 0.35 + 1.15 * rainI;
        double muffle = Math.Round(Math.Max(200, rainMuffle) * (1 + 0.2 * rainI));
        double rainAir = 0.05 * bed / 0.9, rainGain = 1.514 * Math.Pow(5474 / muffle, 0.2);
        // Bach: mehr Wasser – mehr Blasen und Gluckern, volleres Rauschen, schnellere Schübe.
        double k = 0.4 + 1.2 * streamI, sk = Math.Sqrt(k);
        double burst = Math.Round(3.4 * (0.8 + 0.2 * k), 2);
        // Wind: stärkere Böen und tieferes Wummern, wenn er zunimmt; ruhiger klingt er ferner.
        double fo = Math.Max(0, 0.5 - windI) * 1.2, wd = Math.Round(7000 * Math.Pow(1200 / 7000.0, fo));
        // Wellen: größer, dichter, mit mehr Gischt und Grollen.
        double wk = 0.4 + 1.2 * wavesI, wg = 1.25 - 0.5 * wavesI;
        // Braunes Rauschen: weicher rundet die Höhen ab; der verlorene Pegel wird zurückgegeben (gemessen).
        double dB = 0;
        for (int i = 1; i < SoftDb.Length; i++)
        {
            var (x0, y0) = SoftDb[i - 1]; var (x1, y1) = SoftDb[i];
            if (brownSoft <= x1) { dB = y0 + ((y1 - y0) * (brownSoft - x0)) / (x1 - x0); break; }
        }
        return new ScapeTunes(
            bed, rainAir, muffle, rainGain, rainDyn,
            80 * k, 0.65 * k, 0.5 * sk, 0.25 * sk, burst, Far(streamD), 1.265 * FarGain(6744, Math.Max(200, streamD)),
            0.25 * 2 * windI, 90 * (0.7 + 0.6 * windI), wd, 1.4 * FarGain(7000, wd),
            0.6 * wk, 1 * wk, 6 * wg, 10 * wg, 0.7 * wk, 0.8 * wk, 0.45 * Math.Sqrt(wk), Far(wavesD), 1.096 * FarGain(5000, Math.Max(200, wavesD)),
            brownSoft, 1.167 * Math.Pow(10, dB / 20));
    }

    // ---------- Gerät ----------

    private static void EnsureOutputLocked()
    {
        if (_device != null || _opening) return;
        _opening = true;
        Task.Run(OpenOutput);
    }

    private static void OpenOutput()
    {
        int rate = 48000;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var ac = dev.CreateAudioClient();
            rate = ac.MixFormat.SampleRate;
        }
        catch { }
        if (rate < 8000 || rate > 384000) rate = 48000;

        WasapiPlayer? device = null;
        try
        {
            ScapeOut o;
            lock (Gate)
            {
                if (!_playing && _solo < 0) { _opening = false; return; }
                if (_engine == null || (int)_engine.Sr != rate)
                {
                    _engine = new ScapeEngine(rate);
                    if (_tunes != null) _engine.Post(_tunes);
                }
                o = _out = new ScapeOut(_engine);
                o.SetVolume(_vol * _vol, 0);
                _lastDuck = DuckNowLocked();
                o.SetDuck(_lastDuck, 0);
                FillLevels(_engine.Target);
                o.SetFade(1, _solo >= 0 ? 0.4 : 0.6);
            }
            device = new WasapiPlayerBuilder().WithSharedMode().WithLatency(120).Build();
            device.Init(new SampleToWaveProvider(o));
            device.PlaybackStopped += OnStopped;
            device.Play();
            lock (Gate)
            {
                _device = device;
                _opening = false;
                if (!_playing && _solo < 0) { o.SetFade(0, 0.45); ScheduleCloseLocked(); }
            }
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            lock (Gate) { _opening = false; _out = null; }
        }
    }

    // Gerät weg (abgesteckt, Standard gewechselt): beim nächsten Bedarf neu öffnen.
    private static void OnStopped(object? sender, StoppedEventArgs e)
    {
        WasapiPlayer? d = null;
        lock (Gate)
        {
            if (sender is WasapiPlayer w && ReferenceEquals(w, _device)) { d = _device; _device = null; _out = null; }
            if (d != null && (_playing || _solo >= 0) && e.Exception != null)
            {
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1000); lock (Gate) { if (_playing || _solo >= 0) EnsureOutputLocked(); } });
            }
        }
        if (d != null) ThreadPool.QueueUserWorkItem(_ => { try { d.Dispose(); } catch { } });
    }

    // Ist das Ausblenden durch, ruht alles (kostet dann nichts) und das Gerät wird geschlossen.
    private static void ScheduleCloseLocked()
    {
        _closeTimer ??= new Timer(_ => CloseIfSilent());
        _closeTimer.Change(2600, Timeout.Infinite);
    }

    private static void CloseIfSilent()
    {
        WasapiPlayer? d;
        lock (Gate)
        {
            if (_playing || _solo >= 0 || _device == null) return;
            if (_out != null && _out.FadeLevel > 1e-3) { _closeTimer?.Change(500, Timeout.Infinite); return; }
            d = _device;
            _device = null;
            _out = null;
            if (_engine != null) Array.Clear(_engine.Target);
        }
        try { d.PlaybackStopped -= OnStopped; d.Stop(); } catch { }
        try { d.Dispose(); } catch { }
        lock (Gate) { if (_device == null && _engine != null && !_opening) _engine.ResetLevels(); }
    }
}

/// <summary>
/// Die Kette nach den Klängen als ISampleProvider (Stereo, Float): Ein-/Ausblenden → Ducken → Lautstärke,
/// dazu ein kurzer, weicher, dunkler werdender Raum (statt des Faltungshalls im Original ein kleines
/// Rückkopplungsnetz) und drei Pegelbänder für die Balken.
/// </summary>
internal sealed class ScapeOut : ISampleProvider
{
    private readonly ScapeEngine _e;
    private readonly double _sr;
    private readonly double[] _l = new double[ScapeEngine.Block], _r = new double[ScapeEngine.Block];
    private readonly float[] _block = new float[ScapeEngine.Block * 2];
    private int _pos = ScapeEngine.Block, _len = ScapeEngine.Block;

    // Gleitende Werte (setTargetAtTime)
    private double _fade, _duck = 1, _vol = 1;
    private double _fadeT, _duckT = 1, _volT = 1;
    private double _fadeK = 1, _duckK = 1, _volK = 1;

    // Raum
    private readonly Room _room;
    private const double Wet = 0.16;

    // Pegel
    private double _lpM, _subM, _hpPrev, _hpM, _e0, _e1, _e2;
    private int _meterN;
    private readonly double _lpK, _subK, _hpK;
    private readonly float[] _meter = { 0.42f, 0.78f, 0.58f };

    public ScapeOut(ScapeEngine e)
    {
        _e = e;
        _sr = e.Sr;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)e.Sr, 2);
        _room = new Room(_sr);
        _lpK = 1 - Math.Exp(-2 * Math.PI * 470 / _sr);
        _hpK = 1 - Math.Exp(-2 * Math.PI * 2900 / _sr);
        _subK = 1 - Math.Exp(-2 * Math.PI * 94 / _sr);
    }

    public WaveFormat WaveFormat { get; }
    public double FadeLevel => Volatile.Read(ref _fade);

    private double K(double tau) => tau <= 0 ? 1 : 1 - Math.Exp(-1 / (tau * _sr));
    public void SetFade(double v, double tau) { _fadeK = K(tau); Volatile.Write(ref _fadeT, v); if (tau <= 0) _fade = v; }
    public void SetDuck(double v, double tau) { _duckK = K(tau); Volatile.Write(ref _duckT, v); if (tau <= 0) _duck = v; }
    public void SetVolume(double v, double tau) { _volK = K(tau); Volatile.Write(ref _volT, v); if (tau <= 0) _vol = v; }

    public float Meter(int band) => Volatile.Read(ref _meter[Math.Clamp(band, 0, 2)]);

    public int Read(Span<float> buffer)
    {
        int count = buffer.Length & ~1, w = 0;
        while (w < count)
        {
            if (_pos >= _len) Produce();
            int take = Math.Min((_len - _pos) * 2, count - w);
            _block.AsSpan(_pos * 2, take).CopyTo(buffer.Slice(w, take));
            _pos += take / 2;
            w += take;
        }
        return count;
    }

    private void Produce()
    {
        int n = ScapeEngine.Block;
        _e.Render(_l, _r, n);
        double fadeT = Volatile.Read(ref _fadeT), duckT = Volatile.Read(ref _duckT), volT = Volatile.Read(ref _volT);
        double fk = _fadeK, dk = _duckK, vk = _volK;
        double fade = _fade, duck = _duck, vol = _vol;
        double lpM = _lpM, subM = _subM, hpPrev = _hpPrev, hpM = _hpM, e0 = _e0, e1 = _e1, e2 = _e2;
        for (int i = 0; i < n; i++)
        {
            fade += (fadeT - fade) * fk;
            duck += (duckT - duck) * dk;
            vol += (volT - vol) * vk;
            double g = fade * duck, l = _l[i] * g, r = _r[i] * g;
            // Abgriff für die Balken (nach dem Ducken, vor der Lautstärke)
            double m = (l + r) * 0.5;
            lpM += (m - lpM) * _lpK;
            subM += (m - subM) * _subK;
            hpM = (1 - _hpK) * (hpM + m - hpPrev); hpPrev = m;
            double mid = m - lpM - hpM;
            double low = lpM - subM;
            e0 += low * low; e1 += mid * mid; e2 += hpM * hpM;
            l *= vol; r *= vol;
            _room.Run(l, r, out double wl, out double wr);
            _block[2 * i] = (float)(l + wl * Wet);
            _block[2 * i + 1] = (float)(r + wr * Wet);
        }
        _fade = fade; _duck = duck; _vol = vol;
        _lpM = lpM; _subM = subM; _hpPrev = hpPrev; _hpM = hpM;
        _pos = 0; _len = n;
        _meterN += n;
        if (_meterN >= 1024)
        {
            // Etwa wie getByteFrequencyData (−100…−30 dB) über die Bänder des Originals.
            Band(0, e0, 4); Band(1, e1, 26); Band(2, e2, 89);
            e0 = e1 = e2 = 0;
            _meterN = 0;
        }
        _e0 = e0; _e1 = e1; _e2 = e2;
    }

    private void Band(int i, double energy, int bins)
    {
        double rms = Math.Sqrt(energy / Math.Max(1, _meterN));
        // Blackman-Fenster, 512 Punkte: weißes Rauschen mit Effektivwert r liegt je Bin bei etwa r − 32 dB;
        // 10 dB Abstand, weil die Bänder hier gröber getrennt sind (sonst stünden die Balken oben an).
        double binDb = 20 * Math.Log10(rms + 1e-9) - 10 * Math.Log10(bins) + 10 * Math.Log10(256) - 32 - 10;
        double v = Math.Clamp((binDb + 100) / 70, 0, 1);
        Volatile.Write(ref _meter[i], (float)Math.Clamp(0.25 + (v - 0.3 - i * 0.04) * 2.2, 0.22, 1));
    }

    /// <summary>Ein kurzer, weicher Nachhall, der beim Verklingen dunkler wird (≈ e^(−t/0,32 s), 12 ms Vorlauf).</summary>
    private sealed class Room
    {
        private static readonly double[] Lengths = { 0.0297, 0.0371, 0.0411, 0.0437, 0.0503, 0.0571, 0.0613, 0.0683 };
        private readonly double[][] _lines;
        private readonly int[] _idx;
        private readonly double[] _g, _lp, _x = new double[8];
        private readonly double[] _pre;
        private int _pi;
        private readonly double _damp, _scale;

        public Room(double sr)
        {
            _lines = new double[8][];
            _idx = new int[8];
            _g = new double[8];
            _lp = new double[8];
            for (int i = 0; i < 8; i++)
            {
                int len = Math.Max(16, (int)(Lengths[i] * sr));
                _lines[i] = new double[len];
                _g[i] = Math.Exp(-(len / sr) / 0.32);
            }
            _pre = new double[Math.Max(1, (int)(0.012 * sr)) * 2];
            _damp = 1 - Math.Exp(-2 * Math.PI * 3500 / sr);
            // So eingestellt, dass der Raum etwa so viel trägt wie die normierte Faltung des Originals.
            _scale = 0.16;
        }

        public void Run(double inL, double inR, out double outL, out double outR)
        {
            // Vorlauf (12 ms)
            int pl = _pre.Length / 2;
            double dl = _pre[2 * _pi], dr = _pre[2 * _pi + 1];
            _pre[2 * _pi] = inL; _pre[2 * _pi + 1] = inR;
            if (++_pi >= pl) _pi = 0;

            var x = _x;
            for (int i = 0; i < 8; i++) { var line = _lines[i]; x[i] = line[_idx[i]]; }
            outL = (x[0] + x[2] + x[4] + x[6]) * _scale;
            outR = (x[1] + x[3] + x[5] + x[7]) * _scale;
            // Hadamard-Mischung (8×8, normiert)
            Fwht(x);
            const double norm = 0.35355339059327373; // 1/√8
            for (int i = 0; i < 8; i++)
            {
                double v = x[i] * norm * _g[i];
                // dunkler mit jeder Runde
                _lp[i] += (v - _lp[i]) * _damp;
                var line = _lines[i];
                line[_idx[i]] = _lp[i] + ((i & 1) == 0 ? dl : dr) * 0.5;
                if (++_idx[i] >= line.Length) _idx[i] = 0;
            }
        }

        private static void Fwht(double[] a)
        {
            for (int h = 1; h < 8; h <<= 1)
                for (int i = 0; i < 8; i += h << 1)
                    for (int j = i; j < i + h; j++)
                    {
                        double u = a[j], v = a[j + h];
                        a[j] = u + v; a[j + h] = u - v;
                    }
        }
    }
}

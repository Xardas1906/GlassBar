using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using GlassBar.NativeStill.Ui;

namespace GlassBar.NativeStill;

/// <summary>
/// Zeichnet „Still“ ohne Browser: Lichtfeld (Direct3D, Shader aus field.js) klein rendern, auf Bildschirmgröße
/// ziehen samt Körnung, dann Datum/Wetter/Uhr mit Direct2D darüber. Läuft in einem eigenen Thread mit der
/// eingestellten Bildrate; pausiert (schläft) komplett, solange der Hintergrund verdeckt ist.
/// </summary>
public sealed class StillRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FieldParams
    {
        public Vector2 Res; public float Time; public float Energy;
        public float Light; public Vector2 Shift; public float FlowOn;
        public Vector3 Base; public float FlowGlow;
        public Vector3 C0; public float P0;
        public Vector3 C1; public float P1;
        public Vector3 C2; public float P2;
        public Vector3 C3; public float P3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleParams { public float Grain; public float Gain; public Vector2 Pad; public Vector3 Base; public float Pad2; }

    private static readonly Dictionary<string, int> QualityWidth = new() { ["low"] = 560, ["balanced"] = 900, ["high"] = 1400 };

    // Teile, die von außen gefüttert werden
    public StillPalette Palette { get; } = new();
    public FlowField Flow { get; } = new();
    public StillValues Settings { get; private set; } = new();

    private readonly IntPtr _hwnd;
    private int _width, _height;
    private volatile bool _resizePending;

    // Direct3D
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private Vortice.DirectComposition.IDCompositionDevice? _dcomp;
    private Vortice.DirectComposition.IDCompositionTarget? _dcompTarget;
    private Vortice.DirectComposition.IDCompositionVisual? _dcompVisual;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _fieldPs, _upPs;
    /// <summary>Weitere Effekte hinter Uhr und Pillen (Einstellung WallpaperField), gleiche Eingänge wie das Lichtfeld.</summary>
    private readonly Dictionary<string, ID3D11PixelShader> _effectPs = new();
    private Vector2 _parallax;   // geglättete Mausposition für Bokeh (−0,5 … 0,5)
    private ID3D11Buffer? _fieldCb, _upCb;
    private ID3D11SamplerState? _sampler;
    private ID3D11Texture2D? _small, _full, _flowTex;
    private ID3D11RenderTargetView? _smallRtv, _fullRtv, _wetRtv;
    // Regen auf der Scheibe (glass.js): liest das fertige Licht, schreibt in _wet, das dann zurück nach _full geht
    private RainGlass? _rain;
    private ID3D11ShaderResourceView? _fullSrv;
    private ID3D11Texture2D? _wet;
    private ID3D11ShaderResourceView? _smallSrv, _flowSrv;
    private int _smallW, _smallH, _flowCols, _flowRows;

    // Direct2D
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2d;
    private Vortice.DirectWrite.IDWriteFactory? _dwrite;
    private ID2D1Bitmap1? _targetBmp, _fieldBmp;
    // Hintergrund ohne Lichtfeld: Zeichenfläche auf _full und das Windows-Hintergrundbild
    private ID2D1Bitmap1? _fullTarget, _wallBmp;
    private string? _wallKey;
    private double _wallCheck;

    /// <summary>
    /// "Light" = Lichtfeld, "Aurora" = Polarlicht, "Caustics" = Kaustik, "Bokeh" = Bokeh-Lichter, "Smoke" = Nebel,
    /// "Image" = Windows-Hintergrundbild, "Solid" = einfarbig (Grundfarbe der Palette).
    /// </summary>
    public static volatile string BackgroundMode = "Light";
    /// <summary>Tempo und Stärke je Effekt (1 = normal; aus Config.EffectSpeed/EffectIntensity).</summary>
    private static volatile Dictionary<string, (float Speed, float Gain)> _tune = new();

    /// <summary>
    /// Effekt samt Tempo und Stärke aus den Einstellungen übernehmen (gilt für alle Bildschirme). Beim automatischen
    /// Wechsel bestimmt <see cref="WallpaperRotation"/> den Effekt. Der Wechsel wird weich überblendet.
    /// </summary>
    public static void ApplyEffect(Config c)
    {
        var tune = new Dictionary<string, (float, float)>();
        foreach (var m in WallpaperRotation.Effects)
            tune[m] = ((float)(Config.Percent(c.EffectSpeed, m) / 100), (float)(Config.Percent(c.EffectIntensity, m) / 100));
        _tune = tune;
        BackgroundMode = WallpaperRotation.Current(c, DateTime.Now);
    }

    // gerade gezeigter Effekt und Überblendung (1 = ganz sichtbar, 0 = nur Grundfarbe)
    private string? _shownMode;
    private double _fade;
    private StillTextLayer? _text;
    private readonly TextFrame _frame = new();

    // Zeichenschleife
    private readonly Thread _thread;
    private volatile bool _running = true, _paused;
    private readonly AutoResetEvent _wake = new(false);
    private double _fieldTime = Random.Shared.NextDouble() * 600;

    // Musik-Leuchten (processAudio im Original)
    private Func<float[]?>? _audioSource;
    private double _energy, _avg, _kick;

    // Wetter (vom UI-Thread gesetzt)
    private volatile string? _weatherText, _weatherGlyph, _weatherPlace;

    // ---------- Oberfläche (Pillen, Karten, Ebenen) ----------
    /// <summary>Die Oberfläche; Module werden vor dem ersten Bild über <see cref="Ui"/> registriert.</summary>
    public StillUi Ui { get; } = new();
    private readonly UiContext _uiCtx = new();
    private UiFonts? _uiFonts;
    private readonly GlassPainter _glass = new();
    private volatile MediaState _media = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<PointerEvent> _pointer = new();
    private Vector2 _lastPointer;
    private bool _pointerInside;
    private readonly float[] _bands = new float[4];
    private float[]? _spectrum;
    private DateTime _start = DateTime.Now;

    /// <summary>Wiedergabe steuern (vom Einbau gesetzt).</summary>
    public MediaControls Controls { get; set; } = new();
    /// <summary>Kurze Meldung anzeigen (vom Einbau gesetzt, z. B. an die Toast-Ebene).</summary>
    public Action<string> Toast { get; set; } = _ => { };
    /// <summary>Etwas beansprucht die Bühne (Wecker, Training) – vom Einbau als Abfrage gesetzt.</summary>
    public Func<float> StageBusy { get; set; } = () => 0;
    /// <summary>Liegt unter der Maus gerade etwas Bedienbares? (für die Maus-Weiterleitung, threadsicher)</summary>
    public volatile bool OverUi;
    /// <summary>Ein Element der Oberfläche hält die Maus gerade fest (threadsicher).</summary>
    public volatile bool PointerCaptured;

    public void SetMedia(MediaState m) { _media = m; _wake.Set(); }

    /// <summary>Maus-Ereignis in Fensterpixeln (aus jedem Thread).</summary>
    public void PostPointer(PointerEvent e)
    {
        _pointer.Enqueue(e);
        // gleich hier prüfen, damit der Hook beim Drücken schon weiß, ob der Klick der Oberfläche gehört
        if (e.Kind == PointerKind.Move) _lastPointer = e.Pos;
    }

    /// <summary>Uhr, Datum und Wetter zeichnen (aus = nur das Licht).</summary>
    public bool ShowText { get; set; } = true;
    /// <summary>Eigener Effekt für diesen Bildschirm (Nebenbildschirme); null = BackgroundMode wie der Hauptbildschirm.</summary>
    public string? ModeOverride { get; set; }
    /// <summary>Pillen und Karten zeigen (Nebenbildschirme: nach Einstellung, ohne den Hintergrund neu aufzubauen).</summary>
    public volatile bool ShowUi = true;

    /// <summary>Bilder pro Sekunde (WallpaperFps aus den GlassBar-Einstellungen).</summary>
    public int Fps { get; set; } = 30;

    public StillRenderer(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);
        Flow.Resize(_width, _height);
        _thread = new Thread(Loop) { IsBackground = true, Name = "GlassBar Still (nativ)", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Einstellungen verbinden (lebendes Objekt, z. B. NativeStillStore.Values).</summary>
    public void Bind(StillValues values)
    {
        Settings = values;
        Palette.SetTheme(values.Str("theme"));
        values.Changed += key =>
        {
            if (key == "theme") Palette.SetTheme(values.Str("theme"));
            _wake.Set();
        };
    }

    /// <summary>Quelle für das Audiospektrum (128 Werte wie bei Wallpaper Engine, 64 je Kanal).</summary>
    public void SetAudioSource(Func<float[]?> source) => _audioSource = source;

    public void SetWeather(string? text, string? glyph, string? place)
    {
        _weatherText = text; _weatherGlyph = glyph; _weatherPlace = place;
        _wake.Set();
    }

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);
        _resizePending = true;
        _wake.Set();
    }

    /// <summary>Verdeckt: gar nichts mehr zeichnen (kostet dann praktisch nichts).</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _wake.Set();
    }

    // ---------- Schleife ----------

    private void Loop()
    {
        try { Init(); }
        catch (Exception ex) { TaskbarWindow.Debug("Still nativ: Start fehlgeschlagen: " + ex); return; }

        var sw = Stopwatch.StartNew();
        double last = sw.Elapsed.TotalSeconds;
        while (_running)
        {
            if (_paused) { _wake.WaitOne(500); last = sw.Elapsed.TotalSeconds; continue; }
            double now = sw.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - last, 0, 0.1);
            last = now;
            try
            {
                if (_resizePending) { _resizePending = false; CreateSizeDependent(); Flow.Resize(_width, _height); }
                Frame(dt);
            }
            catch (Exception ex)
            {
                TaskbarWindow.Debug("Still nativ: Bild fehlgeschlagen: " + ex.Message);
                // Gerät verloren (Treiber neu gestartet o. Ä.): alles neu aufbauen
                try { ReleaseAll(); Init(); } catch { Thread.Sleep(2000); }
            }
            double frameLen = 1.0 / Math.Clamp(Fps, 10, 60);
            int wait = (int)((last + frameLen - sw.Elapsed.TotalSeconds) * 1000);
            if (wait > 0) _wake.WaitOne(wait);
        }
        ReleaseAll();
    }

    /// <summary>
    /// Statt Lichtfeld: Grundfarbe der Palette und – bei „Image“ – das Windows-Hintergrundbild (wie „Ausfüllen“).
    /// Alles andere (Uhr, Pillen, Glas, Regen) arbeitet unverändert darauf.
    /// </summary>
    private void DrawPlainBackground(ID3D11DeviceContext ctx, double dt, string mode, float fade)
    {
        var c = Palette.Base;
        ctx.ClearRenderTargetView(_fullRtv!, new Vortice.Mathematics.Color4(c.X, c.Y, c.Z, 1));
        if (mode != "Image") return;

        _wallCheck -= dt;
        if (_wallCheck <= 0) { _wallCheck = 3; LoadWallpaper(); }
        if (_wallBmp == null || _fullTarget == null) return;

        var size = _wallBmp.PixelSize;
        float k = Math.Max((float)_width / size.Width, (float)_height / size.Height);
        float w = size.Width * k, h = size.Height * k;
        var dest = new Vortice.RawRectF((_width - w) / 2, (_height - h) / 2, (_width + w) / 2, (_height + h) / 2);
        _d2d!.Target = _fullTarget;
        _d2d.BeginDraw();
        _d2d.DrawBitmap(_wallBmp, dest, fade, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
        _d2d.EndDraw();
        _d2d.Target = null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder buf, uint winIni);

    /// <summary>Aktuelles Windows-Hintergrundbild laden (nur wenn sich Datei oder Änderungszeit geändert haben).</summary>
    private void LoadWallpaper()
    {
        try
        {
            var sb = new System.Text.StringBuilder(520);
            SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0);
            string path = sb.ToString();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // Windows-Spotlight & Co.: zuletzt umgewandeltes Hintergrundbild
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (!File.Exists(path)) { _wallBmp?.Dispose(); _wallBmp = null; _wallKey = null; return; }
            }
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (key == _wallKey) return;
            _wallKey = key;
            _wallBmp?.Dispose(); _wallBmp = null;

            using var src = new System.Drawing.Bitmap(path);
            // höchstens doppelt so groß wie der Bildschirm laden
            float k = Math.Min(1f, Math.Min(2f * _width / src.Width, 2f * _height / src.Height));
            int bw = Math.Max(1, (int)(src.Width * k)), bh = Math.Max(1, (int)(src.Height * k));
            using var bmp = new System.Drawing.Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, bw, bh);
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bw, bh), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                _wallBmp = _d2d!.CreateBitmap(new Vortice.Mathematics.SizeI(bw, bh), data.Scan0, (uint)data.Stride,
                    new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex) { TaskbarWindow.Debug("Still: Hintergrundbild: " + ex.Message); _wallBmp = null; }
    }

    private void Frame(double dt)
    {
        var s = Settings;
        Palette.Step(dt);
        ProcessAudio(dt);
        // Effektwechsel weich: alten Effekt zur Grundfarbe ausblenden, dann den neuen einblenden (je 0,8 s)
        string wanted = ModeOverride ?? BackgroundMode;
        if (_shownMode == null) { _shownMode = wanted; _fade = 1; }
        else if (wanted != _shownMode)
        {
            _fade -= dt / 0.8;
            if (_fade <= 0) { _fade = 0; _shownMode = wanted; }
        }
        else if (_fade < 1) _fade = Math.Min(1, _fade + dt / 0.8);
        string mode = _shownMode;
        float fade = (float)(_fade * _fade * (3 - 2 * _fade));
        var tune = _tune.TryGetValue(mode, out var tv) ? tv : (Speed: 1f, Gain: 1f);

        _fieldTime += dt * s.Num("motion") * Palette.MoodSpeed * tune.Speed;
        bool flowChanged = Flow.Step(dt, s);

        // Qualität: Breite des kleinen Felds
        int maxW = QualityWidth.TryGetValue(s.Str("quality"), out var q) ? q : 900;
        double scale = Math.Min(1, (double)maxW / _width);
        int sw = Math.Max(2, (int)Math.Round(_width * scale)), sh = Math.Max(2, (int)Math.Round(_height * scale));
        if (sw != _smallW || sh != _smallH) CreateSmall(sw, sh);
        if (Flow.Cols != _flowCols || Flow.Rows != _flowRows) { CreateFlow(); flowChanged = true; }
        if (flowChanged) _ctx!.UpdateSubresource<float>(Flow.Data, _flowTex!, 0, (uint)(Flow.Cols * 16));

        var ctx = _ctx!;
        ID3D11PixelShader? effect = mode == "Light" ? _fieldPs : _effectPs.GetValueOrDefault(mode);
        if (effect == null)
        {
            DrawPlainBackground(ctx, dt, mode, fade);
            goto afterField;
        }

        // Bokeh: Lichter weichen der Maus leicht aus (geglättet; ohne Maus zurück zur Mitte)
        var target = _pointerInside && _width > 0 && _height > 0
            ? new Vector2(_lastPointer.X / _width - 0.5f, 0.5f - _lastPointer.Y / _height) : Vector2.Zero;
        _parallax += (target - _parallax) * (float)(1 - Math.Exp(-dt * 2.5));

        // 1) Lichtfeld klein
        var p = new FieldParams
        {
            Res = new Vector2(_smallW, _smallH),
            Time = (float)_fieldTime,
            Energy = (float)Math.Min(3, (_energy * 2 + _kick * 3) * Math.Pow(s.Num("audioGlow"), 1.8)),
            Light = Palette.Light,
            FlowOn = Flow.Active && s.Bool("water") ? 1 : 0,
            FlowGlow = (float)(s.Num("waterShine") * 2.6),
            Base = Palette.Base, C0 = Palette.C0, C1 = Palette.C1, C2 = Palette.C2, C3 = Palette.C3,
            Shift = mode == "Bokeh" ? _parallax : Vector2.Zero,   // das Lichtfeld bleibt wie im Original
        };
        ctx.UpdateSubresource(in p, _fieldCb!);
        ctx.OMSetRenderTargets(_smallRtv!);
        ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _smallW, _smallH));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(effect);
        ctx.PSSetConstantBuffer(0, _fieldCb);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetShaderResource(0, _flowSrv);
        ctx.Draw(3, 0);

        // 2) Auf volle Größe ziehen + Körnung
        var u = new UpscaleParams { Grain = (float)(s.Num("grain") * (Palette.IsLightTheme ? 0.75 : 0.5)), Gain = tune.Gain * fade, Base = Palette.Base };
        ctx.UpdateSubresource(in u, _upCb!);
        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets(_fullRtv!);
        ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _width, _height));
        ctx.PSSetShader(_upPs);
        ctx.PSSetConstantBuffer(0, _upCb);
        ctx.PSSetShaderResource(0, _smallSrv);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);

    afterField:
        // 2b) Regen auf der Scheibe, wenn der Regen-Klang spielt
        if (_rain != null)
        {
            _rain.LightTheme = Palette.IsLightTheme;
            _rain.Update(dt, Audio.Soundscape.IsPlaying("rain") && s.Bool("rainGlass"), s, Audio.Soundscape.RainSurge, _width, _height);
            if (_rain.Active)
            {
                _rain.Render(ctx, _fullSrv!, _wetRtv!, _width, _height);
                ctx.CopyResource(_full!, _wet!);
            }
        }

        // 3) Ins Bild kopieren, Texte darüber
        using (var back = _swap!.GetBuffer<ID3D11Texture2D>(0))
            ctx.CopyResource(back, _full!);
        _frame.Now = DateTime.Now;
        _frame.Settings = s;
        _frame.Accent = Palette.Accent;
        _frame.LightTheme = Palette.IsLightTheme;
        _frame.WeatherText = s.Bool("weather") ? _weatherText : null;
        _frame.WeatherGlyph = _weatherGlyph;
        _frame.WeatherPlace = _weatherPlace;
        _d2d!.Target = _targetBmp;
        _d2d.BeginDraw();
        try
        {
            _frame.StageBusy = SafeBusy();
            if (ShowText) _text?.Draw(_d2d, _fieldBmp!, _width, _height, _frame, dt);
            DrawUi(dt);
        }
        finally { _d2d.EndDraw(); }

        var hr = _swap.Present(0, PresentFlags.None);
        if (_dumpPending) { _dumpPending = false; DumpDebug(hr); }
    }

    // ---------- Fehlersuche (vorübergehend) ----------
    private bool _dumpPending = File.Exists(Path.Combine(Config.Folder, "still-dump"));

    private void DumpDebug(SharpGen.Runtime.Result hr)
    {
        try
        {
            var d = _full!.Description;
            var stDesc = new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            using var st = _device!.CreateTexture2D(stDesc);
            _ctx!.CopyResource(st, _full);
            var map = _ctx.Map(st, 0, MapMode.Read);
            var bmp = new System.Drawing.Bitmap((int)d.Width, (int)d.Height, (int)map.RowPitch, System.Drawing.Imaging.PixelFormat.Format32bppRgb, map.DataPointer);
            bmp.Save(Path.Combine(Config.Folder, "still-dump.png"));
            _ctx.Unmap(st, 0);
            TaskbarWindow.Debug($"Still nativ: Bild gespeichert, Present={hr}, Feld {_smallW}x{_smallH}, Palette base={Palette.Base} c0={Palette.C0}");
        }
        catch (Exception ex) { TaskbarWindow.Debug("Still nativ: Dump fehlgeschlagen: " + ex.Message); }
    }

    private float SafeBusy() { try { return Math.Max(_uiCtx.StageBusy, StageBusy()); } catch { return 0; } }

    /// <summary>Kontext füllen, Maus verteilen, Oberfläche fortschreiben und zeichnen.</summary>
    private void DrawUi(double dt)
    {
        var c = _uiCtx;
        var s = Settings;
        c.Settings = s;
        c.Palette = Palette;
        c.Width = _width; c.Height = _height;
        c.PK = Math.Min(_height / 1080f, _width / 1200f);
        c.K = c.PK * (float)Math.Clamp(s.Num("uiScale"), 0.7, 1.3);
        c.Time = (DateTime.Now - _start).TotalSeconds;
        c.Now = DateTime.Now;
        c.LightTheme = Palette.IsLightTheme;
        c.Fg = c.LightTheme ? new Vector3(24 / 255f, 25 / 255f, 31 / 255f) : new Vector3(246 / 255f, 246 / 255f, 250 / 255f);
        c.Accent = Palette.Accent;
        c.Media = _media;
        c.Controls = Controls;
        c.Bands = _bands; c.Energy = (float)_energy; c.Kick = (float)_kick; c.Spectrum = _spectrum;
        c.D2D = _d2dFactory!; c.DWrite = _dwrite!; c.Field = _fieldBmp!;
        c.Fonts = _uiFonts!; c.Glass = _glass;
        if (_text != null) { c.ClockRect = _text.ClockRect; c.DateRect = _text.DateRect; }
        c.Bloom = (x, y, k) => Flow.Bloom(x, y, k);
        c.Toast = Toast;
        c.StageBusy = 0;

        if (!ShowUi)
        {
            // Oberfläche ausgeblendet (Nebenbildschirm ohne Pillen): Maus nur mitzählen, nichts zeichnen
            while (_pointer.TryDequeue(out var e)) _pointerInside = e.Kind != PointerKind.Leave;
            OverUi = false;
            PointerCaptured = false;
            return;
        }

        while (_pointer.TryDequeue(out var e))
        {
            if (e.Kind == PointerKind.Leave) _pointerInside = false;
            else { _pointerInside = true; c.Pointer = e.Pos; }
            c.PointerInside = _pointerInside;
            try { Ui.OnPointer(c, e); } catch (Exception ex) { TaskbarWindow.Debug("Still nativ: Maus: " + ex.Message); }
        }
        c.PointerInside = _pointerInside;
        PointerCaptured = Ui.HasCapture;
        try
        {
            Ui.Update(c, dt);
            Ui.Draw(_d2d!, c);
            OverUi = _pointerInside && Ui.HitTest(c, _lastPointer);
        }
        catch (Exception ex) { TaskbarWindow.Debug("Still nativ: Oberfläche: " + ex.Message); }
    }

    // ---------- Musik ----------

    private static readonly (int lo, int hi)[] Bands = { (1, 4), (4, 10), (10, 22), (22, 48) };
    private static readonly double[] Gains = { 1.0, 1.5, 2.2, 3.4 };

    private void ProcessAudio(double dt)
    {
        float[]? a = null;
        try { a = _audioSource?.Invoke(); } catch { }
        double e = 0;
        _spectrum = a;
        if (a is { Length: >= 96 })
        {
            int half0 = a.Length >> 1;
            for (int i = 0; i < 4; i++)
            {
                var (lo, hi) = Bands[i];
                double sum = 0;
                for (int j = lo; j < hi; j++) sum += a[j] + a[j + half0];
                _bands[i] = (float)Math.Min(1, Math.Pow(sum / ((hi - lo) * 2) * Gains[i], 0.75));
            }
        }
        else Array.Clear(_bands);
        if (a is { Length: >= 20 })
        {
            int half = a.Length >> 1;
            double low = 0;
            for (int j = 0; j < 10; j++) low += a[j] + a[j + half];
            e = Math.Min(1, low / 20 * 1.4);
        }
        // schnell rauf, damit das Licht mit dem Bass pulsiert; langsamer zurück
        double rate = e > _energy ? 14 : 3.5;
        _energy += (e - _energy) * (1 - Math.Exp(-dt * rate));
        _avg += (e - _avg) * (1 - Math.Exp(-dt * 1.6));
        double hit = Math.Max(0, e - _avg - 0.04) * 3;
        _kick += (hit - _kick) * (1 - Math.Exp(-dt * (hit > _kick ? 30 : 6)));
    }

    // ---------- Aufbau ----------

    private void Init()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0 },
            out _device, out _ctx).CheckError();

        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            // In der Desktop-Ebene zeigt Windows nur Inhalt über DirectComposition an (wie beim Browser):
            // Swapchain für die Komposition anlegen und als Inhalt an das Fenster hängen.
            var desc = new SwapChainDescription1
            {
                Width = (uint)_width, Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            };
            _swap = factory.CreateSwapChainForComposition(_device, desc);
            _dcomp = Vortice.DirectComposition.DComp.DCompositionCreateDevice<Vortice.DirectComposition.IDCompositionDevice>(dxgiDevice);
            _dcomp.CreateTargetForHwnd(_hwnd, true, out _dcompTarget).CheckError();
            _dcompVisual = _dcomp.CreateVisual();
            _dcompVisual.SetContent(_swap);
            _dcompTarget!.SetRoot(_dcompVisual);
            _dcomp.Commit();

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded); // mehrere Threads (Hintergrund, Speicherbereinigung) dürfen Objekte freigeben
            _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
            _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        }
        _dwrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _text = new StillTextLayer(_d2dFactory, _dwrite);
        _uiFonts?.Dispose();
        _uiFonts = new UiFonts(_dwrite);

        var vsCode = Compile(Shaders.Vertex, "vs_5_0");
        _vs = _device.CreateVertexShader(vsCode.Span);
        _fieldPs = _device.CreatePixelShader(Compile(Shaders.Field, "ps_5_0").Span);
        _upPs = _device.CreatePixelShader(Compile(Shaders.Upscale, "ps_5_0").Span);
        foreach (var (name, src) in new[] { ("Aurora", Shaders.Aurora), ("Caustics", Shaders.Caustics), ("Bokeh", Shaders.Bokeh), ("Smoke", Shaders.Smoke) })
        {
            try { _effectPs[name] = _device.CreatePixelShader(Compile(src, "ps_5_0").Span); }
            catch (Exception ex) { Log.Error("Hintergrund-Effekt " + name, ex); } // fehlt er, gibt es einfarbig
        }
        _fieldCb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FieldParams>(), BindFlags.ConstantBuffer));
        _upCb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<UpscaleParams>(), BindFlags.ConstantBuffer));
        _sampler = _device.CreateSamplerState(new SamplerDescription(Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp));

        CreateSizeDependent();
        CreateFlow();
        try { _rain = new RainGlass(_device); }
        catch (Exception ex) { _rain = null; TaskbarWindow.Debug("Still nativ: Regen nicht verfügbar: " + ex.Message); }
    }

    internal static ReadOnlyMemory<byte> Compile(string src, string profile) =>
        Compiler.Compile(src, "main", "still.hlsl", profile, ShaderFlags.OptimizationLevel3);

    private void CreateSizeDependent()
    {
        _d2d!.Target = null;
        _targetBmp?.Dispose(); _targetBmp = null;
        _fieldBmp?.Dispose(); _fieldBmp = null;
        _fullTarget?.Dispose(); _fullTarget = null;
        _fullRtv?.Dispose(); _fullSrv?.Dispose(); _full?.Dispose();
        _wetRtv?.Dispose(); _wet?.Dispose();

        _swap!.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();

        _full = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_width, (uint)_height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _fullRtv = _device.CreateRenderTargetView(_full);
        _fullSrv = _device.CreateShaderResourceView(_full);
        _wet = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_width, (uint)_height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _wetRtv = _device.CreateRenderTargetView(_wet);

        var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        using (var surf = _swap.GetBuffer<IDXGISurface>(0))
            _targetBmp = _d2d.CreateBitmapFromDxgiSurface(surf, props);
        using (var fs = _full.QueryInterface<IDXGISurface>())
        {
            _fieldBmp = _d2d.CreateBitmapFromDxgiSurface(fs,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            _fullTarget = _d2d.CreateBitmapFromDxgiSurface(fs,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        }
        _smallW = _smallH = 0; // wird im nächsten Bild passend neu angelegt
    }

    private void CreateSmall(int w, int h)
    {
        _smallSrv?.Dispose(); _smallRtv?.Dispose(); _small?.Dispose();
        _small = _device!.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _smallRtv = _device.CreateRenderTargetView(_small);
        _smallSrv = _device.CreateShaderResourceView(_small);
        _smallW = w; _smallH = h;
    }

    private void CreateFlow()
    {
        _flowSrv?.Dispose(); _flowTex?.Dispose();
        _flowCols = Flow.Cols; _flowRows = Flow.Rows;
        _flowTex = _device!.CreateTexture2D(new Texture2DDescription(Format.R32G32B32A32_Float, (uint)_flowCols, (uint)_flowRows, 1, 1,
            BindFlags.ShaderResource));
        _flowSrv = _device.CreateShaderResourceView(_flowTex);
        _ctx!.UpdateSubresource<float>(Flow.Data, _flowTex, 0, (uint)(_flowCols * 16));
    }

    private void ReleaseAll()
    {
        _text?.Dispose(); _text = null;
        _uiFonts?.Dispose(); _uiFonts = null;
        if (_d2d != null) _d2d.Target = null;
        _targetBmp?.Dispose(); _fieldBmp?.Dispose(); _targetBmp = _fieldBmp = null;
        _fullTarget?.Dispose(); _wallBmp?.Dispose(); _fullTarget = _wallBmp = null; _wallKey = null;
        _d2d?.Dispose(); _d2dDevice?.Dispose(); _d2dFactory?.Dispose(); _dwrite?.Dispose();
        _d2d = null; _d2dDevice = null; _d2dFactory = null; _dwrite = null;
        _flowSrv?.Dispose(); _flowTex?.Dispose(); _smallSrv?.Dispose(); _smallRtv?.Dispose(); _small?.Dispose();
        _fullRtv?.Dispose(); _fullSrv?.Dispose(); _full?.Dispose();
        _wetRtv?.Dispose(); _wet?.Dispose();
        _rain?.Dispose(); _rain = null;
        _fullSrv = null; _wetRtv = null; _wet = null;
        _sampler?.Dispose(); _fieldCb?.Dispose(); _upCb?.Dispose(); _vs?.Dispose(); _fieldPs?.Dispose(); _upPs?.Dispose();
        foreach (var ps in _effectPs.Values) ps.Dispose();
        _effectPs.Clear();
        _dcompVisual?.Dispose(); _dcompTarget?.Dispose(); _dcomp?.Dispose();
        _dcompVisual = null; _dcompTarget = null; _dcomp = null;
        _swap?.Dispose(); _ctx?.ClearState(); _ctx?.Dispose(); _device?.Dispose();
        _flowSrv = null; _flowTex = null; _smallSrv = null; _smallRtv = null; _small = null; _fullRtv = null; _full = null;
        _sampler = null; _fieldCb = _upCb = null; _vs = null; _fieldPs = _upPs = null; _swap = null; _ctx = null; _device = null;
        _smallW = _smallH = _flowCols = _flowRows = 0;
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(2000);
        try { Ui.Dispose(); _glass.Dispose(); } catch { }
    }
}

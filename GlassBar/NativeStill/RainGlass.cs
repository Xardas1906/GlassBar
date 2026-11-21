using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GlassBar.NativeStill;

/// <summary>
/// Regen auf der Scheibe (glass.js): Solange der Regen spielt, ist der Bildschirm ein Fenster mit dem Regen draußen.
/// Tropfen landen, perlen sich auf und fließen zusammen; ist einer schwer genug, rutscht er ruckweise hinab, nimmt
/// die feinen Perlen auf seinem Weg mit und lässt eine abgerissene Spur zurück. Jeder Tropfen ist eine kleine Linse.
/// Die Simulation läuft wie im Original auf der CPU; gezeichnet wird in zwei Höhenkarten (Perlen, die stehen bleiben
/// und verdunsten, und Tropfen, jedes Bild neu) und am Ende das Bild, durch das Wasser gebrochen.
/// Die Höhenkarten haben die Größe, in der das Original die Scheibe zeichnet (je Qualität höchstens 1280/1920/3840 px
/// breit); das Bild selbst wird in voller Größe zusammengesetzt.
/// </summary>
public sealed class RainGlass : IDisposable
{
    // ---------- Die Zahlen (wie in glass-tuner.html abgestimmt, 2. November 2026) ----------

    private const double AreaRef = 1920 * 1080;
    private const double MM = 3.78;          // Pixel je Millimeter
    private const double HS = 10;            // Höhen werden durch diesen Wert geteilt gespeichert (wie im Original)
    private const double RMax = 4.5 * MM;    // etwa 17 px: größer läuft Wasser als Rinnsal
    private const double VCorner = 15, VPearl = 80;
    private const int Cell = 8;

    private const double TRain = 1.29, TLand = 1.85, TSize = 1, TLandMax = 8;
    private const double TGrip = 1.5, TGripVary = 0.3, TGrow = 0.05, TMaxDrops = 500, THeight = 0.42, TFlat = 0.3, TWobble = 3, TVariety = 1.25;
    private const double TSpeed = 5, TSpeedVary = 1.25, TVmax = 750, TMeander = 0.3, TMeanderScale = 45, TPearl = 2, TMergeKick = 10;
    private const double TRunWobble = 1;
    private const double TLose = 0.03, TSweep = 0.02, TSlick = 0.3;
    private const bool TThreads = true;
    private const double TThreadWidth = 0.1, TJagged = 0.45, TJagScale = 6, TBreaks = 0.3, TThreadHeight = 0.3, TThreadSpeed = 40, TKink = 0.12, TKinkFade = 90;
    private const bool TMist = true;
    private const double TMistSize = 1.4, TMistHeight = 1, TDryTime = 60, TTrailDark = 0.04;
    private const double TReach = 0.82, TRim = 0.4, TMirror = 0.08, TGlint = 0.5, TGlint2 = 0.43, TGlintSize = 1, TBright = 0.4, THard = 0.8, THaze = 0.045;

    // ---------- Shader ----------

    /// <summary>Ein Tropfen (oder eine Perle) als Kuppel aus Höhe; oder, beim Wischen, eine weiche Scheibe, die die Perlen wegnimmt.</summary>
    private const string DropVs = """
        cbuffer D : register(b0) { float2 u_res; float u_wipe; float u_wob; float u_k; float u_var; float2 _pad; };
        struct VIn { float2 pos : POSITION; float2 q : TEXCOORD0; float3 he : TEXCOORD1; float2 run : TEXCOORD2; };
        struct VOut { float4 pos : SV_Position; float2 q : TEXCOORD0; nointerpolation float3 he : TEXCOORD1; nointerpolation float2 run : TEXCOORD2; };
        VOut main(VIn i)
        {
            VOut o;
            o.q = i.q; o.he = i.he; o.run = i.run;
            o.pos = float4(i.pos.x / u_res.x * 2.0 - 1.0, 1.0 - i.pos.y / u_res.y * 2.0, 0.0, 1.0);
            return o;
        }
        """;

    private const string DropPs = """
        cbuffer D : register(b0) { float2 u_res; float u_wipe; float u_wob; float u_k; float u_var; float2 _pad; };
        struct VOut { float4 pos : SV_Position; float2 q : TEXCOORD0; nointerpolation float3 he : TEXCOORD1; nointerpolation float2 run : TEXCOORD2; };

        // smoothstep wie in GLSL
        float sstep(float e0, float e1, float x) { float t = saturate((x - e0) / (e1 - e0)); return t * t * (3.0 - 2.0 * t); }

        float4 main(VOut i) : SV_Target
        {
            float2 q = i.q;
            // Im Lauf behält ein Tropfen nie eine Form: er wird schmaler und breiter, sein Schwanz zieht sich und schnellt zurück.
            float rp = i.run.x, ra = i.run.y;
            q.x *= 1.0 + ra * 0.1 * sin(rp * 2.3);
            // Ein rutschender Tropfen läuft oben in einen Schwanz aus.
            q.x *= 1.0 + i.he.y * 0.8 * (1.0 + ra * 0.4 * sin(rp * 1.3 + 1.7)) * max(0.0, -q.y);
            float d2 = dot(q, q);
            if (u_wipe > 0.5) return float4(0.0, 0.0, 0.0, 1.0 - sstep(0.45, 1.0, d2));
            // Eine nasse Spur: keine Höhe der Rede wert, nur wo es nass ist.
            if (i.he.z < 0.0) return float4(0.0, i.he.x * (1.0 - sstep(0.35, 1.0, d2)), 0.0, 1.0);
            // Keine zwei Tropfen gleich (alles aus seinem Samen); im Lauf richtet er sich nach der Schwerkraft aus.
            if (i.he.z > 0.0)
            {
                float s = i.he.z, v = u_var * (1.0 - i.he.y);
                float h1 = frac(s * 43.17), h2 = frac(s * 71.31), h3 = frac(s * 97.73);
                float h4 = frac(s * 13.91), h5 = frac(s * 29.37), h6 = frac(s * 57.11);
                float th = h4 * 3.1416, c = cos(th), sn = sin(th);
                float2 r = float2(c * q.x + sn * q.y, c * q.y - sn * q.x);
                r.x /= 1.0 + v * 0.24 * h5;
                q = float2(c * r.x - sn * r.y, sn * r.x + c * r.y);
                q.y *= q.y > 0.0 ? 1.0 - v * 0.1 * (0.4 + h6) : 1.0 + v * 0.04;
                // Sein Rand: ein paar breite Buckel, jeder mit eigener Stärke, nie mehr als etwa ein Drittel von rund.
                bool mid = abs(q.x) < 1e-7 && abs(q.y) < 1e-7;
                float a = mid ? 0.0 : atan2(q.y, q.x), p = s * 6.2832, vv = min(v, 1.0);
                float b1 = 1.0 + ra * 0.6 * sin(rp + p), b2 = 1.0 + ra * 0.6 * sin(rp * 1.37 + p * 2.0), b3 = 1.0 + ra * 0.5 * sin(rp * 0.83 + p * 3.0);
                float dev = u_wob * (0.055 * lerp(1.0, 0.25 + 1.7 * h1, vv) * b1 * sin(2.0 * a + p)
                                   + 0.035 * lerp(1.0, 0.15 + 1.9 * h2, vv) * b2 * sin(3.0 * a + p * 2.7))
                          + min(u_wob, 1.0) * 0.02 * lerp(1.0, 0.1 + 2.3 * h3, vv) * b3 * sin(5.0 * a + p * 4.1);
                dev *= 1.0 - 0.5 * i.he.y;
                dev /= sqrt(1.0 + dev * dev / 0.1024);
                d2 = dot(q, q) / ((1.0 + dev) * (1.0 + dev));
            }
            // Eine Kappe statt einer Halbkugel: Wasser sitzt auf Glas etwas flacher.
            float h = (sqrt(max(0.0, 1.0 - d2 * u_k)) - sqrt(1.0 - u_k)) / (1.0 - sqrt(1.0 - u_k));
            return float4(i.he.x * max(0.0, h), 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>Für das Verdunsten: die Farbe zählt nicht, nur der Mischfaktor.</summary>
    private const string FadePs = """
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return float4(0.0, 0.0, 0.0, 0.0); }
        """;

    /// <summary>
    /// Das Bild: das Licht, und wo Wasser ist, das Licht durch das Wasser gebrochen. Gerechnet wird wie im Original
    /// in GL-Koordinaten (Ursprung unten links); erst beim Lesen der Texturen wird auf Direct3D umgeklappt.
    /// </summary>
    private const string ComposePs = """
        cbuffer C : register(b0)
        {
            float2 u_px; float u_unit; float u_hs;
            float u_reach; float u_asp; float u_light; float u_haze;
            float u_trail; float u_fx; float u_rim; float u_mirror;
            float u_glint; float u_glint2; float u_bright; float u_hard;
            float u_gexp; float _p0; float2 u_res;
        };
        Texture2D u_scene : register(t0);
        Texture2D u_beads : register(t1);
        Texture2D u_drops : register(t2);
        SamplerState lin : register(s0);

        float2 dx(float2 uv) { return float2(uv.x, 1.0 - uv.y); }
        float3 scn(float2 uv) { return u_scene.SampleLevel(lin, dx(uv), 0).rgb; }
        float hgt(float2 uv) { return max(u_beads.SampleLevel(lin, dx(uv), 0).r, u_drops.SampleLevel(lin, dx(uv), 0).r) * u_hs; }
        float sstep(float e0, float e1, float x) { float t = saturate((x - e0) / (e1 - e0)); return t * t * (3.0 - 2.0 * t); }

        float hash(float2 p)
        {
            p = frac(p * float2(234.34, 435.345));
            p += dot(p, p + 34.23);
            return frac(p.x * p.y);
        }

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 v_uv = float2(uvIn.x, 1.0 - uvIn.y);
            float3 scene = scn(v_uv);
            // Die Scheibe selbst: ein Hauch Dunst, solange es regnet. Wo ein Tropfen gelaufen ist, ist sie klarer und etwas dunkler.
            float wet = u_beads.SampleLevel(lin, dx(v_uv), 0).g;
            float lum = dot(scene, float3(0.299, 0.587, 0.114));
            float3 col = lerp(scene, float3(lum, lum, lum) * 0.92 + 0.03, u_haze * (1.0 - wet)) * (1.0 - u_trail * wet);
            float h = hgt(v_uv);
            if (h > 0.04)
            {
                float hl = hgt(v_uv - float2(u_px.x, 0.0)), hr = hgt(v_uv + float2(u_px.x, 0.0));
                float hd = hgt(v_uv - float2(0.0, u_px.y)), hu = hgt(v_uv + float2(0.0, u_px.y));
                // Das Gefälle, zur Mitte des Tropfens hin.
                float2 g = float2(hr - hl, hu - hd) * 0.5 * u_unit;
                float sl = length(g);
                // Eine Linse: die Mitte sieht geradeaus durch, die Ränder weit weg und auf dem Kopf.
                float2 off = g / (1.0 + sl) * u_reach * float2(1.0 / u_asp, 1.0);
                float3 thru = scn(v_uv + off);
                // Der Körper eines Tropfens sammelt Licht.
                float body = sstep(0.4, 2.5, h);
                thru = thru * (1.0 + 0.12 * body * u_bright) + thru * thru * 0.25 * body * u_bright;
                // Der steile Rand lenkt Licht ab: ein dunkler Saum, je härter, desto schmaler die Kanten.
                float soft = 1.0 - 0.95 * u_hard;
                float ring = sstep(1.6 - 0.9 * soft, 1.6 + 1.0 * soft, sl);
                float3 d = thru * (1.0 - ring * lerp(0.48 * u_rim, u_rim, body) * lerp(1.0, 0.75, u_light));
                // Unten sammelt sich das Licht, das oben hereinkam.
                float low = sstep(0.5 - 0.5 * soft, 0.5 + 0.5 * soft, max(0.0, g.y / (sl + 1e-4)));
                float band = sstep(0.575 - 0.325 * soft, 0.575 + 0.325 * soft, sl) * (1.0 - sstep(2.1 - 0.7 * soft, 2.1 + 0.7 * soft, sl));
                d += thru * low * band * 0.45 * u_fx;
                float3 n = normalize(float3(-g, 1.0));
                // Steil gesehene Ränder spiegeln das Licht um sie herum (Fresnel).
                float3 env = scn(v_uv - n.xy * 0.25 * float2(1.0 / u_asp, 1.0));
                d += env * (1.0 - n.z) * (1.0 - n.z) * u_mirror;
                // Lichtpunkte: ein Hauptlicht von links, ein weicheres von rechts.
                float c1 = max(dot(n, normalize(float3(-0.34, 0.12, 0.93))), 0.0), c2 = max(dot(n, normalize(float3(0.3, -0.06, 0.95))), 0.0);
                float x1 = u_gexp, x2 = u_gexp * 0.83, w = 0.004 * soft + 0.0005;
                float t1 = pow(0.5, 1.0 / x1), t2 = pow(0.5, 1.0 / x2);
                float k1 = lerp(pow(c1, x1), sstep(t1 - w, t1 + w, c1), u_hard);
                float k2 = lerp(pow(c2, x2), sstep(t2 - w, t2 + w, c2), u_hard);
                d += lerp(float3(1.0, 1.0, 1.0), thru * 1.6 + 0.2, 0.45) * (k1 * u_glint * lerp(1.0, 0.67, u_light) + k2 * u_glint2) * lerp(0.35, 1.0, body);
                col = lerp(col, d, saturate((h - 0.04) / max(0.05, 1.5 * sl / u_unit)));
            }
            float2 fc = float2(pos.x, u_res.y - pos.y);
            col += (hash(fc) - 0.5) / 255.0;
            return float4(col, 1.0);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct DropParams { public Vector2 Res; public float Wipe, Wob, K, Var; public Vector2 Pad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComposeParams
    {
        public Vector2 Px; public float Unit, Hs;
        public float Reach, Asp, Light, Haze;
        public float Trail, Fx, Rim, Mirror;
        public float Glint, Glint2, Bright, Hard;
        public float Gexp, P0; public Vector2 Res;
    }

    private sealed class Drop
    {
        public double X, Y, R, Seed, Sx, Sy, Hk, Grow, Pace, Kx, Mx, Ph, V, Vx, Shed, Age, Tx, Ty;
        public bool Slide, Dead, HasTail;
    }

    /// <summary>Eine wachsende Liste von Dreiern (x, y, r), die beim Leeren ihren Speicher behält.</summary>
    private sealed class Triples
    {
        public double[] A = new double[3 * 256];
        public int Count; // in Zahlen, nicht in Dreiern
        public void Add(double x, double y, double r)
        {
            if (Count + 3 > A.Length) Array.Resize(ref A, A.Length * 2);
            A[Count++] = x; A[Count++] = y; A[Count++] = r;
        }
        public void Clear() => Count = 0;
    }

    // ---------- Gerät ----------

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _dropVs, _fullVs;
    private readonly ID3D11PixelShader _dropPs, _fadePs, _compPs;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vbuf, _dropCb, _compCb;
    private readonly ID3D11BlendState _blendMax, _blendWipe, _blendFade;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Texture2D? _beadTex, _dropTex;
    private ID3D11RenderTargetView? _beadRtv, _dropRtv;
    private ID3D11ShaderResourceView? _beadSrv, _dropSrv;
    private int _texW, _texH;
    private bool _clearBeads;

    private const int Stride = 9;                // Gleitkommazahlen je Ecke
    private const int MaxVerts = 6 * 4096;       // ein Puffer voll; mehr wird in Portionen gezeichnet
    private readonly float[] _verts = new float[MaxVerts * Stride];
    private int _nv;
    private ID3D11DeviceContext? _ctx;           // nur während Render gesetzt

    // ---------- Was auf der Scheibe ist ----------

    private readonly Random _rnd = new();
    private readonly List<Drop> _drops = new(1024);
    private readonly Stack<Drop> _pool = new();
    private readonly Triples _stamps = new(), _threads = new(), _films = new(), _wipes = new();
    private double _a;           // wie stark es gerade auf die Scheibe regnet: 0 trocken, etwa 1,5 Wolkenbruch
    private double _dryFor, _fade, _clock;
    private bool _on, _wanted;
    private double _more = 1, _look = 1;
    private readonly double _seed;
    private float[] _wetT = Array.Empty<float>();
    private int _cols, _rows;
    private int _mapW, _mapH;    // Größe der Höhenkarten
    private double _unit = 1;    // Kartenpixel je Bildschirmpixel (im Sinne von CSS-Pixeln)
    private double _wu, _hu;     // Scheibe in Bildschirmpixeln

    /// <summary>true, solange es etwas zu zeichnen gibt (auch beim Ein- und Ausblenden).</summary>
    public bool Active => _on;

    /// <summary>Helles Thema (body.light im Original): Saum und Hauptlicht etwas zurückgenommen.</summary>
    public bool LightTheme { get; set; }

    /// <summary>
    /// Gerätepixel je logischem Pixel (devicePixelRatio im Original, also Anzeigeskalierung / 100 %). Größen auf der Scheibe
    /// sind in logischen Pixeln bemessen; vorbelegt mit der System-DPI.
    /// </summary>
    public double PixelScale { get; set; } = SystemScale();

    public RainGlass(ID3D11Device device)
    {
        _device = device;
        _seed = _rnd.NextDouble() * 1000;

        var dropVsCode = Compile(DropVs, "vs_5_0");
        _dropVs = device.CreateVertexShader(dropVsCode.Span);
        _layout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32_Float, 16, 0),
            new InputElementDescription("TEXCOORD", 2, Format.R32G32_Float, 28, 0),
        }, dropVsCode.Span);
        _fullVs = device.CreateVertexShader(Compile(Shaders.Vertex, "vs_5_0").Span);
        _dropPs = device.CreatePixelShader(Compile(DropPs, "ps_5_0").Span);
        _fadePs = device.CreatePixelShader(Compile(FadePs, "ps_5_0").Span);
        _compPs = device.CreatePixelShader(Compile(ComposePs, "ps_5_0").Span);

        _vbuf = device.CreateBuffer(new BufferDescription((uint)(MaxVerts * Stride * 4), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _dropCb = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<DropParams>(), BindFlags.ConstantBuffer));
        _compCb = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ComposeParams>(), BindFlags.ConstantBuffer));

        // Höchstwert (Höhen legen sich übereinander), Wischen (Ziel × (1 − Alpha)), Verdunsten (Ziel × Faktor)
        _blendMax = device.CreateBlendState(MakeBlend(Blend.One, Blend.One, BlendOperation.Max));
        _blendWipe = device.CreateBlendState(MakeBlend(Blend.Zero, Blend.InverseSourceAlpha, BlendOperation.Add));
        _blendFade = device.CreateBlendState(MakeBlend(Blend.Zero, Blend.BlendFactor, BlendOperation.Add));
        _raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
    }

    private static ReadOnlyMemory<byte> Compile(string src, string profile) =>
        Compiler.Compile(src, "main", "rain.hlsl", profile, ShaderFlags.OptimizationLevel3);

    private static BlendDescription MakeBlend(Blend src, Blend dst, BlendOperation op)
    {
        var d = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        d.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = src, DestinationBlend = dst, BlendOperation = op,
            SourceBlendAlpha = src == Blend.Zero ? Blend.Zero : Blend.One,
            DestinationBlendAlpha = dst == Blend.BlendFactor ? Blend.BlendFactor : dst == Blend.InverseSourceAlpha ? Blend.InverseSourceAlpha : Blend.One,
            BlendOperationAlpha = op,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return d;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    private static double SystemScale() { try { return Math.Max(1, GetDpiForSystem() / 96.0); } catch { return 1; } }

    // ---------- Fortschreiben ----------

    /// <summary>
    /// Jedes Bild auf dem Render-Thread vor <see cref="Render"/>. on = Regenklang spielt und Einstellung rainGlass;
    /// weatherRain (0–1) = der Hintergrund „Wetter“ zeigt gerade Regen – dann regnet es auch auf die Scheibe.
    /// </summary>
    public void Update(double dt, bool on, StillValues settings, double surge, int width, int height, double weatherRain = 0)
    {
        // Wie stark es regnet, aus dem Klang: Intensität und Wogen, und wie viel davon man auf der Scheibe will.
        // Klangwelt Regen braucht „Effekte auf dem Bildschirm“ (Pillen › Klangwelten); Regen im Wetter-Hintergrund nicht
        _wanted = (on && settings.Bool("soundFx") || weatherRain > 0) && settings.Bool("rainGlass");
        _more = settings.Num("glassAmount");
        _look = settings.Num("glassLook");
        double g = _wanted
            ? Math.Min(2.5, (0.12 + 0.88 * (on ? settings.Num("rainIntensity") : weatherRain)) * TRain * _more * (on ? Math.Max(0.35, 1 + 0.6 * surge) : 1))
            : 0;
        if (!_on && g > 0) _on = true;
        if (!_on) return;

        // Die Scheibe: höchstens so fein wie die Qualität erlaubt, Größen in logischen Pixeln.
        width = Math.Max(2, width); height = Math.Max(2, height);
        double maxW = settings.Str("quality") switch { "low" => 1280, "high" => 3840, _ => 1920 };
        double k = Math.Min(1, maxW / width);
        _mapW = Math.Max(2, (int)Math.Round(width * k));
        _mapH = Math.Max(2, (int)Math.Round(height * k));
        double scale = Math.Max(0.5, PixelScale);
        _unit = _mapW * scale / width;
        _wu = _mapW / _unit;
        _hu = _mapH / _unit;

        Step(dt, g);
        if (_a == 0 && _drops.Count == 0 && _dryFor > 32) Detach();
    }

    private void Detach()
    {
        _on = false;
        foreach (var d in _drops) _pool.Push(d);
        _drops.Clear();
        _stamps.Clear(); _threads.Clear(); _films.Clear(); _wipes.Clear();
        _a = _dryFor = _fade = 0;
        // Ohne Regen kostet die Scheibe nichts, auch keinen Speicher.
        FreeTargets();
    }

    private double Rnd() => _rnd.NextDouble();
    private double Range(double a, double b) => a + (b - a) * _rnd.NextDouble();

    /* Die Scheibe: ein fester, sanft ungleicher Halt (0 glatt, 1 klebrig), und wo zuletzt Wasser lief (nass, verblasst
       über etwa 40 s). */
    private double Hash(double i, double j)
    {
        double h = Math.Sin(i * 127.1 + j * 311.7 + _seed) * 43758.5453;
        return h - Math.Floor(h);
    }

    private double Grain(double x, double y)
    {
        double i = Math.Floor(x), j = Math.Floor(y), fx = x - i, fy = y - j;
        double u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
        double a = Hash(i, j), b = Hash(i + 1, j), c = Hash(i, j + 1), d = Hash(i + 1, j + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private double GripAt(double x, double y) => 0.65 * Grain(x / 14, y / 14) + 0.35 * Grain(x / 5, y / 5);

    private void SizeWet(double wu, double hu)
    {
        int c = (int)Math.Ceiling(wu / Cell) + 1, r = (int)Math.Ceiling(hu / Cell) + 1;
        if (c == _cols && r == _rows) return;
        _cols = c; _rows = r;
        _wetT = new float[c * r];
        Array.Fill(_wetT, -1e6f);
    }

    private double WetAt(double x, double y)
    {
        int i = (int)Math.Floor(x / Cell), j = (int)Math.Floor(y / Cell);
        if (i < 0 || j < 0 || i >= _cols || j >= _rows) return 0;
        return Math.Exp(-(_clock - _wetT[j * _cols + i]) / 40);
    }

    private void Wetten(double x, double y, double r)
    {
        int i0 = Math.Max(0, (int)Math.Floor((x - r) / Cell)), i1 = Math.Min(_cols - 1, (int)Math.Floor((x + r) / Cell));
        int j0 = Math.Max(0, (int)Math.Floor((y - r) / Cell)), j1 = Math.Min(_rows - 1, (int)Math.Floor(y / Cell));
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) _wetT[j * _cols + i] = (float)_clock;
    }

    // Wohin ein Tropfen lieber läuft: glatt und nass (nur von der groben Unebenheit gelenkt, damit Wege mäandern statt zu zacken).
    private double Ease(double x, double y) => 1.2 * WetAt(x, y) - Grain(x / TMeanderScale + 17, y / (TMeanderScale * 1.55));

    private Drop NewDrop(double x, double y, double r)
    {
        var d = _pool.Count > 0 ? _pool.Pop() : new Drop();
        d.X = x; d.Y = y; d.R = r;
        d.Seed = Range(0.01, 1); d.Sx = Range(0.95, 1.05); d.Sy = Range(0.95, 1.05);
        // Jeder hält sich ein wenig anders an der Scheibe.
        d.Hk = Range(0.85, 1.15); d.Grow = Range(0.6, 1.4);
        // Sein Tempo gegenüber anderen seiner Größe (normal verteilt, mit TSpeedVary skaliert).
        d.Pace = (Rnd() + Rnd() + Rnd() - 1.5) * 2;
        d.Kx = d.Mx = 0; d.Ph = Rnd() * 6.28;
        d.V = d.Vx = 0; d.Slide = false; d.Shed = 0; d.Age = 0; d.Dead = false; d.HasTail = false; d.Tx = d.Ty = 0;
        return d;
    }

    // Zusammenfließen erhält das Wasser (r³) und den Schwung; ein laufender Tropfen dreht dabei zur Seite des anderen.
    private static void Absorb(Drop big, Drop small)
    {
        double ma = big.R * big.R * big.R, mb = small.R * small.R * small.R, m = ma + mb;
        if (big.Slide || small.Slide)
        {
            double v = Math.Max(Math.Max(big.V, small.V), 20);
            big.Mx += v * ((small.X - big.X) / (big.R + small.R)) * (mb / m) * TMergeKick * 0.25;
        }
        big.X = (big.X * ma + small.X * mb) / m;
        big.Y = (big.Y * ma + small.Y * mb) / m;
        big.V = (big.V * ma + small.V * mb) / m;
        big.Vx = (big.Vx * ma + small.Vx * mb) / m;
        big.R = Math.Min(RMax, Math.Cbrt(m));
        small.Dead = true;
    }

    // Tropfen, die sich berühren, werden einer: quer sortiert trifft jeder nur seine Nachbarn (Einfügesortierung, fast umsonst).
    private void Merge()
    {
        var drops = _drops;
        for (int i = 1; i < drops.Count; i++)
        {
            var d = drops[i];
            int j = i - 1;
            while (j >= 0 && drops[j].X > d.X) { drops[j + 1] = drops[j]; j--; }
            drops[j + 1] = d;
        }
        for (int i = 0; i < drops.Count; i++)
        {
            var a = drops[i];
            if (a.Dead) continue;
            for (int j = i + 1; j < drops.Count; j++)
            {
                var b = drops[j];
                if (b.X - a.X > a.R + RMax) break;
                if (b.Dead) continue;
                double dx = b.X - a.X, dy = b.Y - a.Y, rr = (a.R + b.R) * 0.9;
                if (dx * dx + dy * dy > rr * rr) continue;
                if (a.R >= b.R) Absorb(a, b);
                else { Absorb(b, a); break; }
            }
        }
    }

    private void Step(double dt, double g)
    {
        if (!_wanted) _a = 0;
        // Es setzt über ein paar Sekunden ein; abgestellt ist es schneller vorbei (ein abklingendes Wogen langsamer).
        _a += (g - _a) * (1 - Math.Exp(-dt / (g > _a ? 2 : g == 0 ? 2.5 : 5)));
        if (g == 0 && _a < 0.02) _a = 0;
        _clock += dt;
        double wu = _wu, hu = _hu, area = wu * hu / AreaRef;
        SizeWet(wu, hu);
        double quick = _wanted ? 1 : 6; // ausgeschaltet trocknet sie in Sekunden
        _dryFor = _a > 0 ? 0 : _dryFor + dt * quick;

        if (_a > 0)
        {
            // Was landet: Größen fallen exponentiell ab, wie bei Regentropfen. Unter 2,5 px ist es Sprühnebel (Perlen), sonst ein Tropfen.
            double m = (0.45 + 0.5 * _a) * TSize;
            double mean = dt * (8 + 45 * _a) * area * TLand * _more;
            int n = (int)Math.Floor(mean);
            if (Rnd() < mean - n) n++;
            for (int i = 0; i < n; i++)
            {
                double x = wu * Rnd(), y = hu * Rnd(), r = Math.Min(TLandMax, 0.8 - m * Math.Log(1 - Rnd()));
                if (r < 2.5) { if (TMist) _stamps.Add(x, y, r * TMistSize); }
                else if (_drops.Count < TMaxDrops * area)
                {
                    _drops.Add(NewDrop(x, y, r));
                    _wipes.Add(x, y, r * 0.8);
                }
            }
        }

        double kUp = 1 - Math.Exp(-dt * 8), kDown = 1 - Math.Exp(-dt * 14), kLean = 1 - Math.Exp(-dt * 2), kKink = 1 - Math.Exp(-dt * 14), kMerge = 1 - Math.Exp(-dt * 4);
        // Mit Index, damit abgeschüttelte Tröpfchen im selben Schritt mitlaufen (wie for…of im Original).
        for (int di = 0; di < _drops.Count; di++)
        {
            var d = _drops[di];
            d.Age += dt;
            // Wo er hier loslässt: der Halt der Scheibe unter ihm, weniger, wo es nass ist.
            double want = 0;
            double bse = TGrip * MM * d.Hk;
            if (d.Slide || d.R > bse * (1 - TGripVary / 2) * 0.7)
            {
                double hold = bse * (1 - TGripVary / 2 + TGripVary * GripAt(d.X, d.Y)) * (1 - TSlick * WetAt(d.X, d.Y));
                if (d.R > hold) want = Math.Min(TVmax, TSpeed * Math.Exp(d.Pace * TSpeedVary) * (d.R * d.R - hold * hold));
            }
            if (want != 0 || d.V != 0)
            {
                d.V += (want - d.V) * (want > d.V ? kUp : kDown);
                if (d.V < 0.5 && want == 0) d.V = 0;
            }
            d.Slide = d.V > 2;
            // Hat der Regen aufgehört, verdunstet alles.
            if (_a == 0) d.R -= dt * (0.08 + 0.02 * d.R) * quick;
            if (!d.Slide)
            {
                // In Ruhe: der Sprühnebel lässt ihn wachsen, solange es regnet.
                if (_a > 0) d.R += dt * TGrow * _a * d.Grow;
                d.Vx = d.Kx = d.Mx = 0;
                d.HasTail = false;
            }
            else
            {
                // Hinab, ein wenig seitwärts zur glatteren, nasseren Seite unter ihm gezogen.
                double ahead = d.Y + d.R * 1.2, side = d.R * 0.9 + 2;
                double lean = d.V * TMeander * Math.Tanh(1.5 * (Ease(d.X + side, ahead) - Ease(d.X - side, ahead)));
                d.Vx += (lean - d.Vx) * kLean;
                // Und von den feinen Unebenheiten kurz zur Seite gerissen: die Knicke im Weg. Schnelle laufen hindurch.
                double calm = 1 / (1 + (d.V / TKinkFade) * (d.V / TKinkFade));
                double kink = d.V * TKink * calm * Math.Tanh(4 * (Grain((d.X + side) / 9 + 31, ahead / 12) - Grain((d.X - side) / 9 + 31, ahead / 12)));
                d.Kx += (kink - d.Kx) * kKink;
                // Der Zug eines Zusammenfließens lässt in Sekundenbruchteilen nach.
                d.Mx -= d.Mx * kMerge;
                double dx = (d.Vx + d.Kx + d.Mx) * dt, dy = d.V * dt;
                d.X += dx;
                d.Y += dy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                // Seine Form atmet im Lauf: kriechend etwa alle drei Sekunden, rasend einmal je Sekunde.
                d.Ph += dt * (2 + 4 * Math.Min(1, d.V / 200));
                // Er räumt seinen Weg frei, hinterlässt eine dünne Spur und nimmt Sprühnebel auf.
                _wipes.Add(d.X, d.Y, d.R * 0.7);
                _films.Add(d.X, d.Y - d.R * 0.15, d.R * 0.75);
                Wetten(d.X, d.Y, d.R * 0.75);
                // Aus seinem Schwanz läuft ein Faden Wasser, als Kette winziger Perlen gelegt.
                double tail = d.Y - d.R * 0.8;
                if (!d.HasTail) { d.Tx = d.X; d.Ty = tail; d.HasTail = true; }
                double seg = Math.Sqrt((d.X - d.Tx) * (d.X - d.Tx) + (tail - d.Ty) * (tail - d.Ty));
                int kk = (int)Math.Min(32, Math.Ceiling(seg / 1.2));
                if (TThreads && d.V > TThreadSpeed)
                {
                    for (int i = 1; i <= kk; i++)
                    {
                        double t = (double)i / kk, x = d.Tx + (d.X - d.Tx) * t, y = d.Ty + (tail - d.Ty) * t;
                        if (Grain(x / 20 + 50, y / 26) < TBreaks) continue;
                        double w = d.R * TThreadWidth * (1 + TJagged * (2 * Grain(x / TJagScale + 9, y / (TJagScale * 1.17)) - 1));
                        if (w > 0.6) _threads.Add(x, y, w);
                    }
                }
                d.Tx = d.X;
                d.Ty = tail;
                d.R = Math.Min(RMax, Math.Cbrt(Math.Max(1, d.R * d.R * d.R + (TSweep * _a - TLose) * d.R * d.R * dist)));
                // Schnell schüttelt er ab und zu Tröpfchen aus dem Schwanz ab.
                if (d.V > VPearl)
                {
                    d.Shed += dt;
                    double p = Math.Min(1, 3 * dt / 0.8 * Math.Min(1, d.Shed / 0.8) * Math.Sqrt(d.V / VPearl) * TPearl);
                    if (Rnd() < p)
                    {
                        double f = Range(0.04, 0.15), rr = d.R * Math.Cbrt(f);
                        d.R *= Math.Cbrt(1 - f);
                        double e = Math.Min(1, (d.V - VCorner) / 135);
                        double tx = d.X + Range(-0.3, 0.3) * rr, ty = d.Y - d.R * (1.15 + 0.35 * e) - rr;
                        if (rr < 2) _stamps.Add(tx, ty, rr);
                        else
                        {
                            var nd = NewDrop(tx, ty, rr);
                            nd.Hk = Math.Max(nd.Hk, rr * 1.3 / (TGrip * MM));
                            _drops.Add(nd);
                        }
                        d.Shed = 0;
                    }
                }
                if (d.Y - d.R * 2 > hu) d.Dead = true;
            }
            if (d.R < 0.6) d.Dead = true;
        }
        Merge();
        int keep = 0;
        for (int i = 0; i < _drops.Count; i++)
        {
            var d = _drops[i];
            if (!d.Dead) _drops[keep++] = d;
            else _pool.Push(d);
        }
        _drops.RemoveRange(keep, _drops.Count - keep);

        // Perlen und Fäden verdunsten: in einer halben Minute, solange es regnet, in Sekunden, wenn es aufhört.
        _fade += dt / (_a > 0 ? TDryTime : 6 / quick);
    }

    // ---------- Zeichnen ----------

    private void FreeTargets()
    {
        _beadSrv?.Dispose(); _beadRtv?.Dispose(); _beadTex?.Dispose();
        _dropSrv?.Dispose(); _dropRtv?.Dispose(); _dropTex?.Dispose();
        _beadSrv = _dropSrv = null; _beadRtv = _dropRtv = null; _beadTex = _dropTex = null;
        _texW = _texH = 0;
    }

    private void EnsureTargets()
    {
        if (_beadTex != null && _texW == _mapW && _texH == _mapH) return;
        FreeTargets();
        // Höhen als Halbfließkomma (glatte Hänge): Rot die Höhe, Grün wo es nass ist.
        var desc = new Texture2DDescription(Format.R16G16_Float, (uint)_mapW, (uint)_mapH, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource);
        _beadTex = _device.CreateTexture2D(desc);
        _beadRtv = _device.CreateRenderTargetView(_beadTex);
        _beadSrv = _device.CreateShaderResourceView(_beadTex);
        _dropTex = _device.CreateTexture2D(desc);
        _dropRtv = _device.CreateRenderTargetView(_dropTex);
        _dropSrv = _device.CreateShaderResourceView(_dropTex);
        _texW = _mapW; _texH = _mapH;
        _clearBeads = true;
    }

    // Eine Kuppel (oder ein Wischer) als zwei Dreiecke: x, y und Radien in Bildschirmpixeln; h schon im Speichermaß.
    private static readonly float[] Corners = { -1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1 };

    private void Quad(double x, double y, double rx, double ry, double h, double e, double seed, double pad = 1.02, double ph = 0, double ra = 0)
    {
        if (_nv + 6 > MaxVerts) Flush();
        // Nie kleiner als anderthalb Pixel, sonst fielen die feinsten Perlen zwischen die Pixel.
        double ax = Math.Max(1.5, rx * _unit * pad), ay = Math.Max(1.5, ry * _unit * pad);
        double qx = ax / (rx * _unit), qy = ay / (ry * _unit), px = x * _unit, py = y * _unit;
        int o = _nv * Stride;
        var v = _verts;
        for (int i = 0; i < 12; i += 2)
        {
            float cx = Corners[i], cy = Corners[i + 1];
            v[o++] = (float)(px + cx * ax);
            v[o++] = (float)(py + cy * ay);
            v[o++] = (float)(cx * qx);
            v[o++] = (float)(cy * qy);
            v[o++] = (float)h;
            v[o++] = (float)e;
            v[o++] = (float)seed;
            v[o++] = (float)ph;
            v[o++] = (float)ra;
        }
        _nv += 6;
    }

    private void Flush()
    {
        if (_nv == 0 || _ctx == null) return;
        var map = _ctx.Map(_vbuf, 0, MapMode.WriteDiscard);
        Marshal.Copy(_verts, 0, map.DataPointer, _nv * Stride);
        _ctx.Unmap(_vbuf, 0);
        _ctx.Draw((uint)_nv, 0);
        _nv = 0;
    }

    private void SetDropParams(ID3D11DeviceContext ctx, float wipe)
    {
        var p = new DropParams
        {
            Res = new Vector2(_texW, _texH), Wipe = wipe, Wob = (float)TWobble,
            K = (float)Math.Min(0.999, Math.Max(0.3, TFlat)), Var = (float)TVariety,
        };
        ctx.UpdateSubresource(in p, _dropCb);
    }

    private void UseDropPipeline(ID3D11DeviceContext ctx)
    {
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vbuf, (uint)(Stride * 4), 0);
        ctx.VSSetShader(_dropVs);
        ctx.PSSetShader(_dropPs);
        ctx.VSSetConstantBuffer(0, _dropCb);
        ctx.PSSetConstantBuffer(0, _dropCb);
    }

    // Die Perlen: verdunsten, neue dazu, und die Wege, die durch sie gewischt werden.
    private void Beads(ID3D11DeviceContext ctx)
    {
        ctx.OMSetRenderTargets(_beadRtv!);
        if (_clearBeads) { ctx.ClearRenderTargetView(_beadRtv!, new Color4(0, 0, 0, 0)); _clearBeads = false; }
        if (_fade > 0.004)
        {
            float keep = (float)Math.Exp(-_fade);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_fullVs);
            ctx.PSSetShader(_fadePs);
            ctx.OMSetBlendState(_blendFade, new Color4(keep, keep, keep, keep), uint.MaxValue);
            ctx.Draw(3, 0);
            _fade = 0;
        }
        UseDropPipeline(ctx);
        if (_wipes.Count > 0)
        {
            ctx.OMSetBlendState(_blendWipe, new Color4(0, 0, 0, 0), uint.MaxValue);
            SetDropParams(ctx, 1);
            var w = _wipes.A;
            for (int i = 0; i < _wipes.Count; i += 3) Quad(w[i], w[i + 1], w[i + 2], w[i + 2], 0, 0, 0);
            Flush();
            _wipes.Clear();
        }
        if (_stamps.Count > 0 || _threads.Count > 0 || _films.Count > 0)
        {
            ctx.OMSetBlendState(_blendMax, new Color4(0, 0, 0, 0), uint.MaxValue);
            SetDropParams(ctx, 0);
            var s = _stamps.A;
            for (int i = 0; i < _stamps.Count; i += 3) { double r = s[i + 2]; Quad(s[i], s[i + 1], r, r, r * TMistHeight / HS, 0, 0); }
            var t = _threads.A;
            for (int i = 0; i < _threads.Count; i += 3) { double r = t[i + 2]; Quad(t[i], t[i + 1], r, r, r * TThreadHeight / HS, 0, 0); }
            var f = _films.A;
            for (int i = 0; i < _films.Count; i += 3) { double r = f[i + 2]; Quad(f[i], f[i + 1], r, r, 1, 0, -1); }
            Flush();
            _stamps.Clear(); _threads.Clear(); _films.Clear();
        }
    }

    /// <summary>
    /// Zeichnet die Scheibe über das Bild: liest <paramref name="scene"/> (Licht in voller Größe, schon mit Körnung),
    /// schreibt <paramref name="target"/> (gleiche Größe, BGRA). Danach sind Mischung, Rasterung, Eingabelayout und
    /// Vertexpuffer wieder auf Standard (null), t0–t2 und das Ziel gelöst.
    /// </summary>
    public void Render(ID3D11DeviceContext ctx, ID3D11ShaderResourceView scene, ID3D11RenderTargetView target, int width, int height)
    {
        if (!_on || _mapW < 2 || _mapH < 2) return;
        EnsureTargets();
        _ctx = ctx;
        try
        {
            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.PSSetShaderResource(2, null!);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.RSSetState(_raster);
            ctx.RSSetViewport(new Viewport(0, 0, _texW, _texH));

            Beads(ctx);

            // Die Tropfen, jedes Bild neu. Beim Landen breitet sich jeder in einem Augenblick aus.
            ctx.OMSetRenderTargets(_dropRtv!);
            ctx.ClearRenderTargetView(_dropRtv!, new Color4(0, 0, 0, 0));
            if (_drops.Count > 0)
            {
                ctx.OMSetBlendState(_blendMax, new Color4(0, 0, 0, 0), uint.MaxValue);
                SetDropParams(ctx, 0);
                double pad = 1.15 + 0.3 * TVariety;
                foreach (var d in _drops)
                {
                    double r = d.R * (0.55 + 0.45 * Math.Min(1, d.Age / 0.07));
                    double rx = r * d.Sx, ry = r * d.Sy, y = d.Y, e = 0;
                    if (d.Slide)
                    {
                        // Im Lauf zieht sich sein Rücken zu einer Ecke aus, je schneller, desto mehr.
                        double c = Math.Max(0, Math.Min(1, (d.V - VCorner) / 135));
                        e = c * c * (3 - 2 * c);
                        rx = r * (1 - 0.12 * e);
                        ry = r * (1 + 0.35 * e);
                        y -= ry - r;
                    }
                    Quad(d.X, y, rx, ry, r * THeight / HS, e, d.Seed, pad, d.Ph, TRunWobble * Math.Min(1, d.V / 60));
                }
                Flush();
            }

            // Das Bild.
            ctx.OMSetBlendState(null);
            ctx.OMSetRenderTargets(target);
            ctx.RSSetViewport(new Viewport(0, 0, width, height));
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_fullVs);
            ctx.PSSetShader(_compPs);
            // Effektstärke regelt, wie stark das Wasser bricht und Licht fängt: bei 0 ist es da, aber unsichtbar.
            double k = _look;
            var cp = new ComposeParams
            {
                Px = new Vector2(1f / _texW, 1f / _texH), Unit = (float)_unit, Hs = (float)HS,
                Reach = (float)(TReach * k), Asp = (float)width / height, Light = LightTheme ? 1 : 0,
                Haze = (float)(Math.Min(THaze * 1.33, THaze * _a) * k),
                Trail = (float)Math.Max(0, Math.Min(1, TTrailDark * k)), Fx = (float)k,
                Rim = (float)Math.Min(1, TRim * k), Mirror = (float)(TMirror * k),
                Glint = (float)(TGlint * k), Glint2 = (float)(TGlint2 * k), Bright = (float)(TBright * k),
                Hard = (float)Math.Max(0, Math.Min(1, THard)),
                // größerer Lichtpunkt, breiterer Abfall: 60 bei 1×
                Gexp = (float)(60 / Math.Max(0.1, TGlintSize * TGlintSize)),
                Res = new Vector2(width, height),
            };
            ctx.UpdateSubresource(in cp, _compCb);
            ctx.PSSetConstantBuffer(0, _compCb);
            ctx.PSSetSampler(0, _sampler);
            ctx.PSSetShaderResource(0, scene);
            ctx.PSSetShaderResource(1, _beadSrv!);
            ctx.PSSetShaderResource(2, _dropSrv!);
            ctx.Draw(3, 0);
        }
        finally
        {
            // Zustand zurück, damit der Aufrufer nichts davon merkt.
            ctx.PSSetShaderResource(0, null!);
            ctx.PSSetShaderResource(1, null!);
            ctx.PSSetShaderResource(2, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            ctx.OMSetBlendState(null);
            ctx.RSSetState(null);
            ctx.IASetInputLayout(null);
            ctx.IASetVertexBuffer(0, null!, 0, 0);
            _ctx = null;
            _nv = 0;
        }
    }

    public void Dispose()
    {
        FreeTargets();
        _sampler.Dispose(); _raster.Dispose();
        _blendMax.Dispose(); _blendWipe.Dispose(); _blendFade.Dispose();
        _vbuf.Dispose(); _dropCb.Dispose(); _compCb.Dispose();
        _layout.Dispose();
        _dropVs.Dispose(); _fullVs.Dispose(); _dropPs.Dispose(); _fadePs.Dispose(); _compPs.Dispose();
    }
}

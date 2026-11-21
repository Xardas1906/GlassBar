namespace GlassBar.NativeStill;

/// <summary>
/// Die Shader des Lichtfelds, übersetzt aus field.js (GLSL → HLSL). Das Feld wird klein gerendert
/// (je nach Qualität 560–1400 px breit) und danach auf Bildschirmgröße hochgezogen; dabei kommt die Körnung dazu.
/// </summary>
internal static class Shaders
{
    /// <summary>Ein übergroßes Dreieck bedeckt den ganzen Bildschirm.</summary>
    public const string Vertex = """
        struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VOut main(uint id : SV_VertexID)
        {
            VOut o;
            float2 p = float2((id == 2) ? 3.0 : -1.0, (id == 1) ? 3.0 : -1.0); // im Uhrzeigersinn (sonst als Rückseite verworfen)
            o.pos = float4(p, 0.0, 1.0);
            o.uv = float2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
            return o;
        }
        """;

    /// <summary>
    /// Alle Effekte außer dem Lichtfeld (das hat seinen eigenen Shader <see cref="Field"/>), Schlüssel wie in
    /// Config.WallpaperField. Renderer, Vorschaubilder und Tests nehmen diese Liste.
    /// </summary>
    public static readonly (string Key, string Source)[] Effects =
    {
        ("Aurora", Aurora), ("Caustics", Caustics), ("Bokeh", Bokeh), ("Smoke", Smoke),
        ("Stars", Stars), ("Silk", Silk), ("Lava", Lava), ("Pond", Pond),
        ("Fireflies", Fireflies), ("Contours", Contours), ("Weather", Weather), ("Waves", Waves),
    };

    /// <summary>
    /// Gemeinsamer Teil aller Hintergrund-Effekte: Eingänge (Palette, Zeit, Musik-Energie, Maus-Strömung) und
    /// Rauschfunktionen. Jeder Effekt hängt seine eigene main() daran.
    /// </summary>
    public const string Common = """
        cbuffer P : register(b0)
        {
            float2 u_res;  float u_time;  float u_energy;
            float u_light; float2 u_shift; float u_fOn;
            float3 u_base; float u_fGlow;
            float3 u_c0;   float u_wxKind;   // Wetter: 0 klar, 1 Wolken, 2 Nebel, 3 Regen, 4 Schnee, 5 Gewitter
            float3 u_c1;   float u_wxAmt;    // Stärke von Regen/Schnee/Nebel 0–1
            float3 u_c2;   float u_wxCloud;  // Bewölkung 0–1
            float3 u_c3;   float u_wxNight;  // 1 = Nacht
            float4 u_bands;                  // Musik: Bass, untere Mitten, obere Mitten, Höhen (0–1)
            float4 u_misc;                   // x = echte Sekunden (unabhängig vom Tempo-Regler: Regen fällt immer gleich schnell)
        };
        Texture2D u_flow : register(t0);
        SamplerState lin : register(s0);

        float hash(float2 p)
        {
            p = frac(p * float2(234.34, 435.345));
            p += dot(p, p + 34.23);
            return frac(p.x * p.y);
        }

        float noise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash(i), hash(i + float2(1.0, 0.0)), u.x),
                        lerp(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), u.x), u.y);
        }

        float fbm(float2 p)
        {
            float v = 0.0, a = 0.5;
            [unroll] for (int i = 0; i < 4; i++)
            {
                v += a * noise(p);
                p = float2(1.6 * p.x - 1.2 * p.y, 1.2 * p.x + 1.6 * p.y); // mat2(1.6, 1.2, -1.2, 1.6) * p
                a *= 0.5;
            }
            return v;
        }

        float blob(float2 p, float2 c, float r)
        {
            float2 d = p - c;
            return exp(-dot(d, d) / (r * r));
        }

        // smoothstep wie in GLSL (auch mit vertauschten Kanten)
        float sstep(float e0, float e1, float x)
        {
            float t = saturate((x - e0) / (e1 - e0));
            return t * t * (3.0 - 2.0 * t);
        }

        float2 hash22(float2 p)
        {
            return float2(hash(p), hash(p + float2(17.31, 41.73)));
        }

        // Farbe aufs Bild bringen: dunkel leuchtet sie (addiert), hell färbt sie (mischt) – sonst wäre hell alles weiß
        float3 glow(float3 col, float3 c, float a)
        {
            return lerp(col + c * a, lerp(col, c, saturate(a * 0.8)), u_light);
        }

        float3 paletteColor(float i)
        {
            i = fmod(i, 4.0);
            return i < 1.0 ? u_c0 : i < 2.0 ? u_c1 : i < 3.0 ? u_c2 : u_c3;
        }

        // Sternenfeld wie am echten Himmel: zufällig verteilt (kein Raster), drei Tiefen, die meisten Sterne ganz
        // schwach und nur wenige hell, leicht unterschiedliche Farben, ruhiges Funkeln (rt = echte Sekunden).
        // density 0–1 (z. B. dichter in der Milchstraße). Gibt Licht und Farbe zurück.
        float3 starField(float2 p, float rt, float density)
        {
            float3 s = float3(0.0, 0.0, 0.0);
            [unroll] for (int L = 0; L < 3; L++)
            {
                float fL = (float)L;
                float sc = 30.0 + fL * 34.0;
                float2 g = p * sc + fL * 17.3;
                float2 id = floor(g);
                float h = hash(id + fL * 3.7);
                if (h > (0.30 + fL * 0.10) * density) continue;
                float2 c = 0.12 + 0.76 * hash22(id + fL * 5.1);       // irgendwo in der Zelle
                float d = length(frac(g) - c);
                float mag = pow(hash(id + 9.7), 7.0);                  // fast alle schwach, ganz wenige hell
                float r = 0.05 + mag * 0.13;
                float core = exp(-d * d / (r * r));
                float b = (0.22 + mag * 1.6) * (1.0 - fL * 0.22);
                float tw = 0.8 + 0.2 * sin(rt * (0.3 + h * 0.8) + h * 40.0);
                float t2 = hash(id + 2.2);
                float3 tint = t2 < 0.33 ? float3(0.80, 0.88, 1.0) : t2 < 0.55 ? float3(1.0, 0.92, 0.80) : float3(1.0, 1.0, 1.0);
                s += tint * core * b * tw;
            }
            return s;
        }

        // Rand abdunkeln, Leuchten unter der Maus, Dithering – für alle Effekte gleich
        float3 finish(float3 col, float2 uv, float asp, float3 fl, float2 fc)
        {
            float v = sstep(1.25, 0.15, length((uv - 0.5) * float2(asp * 0.85, 1.0)));
            col *= lerp(1.0, lerp(0.45, 1.0, v), 1.0 - u_light * 0.8);
            if (u_fOn > 0.5)
            {
                float g = clamp(fl.b, 0.0, 1.2);
                float3 tint = lerp(float3(1.0, 1.0, 1.0), u_c0 * 1.7 + 0.12, 0.55);
                col += tint * (g * g * 0.35 + g * 0.65) * u_fGlow * lerp(0.24, 0.08, u_light);
            }
            col += (hash(fc) - 0.5) / 255.0;
            return saturate(col);
        }

        // Gemeinsamer Anfang: Bildkoordinaten (Ursprung unten links wie gl_FragCoord) und Maus-Strömung
        void setup(float4 pos, out float2 fc, out float2 uv, out float asp, out float3 fl)
        {
            fc = float2(pos.x, u_res.y - pos.y);
            uv = fc / u_res;
            asp = u_res.x / u_res.y;
            fl = float3(0.0, 0.0, 0.0);
            if (u_fOn > 0.5) fl = u_flow.SampleLevel(lin, float2(uv.x, 1.0 - uv.y), 0).rgb;
        }
        """;

    /// <summary>Das Lichtfeld (1:1 aus field.js, nur die Koordinaten an Direct3D angepasst).</summary>
    public const string Field = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            // gl_FragCoord hat den Ursprung unten links
            float2 fc = float2(pos.x, u_res.y - pos.y);
            float2 uv = fc / u_res;
            float asp = u_res.x / u_res.y;

            float3 fl = float3(0.0, 0.0, 0.0);
            if (u_fOn > 0.5) fl = u_flow.SampleLevel(lin, float2(uv.x, 1.0 - uv.y), 0).rgb;

            float2 p = (uv - 0.5 - float2(fl.r, -fl.g)) * float2(asp, 1.0) + u_shift;
            float t = u_time;

            float2 w = float2(fbm(p * 1.1 + float2(t * 0.021, -t * 0.017)),
                              fbm(p * 1.1 + float2(5.2 - t * 0.015, 1.3 + t * 0.019)));
            p += (w - 0.5) * 0.6;

            float2 c0 = float2(sin(t * 0.050)       * 0.38 * asp, sin(t * 0.037 + 1.7) * 0.22);
            float2 c1 = float2(sin(t * 0.041 + 2.4) * 0.42 * asp, cos(t * 0.029 + 0.6) * 0.26);
            float2 c2 = float2(cos(t * 0.033 + 4.1) * 0.36 * asp, sin(t * 0.045 + 3.3) * 0.24);
            float2 c3 = float2(cos(t * 0.021 + 1.0) * 0.30 * asp, cos(t * 0.027 + 2.2) * 0.20);

            float e = u_energy;
            float3 col = u_base;
            col = lerp(col, u_c3, blob(p, c3, 0.72 + e * 0.12) * 0.70);
            col = lerp(col, u_c2, blob(p, c2, 0.34 + e * 0.12) * 0.85);
            col = lerp(col, u_c1, blob(p, c1, 0.38 + e * 0.12) * 0.88);
            col = lerp(col, u_c0, blob(p, c0, 0.44 + e * 0.18) * 0.92);

            float lift = 1.0 - u_light;
            col += u_c0 * blob(p, c0, 0.18) * (0.35 + e * 1.1) * lift;
            col += u_c1 * blob(p, c1, 0.16) * e * 0.5 * lift;
            col *= 1.0 + e * 0.09;
            col += u_c1 * blob(p, c1, 0.16) * 0.25 * lift;
            col += u_c2 * blob(p, c2, 0.14) * 0.22 * lift;

            float v = sstep(1.25, 0.15, length((uv - 0.5) * float2(asp * 0.85, 1.0)));
            col *= lerp(1.0, lerp(0.45, 1.0, v), 1.0 - u_light * 0.8);

            if (u_fOn > 0.5)
            {
                float g = clamp(fl.b, 0.0, 1.2);
                float3 tint = lerp(float3(1.0, 1.0, 1.0), u_c0 * 1.7 + 0.12, 0.55);
                col += tint * (g * g * 0.35 + g * 0.65) * u_fGlow * lerp(0.24, 0.08, u_light);
            }

            col += (hash(fc) - 0.5) / 255.0;
            return float4(saturate(col), 1.0);
        }
        """;

    /// <summary>Polarlicht: weiche Lichtvorhänge, die von oben herabhängen und langsam wehen; nachts mit Sternen.</summary>
    public const string Aurora = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.6) * float2(asp, 1.0);

            // Himmel: nach oben dunkler (dunkles Design) bzw. heller (helles Design)
            float3 col = lerp(u_base, u_base * lerp(0.55, 1.05, u_light), saturate(uv.y * 1.2));

            // Sterne, nur im dunklen Design, funkeln leise
            float2 sg = fc / 3.0;
            float2 sid = floor(sg);
            float sr = hash(sid);
            if (sr > 0.9965)
            {
                float tw = 0.7 + 0.3 * sin(u_misc.x * (0.25 + sr * 0.6) + sr * 40.0);   // ruhiges Funkeln, in echter Zeit
                float d = length(frac(sg) - 0.5);
                col += sstep(0.5, 0.0, d) * tw * 0.55 * (1.0 - u_light) * saturate(uv.y * 1.6 - 0.2);
            }

            // vier Vorhänge in den Farben der Palette: helle Unterkante, nach oben ausfransend, mit senkrechten Strahlen
            [unroll] for (int i = 0; i < 4; i++)
            {
                float fi = (float)i;
                float x = p.x * (0.85 + fi * 0.15) + fi * 1.7;
                float wave = fbm(float2(x * 0.7 + t * (0.022 + fi * 0.007), t * 0.015 + fi * 3.1));
                float sway = sin(x * 1.3 + t * (0.05 + fi * 0.02) + fi * 2.0) * 0.06;
                float edge = -0.12 + fi * 0.11 + (wave - 0.5) * 0.65 + sway;   // Unterkante des Vorhangs
                float d = p.y - edge;
                float body = sstep(-0.02, 0.012, d) * exp(-max(d, 0.0) * (3.2 + fi * 0.8));
                // Strahlen: schmale, senkrechte Streifen, die langsam seitwärts wandern
                float streak = fbm(float2(x * 11.0 + t * 0.12, d * 0.8 - t * 0.04));
                float rays = 0.25 + 1.1 * streak * streak;
                float rim = exp(-abs(d) * 38.0) * 0.6;                 // leuchtende Kante unten
                float a = (body * rays + rim * rays) * (0.85 + e * 0.45) * (1.0 - fi * 0.14);
                float3 c = lerp(paletteColor(fi), paletteColor(fi + 1.0), saturate(d * 3.0)); // nach oben in die Nachbarfarbe
                col = glow(col, c * 1.25, a);
            }
            col *= 1.0 + e * 0.06;
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Kaustik: Lichtnetz wie auf dem Grund eines Pools; die Maus-Strömung verzerrt das Wasser.</summary>
    public const string Caustics = Common + """

        // Lichtnetz: Abstand zur nächsten Zellgrenze eines langsam wandernden Zellmusters (Voronoi, F2 − F1).
        // Wo zwei Zellen aneinanderstoßen, bündelt sich das Licht – wie hinter gewellter Wasseroberfläche.
        float net(float2 p, float t)
        {
            float2 id = floor(p), f = frac(p);
            float f1 = 8.0, f2 = 8.0;
            [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float2 o = float2(x, y);
                float2 h = hash22(id + o);
                float2 c = o + 0.5 + 0.38 * sin(t * (0.35 + h * 0.3) + h * 6.2831);
                float d = length(f - c);
                if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) { f2 = d; }
            }
            return exp(-(f2 - f1) * 9.0);
        }

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 1.6) * float2(asp, 1.0) * 3.2;
            // Wellen verbiegen das Netz ein wenig
            p += (float2(fbm(p * 0.5 + t * 0.03), fbm(p * 0.5 + float2(4.1, 7.3) - t * 0.025)) - 0.5) * 0.9;

            // Wasser: oben heller (näher an der Oberfläche), unten tiefer
            float3 deep = lerp(u_base, u_c3, 0.40);
            float3 col = lerp(deep * lerp(0.75, 1.0, u_light), lerp(u_base, u_c2, 0.30), saturate(uv.y));

            float a = net(p, t);
            float b = net(p * 1.6 + float2(3.7, 1.9), t * 1.25);
            float light = a * 0.6 + b * 0.35 + a * b * 0.9;           // wo sich zwei Netze kreuzen, leuchtet es am hellsten
            light *= 0.8 + 0.3 * fbm(p * 0.3 + t * 0.02);              // ungleichmäßige Helligkeit
            float3 tint = lerp(u_c0, u_c1, saturate(fbm(p * 0.25 + t * 0.01)));
            col = glow(col, tint * 1.2, light * (0.55 + e * 0.35));
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Bokeh: unscharfe, schwebende Lichtpunkte in drei Tiefen; sie weichen der Maus leicht aus (Parallaxe).</summary>
    public const string Bokeh = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5) * float2(asp, 1.0);

            // Hintergrund: weicher Verlauf mit etwas Dunst
            float3 col = lerp(u_base, lerp(u_base, u_c3, 0.35), saturate(1.0 - uv.y));
            col = glow(col, u_c2, fbm(p * 1.3 + t * 0.01) * 0.10);

            [unroll] for (int L = 0; L < 3; L++)
            {
                float fl3 = (float)L;
                float scale = 3.2 + fl3 * 2.4;                       // hinten kleiner und dichter
                float depth = 1.0 - fl3 * 0.3;                       // vorne näher: stärkere Parallaxe, größer
                float2 g = (p - u_shift * 0.06 * depth - float2(fl.r, -fl.g) * 0.4 * depth) * scale
                         + float2(t * 0.010 * (fl3 + 1.0), t * 0.018 * (fl3 + 1.0));
                float2 id = floor(g);
                float2 f = frac(g);
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 cell = id + float2(x, y);
                    float pick = hash(cell + fl3 * 13.7);
                    if (pick < 0.55) continue;                       // nicht jede Zelle hat ein Licht
                    float2 c = float2(x, y) + 0.2 + 0.6 * hash22(cell + fl3 * 7.1);
                    float r = (0.16 + 0.22 * hash(cell * 1.7 + 3.0)) * (0.65 + depth * 0.45);
                    float d = length(f - c);
                    float soft = 0.18 + fl3 * 0.22;                  // hinten unschärfer
                    float disc = sstep(r, r * (1.0 - soft), d);
                    float ring = sstep(r, r * 0.86, d) - sstep(r * 0.86, r * 0.70, d);  // heller Rand wie bei echtem Bokeh
                    float tw = 0.7 + 0.3 * sin(t * (0.4 + pick) + pick * 30.0);
                    float a = (disc * 0.55 + ring * 0.25) * tw * (0.55 - fl3 * 0.12) * (0.8 + e * 0.5);
                    col = glow(col, paletteColor(floor(pick * 37.0)), a);
                }
            }
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Nebel: dichter, langsam wabernder Rauch (verschachtelt verzerrtes Rauschen) in den Palettenfarben.</summary>
    public const string Smoke = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.9) * float2(asp, 1.0) * 1.5;

            float2 q = float2(fbm(p + float2(t * 0.020, -t * 0.012)), fbm(p + float2(5.2 - t * 0.016, 1.3 + t * 0.011)));
            float2 r = float2(fbm(p + 3.5 * q + float2(1.7 + t * 0.014, 9.2)), fbm(p + 3.5 * q + float2(8.3, 2.8 - t * 0.013)));
            float f = fbm(p + 3.5 * r);

            float3 col = u_base;
            col = lerp(col, lerp(u_base, u_c3, 0.8), saturate(f * f * 2.2));
            col = lerp(col, lerp(u_base, u_c1, 0.7), saturate(length(q) * 0.55));
            col = lerp(col, lerp(u_base, u_c0, 0.8), saturate(r.x * r.x * 0.9));
            // Lichtsäume an den dichtesten Stellen, zur Musik etwas heller
            float hi = sstep(0.55, 0.85, f);
            col = glow(col, u_c2, hi * (0.18 + e * 0.25));
            col *= lerp(0.75 + f * 0.5, 1.0, u_light);
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Sternenhimmel: driftender Nebel in den Palettenfarben, funkelnde Sterne in drei Tiefen, ab und zu eine Sternschnuppe.</summary>
    public const string Stars = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5) * float2(asp, 1.0);

            // Nebel: zwei verschachtelte Rauschschichten, langsam driftend, mit etwas Maus-Parallaxe
            float2 q = p * 1.2 - u_shift * 0.03 - float2(fl.r, -fl.g) * 0.3 + float2(t * 0.004, t * 0.002);
            float n1 = fbm(q * 1.5 + fbm(q * 0.8 + t * 0.01) * 1.5);
            float n2 = fbm(q * 2.3 + float2(3.1, 7.7) - t * 0.006);
            float3 col = u_base * lerp(0.8, 1.0, u_light);
            col = glow(col, u_c1 * 1.1, pow(n1, 2.0) * 0.85);
            col = glow(col, u_c2 * 1.1, pow(n2, 2.6) * 0.7);
            col = glow(col, u_c0, pow(saturate(n1 * n2 * 1.8), 3.0) * 0.4 * (1.0 + e * 0.6));

            // Sterne wie am echten Himmel (siehe starField), mit leichter Maus-Parallaxe; im Nebel etwas dichter
            col = glow(col, starField(p - u_shift * 0.03, u_misc.x, 1.1 + n1 * 0.6), lerp(1.0, 0.5, u_light));

            // Sternschnuppe: etwa alle 9 Zeiteinheiten eine, kurz sichtbar
            float per = 9.0;
            float k = floor(t / per), ph = frac(t / per) * per;
            if (ph < 1.4)
            {
                float2 s0 = (hash22(float2(k, 3.7)) - 0.5) * float2(asp * 0.9, 0.6) + float2(0.0, 0.25);
                float2 dir = normalize(float2(-0.8, -0.45));
                float2 head = s0 + dir * ph * 0.55;
                float2 rel = p - head;
                float along = dot(rel, -dir);                       // hinter dem Kopf
                float across = abs(rel.x * dir.y - rel.y * dir.x);
                float tail = sstep(0.0, 0.02, along) * sstep(0.35, 0.0, along) * sstep(0.006, 0.0, across);
                float fade = sstep(0.0, 0.2, ph) * sstep(1.4, 0.9, ph);
                col = glow(col, float3(1.0, 1.0, 1.0), tail * fade * 0.9);
            }
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Seide: breite, fließende Farbbahnen wie wehende Tücher, mit sanftem Glanz.</summary>
    public const string Silk = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.8) * float2(asp, 1.0);

            float3 col = lerp(u_base, lerp(u_base, u_c3, 0.25), saturate(uv.y));
            [unroll] for (int i = 0; i < 4; i++)
            {
                float fi = (float)i;
                // Mittellinie der Bahn: große, langsame Welle plus etwas Rauschen
                float y = p.y - (fi - 1.5) * 0.17
                        + sin(p.x * (1.0 + fi * 0.25) + t * (0.06 + fi * 0.015) + fi * 1.7) * 0.16
                        + (fbm(float2(p.x * 0.7 + fi * 3.0, t * 0.025 + fi)) - 0.5) * 0.30;
                float w = 0.075 + 0.035 * sin(p.x * 1.7 + t * 0.04 + fi * 2.3);  // Breite wechselt
                float band = exp(-(y * y) / (w * w));
                // Glanz: schräg über die Bahn laufende Lichtfalten
                float sheen = pow(saturate(0.5 + 0.5 * sin(p.x * 2.6 - y * 26.0 + t * 0.18 + fi * 1.3)), 5.0);
                float a = band * (0.38 + sheen * 0.55) * (0.9 + e * 0.35);
                col = glow(col, lerp(paletteColor(fi), paletteColor(fi + 1.0), sheen * 0.5) * 1.1, a);
            }
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Lavalampe: große, weiche Blasen steigen langsam auf, verschmelzen und trennen sich; die Musik lässt sie pulsieren.</summary>
    public const string Lava = Common + """

        // Summe der Blasen (Metaballs): > 1 heißt „im Wachs“; tint = gewichtete Farbe der beteiligten Blasen
        float lavaField(float2 p, float t, float e, float asp, out float3 tint)
        {
            float field = 0.0;
            tint = float3(0.0, 0.0, 0.0);
            [unroll] for (int i = 0; i < 10; i++)
            {
                float fi = (float)i;
                float3 h = float3(hash(float2(fi, 1.3)), hash(float2(fi, 7.9)), hash(float2(fi, 4.1)));
                // wie in einer echten Lavalampe: langsam aufsteigen, oben verweilen, wieder absinken (stetig, ohne Sprung)
                float y = sin(t * (0.017 + h.z * 0.015) + h.y * 6.2831) * 0.5 + (h.x - 0.5) * 0.12;   // gemächlich wie echtes Wachs
                // über die ganze Breite verteilt, jede Blase in ihrer eigenen Spalte
                float x = ((fi + 0.5) / 10.0 - 0.5) * asp * 0.92 + (h.x - 0.5) * 0.12 + sin(t * (0.025 + h.z * 0.02) + fi * 2.1) * 0.06;
                float r = (0.08 + h.z * 0.07) * (0.85 + 0.15 * sin(t * 0.07 + fi));  // ohne Musik-Zucken: das ließ Verbindungen flackern
                float2 d = p - float2(x, y);
                float f = r * r / (dot(d, d) + r * r * 0.2);   // in der Mitte begrenzt (kein Lichtpunkt)
                field += f;
                tint += paletteColor(fi) * f;
            }
            tint /= max(field, 1e-4);
            return field;
        }

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.6) * float2(asp, 1.0);

            float3 tint, tmp;
            float field = lavaField(p, t, e, asp, tint);
            // Licht von oben links: Neigung der „Oberfläche“ aus zwei Nachbarwerten
            float eps = 0.006;
            float fx = lavaField(p + float2(eps, 0.0), t, e, asp, tmp) - field;
            float fy = lavaField(p + float2(0.0, eps), t, e, asp, tmp) - field;
            // Höhe als Kuppel (0 am Rand, 1 in der Mitte): daraus die Neigung, damit die Blasen rund wirken
            // Höhe mit endlicher Steigung am Rand (sqrt wäre dort unendlich steil und springt beim Verschmelzen)
            float H = 1.0 - exp(-max(field - 0.8, 0.0) * 1.6);
            float Hx = 1.0 - exp(-max(field + fx - 0.8, 0.0) * 1.6) - H;
            float Hy = 1.0 - exp(-max(field + fy - 0.8, 0.0) * 1.6) - H;
            float3 n = normalize(float3(-Hx, -Hy, eps * 4.0));
            float diff = saturate(dot(n, normalize(float3(-0.45, 0.6, 0.65))));
            float spec = pow(diff, 18.0);

            float3 col = lerp(u_base, lerp(u_base, u_c3, 0.35), saturate(1.0 - uv.y));
            // klare, aber geglättete Kante (eine Pixelbreite); Hälse zwischen Blasen bleiben fließend, weil das Feld stetig ist
            float aa = max(fwidth(field) * 1.2, 0.015);
            float inside = sstep(1.0 - aa, 1.0 + aa, field);
            float3 wax = tint * (0.45 + diff * 0.65) + spec * 0.35;
            col = lerp(col, lerp(wax, wax * 0.8 + 0.15, u_light * 0.4), inside);
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Teich: Regentropfen auf einer Wasseroberfläche, Ringe breiten sich aus; Bässe lassen sie stärker leuchten.</summary>
    public const string Pond = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 1.2) * float2(asp, 1.0);

            // Wasser mit leichtem Schimmer
            float3 col = lerp(lerp(u_base, u_c3, 0.35), u_base, saturate(uv.y));
            col = glow(col, u_c2, fbm(p * 2.0 + float2(t * 0.02, -t * 0.015)) * 0.12);

            float ring = 0.0;
            [unroll] for (int i = 0; i < 9; i++)
            {
                float fi = (float)i;
                float per = 5.0 + hash(float2(fi, 2.2)) * 4.0;
                float lt = t * 0.9 + fi * per / 9.0 * 3.1;
                float k = floor(lt / per);
                float age = frac(lt / per) * per;                   // Zeit seit dem Tropfen
                float2 c = (hash22(float2(k * 1.7 + fi, fi * 3.3)) - 0.5) * float2(asp * 0.95, 0.95);
                float d = length(p - c);
                float r = age * 0.11;
                float amp = exp(-age * 0.55);
                // zwei, drei Wellenkämme hintereinander
                float w = sin((d - r) * 70.0) * exp(-pow((d - r) * 14.0, 2.0)) ;
                ring += max(w, 0.0) * amp + exp(-pow((d - r) * 30.0, 2.0)) * amp * 0.5;
            }
            float3 rc = lerp(u_c0, u_c1, saturate(fbm(p * 0.6 + t * 0.01)));
            col = glow(col, rc * 1.2, ring * (0.45 + e * 0.45));
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Glühwürmchen: kleine Lichtpunkte schwirren umher und sammeln sich sanft dort, wo die Maus ist.</summary>
    public const string Fireflies = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5) * float2(asp, 1.0);
            float2 mouse = u_shift * float2(asp, 1.0);              // geglättete Mausposition (Mitte = 0)

            float3 col = lerp(u_base, lerp(u_base, u_c3, 0.4), saturate(1.0 - uv.y * 1.2));
            col = glow(col, u_c2, fbm(p * 1.2 + t * 0.008) * 0.08);

            [unroll] for (int L = 0; L < 2; L++)
            {
                float fL = (float)L;
                float sc = 5.0 + fL * 3.0;
                float2 g = p * sc;
                float2 id = floor(g);
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 cell = id + float2(x, y);
                    float h = hash(cell + fL * 5.1);
                    if (h < 0.72) continue;                              // nur wenige Zellen haben eins
                    // umherschwirren
                    float2 wob = float2(sin(t * (0.21 + h * 0.3) + h * 20.0), cos(t * (0.17 + h * 0.25) + h * 31.0)) * 0.45;
                    float2 c = (cell + 0.5 + wob) / sc;
                    // zur Maus hingezogen, je näher, desto stärker
                    float2 toM = mouse - c;
                    c += toM * 0.22 * exp(-dot(toM, toM) * 3.0);
                    float d = length(p - c) * sc;
                    float blink = 0.35 + 0.65 * pow(0.5 + 0.5 * sin(t * (0.9 + h * 1.5) + h * 60.0), 3.0);
                    float a = exp(-d * d * 180.0) * 1.2 + exp(-d * d * 14.0) * 0.16; // heller Kern, weicher Schein
                    float3 fcol = lerp(lerp(u_c0, u_c1, h) * 1.4 + 0.12, float3(0.95, 1.0, 0.55), 0.35);   // leicht warm wie echte
                    col = glow(col, fcol, a * blink * (1.0 - fL * 0.35) * (0.85 + e * 0.4));
                }
            }
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Höhenlinien: langsam wandernde Linien wie auf einer Landkarte, jede fünfte etwas kräftiger.</summary>
    public const string Contours = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.7) * float2(asp, 1.0);

            float h = fbm(p * 1.3 + float2(t * 0.010, -t * 0.007)) + 0.35 * fbm(p * 3.1 - t * 0.012);
            float v = h * 16.0;
            float dv = max(fwidth(v), 1e-3);
            float f = frac(v);
            float isoLine = 1.0 - sstep(0.0, dv * 1.3, min(f, 1.0 - f));
            float major = fmod(floor(v + 0.5), 5.0) < 0.5 ? 1.0 : 0.0;

            float3 col = u_base;
            col = lerp(col, lerp(u_base, u_c3, 0.45), saturate(h * 1.1 - 0.2));   // leichte Schummerung nach Höhe
            float3 lc = lerp(u_c0, u_c2, saturate(h * 1.2 - 0.1));
            col = glow(col, lc * 1.2, isoLine * lerp(0.32, 0.7, major) * (0.9 + e * 0.4));
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>
    /// Wetter: zeigt das echte Wetter der Wetter-Pille – Wolken, Regen, Schnee, Nebel, Gewitter mit Wetterleuchten,
    /// in klaren Nächten Sterne. Ohne Wetterdaten ein klarer Himmel (Tag/Nacht nach Uhrzeit).
    /// </summary>
    public const string Weather = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.5) * float2(asp, 1.0);
            float kind = u_wxKind, amt = u_wxAmt, cloud = u_wxCloud, night = u_wxNight;
            bool rain = kind > 2.5 && kind < 3.5 || kind > 4.5, snow = kind > 3.5 && kind < 4.5, fog = kind > 1.5 && kind < 2.5;

            // Himmel: Verlauf aus den Palettenfarben, nachts dunkler, bei dichten Wolken grauer
            float3 top = lerp(lerp(u_base, u_c0, 0.45), u_base * 0.6, night);
            float3 bottom = lerp(lerp(u_base, u_c2, 0.35), lerp(u_base, u_c3, 0.3), night);
            float3 col = lerp(bottom, top, saturate(uv.y));
            float3 grey = dot(col, float3(0.3, 0.5, 0.2)).xxx;
            col = lerp(col, grey * 1.05, cloud * 0.45);

            // tagsüber sanfter Sonnenschein oben rechts, nachts ein Mondschein; bei Wolken schwächer
            float2 sun = float2(asp * 0.32, 0.38);
            float3 sunCol = lerp(lerp(u_c0, float3(1.0, 0.92, 0.75), 0.5), float3(0.75, 0.82, 1.0), night);
            col = glow(col, sunCol, exp(-length(p - sun) * lerp(2.4, 4.0, night)) * lerp(0.35, 0.3, night) * (1.0 - cloud * 0.8));

            // Sterne in klaren Nächten (bei Niederschlag und Nebel keine), mit zarter Milchstraße und Mondscheibe
            if (night > 0.5 && !rain && !snow && !fog)
            {
                float clear = saturate(1.0 - cloud * 1.3) * saturate(uv.y * 1.3 + 0.15);
                float2 mw = float2(p.x * 0.94 + p.y * 0.34, -p.x * 0.34 + p.y * 0.94);   // schräger Streifen
                float band = exp(-pow((mw.y - 0.08) / 0.16, 2.0)) * (0.55 + 0.45 * fbm(mw * 4.0 + 3.1));
                col = glow(col, lerp(u_c2, float3(0.8, 0.82, 0.9), 0.5), band * 0.10 * clear);
                float md = length(p - sun);
                float moon = sstep(0.033, 0.029, md);                  // Mondscheibe (Sterne liegen dahinter)
                col = glow(col, starField(p, u_misc.x, 1.0 + band * 1.2), 0.95 * clear * (1.0 - moon));
                // Mond: zum Rand dunkler, mit leichten Flecken (Mare), nicht grell
                float rim = saturate(md / 0.031);
                float mare = fbm((p - sun) * 70.0 + 4.0);
                float3 moonCol = float3(0.86, 0.88, 0.92) * (1.0 - rim * rim * 0.35) * (0.82 + 0.18 * sstep(0.62, 0.38, mare));
                col = lerp(col, moonCol * lerp(0.9, 1.0, u_light), moon * (1.0 - cloud * 0.7));
            }

            // Wolken: zwei Schichten, die langsam ziehen
            float2 cq = p * float2(1.2, 2.2) + float2(t * 0.02, 0.0);
            float c1 = fbm(cq + fbm(cq * 0.7 + t * 0.005));
            float c2 = fbm(cq * 1.9 + float2(5.0, 2.0) + float2(t * 0.035, 0.0));
            float cover = sstep(0.62 - cloud * 0.4, 0.95 - cloud * 0.3, c1 * 0.7 + c2 * 0.45);
            float3 cc = lerp(lerp(u_c1, float3(0.85, 0.87, 0.92), 0.65), lerp(u_base, u_c1, 0.6) + 0.04, night * 0.7);
            col = lerp(col, cc * lerp(0.6, 1.0, u_light), cover * (0.5 + cloud * 0.45) * saturate(uv.y * 1.4 + 0.2));

            // Regen: dünne, bewegungsunscharfe Tropfen in vier Tiefen (vorne wenige, lange und helle, hinten viele,
            // kurze und blasse), leicht vom Wind geneigt, dazu ziehende Regenschleier. Läuft in echter Zeit –
            // ein Regen in Zeitlupe wirkt nie echt.
            if (rain)
            {
                float rt = u_misc.x;
                // Schleier: senkrecht gestrecktes Rauschen, das schnell nach unten zieht – wie Regenvorhänge in der Ferne
                float sheets = fbm(float2(p.x * 3.5 + p.y * 0.4, p.y * 0.35 + rt * 0.9));
                col = lerp(col, grey * 1.12 + 0.03, (0.10 + sheets * 0.22) * amt);

                [unroll] for (int L = 0; L < 4; L++)
                {
                    float fL = (float)L;
                    float cols = 38.0 + fL * 55.0;                       // Spalten je Bildhöhe: hinten dichter
                    float cellH = 0.42 - fL * 0.06;                      // Höhe einer Zelle (ein Tropfen je Zelle)
                    float2 q = float2(p.x + p.y * (0.10 + fL * 0.015), p.y);   // Wind: leicht schräg
                    float colId = floor(q.x * cols);
                    float speed = (2.4 - fL * 0.35) * (0.85 + 0.3 * hash(float2(colId, fL * 7.0)));
                    float y = q.y + rt * speed + hash(float2(colId, 3.0 + fL)) * 17.0;
                    float2 id = float2(colId, floor(y / cellH));
                    float fx = frac(q.x * cols);
                    float fy = frac(y / cellH);
                    float h = hash(id + fL * 13.1);
                    if (h > (0.18 + fL * 0.12) * (0.15 + amt * 0.85)) continue;  // Anteil der Zellen mit Tropfen: hängt stark von der Menge ab
                    float cx = 0.25 + 0.5 * hash(id + 5.3);
                    float len = (0.25 + 0.45 * hash(id + 9.1)) * (1.0 - fL * 0.15);  // Länge in Zellen (Bewegungsunschärfe)
                    float cy = hash(id + 2.7) * (1.0 - len);
                    float d = (fy - cy) / len;                           // 0 = Kopf (unten), 1 = Ende der Schliere
                    if (d < 0.0 || d > 1.0) continue;
                    float wid = 0.16 + fL * 0.06;                        // hinten breiter in Zellen, aber Zellen schmaler
                    float core = sstep(wid, 0.0, abs(fx - cx));
                    // gleichmäßiger Strich mit weichen Enden (wie auf einem Foto mit Belichtungszeit), nicht als Komet
                    float a = core * pow(sin(3.14159 * d), 0.6) * (0.5 + 0.5 * hash(id + 1.1));
                    float bright = (0.42 - fL * 0.09) * (0.55 + amt * 0.6);
                    col = glow(col, lerp(float3(0.78, 0.83, 0.92), u_c1 + 0.35, 0.2), a * bright);
                }
            }

            // Schnee: einzelne Flocken in drei Tiefen, langsam taumelnd (in echter Zeit, wie der Regen)
            if (snow)
            {
                [unroll] for (int L = 0; L < 3; L++)
                {
                    float fL = (float)L;
                    float sc = 9.0 + fL * 7.0;
                    float2 sp = p * sc;
                    float st = u_misc.x;
                    sp.y += st * sc * (0.06 + (2.0 - fL) * 0.015);       // vorne etwas schneller (näher)
                    sp.x += sin(sp.y * 0.6 + st * 0.5 + fL) * 0.35;
                    float2 id = floor(sp);
                    float h = hash(id + fL * 4.4);
                    if (h > 1.0 - amt * (0.22 - fL * 0.04))                // deutlich weniger Flocken
                    {
                        float2 c = 0.25 + 0.5 * hash22(id + 1.7);
                        float d = length(frac(sp) - c);
                        col = glow(col, float3(1.0, 1.0, 1.0), sstep(0.13 - fL * 0.03, 0.0, d) * (0.7 - fL * 0.2));
                    }
                }
            }

            // Nebel: dichter Dunst, unten stärker
            if (fog)
            {
                float m = fbm(p * float2(1.5, 3.0) + float2(t * 0.02, 0.0));
                float haze = saturate((1.1 - uv.y) * (0.5 + m * 0.8)) * amt;
                col = lerp(col, lerp(grey * 1.3 + 0.12, u_c2 * 0.6 + 0.3, 0.3), saturate(haze * 1.1));
            }

            // Gewitter: ab und zu ein Wetterleuchten in den Wolken
            if (kind > 4.5)
            {
                float k = floor(t * 0.35);
                float ph = frac(t * 0.35);
                float hit = step(0.72, hash(float2(k, 9.1)));
                float flash = hit * exp(-ph * 9.0) * (0.6 + 0.4 * sin(ph * 60.0));
                float2 at = (hash22(float2(k, 2.2)) - 0.5) * float2(asp, 0.6) + float2(0.0, 0.2);
                float near = exp(-dot(p - at, p - at) * 2.5);
                col = glow(col, float3(0.85, 0.88, 1.0), flash * (0.25 + near * 0.6) * saturate(uv.y + 0.3));
            }
            col *= 1.0 + e * 0.05;
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>Musik-Wellen: vier weiche Lichtbänder, je eines für Bass, Mitten und Höhen; ohne Musik ruhig.</summary>
    public const string Waves = Common + """

        float4 main(float4 pos : SV_Position, float2 uvIn : TEXCOORD0) : SV_Target
        {
            float2 fc, uv; float asp; float3 fl;
            setup(pos, fc, uv, asp, fl);
            float t = u_time, e = u_energy;
            float2 p = (uv - 0.5 - float2(fl.r, -fl.g) * 0.6) * float2(asp, 1.0);

            float3 col = lerp(u_base, lerp(u_base, u_c3, 0.3), saturate(1.0 - abs(p.y) * 1.6));
            float bands[4] = { u_bands.x, u_bands.y, u_bands.z, u_bands.w };
            [unroll] for (int i = 0; i < 4; i++)
            {
                float fi = (float)i;
                float b = bands[i];
                float freq = 1.4 + fi * 1.1;                        // tiefe Töne: lange Wellen, hohe: kurze
                float amp = 0.035 + b * (0.20 - fi * 0.03);
                float y = (fi - 1.5) * 0.055
                        + sin(p.x * freq + t * (0.25 + fi * 0.12) + fi * 1.9) * amp
                        + sin(p.x * freq * 2.3 - t * (0.18 + fi * 0.07)) * amp * 0.35;
                float d = abs(p.y - y);
                float core = exp(-d * (110.0 - b * 40.0));
                float soft = exp(-d * 14.0) * (0.10 + b * 0.25);
                col = glow(col, paletteColor(fi) * 1.25, (core * 0.75 + soft) * (0.7 + b * 0.8));
            }
            return float4(finish(col, uv, asp, fl, fc), 1.0);
        }
        """;

    /// <summary>
    /// Feld auf Bildschirmgröße ziehen und die Körnung darüberlegen (wie .grain: zufälliges 256-px-Muster,
    /// zwei Zufallswerte addiert, Überlagern-Mischung mit grain × 0,5 bzw. × 0,75 beim hellen Theme).
    /// </summary>
    public const string Upscale = """
        cbuffer U : register(b0) { float u_grain; float u_gain; float2 _pad; float3 u_ubase; float _pad2; };
        Texture2D src : register(t0);
        SamplerState lin : register(s0);

        float hash2(float2 p)
        {
            p = frac(p * float2(443.897, 441.423));
            p += dot(p, p.yx + 19.19);
            return frac((p.x + p.y) * p.x);
        }

        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            float3 b = src.SampleLevel(lin, uv, 0).rgb;
            // Stärke des Effekts: Abstand zur Grundfarbe verkleinern oder vergrößern (1 = unverändert)
            b = saturate(u_ubase + (b - u_ubase) * u_gain);
            if (u_grain > 0.0)
            {
                float2 cell = fmod(floor(pos.xy), 256.0);
                float n = (hash2(cell) + hash2(cell + 97.0)) * 0.5;
                float3 ov = b < 0.5 ? 2.0 * b * n : 1.0 - 2.0 * (1.0 - b) * (1.0 - n);
                b = lerp(b, ov, u_grain);
            }
            return float4(b, 1.0);
        }
        """;
}

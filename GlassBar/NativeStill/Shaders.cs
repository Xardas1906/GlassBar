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
    /// Gemeinsamer Teil aller Hintergrund-Effekte: Eingänge (Palette, Zeit, Musik-Energie, Maus-Strömung) und
    /// Rauschfunktionen. Lichtfeld, Polarlicht, Kaustik, Bokeh und Nebel hängen ihre eigene main() daran.
    /// </summary>
    public const string Common = """
        cbuffer P : register(b0)
        {
            float2 u_res;  float u_time;  float u_energy;
            float u_light; float2 u_shift; float u_fOn;
            float3 u_base; float u_fGlow;
            float3 u_c0;   float _p0;
            float3 u_c1;   float _p1;
            float3 u_c2;   float _p2;
            float3 u_c3;   float _p3;
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
                float tw = 0.55 + 0.45 * sin(t * (0.6 + sr * 2.0) + sr * 40.0);
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

    /// <summary>
    /// Feld auf Bildschirmgröße ziehen und die Körnung darüberlegen (wie .grain: zufälliges 256-px-Muster,
    /// zwei Zufallswerte addiert, Überlagern-Mischung mit grain × 0,5 bzw. × 0,75 beim hellen Theme).
    /// </summary>
    public const string Upscale = """
        cbuffer U : register(b0) { float u_grain; float3 _pad; };
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

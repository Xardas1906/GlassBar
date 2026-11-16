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

    /// <summary>Das Lichtfeld (1:1 aus field.js, nur die Koordinaten an Direct3D angepasst).</summary>
    public const string Field = """
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

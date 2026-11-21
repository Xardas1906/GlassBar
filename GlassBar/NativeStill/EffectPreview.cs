using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlassBar.NativeStill;

/// <summary>
/// Kleine Vorschaubilder der Hintergründe für die Einstellungen: jeden Effekt einmal mit den Farben der Palette
/// in eine kleine Textur zeichnen und als Bild zurückgeben (eigenes Direct3D-Gerät, unabhängig vom Hintergrund).
/// </summary>
public static class EffectPreview
{
    public const int Width = 160, Height = 90;

    /// <summary>Vorschau für alle Hintergründe (Schlüssel wie Config.WallpaperField). Läuft im Hintergrund-Thread.</summary>
    public static Dictionary<string, BitmapSource> RenderAll(StillPalette palette)
    {
        var result = new Dictionary<string, BitmapSource>();
        try { RenderEffects(palette, result); }
        catch (Exception ex) { Log.Error("Vorschau der Hintergründe", ex); }
        try { if (WallpaperThumb() is { } img) result["Image"] = img; } catch { }
        result["Solid"] = SolidThumb(palette.Base);
        return result;
    }

    private static void RenderEffects(StillPalette pal, Dictionary<string, BitmapSource> result)
    {
        var wx = EffectWeather.Effective();
        var p = new StillRenderer.FieldParams
        {
            Time = 140,   // ein Zeitpunkt, an dem alle Effekte typisch aussehen
            Light = pal.Light,
            Base = pal.Base, C0 = pal.C0, C1 = pal.C1, C2 = pal.C2, C3 = pal.C3,
            // Wetter: wie gerade draußen; Musik-Wellen: mittlere Lautstärke, damit man die Bänder sieht
            WxKind = wx.Kind, WxAmount = wx.Amount, WxCloud = wx.Cloud, WxNight = wx.Night,
            Bands = new Vector4(0.6f, 0.45f, 0.35f, 0.25f),
        };
        Render(Shaders.Effects.Prepend(("Light", Shaders.Field)), p, Width, Height, result);
    }

    /// <summary>Einen Effekt in beliebiger Größe mit vorgegebenen Werten zeichnen (z. B. zum Prüfen der Wetterarten).</summary>
    internal static BitmapSource? RenderOne(string key, StillRenderer.FieldParams p, int width, int height)
    {
        var src = key == "Light" ? Shaders.Field : Array.Find(Shaders.Effects, x => x.Key == key).Source;
        if (src == null) return null;
        var result = new Dictionary<string, BitmapSource>();
        Render(new[] { (key, src) }, p, width, height, result);
        return result.GetValueOrDefault(key);
    }

    private static void Render(IEnumerable<(string Key, string Source)> effects, StillRenderer.FieldParams p, int w, int h,
                               Dictionary<string, BitmapSource> result)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        // Grafikkarte, sonst Software (WARP)
        ID3D11Device device;
        ID3D11DeviceContext ctx;
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out ctx).Failure)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out ctx).CheckError();
        using var dev = device;
        using var c = ctx;

        using var vs = dev.CreateVertexShader(StillRenderer.Compile(Shaders.Vertex, "vs_5_0").Span);
        using var cb = dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<StillRenderer.FieldParams>(), BindFlags.ConstantBuffer));
        using var sampler = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        using var target = dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget));
        using var rtv = dev.CreateRenderTargetView(target);
        using var staging = dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        p.Res = new Vector2(w, h);
        c.UpdateSubresource(in p, cb);

        foreach (var (name, src) in effects)
        {
            try
            {
                using var ps = dev.CreatePixelShader(StillRenderer.Compile(src, "ps_5_0").Span);
                c.OMSetRenderTargets(rtv);
                c.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, w, h));
                c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                c.VSSetShader(vs);
                c.PSSetShader(ps);
                c.PSSetConstantBuffer(0, cb);
                c.PSSetSampler(0, sampler);
                c.Draw(3, 0);
                c.CopyResource(staging, target);

                var map = c.Map(staging, 0, MapMode.Read);
                try
                {
                    var pixels = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * w * 4, w * 4);
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                    bmp.Freeze();
                    result[name] = bmp;
                }
                finally { c.Unmap(staging, 0); }
            }
            catch (Exception ex) { Log.Error("Vorschau " + name, ex); }
        }
        c.ClearState();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder buf, uint winIni);

    /// <summary>Das aktuelle Windows-Hintergrundbild, verkleinert.</summary>
    private static BitmapSource? WallpaperThumb()
    {
        var sb = new System.Text.StringBuilder(520);
        SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0);
        string path = sb.ToString();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (!File.Exists(path)) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = Width * 2;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static BitmapSource SolidThumb(Vector3 c)
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(Math.Clamp(c.Z, 0, 1) * 255);
            pixels[i + 1] = (byte)(Math.Clamp(c.Y, 0, 1) * 255);
            pixels[i + 2] = (byte)(Math.Clamp(c.X, 0, 1) * 255);
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        bmp.Freeze();
        return bmp;
    }
}

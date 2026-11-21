using System.Windows.Media.Imaging;
using GlassBar.NativeStill;
using Xunit;

public class EffectPreviewTests
{
    [Fact]
    public void RendersEveryEffectWithVisibleContent()
    {
        var previews = EffectPreview.RenderAll(new StillPalette());
        foreach (var key in new[] { "Light", "Aurora", "Caustics", "Bokeh", "Smoke", "Solid" })
            Assert.True(previews.ContainsKey(key), key);
        foreach (var key in WallpaperRotation.Effects)
        {
            var bmp = previews[key];
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            int min = 255, max = 0;
            for (int i = 0; i < px.Length; i += 4) { int l = px[i] + px[i + 1] + px[i + 2]; min = Math.Min(min, l); max = Math.Max(max, l); }
            Assert.True(max - min > 20, $"{key}: Vorschau ist einfarbig ({min}..{max})");
        }
    }
}

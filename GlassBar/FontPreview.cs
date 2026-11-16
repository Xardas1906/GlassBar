using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace GlassBar;

/// <summary>
/// Lädt die Schriften des Hintergrunds „Still“ (Google Fonts) einmalig herunter, damit die Vorschau in den
/// Einstellungen sie anzeigen kann. Sie liegen danach in %AppData%\GlassBar\Fonts.
/// </summary>
public static class FontPreview
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string Folder = Path.Combine(Config.Folder, "Fonts");
    private static readonly Dictionary<string, Task<FontFamily?>> Cache = new();

    /// <summary>Schlüssel aus „Still“ → (Familienname, Google-Fonts-Abfrage, Strichstärke der Uhr).</summary>
    public static readonly Dictionary<string, (string Family, string? Query, int Weight)> Fonts = new()
    {
        ["dmsans"] = ("DM Sans", "DM+Sans:wght@200", 200),
        ["manrope"] = ("Manrope", "Manrope:wght@200", 200),
        ["poppins"] = ("Poppins", "Poppins:wght@200", 200),
        ["inter"] = ("Inter", "Inter:wght@200", 200),
        ["geist"] = ("Geist", "Geist:wght@200", 200),
        ["outfit"] = ("Outfit", "Outfit:wght@200", 200),
        ["serif"] = ("Instrument Serif", "Instrument+Serif", 400),
        ["system"] = ("Segoe UI Variable Display", null, 300),
    };

    public static Task<FontFamily?> GetAsync(string key)
    {
        if (!Cache.TryGetValue(key, out var t)) Cache[key] = t = LoadAsync(key);
        return t;
    }

    private static async Task<FontFamily?> LoadAsync(string key)
    {
        if (!Fonts.TryGetValue(key, out var f)) return null;
        if (f.Query == null) return new FontFamily(f.Family); // in Windows enthalten
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, key + ".ttf");
            if (!File.Exists(file))
            {
                // Ohne Browser-Kennung liefert Google Fonts TTF-Dateien, die WPF direkt lesen kann
                var css = await Http.GetStringAsync($"https://fonts.googleapis.com/css2?family={f.Query}");
                var m = Regex.Match(css, @"url\((https://[^)]+\.ttf)\)");
                if (!m.Success) return null;
                await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(m.Groups[1].Value));
            }
            // Familie gezielt aus genau dieser Datei laden. (Fonts.GetFontFamilies liest den ganzen
            // Ordner und lieferte für jede Datei dieselbe erste Schrift.)
            var glyphs = new GlyphTypeface(new Uri(file));
            var name = glyphs.FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var n)
                ? n : glyphs.FamilyNames.Values.First();
            return new FontFamily(new Uri(Folder + Path.DirectorySeparatorChar), $"./{key}.ttf#{name}");
        }
        catch { return null; }
    }

    public static System.Windows.FontWeight WeightOf(string key) =>
        System.Windows.FontWeight.FromOpenTypeWeight(Fonts.TryGetValue(key, out var f) ? f.Weight : 300);
}

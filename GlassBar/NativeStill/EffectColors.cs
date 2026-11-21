using System.Numerics;

namespace GlassBar.NativeStill;

/// <summary>
/// Farben der Hintergrund-Effekte: entweder die Palette von „Still“ (samt Albumcover-Färbung) oder – für Effekte,
/// die etwas Echtes zeigen – natürliche Farben (Himmel, Wasser, Nacht). Wetter, Teich und Sternenhimmel stehen
/// standardmäßig auf natürlich; umschalten unter Einstellungen › Hintergrund.
/// </summary>
public static class EffectColors
{
    public readonly record struct Set(Vector3 Base, Vector3 C0, Vector3 C1, Vector3 C2, Vector3 C3);

    /// <summary>Natürliche Farben (dunkles Design); im hellen Design aufgehellt, siehe <see cref="Natural"/>.</summary>
    private static readonly Dictionary<string, Set> NaturalDark = new()
    {
        // Himmel: Tagblau, Wolkengrau, heller Horizont, tiefes Blau
        ["Weather"] = new(new(0.07f, 0.09f, 0.13f), new(0.30f, 0.45f, 0.70f), new(0.55f, 0.60f, 0.68f), new(0.45f, 0.52f, 0.62f), new(0.10f, 0.14f, 0.22f)),
        // Wasser: Türkis, Schilfgrün, Tiefe
        ["Pond"] = new(new(0.02f, 0.06f, 0.07f), new(0.25f, 0.62f, 0.68f), new(0.32f, 0.52f, 0.42f), new(0.15f, 0.35f, 0.40f), new(0.04f, 0.16f, 0.20f)),
        // Nacht: fast schwarz, zarte violette und blaue Nebel
        ["Stars"] = new(new(0.012f, 0.015f, 0.035f), new(0.42f, 0.38f, 0.72f), new(0.20f, 0.30f, 0.60f), new(0.33f, 0.24f, 0.48f), new(0.05f, 0.07f, 0.16f)),
    };

    /// <summary>Effekte, für die es natürliche Farben gibt (zeigen die Wahl „Palette / Natürlich“).</summary>
    public static IEnumerable<string> WithNatural => NaturalDark.Keys;

    public static bool HasNatural(string effect) => NaturalDark.ContainsKey(effect);

    /// <summary>Welche Effekte gerade natürliche Farben nutzen (vom UI-Thread gesetzt, vom Zeichen-Thread gelesen).</summary>
    private static volatile HashSet<string> _natural = new(NaturalDark.Keys);

    /// <summary>Aus den Einstellungen übernehmen (fehlt ein Eintrag, gilt natürlich).</summary>
    public static void Apply(Config c)
    {
        var set = new HashSet<string>();
        foreach (var k in NaturalDark.Keys)
            if (UsesNatural(c, k)) set.Add(k);
        _natural = set;
    }

    public static bool UsesNatural(Config c, string effect) =>
        HasNatural(effect) && !(c.EffectColorSource is { } d && d.TryGetValue(effect, out var v) && v == "Palette");

    /// <summary>Natürliche Farben eines Effekts für das helle oder dunkle Design.</summary>
    public static Set Natural(string effect, bool light)
    {
        var s = NaturalDark[effect];
        if (!light) return s;
        var paper = new Vector3(0.93f, 0.94f, 0.96f);
        return new(Vector3.Lerp(s.Base, paper, 0.85f), Vector3.Lerp(s.C0, Vector3.One, 0.25f), Vector3.Lerp(s.C1, Vector3.One, 0.25f),
                   Vector3.Lerp(s.C2, Vector3.One, 0.25f), Vector3.Lerp(s.C3, paper, 0.5f));
    }

    /// <summary>Die Farben, mit denen <paramref name="effect"/> gerade gezeichnet wird.</summary>
    public static Set For(string effect, StillPalette palette) =>
        _natural.Contains(effect) ? Natural(effect, palette.IsLightTheme)
                                  : new(palette.Base, palette.C0, palette.C1, palette.C2, palette.C3);
}

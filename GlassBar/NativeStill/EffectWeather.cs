namespace GlassBar.NativeStill;

/// <summary>
/// Das aktuelle Wetter für den Hintergrund-Effekt „Wetter“: aus dem WMO-Code des Wetterdienstes wird
/// Art (klar, Wolken, Nebel, Regen, Schnee, Gewitter), Stärke, Bewölkung und Tag/Nacht.
/// Vom UI-Thread gesetzt (beim Wetter-Abruf), vom Zeichen-Thread gelesen.
/// </summary>
public static class EffectWeather
{
    public const float Clear = 0, Clouds = 1, Fog = 2, Rain = 3, Snow = 4, Storm = 5;

    // ohne Wetterdaten (keine Stadt, Wetter aus): leicht bewölkt; Night −1 = unbekannt → nach Uhrzeit
    public static volatile float Kind = Clouds, Amount, Cloud = 0.35f, Night = -1;

    /// <summary>WMO-Code (Open-Meteo) in Art, Stärke (0–1) und Bewölkung (0–1) umrechnen.</summary>
    public static (float Kind, float Amount, float Cloud) FromWmo(int code) => code switch
    {
        0 => (Clear, 0, 0),
        1 => (Clear, 0, 0.2f),
        2 => (Clouds, 0, 0.5f),
        3 => (Clouds, 0, 0.9f),
        45 or 48 => (Fog, 0.8f, 0.7f),
        51 or 56 => (Rain, 0.25f, 0.8f),          // leichter Niesel
        53 or 57 => (Rain, 0.35f, 0.85f),
        55 => (Rain, 0.45f, 0.9f),
        61 or 66 or 80 => (Rain, 0.55f, 0.9f),
        63 or 81 => (Rain, 0.75f, 0.95f),
        65 or 67 or 82 => (Rain, 1f, 1f),
        71 or 85 => (Snow, 0.4f, 0.85f),
        73 => (Snow, 0.65f, 0.9f),
        75 or 77 or 86 => (Snow, 1f, 0.95f),
        95 => (Storm, 0.75f, 1f),
        96 or 99 => (Storm, 1f, 1f),
        _ => (Clouds, 0, 0.5f),
    };

    public static void Set(int wmoCode, bool isDay)
    {
        var (k, a, c) = FromWmo(wmoCode);
        Kind = k; Amount = a; Cloud = c; Night = isDay ? 0 : 1;
    }

    /// <summary>Nacht für den Shader: vom Wetterdienst, sonst nach Uhrzeit (20–6 Uhr).</summary>
    public static float NightNow() => Night >= 0 ? Night : DateTime.Now.Hour is >= 20 or < 6 ? 1 : 0;
}

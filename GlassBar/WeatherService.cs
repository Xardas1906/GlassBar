using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassBar;

/// <summary>
/// Wetter von Open-Meteo (kostenlos, ohne Konto) – dieselbe Quelle wie im Hintergrund „Still“.
/// Die Stadt kommt aus den GlassBar-Einstellungen oder, wenn dort leer, aus Wallpaper Engine.
/// </summary>
public class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Place { get; private set; } = "";
    public double Temperature { get; private set; }
    public double FeelsLike { get; private set; }
    public double High { get; private set; }
    public double Low { get; private set; }
    public int RainChance { get; private set; }
    public int Code { get; private set; }
    public bool IsDay { get; private set; } = true;
    public bool HasData { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public string? Error { get; private set; }

    private string? _geoFor;
    private double _lat, _lon;

    /// <summary>Stadt aus den Eigenschaften des Hintergrunds „Still“ in Wallpaper Engine.</summary>
    public static string WallpaperCity()
    {
        try
        {
            var cfg = @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\config.json";
            if (!File.Exists(cfg)) return "";
            var m = Regex.Match(File.ReadAllText(cfg), "\"prayerplace\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    public static string EffectiveCity() =>
        !string.IsNullOrWhiteSpace(App.Config.WeatherCity) ? App.Config.WeatherCity.Trim() : WallpaperCity();

    public async Task<bool> RefreshAsync()
    {
        var city = EffectiveCity();
        if (string.IsNullOrWhiteSpace(city)) { Error = L.T("Keine Stadt festgelegt"); HasData = false; return false; }
        try
        {
            if (_geoFor != city)
            {
                if (!TryParseCoords(city, out _lat, out _lon))
                {
                    var geo = await Http.GetStringAsync(
                        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language={(L.En ? "en" : "de")}&format=json");
                    using var gd = JsonDocument.Parse(geo);
                    if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0)
                    { Error = L.F("„{0}“ nicht gefunden", city); HasData = false; return false; }
                    var r = res[0];
                    _lat = r.GetProperty("latitude").GetDouble();
                    _lon = r.GetProperty("longitude").GetDouble();
                    Place = r.GetProperty("name").GetString() ?? city;
                }
                else Place = city;
                _geoFor = city;
            }

            var inv = CultureInfo.InvariantCulture;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={_lat.ToString(inv)}&longitude={_lon.ToString(inv)}" +
                      "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=1";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            Temperature = cur.GetProperty("temperature_2m").GetDouble();
            FeelsLike = cur.GetProperty("apparent_temperature").GetDouble();
            Code = cur.GetProperty("weather_code").GetInt32();
            IsDay = cur.GetProperty("is_day").GetInt32() == 1;
            var daily = doc.RootElement.GetProperty("daily");
            High = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            Low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
            RainChance = daily.GetProperty("precipitation_probability_max")[0].ValueKind == JsonValueKind.Number
                ? daily.GetProperty("precipitation_probability_max")[0].GetInt32() : 0;
            UpdatedAt = DateTime.Now;
            HasData = true; Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = L.T("Wetterdienst nicht erreichbar");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private static bool TryParseCoords(string s, out double lat, out double lon)
    {
        lat = lon = 0;
        var parts = s.Split(',');
        return parts.Length == 2 &&
               double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
               double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
    }

    /// <summary>WMO-Wettercode → deutsche Beschreibung.</summary>
    public string Description => L.T(Code switch
    {
        0 => "Klar",
        1 => "Überwiegend klar",
        2 => "Teilweise bewölkt",
        3 => "Bewölkt",
        45 or 48 => "Nebel",
        51 or 53 or 55 => "Nieselregen",
        56 or 57 => "Gefrierender Nieselregen",
        61 => "Leichter Regen",
        63 => "Regen",
        65 => "Starker Regen",
        66 or 67 => "Gefrierender Regen",
        71 => "Leichter Schneefall",
        73 => "Schneefall",
        75 => "Starker Schneefall",
        77 => "Schneegriesel",
        80 or 81 => "Schauer",
        82 => "Starke Schauer",
        85 or 86 => "Schneeschauer",
        95 => "Gewitter",
        96 or 99 => "Gewitter mit Hagel",
        _ => "Wetter",
    });

    /// <summary>Symbol (Segoe UI Symbol).</summary>
    public string Symbol => Code switch
    {
        0 or 1 => IsDay ? "\u2600" : "\u263E",   // ☀ / ☾
        2 => IsDay ? "\u26C5" : "\u2601",        // ⛅ / ☁
        3 => "\u2601",                           // ☁
        45 or 48 => "\u2248",                    // ≈
        >= 51 and <= 67 or 80 or 81 or 82 => "\u2602", // ☂
        >= 71 and <= 77 or 85 or 86 => "\u2744",  // ❄
        >= 95 => "\u26A1",                       // ⚡
        _ => "\u2601",
    };
}

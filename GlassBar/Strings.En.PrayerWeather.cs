namespace GlassBar;

public static partial class Strings
{
    /// <summary>Gebetszeiten (PrayerPill, PrayTimes) und Wetterkarte (WeatherCard).</summary>
    internal static readonly Dictionary<string, string> EnPrayerWeather = new()
    {
        // ---------- Gebete ----------
        ["Fadschr"] = "Fajr",
        ["Dhuhr"] = "Dhuhr",
        ["Asr"] = "Asr",
        ["Maghrib"] = "Maghrib",
        ["Ischa"] = "Isha",
        ["Dschumu’a"] = "Jumu’ah",
        ["Gebetszeiten"] = "Prayer times",

        // Auswahl: wann gebetet
        ["Optimal"] = "Best",
        ["Gut"] = "Good",
        ["Spät"] = "Late",
        ["Nachholen"] = "Make up",

        // Meldungen
        ["{0} Tage in Folge. MashaAllah."] = "{0} days in a row. MashaAllah.",
        ["Alle fünf gebetet. Alhamdulillah."] = "All five prayed. Alhamdulillah.",
        ["{0} hat begonnen"] = "{0} has begun",

        // Ohne Ort
        ["Trage deine Stadt in den GlassBar-Einstellungen ein (oder Breitengrad, Längengrad)."] = "Enter your city in the GlassBar settings (or latitude, longitude).",
        ["Deine Stadt wird gesucht…"] = "Looking up your city…",
        ["Ort nicht gefunden. Versuch „Stadt, Land“ oder Breitengrad, Längengrad."] = "Place not found. Try “City, Country” or latitude, longitude.",
        ["Der Kartendienst ist nicht erreichbar, um deine Stadt zu finden. Gleich neuer Versuch."] = "The map service can’t be reached to find your city. Retrying shortly.",
        ["Die Sonne erreicht hier heute nicht die Gebetswinkel."] = "The sun doesn’t reach the prayer angles here today.",
        ["Stadt festlegen"] = "Set city",
        ["Stadt nicht gefunden"] = "City not found",
        ["Offline"] = "Offline",
        ["Heute keine Zeiten"] = "No times today",

        // Pille und Karte
        ["{0} endet in {1}"] = "{0} ends in {1}",
        ["{0} · noch {1}"] = "{0} · {1} left",
        ["{0} um {1}"] = "{0} at {1}",
        ["bis {0}"] = "until {0}",
        ["um {0}"] = "at {0}",
        ["Heute entschuldigt. Erinnerungen sind pausiert."] = "Excused today. Reminders are paused.",
        ["Endet in {0}. Hake es ab, sobald du gebetet hast."] = "Ends in {0}. Check it off once you’ve prayed.",
        ["Beste Zeit · noch {0}"] = "Best time · {0} left",
        ["Gute Zeit · noch {0}"] = "Good time · {0} left",
        ["Endet bald · noch {0}"] = "Ending soon · {0} left",
        ["noch {0}"] = "{0} left",
        ["Gebetet. {0} um {1}."] = "Prayed. {0} at {1}.",
        ["{0} in {1}"] = "{0} in {1}",

        // Hinweise in den Zeilen
        ["endet in {0}"] = "ends in {0}",
        ["beste zeit"] = "best time",
        ["endet bald"] = "ending soon",
        ["jetzt"] = "now",
        ["beste Zeit"] = "best time",
        ["spät"] = "late",
        ["nachgeholt"] = "made up",
        ["nachzuholen"] = "to make up",
        ["nicht markiert"] = "not marked",

        // Fuß
        ["{0}-Tage-Serie · Rekord {1}"] = "{0}-day streak · best {1}",
        ["{0}-Tage-Serie"] = "{0}-day streak",
        ["Längste Serie 1 Tag"] = "Longest streak 1 day",
        ["Längste Serie {0} Tage"] = "Longest streak {0} days",
        ["Noch keine Serie"] = "No streak yet",
        ["{0} nachzuholen"] = "{0} to make up",
        ["Alles erledigt"] = "All caught up",
        ["Heute entschuldigt"] = "Excused today",

        // Hidschri-Datum
        ["{0}. {1} {2} AH"] = "{0} {1} {2} AH",
        ["Muharram"] = "Muharram",
        ["Safar"] = "Safar",
        ["Rabiʿ I"] = "Rabiʿ I",
        ["Rabiʿ II"] = "Rabiʿ II",
        ["Dschumada I"] = "Jumada I",
        ["Dschumada II"] = "Jumada II",
        ["Radschab"] = "Rajab",
        ["Schaʿban"] = "Shaʿban",
        ["Ramadan"] = "Ramadan",
        ["Schawwal"] = "Shawwal",
        ["Dhu l-qaʿda"] = "Dhu l-Qaʿda",
        ["Dhu l-Hiddscha"] = "Dhu l-Hijja",

        // ---------- Wetter ----------
        ["Klar"] = "Clear",
        ["Überwiegend klar"] = "Mostly clear",
        ["Teilweise bewölkt"] = "Partly cloudy",
        ["Überwiegend bewölkt"] = "Mostly cloudy",
        ["Bewölkt"] = "Cloudy",
        ["Nebel"] = "Fog",
        ["Gefrierender Nebel"] = "Freezing fog",
        ["Leichter Nieselregen"] = "Light drizzle",
        ["Nieselregen"] = "Drizzle",
        ["Starker Nieselregen"] = "Heavy drizzle",
        ["Gefrierender Nieselregen"] = "Freezing drizzle",
        ["Leichter Regen"] = "Light rain",
        ["Regen"] = "Rain",
        ["Starker Regen"] = "Heavy rain",
        ["Gefrierender Regen"] = "Freezing rain",
        ["Leichter Schneefall"] = "Light snow",
        ["Schnee"] = "Snow",
        ["Starker Schneefall"] = "Heavy snow",
        ["Schneegriesel"] = "Snow grains",
        ["Leichte Schauer"] = "Light showers",
        ["Schauer"] = "Showers",
        ["Starke Schauer"] = "Heavy showers",
        ["Schneeschauer"] = "Snow showers",
        ["Starke Schneeschauer"] = "Heavy snow showers",
        ["Gewitter"] = "Thunderstorm",
        ["Gewitter, Hagel"] = "Thunderstorm, hail",

        ["Gefühlt {0}"] = "Feels like {0}",
        ["H {0} · T {1}"] = "H {0} · L {1}",
        ["Sonnenaufgang {0}"] = "Sunrise {0}",
        ["Sonnenuntergang {0}"] = "Sunset {0}",
        ["Sicht {0:0.0} km"] = "Visibility {0:0.0} km",
        ["{0:0} % Regenwahrscheinlichkeit"] = "{0:0}% chance of rain",
        ["Jetzt"] = "Now",
        ["Dein Standort"] = "Your location",
        ["{0} · aktualisiert {1}"] = "{0} · updated {1}",
        ["gerade eben"] = "just now",
        ["vor {0} Min."] = "{0} min ago",
        ["vor {0} Std."] = "{0} hr ago",
    };
}

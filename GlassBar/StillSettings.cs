namespace GlassBar;

/// <summary>Eine Einstellung des Hintergrunds „Still“ (Schlüssel wie in dessen Speicher „still.settings.v2“).</summary>
public record StillSetting(string Tab, string Label, string Key, string Kind,
                           double Min = 0, double Max = 1, double Step = 0.01, string Fmt = "pct",
                           (object Value, string Label)[]? Options = null, string? Note = null, string? Section = null);

/// <summary>Alle Einstellungen von „Still“, nachgebaut für das GlassBar-Einstellungsfenster.</summary>
public static class StillSettings
{
    public static readonly string[] Tabs =
        { "Aussehen", "Schrift", "Uhr", "Wecker", "Wetter", "Musik", "Trinken", "Pomodoro", "Training", "Effekte", "Gebet" };

    private static (object, string)[] O(params (object, string)[] o) => o;
    private static readonly (object, string)[] Tones = O(("glint", "Glint"), ("beacon", "Beacon"), ("dewdrop", "Dewdrop"), ("bamboo", "Bamboo"), ("off", "Aus"));

    public static readonly StillSetting[] All =
    {
        // Aussehen
        // Farben und Bild des Hintergrunds: stehen in GlassBar unter „Hintergrund“ (Tab „Hintergrund“ ist kein Still-Reiter)
        new("Hintergrund", "Palette", "theme", "palette"),
        new("Hintergrund", "Mit Albumcover einfärben", "followAlbum", "toggle", Note: "Nimmt die Farben dessen an, was gerade läuft."),
        new("Hintergrund", "Stärke der Tönung", "tintStrength", "slider"),
        new("Hintergrund", "Körnung", "grain", "slider"),
        new("Hintergrund", "Musikleuchten", "audioGlow", "slider", Note: "Das Licht pulsiert mit dem Bass dessen, was auf diesem PC läuft."),
        new("Aussehen", "Kartenschatten", "cardShadow", "slider", 0, 1.5),
        new("Aussehen", "Farbige Karten", "cardColors", "toggle", Note: "Aus: Die Karten sind weiß und grau. Gebetszeiten behalten ihre Farben."),
        new("Hintergrund", "Qualität", "quality", "choice", Options: O(("low", "Niedrig"), ("balanced", "Ausgewogen"), ("high", "Hoch"))),
        // Schrift
        new("Schrift", "Schriftart", "font", "choice", Options: O(("dmsans", "DM Sans"), ("manrope", "Manrope"), ("poppins", "Poppins"), ("inter", "Inter"),
            ("geist", "Geist"), ("outfit", "Outfit"), ("serif", "Instrument"), ("system", "Segoe"))),
        new("Schrift", "Uhrgröße", "clockScale", "slider", 0.6, 1.4),
        new("Schrift", "Größe der Oberfläche", "uiScale", "slider", 0.7, 1.3),
        // Uhr
        new("Uhr", "Uhr", "showClock", "toggle"),
        new("Uhr", "Format", "clock24", "choice", Options: O((true, "24h"), (false, "12h"))),
        new("Uhr", "Sekunden", "seconds", "choice", Options: O(("off", "Aus"), ("small", "Klein"), ("full", "Voll"))),
        new("Uhr", "Gewicht", "clockWeight", "choice", Options: O(("auto", "Auto"), ("thin", "Dünn"), ("light", "Leicht"), ("regular", "Normal"), ("medium", "Mittel"), ("bold", "Fett"))),
        new("Uhr", "Mit dem Hintergrund verschmelzen", "clockBlend", "slider"),
        new("Uhr", "Datum", "showDate", "toggle"),
        new("Uhr", "Heutige Aufgaben", "showFocus", "toggle", Note: "Die Aufgaben selbst trägst du unter „Hintergrund“ ein."),
        new("Uhr", "Feiern, wenn erledigt", "celebrate", "toggle"),
        new("Uhr", "Erledigt-Klang", "taskSound", "toggle"),
        new("Uhr", "Horizontal", "clockX", "slider", -1, 1, Fmt: "pos", Section: "Position"),
        new("Uhr", "Vertikal", "clockY", "slider", -1, 1, Fmt: "pos"),
        // Wecker
        new("Wecker", "Klicke auf die Uhrzeit für Timer und Wecker", "clockTap", "toggle", Note: "Wecker selbst stellst du direkt auf dem Desktop: Klick auf die Uhrzeit."),
        new("Wecker", "Timer-Klang", "timerSound", "choice", Options: Tones),
        new("Wecker", "Weckton", "alarmSound", "choice", Options: O(("horizon", "Horizon"), ("silk", "Silk"), ("rise", "Aufgang"), ("harbor", "Harbor"), ("off", "Aus"))),
        new("Wecker", "Schlummern", "snoozeMin", "choice", Options: O((5d, "5 min"), (9d, "9 min"), (10d, "10 min"))),
        // Wetter
        new("Wetter", "Wetter", "weather", "toggle", Note: "Die Stadt kommt aus den GlassBar-Einstellungen („Stadt fürs Wetter“)."),
        new("Wetter", "Position", "weatherPos", "choice", Options: O(("beside", "Neben dem Datum"), ("above", "Darüber, mit Ort"))),
        new("Wetter", "Einheiten", "weatherUnit", "choice", Options: O(("auto", "Auto"), ("c", "°C"), ("f", "°F"))),
        new("Wetter", "Licht folgt dem Wetter", "weatherMood", "toggle"),
        // Musik
        new("Musik", "Läuft gerade", "showPill", "toggle"),
        new("Musik", "Stil", "pillStyle", "choice", Options: O(("compact", "Kompakt"), ("expanded", "Aufgeklappt"))),
        new("Musik", "Beim Drüberfahren aufklappen", "hoverExpand", "toggle"),
        new("Musik", "Neue Titel anzeigen", "announce", "toggle"),
        new("Musik", "Klangbalken", "visualizer", "toggle"),
        new("Musik", "Wiedergabetasten", "showControls", "toggle"),
        new("Musik", "Liedtext anzeigen", "lyrics", "choice", Options: O(("stage", "Bildschirm"), ("card", "Karte"), ("off", "Aus")), Section: "Liedtext"),
        new("Musik", "Größe", "lyricsSize", "slider", 0.6, 1.6),
        new("Musik", "Gewicht", "lyricsWeight", "choice", Options: O(("light", "Leicht"), ("regular", "Normal"), ("medium", "Mittel"), ("bold", "Fett"))),
        new("Musik", "Helligkeit", "lyricsBright", "slider", 0.3, 1),
        new("Musik", "Timing-Tasten", "lyricsTiming", "choice", Options: O(("hover", "Auf dem Liedtext"), ("settings", "In den Einstellungen"))),
        // Trinken
        new("Trinken", "Wasser-Pille", "hydrate", "toggle"),
        new("Trinken", "Tagesziel", "waterGoal", "slider", 1000, 5000, 250, "ml"),
        new("Trinken", "Bechergröße", "cupSize", "slider", 100, 1000, 50, "ml"),
        new("Trinken", "Einheiten", "waterUnit", "choice", Options: O(("ml", "Liter"), ("oz", "Unzen"))),
        new("Trinken", "Erinnerungen", "waterRemind", "toggle"),
        new("Trinken", "Erinnern alle", "remindEvery", "slider", 15, 180, 15, "min"),
        new("Trinken", "Aktiv ab", "activeFrom", "slider", 0, 23, 1, "hour"),
        new("Trinken", "Aktiv bis", "activeTo", "slider", 1, 24, 1, "hour"),
        new("Trinken", "Klang", "waterChime", "toggle"),
        // Pomodoro
        new("Pomodoro", "Pomodoro-Pille", "pomo", "toggle"),
        new("Pomodoro", "Fokus", "focusMin", "slider", 5, 90, 5, "min"),
        new("Pomodoro", "Kurze Pause", "shortMin", "slider", 1, 30, 1, "min"),
        new("Pomodoro", "Lange Pause", "longMin", "slider", 5, 60, 5, "min"),
        new("Pomodoro", "Lange Pause alle", "longEvery", "slider", 2, 8, 1, "sessions"),
        new("Pomodoro", "Pausen automatisch starten", "autoBreaks", "toggle"),
        new("Pomodoro", "Fokus automatisch starten", "autoFocus", "toggle"),
        new("Pomodoro", "Tagesziel", "pomoGoal", "slider", 1, 16, 1, "sessions"),
        new("Pomodoro", "Klang", "pomoChime", "toggle"),
        // Training
        new("Training", "Trainings-Pille", "workout", "toggle"),
        new("Training", "Fokus", "workoutFocus", "choice", Options: O(("muscle", "Muskelaufbau"), ("strength", "Kraft")),
            Note: "Muskelaufbau: 8 bis 20 Wiederholungen nah am Muskelversagen. Kraft: 3 bis 8 Wiederholungen, schwerere Varianten."),
        new("Training", "Wochenziel", "workoutGoal", "slider", 2, 6, 1, "days"),
        new("Training", "Pause zwischen den Sätzen", "workoutRest", "slider", 0.5, 2, 0.05),
        new("Training", "Klanghinweise", "workoutChime", "toggle"),
        new("Training", "Ich habe einen stabilen Tisch", "workoutTable", "toggle", Note: "Fügt Tischrudern hinzu. Nutze nur einen Tisch, der nicht kippen kann."),
        new("Training", "BMI auf der Karte zeigen", "workoutBmi", "toggle"),
        // Effekte
        new("Effekte", "Klangwelten-Pille", "sounds", "toggle"),
        new("Effekte", "Effekte auf dem Bildschirm", "soundFx", "toggle"),
        new("Effekte", "Tropfen auf dem Glas", "rainGlass", "toggle"),
        new("Effekte", "Menge", "glassAmount", "slider", 0.25, 2, 0.05),
        new("Effekte", "Effektstärke", "glassLook", "slider", 0, 2, 0.05),
        new("Effekte", "Während Musik läuft", "soundDuck", "choice", Options: O(("quiet", "Leiser"), ("pause", "Pause"), ("keep", "Weiterlaufen"))),
        new("Effekte", "Ab und zu einen Tipp zeigen", "tips", "toggle"),
        // Gebet
        new("Gebet", "Gebetszeiten", "prayer", "toggle"),
        new("Gebet", "Methode", "prayerMethod", "choice", Options: O(("auto", "Auto"), ("mwl", "Islamische Weltliga"), ("isna", "ISNA"), ("egypt", "Ägypten"),
            ("makkah", "Umm al-Qura"), ("karachi", "Karachi"), ("gulf", "Golfregion"), ("kuwait", "Kuwait"), ("qatar", "Katar"), ("singapore", "Singapur"),
            ("turkey", "Diyanet"), ("tehran", "Teheran"), ("jafari", "Schiitisch"), ("france", "Frankreich"), ("russia", "Russland"), ("dubai", "Dubai"))),
        new("Gebet", "Asr", "prayerAsr", "choice", Options: O(("auto", "Auto"), ("standard", "Standard"), ("hanafi", "Hanafi"))),
        new("Gebet", "Ende von Ischa", "prayerIsha", "choice", Options: O(("fajr", "Zu Fadschr"), ("midnight", "Um Mitternacht"))),
        new("Gebet", "Wenn ein Gebet beginnt", "prayerStart", "toggle"),
        new("Gebet", "Bevor es endet", "prayerBefore", "slider", 0, 45, 5, "minOff"),
        new("Gebet", "Klang", "prayerSound", "choice", Options: Tones),
        // Mauszeiger
        // Maus: wirkt auf den Hintergrund, steht darum unter GlassBar › Hintergrund (Abschnitt „Maus“)
        new("Hintergrund", "Licht folgt dem Mauszeiger", "water", "toggle", Section: "Maus"),
        new("Hintergrund", "Drücken", "waterStrength", "slider"),
        new("Hintergrund", "Leuchten", "waterShine", "slider"),
        new("Hintergrund", "Größe", "waterSize", "slider"),
        new("Hintergrund", "Nachleuchten", "waterDecay", "slider"),
        new("Hintergrund", "Aufblühen beim Klick", "waterClick", "toggle"),
        new("Hintergrund", "Maus-Parallaxe", "bgParallax", "toggle", Note: "Bokeh-Lichter und Sternenhimmel weichen der Maus leicht aus."),
    };

    /// <summary>Standardwerte (aus „Still“).</summary>
    public static readonly Dictionary<string, object> Defaults = new()
    {
        ["theme"] = "ink", ["followAlbum"] = true, ["tintStrength"] = 0.8, ["grain"] = 0.15, ["motion"] = 4.0, ["audioGlow"] = 0.75,
        ["bgParallax"] = true, ["quality"] = "high", ["font"] = "manrope", ["clockScale"] = 0.85, ["uiScale"] = 1.0, ["clockWeight"] = "bold",
        ["clockBlend"] = 0.59, ["clockX"] = 0.0, ["clockY"] = -0.12, ["showClock"] = true, ["clock24"] = true, ["seconds"] = "off",
        ["showDate"] = true, ["showFocus"] = true, ["celebrate"] = true, ["taskSound"] = true, ["showPill"] = true, ["pillStyle"] = "compact",
        ["hoverExpand"] = true, ["announce"] = true, ["visualizer"] = true, ["showControls"] = true, ["lyrics"] = "stage", ["lyricsSize"] = 0.74,
        ["lyricsWeight"] = "light", ["lyricsBright"] = 0.56, ["lyricsTiming"] = "hover", ["water"] = true, ["waterStrength"] = 0.5,
        ["waterSize"] = 0.5, ["waterDecay"] = 0.5, ["waterShine"] = 0.27, ["waterClick"] = true, ["hydrate"] = true, ["waterGoal"] = 2000.0,
        ["cupSize"] = 250.0, ["waterUnit"] = "ml", ["waterRemind"] = true, ["remindEvery"] = 60.0, ["activeFrom"] = 8.0, ["activeTo"] = 22.0,
        ["waterChime"] = false, ["pomo"] = true, ["focusMin"] = 25.0, ["shortMin"] = 5.0, ["longMin"] = 15.0, ["longEvery"] = 4.0,
        ["autoBreaks"] = true, ["autoFocus"] = false, ["pomoGoal"] = 8.0, ["pomoChime"] = true, ["prayer"] = false, ["prayerMethod"] = "auto",
        ["prayerAsr"] = "auto", ["prayerIsha"] = "fajr", ["prayerStart"] = true, ["prayerBefore"] = 20.0, ["prayerSound"] = "dewdrop",
        ["weather"] = true, ["weatherPos"] = "beside", ["weatherUnit"] = "auto", ["weatherMood"] = true, ["clockTap"] = true,
        ["timerSound"] = "bamboo", ["alarmSound"] = "horizon", ["snoozeMin"] = 9.0, ["cardShadow"] = 0.8, ["cardColors"] = false,
        ["sounds"] = true, ["soundDuck"] = "quiet", ["soundFx"] = true, ["rainGlass"] = true, ["glassAmount"] = 1.1, ["glassLook"] = 1.1,
        ["tips"] = true, ["workout"] = true, ["workoutFocus"] = "muscle", ["workoutGoal"] = 3.0, ["workoutRest"] = 1.0,
        ["workoutChime"] = true, ["workoutTable"] = false, ["workoutBmi"] = true,
    };

    /// <summary>Paletten: Schlüssel, Name, Farbe für das Farbfeld.</summary>
    public static readonly (string Key, string Label, string Color)[] Palettes =
    {
        ("ink", "Tinte", "#30358a"), ("dusk", "Dämmerung", "#86385a"), ("moss", "Moos", "#1e5b49"),
        ("ember", "Glut", "#8f3c1f"), ("graphite", "Graphit", "#3c3d44"), ("paper", "Papier", "#c3cff3"),
    };

    public static string Format(StillSetting s, double v) => s.Fmt switch
    {
        "pct" => $"{Math.Round(v * 100)} %",
        "pos" => v == 0 ? L.T("Mitte") : $"{Math.Round(Math.Abs(v) * 100)} % {L.T(s.Key == "clockX" ? (v < 0 ? "links" : "rechts") : (v < 0 ? "oben" : "unten"))}",
        "ml" => v >= 1000 ? L.F("{0:0.##} L", v / 1000) : L.F("{0:0} ml", v),
        "min" => L.F("{0:0} min", v),
        "minOff" => v <= 0 ? L.T("Aus") : L.F("{0:0} min", v),
        "hour" => $"{v:00}:00",
        "sessions" => L.F("{0:0} Einheiten", v),
        "days" => L.F("{0:0} Tage", v),
        _ => v.ToString("0.##", L.Culture),
    };

    /// <summary>Angezeigter Name eines Reiters (die Reiter selbst bleiben deutsch, sie dienen als Kennung).</summary>
    public static string TabName(string tab) => L.T(tab);
}

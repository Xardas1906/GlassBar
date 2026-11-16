using System.Globalization;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Gebetszeiten, aus dem Sonnenstand berechnet (ohne Netz) – Übertragung von praytimes.js.
/// Für jeden Tag: Deklination der Sonne und Zeitgleichung, dann der Moment, in dem die Sonne den Winkel des
/// jeweiligen Gebets erreicht. Fadschr und Ischa folgen den Winkeln der gewählten Methode, Asr der Schattenlänge.
/// Dazu das Hidschri-Datum (Umm al-Qura).
/// </summary>
public static class PrayTimes
{
    private const double R = Math.PI / 180;
    private static double Sin(double d) => Math.Sin(d * R);
    private static double Cos(double d) => Math.Cos(d * R);
    private static double Tan(double d) => Math.Tan(d * R);
    private static double Asin(double x) => Math.Asin(x) / R;
    private static double Acos(double x) => Math.Acos(x) / R;
    private static double Atan2(double y, double x) => Math.Atan2(y, x) / R;
    private static double Acot(double x) => Math.Atan(1 / x) / R;
    private static double Fix(double a, double b) => a - b * Math.Floor(a / b);

    /// <summary>Eine Berechnungsmethode. Winkel in Grad unter dem Horizont; IshaMin = Minuten nach Maghrib.</summary>
    public sealed record Method(string Label, double Fajr, double? Isha = null, double? IshaMin = null,
        double? RamadanMin = null, double? Maghrib = null, Tune? Tune = null);

    /// <summary>Feste Verschiebungen einer Methode in Minuten (Diyanet).</summary>
    public sealed record Tune(double Sunrise = 0, double Dhuhr = 0, double Asr = 0, double Maghrib = 0);

    public static readonly IReadOnlyDictionary<string, Method> Methods = new Dictionary<string, Method>
    {
        ["mwl"] = new("Islamische Weltliga", 18, Isha: 17),
        ["karachi"] = new("Karachi", 18, Isha: 18),
        ["isna"] = new("ISNA, Nordamerika", 15, Isha: 15),
        ["egypt"] = new("Ägyptische Behörde", 19.5, Isha: 17.5),
        ["makkah"] = new("Umm al-Qura, Mekka", 18.5, IshaMin: 90, RamadanMin: 120),
        ["gulf"] = new("Golfregion", 19.5, IshaMin: 90),
        ["dubai"] = new("Dubai", 18.2, Isha: 18.2),
        ["kuwait"] = new("Kuwait", 18, Isha: 17.5),
        ["qatar"] = new("Qatar", 18, IshaMin: 90),
        ["singapore"] = new("Singapur, Malaysia, Indonesien", 20, Isha: 18),
        ["turkey"] = new("Diyanet, Türkiye", 18, Isha: 17, Tune: new Tune(Sunrise: -7, Dhuhr: 5, Asr: 4, Maghrib: 7)),
        ["tehran"] = new("Teheran", 17.7, Isha: 14, Maghrib: 4.5),
        ["jafari"] = new("Schiitisch (Ithna-Ashari)", 16, Isha: 14, Maghrib: 4),
        ["france"] = new("Frankreich", 12, Isha: 12),
        ["russia"] = new("Russland", 16, Isha: 15),
    };

    // Was die meisten in einem Land befolgen; alles andere bekommt MWL.
    private static readonly Dictionary<string, string> ByCountry = new()
    {
        ["BD"] = "karachi", ["PK"] = "karachi", ["IN"] = "karachi", ["AF"] = "karachi",
        ["SA"] = "makkah", ["YE"] = "makkah", ["US"] = "isna", ["CA"] = "isna",
        ["EG"] = "egypt", ["SD"] = "egypt", ["LY"] = "egypt", ["SY"] = "egypt", ["LB"] = "egypt", ["IQ"] = "egypt", ["JO"] = "egypt", ["PS"] = "egypt",
        ["AE"] = "dubai", ["KW"] = "kuwait", ["QA"] = "qatar", ["BH"] = "gulf", ["OM"] = "gulf",
        ["SG"] = "singapore", ["MY"] = "singapore", ["ID"] = "singapore", ["BN"] = "singapore",
        ["TR"] = "turkey", ["IR"] = "tehran", ["FR"] = "france", ["RU"] = "russia",
    };
    private static readonly HashSet<string> Hanafi = new() { "BD", "PK", "IN", "AF" };

    // Für Koordinaten ohne Stadt: das Land aus der Zeitzone des PCs raten.
    private static readonly Dictionary<string, string> ByZone = new()
    {
        ["Asia/Dhaka"] = "BD", ["Asia/Karachi"] = "PK", ["Asia/Kolkata"] = "IN", ["Asia/Calcutta"] = "IN", ["Asia/Kabul"] = "AF",
        ["Asia/Riyadh"] = "SA", ["Asia/Aden"] = "YE", ["Africa/Cairo"] = "EG", ["Asia/Dubai"] = "AE", ["Asia/Kuwait"] = "KW",
        ["Asia/Qatar"] = "QA", ["Asia/Bahrain"] = "BH", ["Asia/Muscat"] = "OM", ["Asia/Singapore"] = "SG",
        ["Asia/Kuala_Lumpur"] = "MY", ["Asia/Jakarta"] = "ID", ["Asia/Brunei"] = "BN", ["Europe/Istanbul"] = "TR",
        ["Asia/Tehran"] = "IR", ["Europe/Paris"] = "FR", ["Europe/Moscow"] = "RU", ["Asia/Baghdad"] = "IQ", ["Asia/Amman"] = "JO",
    };

    /// <summary>Land aus der Zeitzone des PCs ("" = unbekannt).</summary>
    public static string ZoneCountry()
    {
        string z = "";
        try
        {
            var id = TimeZoneInfo.Local.Id;
            z = TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
        }
        catch { }
        if (ByZone.TryGetValue(z, out var cc)) return cc;
        if (z.StartsWith("America/", StringComparison.Ordinal))
        {
            foreach (var city in new[] { "Toronto", "Vancouver", "Edmonton", "Winnipeg", "Halifax", "Regina" })
                if (z.Contains(city, StringComparison.Ordinal)) return "CA";
            return "US";
        }
        return "";
    }

    /// <summary>Voreinstellung für ein Land: Methode und Asr-Schule ("standard"/"hanafi").</summary>
    public static (string Method, string Asr) Defaults(string? cc)
    {
        cc ??= "";
        return (ByCountry.TryGetValue(cc, out var m) ? m : "mwl", Hanafi.Contains(cc) ? "hanafi" : "standard");
    }

    private static double Julian(int y, int m, int d)
    {
        if (m <= 2) { y -= 1; m += 12; }
        double a = Math.Floor(y / 100.0), b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
    }

    // Deklination der Sonne und Zeitgleichung (Stunden) für ein julianisches Datum.
    private static (double Decl, double Eqt) Sun(double jd)
    {
        double dd = jd - 2451545.0;
        double g = Fix(357.529 + 0.98560028 * dd, 360);
        double q = Fix(280.459 + 0.98564736 * dd, 360);
        double l = Fix(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g), 360);
        double e = 23.439 - 0.00000036 * dd;
        double ra = Fix(Atan2(Cos(e) * Sin(l), Cos(l)) / 15, 24);
        return (Asin(Sin(e) * Sin(l)), q / 15 - ra);
    }

    /// <summary>Optionen für <see cref="Times"/>; Adjust* in Minuten.</summary>
    public struct Options
    {
        public string Method;
        public string Asr;   // "standard" | "hanafi"
        public bool Ramadan;
        public double AdjFajr, AdjDhuhr, AdjAsr, AdjMaghrib, AdjIsha;
    }

    /// <summary>Zeiten eines Tages als Unix-Millisekunden (UTC). NaN, wo die Sonne einen Winkel nie erreicht.</summary>
    public struct Day
    {
        public double Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha, Sunset;
    }

    /// <summary>Zeiten für einen Kalendertag (y, m, d) an einem Ort.</summary>
    public static Day Times(int y, int m, int d, double lat, double lng, in Options opt)
    {
        var mt = Methods.TryGetValue(opt.Method ?? "", out var found) ? found : Methods["mwl"];
        double jd = Julian(y, m, d) - lng / (15 * 24);

        double Noon(double t) => Fix(12 - Sun(jd + t / 24).Eqt, 24);
        double AtAngle(double angle, double t, bool before)
        {
            var s = Sun(jd + t / 24);
            double x = (-Sin(angle) - Sin(s.Decl) * Sin(lat)) / (Cos(s.Decl) * Cos(lat));
            double span = Acos(x) / 15;
            return Noon(t) + (before ? -span : span);
        }
        double AsrAt(double factor, double t)
        {
            var s = Sun(jd + t / 24);
            return AtAngle(-Acot(factor + Tan(Math.Abs(lat - s.Decl))), t, false);
        }

        const double rise = 0.833;   // Brechung plus halbe Sonnenscheibe
        double factor = opt.Asr == "hanafi" ? 2 : 1;

        // Die Sonne wird an groben Schätzungen jeder Zeit gelesen; ein Durchgang ist das, was die
        // veröffentlichten Tabellen verwenden.
        double fajr = AtAngle(mt.Fajr, 5, true);
        double sunrise = AtAngle(rise, 6, true);
        double dhuhr = Noon(12);
        double asr = AsrAt(factor, 13);
        double sunset = AtAngle(rise, 18, false);
        double maghrib = mt.Maghrib is { } mg ? AtAngle(mg, 18, false) : sunset;
        double isha = mt.Isha is { } ia ? AtAngle(ia, 18, false)
            : maghrib + ((opt.Ramadan && mt.RamadanMin is { } rm ? rm : mt.IshaMin ?? 90) / 60);

        // Hohe Breiten: Fadschr und Ischa bleiben innerhalb eines Anteils der Nacht, den ihr Winkel bestimmt.
        double night = Fix(sunrise - sunset, 24);
        double Cap(double time, double b, double angle, bool before)
        {
            double portion = angle / 60 * night;
            double gap = before ? Fix(b - time, 24) : Fix(time - b, 24);
            return double.IsNaN(time) || gap > portion ? b + (before ? -portion : portion) : time;
        }
        fajr = Cap(fajr, sunrise, mt.Fajr, true);
        if (mt.Isha is { } ic) isha = Cap(isha, sunset, ic, false);

        var tune = mt.Tune ?? new Tune();
        double baseMs = new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        double Out(double h, double minutes)
        {
            double v = h - lng / 15 + minutes / 60;
            return Math.Round((baseMs + v * 3600000) / 60000) * 60000;
        }
        return new Day
        {
            Fajr = Out(fajr, opt.AdjFajr),
            Sunrise = Out(sunrise, tune.Sunrise),
            Dhuhr = Out(dhuhr, tune.Dhuhr + opt.AdjDhuhr),
            Asr = Out(asr, tune.Asr + opt.AdjAsr),
            Maghrib = Out(maghrib, tune.Maghrib + opt.AdjMaghrib),
            Isha = Out(isha, opt.AdjIsha),
            Sunset = Out(sunset, 0),
        };
    }

    // ---------- Hidschri-Datum ----------

    private static readonly UmAlQuraCalendar UmAlQura = new();
    private static readonly string[] HijriMonths =
    {
        "Muharram", "Safar", "Rabiʿ I", "Rabiʿ II", "Dschumada I", "Dschumada II",
        "Radschab", "Schaʿban", "Ramadan", "Schawwal", "Dhu l-qaʿda", "Dhu l-Hiddscha",
    };

    /// <summary>Hidschri-Datum (Umm al-Qura), um adjustDays für die örtliche Mondsichtung verschoben; "" außerhalb des Kalenders.</summary>
    public static string Hijri(DateTime local, double adjustDays)
    {
        try
        {
            var d = local.AddDays(adjustDays);
            int day = UmAlQura.GetDayOfMonth(d), month = UmAlQura.GetMonth(d), year = UmAlQura.GetYear(d);
            return $"{day}. {HijriMonths[month - 1]} {year} AH";
        }
        catch { return ""; }
    }

    /// <summary>Ist an diesem Tag Ramadan (Umm al-Qura, mit Verschiebung)?</summary>
    public static bool IsRamadan(DateTime local, double adjustDays)
    {
        try { return UmAlQura.GetMonth(local.AddDays(adjustDays)) == 9; }
        catch { return false; }
    }
}

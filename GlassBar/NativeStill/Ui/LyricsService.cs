using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GlassBar.NativeStill.Ui;

/// <summary>Stand des Liedtexts (wie lyricsStatus im Original, dazu „nur ungetaktet“).</summary>
public enum LyricsStatus
{
    /// <summary>Kein Lied oder Liedtext aus („idle“).</summary>
    Idle,
    /// <summary>Wird gesucht („loading“).</summary>
    Loading,
    /// <summary>Getakteter Liedtext gefunden („found“).</summary>
    Synced,
    /// <summary>Nur ungetakteter Text vorhanden – wird wie im Original nicht gezeigt („missing“).</summary>
    Plain,
    /// <summary>Nichts gefunden („missing“).</summary>
    None,
    /// <summary>Nicht erreichbar („unreachable“).</summary>
    Error,
}

/// <summary>Eine getaktete Zeile: Zeit in Sekunden, Text ("" = instrumentale Lücke).</summary>
public readonly record struct LyricLine(double T, string Text);

/// <summary>
/// Liedtexte von LRCLIB (lrclib.net), der freien, offenen Datenbank, die auch das Original nutzt. Jedes Lied wird
/// einmal gesucht und gemerkt: im Speicher und unter %AppData%\GlassBar\Lyrics. Alles läuft im Hintergrund; der
/// Render-Thread liest nur fertige Ergebnisse (<see cref="Lines"/>, <see cref="Status"/>, <see cref="Version"/>).
/// Fehlschläge werden täglich neu gefragt, bei Serverproblemen stündlich; offline wird gar nicht gemerkt.
/// </summary>
public sealed class LyricsService
{
    private const string Api = "https://lrclib.net/api";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(7);

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, CacheEntry> Memory = new();
    private static readonly object MemoryLock = new();
    private static string Folder => Path.Combine(Config.Folder, "Lyrics");

    private int _token;
    private volatile Result _result = new(Array.Empty<LyricLine>(), LyricsStatus.Idle, "");

    /// <summary>Schlüssel des Lieds, zu dem das Ergebnis gehört („künstler|titel“, klein).</summary>
    public string Key => _result.Key;
    public IReadOnlyList<LyricLine> Lines => _result.Lines;
    public LyricsStatus Status => _result.Status;
    /// <summary>Letzte Fehlermeldung (wie lyricsError).</summary>
    public string LastError { get; private set; } = "";
    /// <summary>Erhöht sich bei jedem neuen Ergebnis.</summary>
    public int Version => _result.Version;

    /// <summary>Stand mit den Namen des Originals: idle, loading, found, missing, unreachable.</summary>
    public string StatusCode => Status switch
    {
        LyricsStatus.Loading => "loading",
        LyricsStatus.Synced => "found",
        LyricsStatus.Plain or LyricsStatus.None => "missing",
        LyricsStatus.Error => "unreachable",
        _ => "idle",
    };

    /// <summary>Schlüssel wie im Original: `${artist}|${title}` in Kleinbuchstaben.</summary>
    public static string MakeKey(MediaState m) => $"{m.Artist}|{m.Title}".ToLowerInvariant();

    /// <summary>
    /// Liedtext zum Lied suchen (aus dem Render-Thread aufrufbar, kehrt sofort zurück). <paramref name="latest"/>
    /// liefert den jeweils neuesten Musikzustand: nach kurzem Warten meldet der Player meist auch die Dauer,
    /// die den Treffer genau macht.
    /// </summary>
    public void Load(MediaState media, bool enabled, Func<MediaState> latest)
    {
        string key = MakeKey(media);
        int token = Interlocked.Increment(ref _token);
        if (!enabled || media.Title.Length == 0)
        {
            Publish(token, key, Array.Empty<LyricLine>(), LyricsStatus.Idle);
            return;
        }
        Publish(token, key, Array.Empty<LyricLine>(), LyricsStatus.Loading);
        _ = Task.Run(() => LoadAsync(token, key, latest));
    }

    /// <summary>Vergessen (Lied weg): Stand „idle“.</summary>
    public void Clear() => Publish(Interlocked.Increment(ref _token), "", Array.Empty<LyricLine>(), LyricsStatus.Idle);

    private async Task LoadAsync(int token, string key, Func<MediaState> latest)
    {
        try
        {
            var cached = CacheRead(key);
            if (cached == null)
            {
                // Dem Player einen Moment für die Dauer geben – sie macht den Treffer genau
                await Task.Delay(700).ConfigureAwait(false);
                if (token != _token) return;
                var m = latest();
                if (MakeKey(m) != key) return;
                Found found;
                try
                {
                    found = await FindAsync(m).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Offline oder gesperrt: so sagen und nicht merken, damit das nächste Abspielen neu fragt
                    LastError = ex is TaskCanceledException ? "Zeitüberschreitung" : ex.Message;
                    if (token == _token) Publish(token, key, Array.Empty<LyricLine>(), LyricsStatus.Error);
                    return;
                }
                cached = new CacheEntry { At = Now(), Lrc = found.Lrc, Soft = found.Soft && found.Lrc == null, Plain = found.Plain };
                CacheWrite(key, cached);
                if (token != _token) return;
            }
            var lines = cached.Lrc != null ? ParseLrc(cached.Lrc) : Array.Empty<LyricLine>();
            var status = lines.Length > 0 ? LyricsStatus.Synced : cached.Plain ? LyricsStatus.Plain : LyricsStatus.None;
            Publish(token, key, lines, status);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Publish(token, key, Array.Empty<LyricLine>(), LyricsStatus.Error);
        }
    }

    private void Publish(int token, string key, LyricLine[] lines, LyricsStatus status)
    {
        if (token != Volatile.Read(ref _token)) return;
        _result = new Result(lines, status, key) { Version = _result.Version + 1 };
    }

    // ---------- LRCLIB ----------

    private readonly record struct Found(string? Lrc, bool Plain, bool Soft);

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = RequestTimeout,
        };
        // Wie im Original meldet sich der Hintergrund mit Namen (LRCLIB bittet darum)
        h.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client", "Still-Hintergrund");
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Still-Hintergrund (GlassBar)");
        return h;
    }

    /// <summary>Anfrage an LRCLIB. 404 = nichts; 5xx = erreichbar, aber nichts (nur kurz gemerkt).</summary>
    private static async Task<(JsonNode? Json, bool Soft)> RequestAsync(string path, IEnumerable<(string, string)> query)
    {
        var sb = new StringBuilder(Api).Append('/').Append(path).Append('?');
        bool first = true;
        foreach (var (k, v) in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        using var r = await Http.GetAsync(sb.ToString()).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return (null, false);
        if ((int)r.StatusCode >= 500) return (null, true);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException(((int)r.StatusCode).ToString(CultureInfo.InvariantCulture));
        var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (JsonNode.Parse(text), false);
    }

    private static async Task<Found> FindAsync(MediaState m)
    {
        double dur = m.Duration;
        bool soft = false, plain = false;
        if (dur > 0 && m.Album.Length > 0)
        {
            var (exact, s) = await RequestAsync("get", new[]
            {
                ("track_name", m.Title), ("artist_name", m.Artist), ("album_name", m.Album),
                ("duration", Math.Round(dur).ToString(CultureInfo.InvariantCulture)),
            }).ConfigureAwait(false);
            soft |= s;
            if (exact is JsonObject o)
            {
                if (Bool(o, "instrumental")) return new Found(null, false, soft);
                var lrc = Str(o, "syncedLyrics");
                if (!string.IsNullOrWhiteSpace(lrc)) return new Found(lrc, false, soft);
                plain |= !string.IsNullOrWhiteSpace(Str(o, "plainLyrics"));
            }
        }

        var tries = new List<(string Track, string Artist)> { (CleanTitle(m.Title), CleanArtist(m.Artist)) };
        var dash = DashRx.Match(CleanTitle(m.Title));   // „Künstler - Lied“ in Videotiteln
        if (dash.Success) tries.Add((CleanTitle(dash.Groups[2].Value), CleanArtist(dash.Groups[1].Value)));
        foreach (var (track, artist) in tries)
        {
            if (track.Length == 0) continue;
            var (list, s) = await RequestAsync("search", artist.Length > 0
                ? new[] { ("track_name", track), ("artist_name", artist) }
                : new[] { ("q", track) }).ConfigureAwait(false);
            soft |= s;
            if (list is not JsonArray arr) continue;
            var lrc = PickSynced(arr, dur, ref plain);
            if (lrc != null) return new Found(lrc, false, soft);
        }
        return new Found(null, plain, soft);
    }

    /// <summary>Nächster getakteter Treffer nach Dauer; eine weit abweichende Dauer ist eine andere Fassung.</summary>
    private static string? PickSynced(JsonArray list, double dur, ref bool plain)
    {
        string? best = null;
        double bestD = double.MaxValue;
        foreach (var n in list)
        {
            if (n is not JsonObject o || Bool(o, "instrumental")) continue;
            var lrc = Str(o, "syncedLyrics");
            if (string.IsNullOrWhiteSpace(lrc))
            {
                plain |= !string.IsNullOrWhiteSpace(Str(o, "plainLyrics"));
                continue;
            }
            double d = dur > 0 ? Math.Abs(Num(o, "duration") - dur) : 0;
            if (best == null || d < bestD) { best = lrc; bestD = d; }
            if (dur <= 0) break;   // ohne Dauer: der erste Treffer
        }
        if (best != null && dur > 0 && bestD > 6) return null;
        return best;
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static double Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    // Player (und besonders Videoseiten) schmücken Titel aus; LRCLIB möchte das Lied.
    private static readonly Regex DecorRx = new(@"\s*[([](official|lyrics?|audio|video|visuali[sz]er|hd|hq|4k|explicit|clean|remaster(ed)?|live)\b[^)\]]*[)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeatRx = new(@"\s*[([]?\s*\b(feat\.?|ft\.?|featuring)\s[^)\]]*[)\]]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemasterRx = new(@"\s+-\s+(\d{4}\s+)?remaster(ed)?.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TopicRx = new(@"\s*-\s*Topic$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VevoRx = new(@"VEVO$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArtistSplitRx = new(@"\s*(?:,|&|\bx\b|\bfeat\.?|\bft\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DashRx = new(@"^(.+?)\s+[-–—]\s+(.+)$", RegexOptions.Compiled);

    private static string CleanTitle(string t) =>
        RemasterRx.Replace(FeatRx.Replace(DecorRx.Replace(t, ""), ""), "").Trim();

    private static string CleanArtist(string a) =>
        ArtistSplitRx.Split(VevoRx.Replace(TopicRx.Replace(a, ""), ""))[0].Trim();

    // ---------- LRC ----------

    private static readonly Regex OffsetRx = new(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StampRx = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>Getaktetes LRC lesen (wie parseLRC): mehrere Zeitmarken je Zeile, [offset:] in ms.</summary>
    public static LyricLine[] ParseLrc(string text)
    {
        double offset = 0;
        var outList = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var off = OffsetRx.Match(line);
            if (off.Success)
            {
                offset = double.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture) / 1000;
                continue;
            }
            var stamps = StampRx.Matches(line);
            if (stamps.Count == 0) continue;
            var words = TagRx.Replace(line, "").Trim();
            foreach (Match s in stamps)
            {
                double min = double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.TryParse(s.Groups[2].Value.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                outList.Add(new LyricLine(min * 60 + sec - offset, words));
            }
        }
        // Stabil sortieren (wie Array.prototype.sort)
        return outList.Select((l, i) => (l, i)).OrderBy(x => x.l.T).ThenBy(x => x.i).Select(x => x.l).ToArray();
    }

    // ---------- Merken ----------

    private sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public long At { get; set; }
        public string? Lrc { get; set; }
        public bool Soft { get; set; }
        public bool Plain { get; set; }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string FileFor(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Folder, Convert.ToHexString(hash).ToLowerInvariant() + ".json");
    }

    /// <summary>Gemerkter Eintrag oder null (unbekannt bzw. ein Fehlschlag, der neu gefragt werden soll).</summary>
    private static CacheEntry? CacheRead(string key)
    {
        CacheEntry? e;
        lock (MemoryLock) Memory.TryGetValue(key, out e);
        if (e == null)
        {
            try
            {
                var path = FileFor(key);
                if (File.Exists(path)) e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
                if (e != null && e.Key != key) e = null;   // (theoretisch) andere Kennung
            }
            catch { e = null; }
            if (e != null) lock (MemoryLock) Memory[key] = e;
        }
        if (e == null) return null;
        // Fehlschläge täglich neu fragen, stündlich, wenn der Server Probleme hatte
        if (e.Lrc == null && Now() - e.At > (e.Soft ? 3_600_000 : 86_400_000)) return null;
        return e;
    }

    private static void CacheWrite(string key, CacheEntry e)
    {
        e.Key = key;
        lock (MemoryLock) Memory[key] = e;
        try
        {
            Directory.CreateDirectory(Folder);
            var path = FileFor(key);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(e));
            File.Move(path + ".tmp", path, true);
            Prune();
        }
        catch { /* voll oder nicht verfügbar: dann eben nicht merken */ }
    }

    /// <summary>Höchstens 500 Lieder auf der Platte; die ältesten gehen.</summary>
    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.json");
        if (files.Length <= 500) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - 500))
        {
            try { f.Delete(); } catch { }
        }
    }

    private sealed record Result(LyricLine[] Lines, LyricsStatus Status, string Key)
    {
        public int Version { get; init; }
    }
}

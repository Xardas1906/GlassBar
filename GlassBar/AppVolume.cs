using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace GlassBar;

/// <summary>
/// Lautstärke des Programms, das gerade Musik abspielt (Spotify, Browser mit YouTube …) statt der Systemlautstärke –
/// wie im Lautstärkemixer von Windows. Gefunden wird die Audio-Sitzung über die App-Kennung, die Windows für die
/// Medienwiedergabe meldet (SourceAppUserModelId).
/// </summary>
public static class AppVolume
{
    /// <summary>
    /// Lautstärke ändern (<paramref name="delta"/> z. B. +0,04). Gibt die neue Lautstärke (0–1) zurück,
    /// oder null, wenn keine passende Audio-Sitzung läuft (dann bleibt alles unverändert).
    /// </summary>
    public static float? Change(string sourceAumid, float delta) => WithSessions(sourceAumid, "Lautstärke des Programms", matches =>
    {
        // Ein Browser hat oft mehrere Sitzungen: alle auf denselben Wert, ausgehend von der lautesten
        float current = matches.Max(m => m.SimpleAudioVolume.Volume);
        float next = Math.Clamp(MathF.Round((current + delta) * 50) / 50, 0f, 1f); // in 2-%-Schritten
        foreach (var m in matches)
        {
            m.SimpleAudioVolume.Volume = next;
            if (next > 0 && m.SimpleAudioVolume.Mute) m.SimpleAudioVolume.Mute = false;
        }
        return (float?)next;
    });

    /// <summary>
    /// Programm stumm schalten bzw. wieder laut. Gibt (stumm, Lautstärke) zurück, oder null ohne passende Audio-Sitzung.
    /// </summary>
    public static (bool Muted, float Volume)? ToggleMute(string sourceAumid) => WithSessions(sourceAumid, "Programm stumm schalten", matches =>
    {
        bool mute = !matches.Any(m => m.SimpleAudioVolume.Mute); // eine Sitzung stumm → alle wieder laut
        foreach (var m in matches) m.SimpleAudioVolume.Mute = mute;
        return ((bool, float)?)(mute, matches.Max(m => m.SimpleAudioVolume.Volume));
    });

    /// <summary>Alle Audio-Sitzungen der Medienquelle suchen und <paramref name="act"/> darauf anwenden (null = keine gefunden).</summary>
    private static T? WithSessions<T>(string sourceAumid, string what, Func<List<AudioSessionControl>, T?> act)
    {
        if (string.IsNullOrWhiteSpace(sourceAumid)) return default;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;

            var matches = new List<AudioSessionControl>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                uint pid = s.GetProcessID;
                if (pid == 0) continue;
                var (name, aumid, family) = Identify(pid);
                if (name != null && Matches(sourceAumid, name, aumid, family)) matches.Add(s);
            }
            return matches.Count == 0 ? default : act(matches);
        }
        catch (Exception ex)
        {
            Log.Error(what, ex);
            return default;
        }
    }

    /// <summary>
    /// Gehört ein Prozess zur Medienquelle? Store-Apps über ihre App-Kennung bzw. Paketfamilie,
    /// Desktop-Programme über den Programmnamen („Spotify.exe“, „Chrome“, „MSEdge“ …).
    /// </summary>
    internal static bool Matches(string sourceAumid, string processName, string? processAumid, string? processFamily)
    {
        string src = sourceAumid.Trim();
        if (processAumid != null && string.Equals(src, processAumid, StringComparison.OrdinalIgnoreCase)) return true;
        int bang = src.IndexOf('!');
        if (bang > 0 && processFamily != null && string.Equals(src[..bang], processFamily, StringComparison.OrdinalIgnoreCase)) return true;

        string proc = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        string key = bang > 0 ? src[(bang + 1)..] : src;
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
        // Pfad als Kennung (manche Programme melden C:\…\app.exe)
        if (key.Contains('\\')) key = Path.GetFileName(key);
        if (string.Equals(key, proc, StringComparison.OrdinalIgnoreCase)) return true;

        // Bekannte Browser melden eigene Kennungen
        foreach (var (id, exe) in KnownApps)
            if (src.Contains(id, StringComparison.OrdinalIgnoreCase) && string.Equals(proc, exe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly (string Id, string Exe)[] KnownApps =
    {
        ("MSEdge", "msedge"), ("Chrome", "chrome"), ("308046B0AF4A39CB", "firefox"), ("Firefox", "firefox"),
        ("Brave", "brave"), ("Opera", "opera"), ("Vivaldi", "vivaldi"),
    };

    // ---------- Prozess erkennen ----------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    private static (string? Name, string? Aumid, string? Family) Identify(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return default;
        try
        {
            string? name = null, aumid = null, family = null;
            int size = 1024;
            var sb = new StringBuilder(size);
            if (QueryFullProcessImageName(h, 0, sb, ref size)) name = Path.GetFileName(sb.ToString());
            uint len = 256;
            sb = new StringBuilder((int)len);
            if (GetApplicationUserModelId(h, ref len, sb) == 0) aumid = sb.ToString();
            len = 256;
            sb = new StringBuilder((int)len);
            if (GetPackageFamilyName(h, ref len, sb) == 0) family = sb.ToString();
            return (name, aumid, family);
        }
        finally { CloseHandle(h); }
    }
}

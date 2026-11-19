using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedShell;

namespace GlassBar;

/// <summary>
/// Holt Infobereich-Symbole nach, die GlassBar beim Start entgangen sind.
///
/// Programme, die vor GlassBar starten (Autostart), melden ihr Symbol bei der Windows-Taskleiste an. GlassBar schickt
/// beim Übernehmen des Infobereichs zwar „TaskbarCreated“ an alle Fenster, damit sie ihr Symbol neu anmelden – doch
/// landet die Anmeldung bei dem Taskleisten-Fenster, das in diesem Moment vorne liegt, und das ist in den ersten
/// Sekunden manchmal noch das von Windows. Die Symbole fehlen dann in GlassBar.
///
/// Windows merkt sich unter HKCU\Control Panel\NotifyIconSettings, welche Programme ein Symbol haben. Läuft so ein
/// Programm, hat aber kein Symbol bei GlassBar, bekommt nur es „TaskbarCreated“ noch einmal – gezielt an seine
/// Fenster, nicht an alle (manche Programme zeigen bei dieser Nachricht ihr Fenster).
/// </summary>
internal static class TrayRescue
{
    private static readonly int[] DelaysSeconds = { 3, 8, 20, 60 };
    private static DispatcherTimer? _timer;
    private static int _round;
    private static ShellManager? _shell;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public static void Start(ShellManager shell)
    {
        _shell = shell;
        _round = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DelaysSeconds[0]) };
        _timer.Tick += (_, _) =>
        {
            try { Rescue(); }
            catch (Exception ex) { Log.Error("Infobereich nachlesen", ex); }
            _round++;
            if (_round >= DelaysSeconds.Length) { _timer!.Stop(); return; }
            _timer!.Interval = TimeSpan.FromSeconds(DelaysSeconds[_round] - DelaysSeconds[_round - 1]);
        };
        _timer.Start();
    }

    /// <summary>Beim Beenden: nichts mehr nachholen.</summary>
    public static void Stop() => _timer?.Stop();

    private static void Rescue()
    {
        if (_shell?.NotificationArea is not { } area) return;

        // Erst, wenn Anmeldungen bei uns ankommen: das vorderste „Shell_TrayWnd“ muss unseres sein
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        GetWindowThreadProcessId(tray, out uint trayPid);
        if (tray == IntPtr.Zero || trayPid != Environment.ProcessId) return;

        // Programme (Dateinamen), die ein Symbol bei uns haben – über das Fenster des Symbols -> Prozess -> Datei.
        // Dateiname statt Pfad: Programme wie Discord wechseln bei Updates den Versionsordner.
        var haveExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in area.TrayIcons.ToList())
        {
            if (icon.HWnd == IntPtr.Zero || GetWindowThreadProcessId(icon.HWnd, out uint p) == 0) continue;
            try { using var proc = Process.GetProcessById((int)p); if (proc.MainModule?.FileName is { } exe) haveExes.Add(Path.GetFileName(exe)); }
            catch { }
        }

        // Programme, die laut Windows ein Symbol haben, laufen, aber keines bei uns zeigen.
        // Ein Programm wie Chrome oder Discord hat viele Prozesse derselben Datei: zählt als eines.
        var known = KnownTrayExes();
        if (known.Count == 0) return;
        var missing = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == Environment.ProcessId) { proc.Dispose(); continue; }
                string? name = Path.GetFileName(proc.MainModule?.FileName);
                if (name == null || haveExes.Contains(name) || !known.Contains(name)) { proc.Dispose(); continue; }
                if (!missing.TryGetValue(name, out var list)) missing[name] = list = new();
                list.Add(proc);
            }
            catch { proc.Dispose(); } // fremde/geschützte Prozesse: kein Zugriff auf den Pfad
        }
        if (missing.Count == 0) return;

        uint msg = RegisterWindowMessage("TaskbarCreated");
        if (msg == 0) return;
        var procs = missing.Values.SelectMany(l => l).ToList();
        var pids = procs.Select(p => (uint)p.Id).ToHashSet();
        int sent = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (pids.Contains(p) && SendNotifyMessage(h, msg, IntPtr.Zero, IntPtr.Zero)) sent++;
            return true;
        }, IntPtr.Zero);
        // auch reine Nachrichtenfenster (HWND_MESSAGE), die manche Programme fürs Symbol benutzen
        for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(HWND_MESSAGE, h, null, null)) != IntPtr.Zero;)
        {
            GetWindowThreadProcessId(h, out uint p);
            if (pids.Contains(p) && SendNotifyMessage(h, msg, IntPtr.Zero, IntPtr.Zero)) sent++;
        }
        // Darunter sind auch Programme, die gerade gar kein Symbol zeigen (Windows merkt sich jedes jemals gezeigte):
        // die ignorieren die Nachricht einfach.
        TaskbarWindow.Debug($"Infobereich: ohne Symbol: {string.Join(", ", missing.Keys)} – TaskbarCreated an {sent} Fenster geschickt");
        foreach (var p in procs) p.Dispose();
    }

    /// <summary>Dateinamen der Programme aus HKCU\Control Panel\NotifyIconSettings (Windows 11 merkt sich dort jedes Symbol).</summary>
    private static HashSet<string> KnownTrayExes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return set;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("ExecutablePath") is string exe && exe.Length > 0) set.Add(Path.GetFileName(exe));
            }
            set.Remove("explorer.exe"); // Windows selbst (Lautstärke, Netzwerk …) läuft immer und zeigt nicht immer alles
        }
        catch (Exception ex) { Log.Error("NotifyIconSettings lesen", ex); }
        return set;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassBar;

/// <summary>
/// Kleiner zweiter Prozess („GlassBar.exe --watchdog …“), der aufpasst, ob GlassBar unerwartet endet.
///
/// Beendet sich GlassBar normal, meldet es das über ein Ereignis, und der Wächter geht still mit.
/// Endet GlassBar ohne diese Meldung (Absturz, Task-Manager), holt der Wächter die Windows-Taskleiste zurück.
/// Nach einem Absturz startet er GlassBar zusätzlich neu – aber nicht, wenn GlassBar im Task-Manager beendet wurde
/// oder schon in der ersten Minute wieder abgestürzt ist (sonst entstünde eine Absturzschleife).
/// </summary>
internal static class Watchdog
{
    /// <summary>Exit-Code, mit dem GlassBar um einen Neustart bittet (zu viele Fehler hintereinander).</summary>
    public const int RestartExitCode = 0x4742;

    private const string CleanExitPrefix = "GlassBar_CleanExit_";
    private static EventWaitHandle? _cleanExit;

    // ---------- im Hauptprozess ----------

    /// <summary>Wächter starten (vor dem Ausblenden der Taskleiste aufrufen, damit ihr Zustand noch stimmt).</summary>
    public static void Start(bool afterCrash)
    {
        if (Debugger.IsAttached) return; // beim Entwickeln stört ein Neustart nach „Debuggen beenden“
        try
        {
            int pid = Environment.ProcessId;
            _cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanExitPrefix + pid);
            int state = GetTaskbarState();
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Kein Programmpfad");
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {pid} {state} {(afterCrash ? 1 : 0)}") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("Wächter starten", ex); }
    }

    /// <summary>GlassBar endet geordnet – der Wächter soll nichts tun.</summary>
    public static void SignalCleanExit()
    {
        try { _cleanExit?.Set(); } catch { }
    }

    // ---------- im Wächter-Prozess ----------

    /// <summary>Läuft im Wächter, bis GlassBar endet; <paramref name="showMessage"/> zeigt bei Bedarf einen Hinweis.</summary>
    public static void Run(string[] args, Action<string> showMessage)
    {
        int i = Array.FindIndex(args, a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 3 >= args.Length || !int.TryParse(args[i + 1], out int pid) || !int.TryParse(args[i + 2], out int state)) return;
        bool parentWasCrashRestart = args[i + 3] == "1";

        // Solange der Wächter läuft, ist GlassBar.exe belegt: Installer und Updater warten auf diesen Mutex
        using var alive = new Mutex(false, "GlassBar_Watchdog");

        // so wenig Platz wie möglich belegen
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.Idle;
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch { }

        Process parent;
        EventWaitHandle cleanExit;
        try
        {
            parent = Process.GetProcessById(pid);
            cleanExit = EventWaitHandle.OpenExisting(CleanExitPrefix + pid);
        }
        catch { return; } // GlassBar läuft schon nicht mehr

        DateTime started;
        try { started = parent.StartTime; } catch { started = DateTime.Now; }
        parent.WaitForExit();

        if (cleanExit.WaitOne(0)) return;               // geordnet beendet
        if (GetSystemMetrics(0x2000 /* SM_SHUTTINGDOWN */) != 0) return; // Windows fährt herunter

        int code;
        try { code = parent.ExitCode; } catch { code = -1; }
        Log.Info($"GlassBar wurde unerwartet beendet (Exit-Code 0x{code:X}). Der Wächter holt die Windows-Taskleiste zurück.");
        RestoreTaskbar(state);

        // Nur nach einem echten Absturz neu starten. Im Task-Manager oder mit taskkill beendet (Exit-Code 1 oder -1)
        // war Absicht: dann nur die Taskleiste zurückgeben.
        if (!IsCrash(code)) return;

        // Absturzschleife vermeiden: war GlassBar selbst schon ein Neustart und hielt keine Minute, nicht noch einmal
        bool longEnough = DateTime.Now - started > TimeSpan.FromMinutes(1);
        if (longEnough || !parentWasCrashRestart)
        {
            try
            {
                string exe = Environment.ProcessPath!;
                Process.Start(new ProcessStartInfo(exe, "--after-crash") { UseShellExecute = false });
                return;
            }
            catch (Exception ex) { Log.Error("Neustart nach Absturz", ex); }
        }
        showMessage(L.T("GlassBar wurde wegen eines Fehlers beendet. Die Windows-Taskleiste ist wieder da.") + "\n\n" +
                    L.F("Einzelheiten stehen in {0}", Log.FilePath));
    }

    /// <summary>
    /// Sieht der Exit-Code nach einem Absturz aus? Ja: unsere Bitte um Neustart (zu viele Fehler) sowie
    /// Fehlercodes von Windows und .NET (0xC0000005 Zugriffsverletzung, 0xE0434352 unbehandelte .NET-Ausnahme,
    /// 0x80131623 FailFast …). Nein: 0, 1 und 0xFFFFFFFF (= -1) – so enden Prozesse, die beendet wurden.
    /// </summary>
    internal static bool IsCrash(int exitCode)
    {
        uint u = unchecked((uint)exitCode);
        return exitCode == RestartExitCode || (u >= 0x80000000 && u != 0xFFFFFFFF);
    }

    // ---------- Windows-Taskleiste ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    private const int ABS_ALWAYSONTOP = 2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_ASYNCWINDOWPOS = 0x4000;

    /// <summary>Zustand der Windows-Taskleiste (automatisch ausblenden ja/nein), bevor GlassBar sie versteckt.</summary>
    private static int GetTaskbarState()
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref d);
    }

    /// <summary>Wie ManagedShell beim Beenden: Zustand zurücksetzen und alle Taskleisten wieder zeigen.</summary>
    internal static void RestoreTaskbar(int state)
    {
        // In einem eigenen Thread mit Zeitlimit: hängt der Explorer gerade, soll der Wächter nicht mit hängen
        var t = new Thread(() =>
        {
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                // SWP_ASYNCWINDOWPOS: nur anstoßen, nicht auf den Explorer warten
                const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS;
                if (tray != IntPtr.Zero) SetWindowPos(tray, IntPtr.Zero, 0, 0, 0, 0, flags);
                for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero;)
                    SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, flags);

                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray, lParam = state > 0 ? state : ABS_ALWAYSONTOP };
                SHAppBarMessage(ABM_SETSTATE, ref d);
            }
            catch (Exception ex) { Log.Error("Taskleiste zurückholen", ex); }
        }) { IsBackground = true };
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(5))) Log.Info("Taskleiste zurückholen: der Explorer antwortet nicht.");
    }
}

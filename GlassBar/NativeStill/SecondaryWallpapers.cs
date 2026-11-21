using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlassBar.NativeStill;

/// <summary>
/// Der native Hintergrund auf den Nebenbildschirmen – unabhängig davon, ob dort ein Dock ist.
/// Je nach Einstellung „Hintergrund auf Nebenbildschirmen“: aus, nur das Licht, oder Licht mit Uhr und Datum;
/// dazu wahlweise die Pillen Musik, Gebetszeiten und Wetter (siehe StillModules.RegisterSecondary).
/// Die übrigen Pillen gibt es nur auf dem Hauptbildschirm. Jeder Bildschirm schläft einzeln, wenn ein Fenster
/// ihn verdeckt (maximiert, Vollbild oder fast bildschirmfüllend).
/// </summary>
public static class SecondaryWallpapers
{
    private static readonly Dictionary<string, NativeStillWindow> Windows = new();
    private static DispatcherTimer? _coverTimer;
    private static Func<float[]?>? _audio;

    /// <summary>Alle laufenden Renderer der Nebenbildschirme (zum Verteilen von Musik, Wetter, Farben).</summary>
    public static IEnumerable<StillRenderer> Renderers =>
        Windows.Values.Select(w => w.Renderer).Where(r => r != null).Cast<StillRenderer>().ToList();

    /// <summary>Gewünschten Stand herstellen (beim Start, nach Einstellungen, bei geänderten Bildschirmen).</summary>
    public static void Update(bool nativeActive, Func<float[]?>? audio, Ui.MediaControls? controls = null)
    {
        _audio = audio;
        string mode = App.Config.WallpaperSecondary;
        bool want = nativeActive && mode != "Off";
        var screens = want
            ? System.Windows.Forms.Screen.AllScreens.Where(s => !s.Primary).ToDictionary(s => s.DeviceName)
            : new Dictionary<string, System.Windows.Forms.Screen>();

        // Pillen (Musik, Gebetszeiten, Wetter) nur, wenn eingestellt und mit Uhr – im Modus „Nur Licht“ bleibt es ruhig
        bool pills = App.Config.WallpaperSecondaryPills && mode != "Light";

        // Nicht mehr gewünschte Bildschirme schließen
        foreach (var name in Windows.Keys.Where(n => !screens.ContainsKey(n)).ToList())
        {
            Windows[name].Dispose();
            Windows.Remove(name);
        }
        foreach (var (name, screen) in screens)
        {
            if (Windows.ContainsKey(name)) continue;
            try
            {
                var w = new NativeStillWindow(screen);
                w.Renderer?.Bind(NativeStillStore.Values);
                if (_audio != null) w.Renderer?.SetAudioSource(_audio);
                // Pillen immer anmelden, aber nur bei eingeschalteter Einstellung zeigen: so bleibt der Hintergrund beim
                // Umschalten stehen (Neuaufbau ließe ihn kurz verschwinden)
                if (w.Renderer is { } pr) StillModules.RegisterSecondary(pr);
                Windows[name] = w;
            }
            catch (Exception ex) { TaskbarWindow.Debug("Still nativ (Nebenbildschirm): " + ex.Message); }
        }
        foreach (var w in Windows.Values)
        {
            if (w.Renderer is not { } r) continue;
            if (controls != null) r.Controls = controls; // Musik-Knöpfe, Springen, Lautstärke wie auf dem Hauptbildschirm
            r.ShowUi = pills;
            r.ShowText = mode != "Light";
            r.ModeOverride = App.Config.WallpaperSecondaryField is { Length: > 0 } f && f != "Same" ? f : null;
            r.Fps = (int)Math.Clamp(App.Config.WallpaperFps, 10, 60);
        }

        if (Windows.Count > 0 && _coverTimer == null)
        {
            _coverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _coverTimer.Tick += (_, _) => CheckCovered();
            _coverTimer.Start();
        }
        else if (Windows.Count == 0) { _coverTimer?.Stop(); _coverTimer = null; }
    }

    public static void Shutdown()
    {
        foreach (var w in Windows.Values) w.Dispose();
        Windows.Clear();
        _coverTimer?.Stop(); _coverTimer = null;
    }

    // ---------- Verdeckt? ----------

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Pro Bildschirm: liegt ganz oben ein Fenster, das ihn (fast) ganz bedeckt? Dann schlafen.</summary>
    private static void CheckCovered()
    {
        foreach (var (name, w) in Windows)
        {
            var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name);
            if (scr == null || w.Renderer is not { } r) continue;
            r.SetPaused(Covered(scr.Bounds));
        }
    }

    private static bool Covered(System.Drawing.Rectangle s)
    {
        bool covered = false;
        var cls = new System.Text.StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            // „Desktop anzeigen“ (Win+D): Windows hebt den Desktop über alle Fenster – was darunter liegt, sieht man nicht
            if (Native.IsDesktopWindow(h)) return false;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == OwnPid) return true;
            long ex = GetWindowLongPtr(h, -20).ToInt64();
            if ((ex & (0x80 | 0x20 | 0x08000000)) != 0) return true; // Werkzeug-, durchklickbare, nicht aktivierbare Fenster
            if (DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            cls.Clear(); GetClassName(h, cls, 64);
            if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            GetWindowRect(h, out var r);
            // Maximierte Fenster ragen 8 px über ihren Bildschirm hinaus – auf die Nachbarn. Rand abziehen.
            r.L += 8; r.T += 8; r.R -= 8; r.B -= 8;
            long iw = Math.Max(0, Math.Min(r.R, s.Right) - Math.Max(r.L, s.Left));
            long ih = Math.Max(0, Math.Min(r.B, s.Bottom) - Math.Max(r.T, s.Top));
            // Nur Fenster, die wirklich auf diesem Bildschirm liegen (mehr als ein Streifen), entscheiden
            if (iw * ih < 0.02 * s.Width * s.Height) return true;
            // Das oberste Fenster auf diesem Bildschirm entscheidet
            covered = IsZoomed(h) || iw * ih >= 0.85 * s.Width * s.Height;
            return false;
        }, IntPtr.Zero);
        return covered;
    }
}

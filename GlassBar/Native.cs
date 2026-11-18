using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GlassBar;

internal static class Native
{
    /// <summary>Skalierung des Systems (z. B. 1.25 bei 125 %).</summary>
    public static double SystemScale
    {
        get
        {
            try { return System.Windows.Media.VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow ?? new System.Windows.Window()).DpiScaleX; }
            catch { return 1; }
        }
    }

    /// <summary>Fläche des Bildschirms, auf dem dieser Punkt (in DIPs) liegt – in DIPs.</summary>
    public static System.Windows.Rect ScreenAt(System.Windows.Point dip)
    {
        double s = SystemScale;
        var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(dip.X * s), (int)(dip.Y * s)));
        var b = scr.Bounds;
        return new System.Windows.Rect(b.X / s, b.Y / s, b.Width / s, b.Height / s);
    }

    // ---------- Maus / Tastatur ----------
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const byte VK_LWIN = 0x5B;
    private const uint KEYUP = 0x2;

    /// <summary>Mausposition im Format, das Tray-Icons erwarten (LOWORD=x, HIWORD=y).</summary>
    [StructLayout(LayoutKind.Sequential)] public struct POINTPUB { public int X, Y; }

    public static uint CursorParam()
    {
        GetCursorPos(out var p);
        return (uint)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
    }

    /// <summary>Drückt Win (+ optional eine Taste), z. B. Startmenü, Win+S, Win+Tab.</summary>
    public static void WinKey(char? key = null)
    {
        keybd_event(VK_LWIN, 0, 0, WinKeyHook.Marker);
        if (key is char k)
        {
            byte vk = (byte)char.ToUpperInvariant(k);
            keybd_event(vk, 0, 0, WinKeyHook.Marker);
            keybd_event(vk, 0, KEYUP, WinKeyHook.Marker);
        }
        keybd_event(VK_LWIN, 0, KEYUP, WinKeyHook.Marker);
    }

    // ---------- Fensterstile ----------
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    private const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    public static void AddExStyle(IntPtr hwnd, long style) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | style));

    // ---------- DWM (Acryl-Hintergrund, abgerundete Ecken, Live-Vorschau) ----------
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    public const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);

    /// <summary>Dunkles Acryl-Fenster mit runden Ecken (Windows 11).</summary>
    public static void ApplyAcrylic(IntPtr hwnd)
    {
        int dark = 1, round = 2, backdrop = 3; // DWMSBT_TRANSIENTWINDOW = Acryl
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);      // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);     // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);  // DWMWA_SYSTEMBACKDROP_TYPE
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    // ---------- Unschärfe mit einstellbarer Tönung (durchsichtiger als das Standard-Acryl) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    /// <summary>
    /// Glas-Effekt: Unschärfe hinter dem Fenster, getönt mit <paramref name="tint"/> (ARGB, z. B. 0x60202020 =
    /// dunkelgrau, ca. 38 % deckend). Rundung und dunkler Rahmen kommen von Windows 11.
    /// </summary>
    public static void ApplyGlass(IntPtr hwnd, uint tint, bool blur = true, bool plainBlur = false)
    {
        int dark = 1, round = 2, none = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);   // dunkler Modus
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // runde Ecken
        DwmSetWindowAttribute(hwnd, 38, ref none, 4);   // kein System-Backdrop (wir machen es selbst)
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);

        // ARGB -> ABGR für die Accent-API
        uint a = tint >> 24, r = (tint >> 16) & 0xFF, g = (tint >> 8) & 0xFF, b = tint & 0xFF;
        var accent = new AccentPolicy
        {
            // 4 = ACRYLICBLURBEHIND (Tönung = GradientColor), 3 = BLURBEHIND (nur Unschärfe, die Tönung malt dann WPF,
            // siehe Theme.ApplyGlass), 2 = TRANSPARENTGRADIENT (ohne Unschärfe)
            AccentState = !blur ? 2 : plainBlur ? 3 : 4,
            AccentFlags = 2,
            GradientColor = unchecked((int)((a << 24) | (b << 16) | (g << 8) | r)),
        };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttribData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void SetCorners(IntPtr hwnd, bool round)
    {
        int pref = round ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
    }

    // ---------- Dunkle Windows-Menüs (Kontextmenüs der Shell) ----------
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

    /// <summary>Schaltet für diesen Prozess den dunklen Modus für Win32-Menüs ein (wie im Explorer).</summary>
    public static void EnableDarkMenus() => SetMenuTheme(light: false);

    /// <summary>Win32-Menüs dieses Prozesses hell oder dunkel (passend zum Design, siehe Theme).</summary>
    public static void SetMenuTheme(bool light)
    {
        try
        {
            SetPreferredAppMode(light ? 3 /* ForceLight */ : 2 /* ForceDark */);
            FlushMenuThemes();
        }
        catch { }
    }

    // ---------- Autostart ----------
    // Über die Aufgabenplanung statt über den Run-Schlüssel: Run-Einträge startet Windows absichtlich verzögert
    // (bei dir rund 2 Minuten nach dem Hochfahren). Eine Aufgabe „bei Anmeldung“ startet sofort.
    // Das Anlegen braucht einmal Administratorrechte (Windows fragt nach); GlassBar selbst läuft danach normal.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "GlassBar";

    public static bool Autostart
    {
        get => TaskExists() || RunKeyExists();
        set
        {
            if (value)
            {
                if (CreateTask()) RemoveRunKey();
                else SetRunKey(); // Rückfrage abgelehnt: wenigstens der (spätere) normale Autostart
            }
            else
            {
                RemoveRunKey();
                if (TaskExists()) DeleteTask();
            }
        }
    }

    private static bool RunKeyExists()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("GlassBar") != null;
    }

    private static void SetRunKey()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue("GlassBar", $"\"{Environment.ProcessPath}\"");
    }

    private static void RemoveRunKey()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.DeleteValue("GlassBar", false);
    }

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    private static bool CreateTask()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var exe = Environment.ProcessPath ?? "";
        var esc = (string t) => System.Security.SecurityElement.Escape(t);
        // Priorität 4 = normal (Standard der Aufgabenplanung wäre „niedriger als normal“); keine Zeitbegrenzung,
        // auch im Akkubetrieb; nur für dich, ohne Administratorrechte zur Laufzeit.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Startet GlassBar sofort bei der Anmeldung.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{esc(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <StartWhenAvailable>true</StartWhenAvailable>
              </Settings>
              <Actions Context="Author"><Exec><Command>{esc(exe)}</Command><WorkingDirectory>{esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "GlassBar-Autostart.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            // Für den eigenen Benutzer klappt es meist ohne Rückfrage; nur wenn nicht, mit Administratorrechten
            var args = $"/Create /F /TN \"{TaskName}\" /XML \"{file}\"";
            if (RunSchtasks(args, elevated: false) == 0 && TaskExists()) return true;
            return RunSchtasks(args, elevated: true) == 0 && TaskExists();
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    private static void DeleteTask()
    {
        if (RunSchtasks($"/Delete /F /TN \"{TaskName}\"", elevated: false) != 0 && TaskExists())
            RunSchtasks($"/Delete /F /TN \"{TaskName}\"", elevated: true);
    }

    /// <summary>schtasks.exe ausführen; elevated = mit Windows-Rückfrage (UAC). Liefert den Exit-Code (-1 = abgelehnt).</summary>
    private static int RunSchtasks(string args, bool elevated)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = elevated,
                Verb = elevated ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !elevated,
                RedirectStandardError = !elevated,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; } // z. B. Rückfrage abgelehnt
    }

    /// <summary>Löst eine .lnk-Verknüpfung auf (Ziel, Argumente).</summary>
    public static (string Target, string Args) ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (path, "");
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return (path, "");
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(path);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            return (string.IsNullOrEmpty(target) ? path : target, args ?? "");
        }
        catch { return (path, ""); }
    }

    public static void Launch(string path, string? args = null)
    {
        // Programme in Paketordnern (Microsoft Store) verweigern den direkten Start
        if (PackagedApps.IsPackageExe(path))
        {
            string shellPath = PackagedApps.Normalize(path);
            if (!PackagedApps.IsShellPath(shellPath))
            {
                System.Windows.MessageBox.Show($"{L.T("Konnte nicht geöffnet werden:")}\n{path}\n\n{L.T("Diese App ist nicht mehr installiert.")}", "GlassBar",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            path = shellPath;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"{L.T("Konnte nicht geöffnet werden:")}\n{path}\n\n{ex.Message}", "GlassBar",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}

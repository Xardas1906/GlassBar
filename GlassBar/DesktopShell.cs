using System.Runtime.InteropServices;

namespace GlassBar;

/// <summary>Kleine Helfer für den Windows-Desktop (Desktopmenü, Desktopsymbole).</summary>
public static class DesktopShell
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    /// <summary>„Desktopsymbole anzeigen“ ist in Windows ausgeschaltet.</summary>
    public static bool IconsHidden()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return k?.GetValue("HideIcons") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Das Fenster, das die Desktop-Symbole (SHELLDLL_DefView) enthält.</summary>
    public static IntPtr FindDefViewHost()
    {
        var progman = FindWindow("Progman", null);
        if (FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;
        IntPtr host = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { host = h; return false; }
            return true;
        }, IntPtr.Zero);
        return host;
    }

    /// <summary>Desktop-Symbole ein- oder ausblenden (wie Rechtsklick → Ansicht → Desktopsymbole anzeigen).</summary>
    public static void ToggleDesktopIcons()
    {
        var host = FindDefViewHost();
        var defView = FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) PostMessage(defView, 0x0111 /* WM_COMMAND */, new IntPtr(0x7402), IntPtr.Zero);
    }

    /// <summary>Öffnet das normale Windows-Desktopmenü an dieser Stelle (Ansicht, Neu, Anpassen …).</summary>
    public static void ShowDesktopMenu(int x, int y)
    {
        var defView = FindWindowEx(FindDefViewHost(), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) return;
        SetForegroundWindow(defView);
        PostMessage(defView, 0x007B /* WM_CONTEXTMENU */, defView, new IntPtr((y << 16) | (x & 0xFFFF)));
    }
}

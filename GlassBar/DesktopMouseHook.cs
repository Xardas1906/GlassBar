using System.Runtime.InteropServices;
using System.Text;

namespace GlassBar;

/// <summary>
/// Gibt Mausbewegungen und Klicks auf dem freien Desktop an den Hintergrund weiter (wie Wallpaper Engine),
/// damit Uhr, Musik-Pille usw. im Hintergrund bedienbar sind. Die Maus selbst wird nicht abgefangen –
/// Desktop-Symbole funktionieren normal weiter.
/// Der Hook läuft in einem eigenen Thread, damit die Maus nie auf GlassBar warten muss (sonst ruckelt z. B.
/// das Markieren auf dem Desktop).
/// </summary>
internal sealed class DesktopMouseHook : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINTPUB p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

    private HookProc? _proc;
    private IntPtr _hook;
    private uint _threadId;
    private readonly Action<uint, int, int, int> _forward;
    private readonly Func<bool> _interactive, _overUi, _editing, _captured;
    private readonly Action<int, int> _rightClick;
    private readonly Action<bool> _desktopChanged;

    private readonly StringBuilder _cls = new(64);
    private bool _leftDown, _rightToPage, _clickToPage;
    private readonly bool _swallowUiClicks, _allScreens;
    private int _onDesktop = -1; // unbekannt

    /// <param name="forward">Maus an den Hintergrund senden (wird im Hook-Thread aufgerufen).</param>
    /// <param name="interactive">true = der Hintergrund liegt vorn und bekommt die Maus selbst.</param>
    /// <param name="overUi">Die Maus ist über einem Element des Hintergrunds (Pille, Karte …).</param>
    /// <param name="editing">Der Hintergrund ist im Anordnen-Modus (Ziehen muss weitergegeben werden).</param>
    /// <param name="rightClick">Rechtsklick auf den freien Desktop → Windows-Desktopmenü öffnen.</param>
    /// <param name="desktopChanged">Die Maus kommt auf den Desktop (true) oder verlässt ihn (false).</param>
    /// <param name="swallowUiClicks">
    /// Der Hintergrund bekommt die Maus nur über diesen Hook (nativer Hintergrund): Klicks und Mausrad über seiner
    /// Oberfläche werden dann nicht an den Desktop weitergegeben (sonst würde dort markiert/abgewählt).
    /// </param>
    /// <param name="captured">Ein Element des Hintergrunds hält die Maus gerade fest (Ziehen darf es verlassen).</param>
    public DesktopMouseHook(Action<uint, int, int, int> forward, Func<bool> interactive, Func<bool> overUi, Func<bool> editing,
                            Action<int, int> rightClick, Action<bool> desktopChanged, bool swallowUiClicks = false, bool allScreens = false,
                            Func<bool>? captured = null)
    {
        _swallowUiClicks = swallowUiClicks;
        _allScreens = allScreens;
        _forward = forward;
        _interactive = interactive;
        _overUi = overUi;
        _editing = editing;
        _captured = captured ?? (() => false);
        _rightClick = rightClick;
        _desktopChanged = desktopChanged;

        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = Callback;
            using (var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule)
                _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(mod?.ModuleName), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "GlassBar Maus", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(2000);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            uint msg = (uint)wParam;
            var m = Marshal.PtrToStructure<MSLL>(lParam);
            bool over = IsOverDesktop(m.X, m.Y);

            if (msg == 0x0200)
            {
                int now = over ? 1 : 0;
                if (now != _onDesktop) { _onDesktop = now; _desktopChanged(over); }
            }

            // Rechtsklick wie bei Wallpaper Engine: auf freiem Desktop das Windows-Desktopmenü.
            // Über einer Pille oder Karte bekommt ihn der Hintergrund selbst – die Karte bleibt offen.
            if (msg is 0x0204 or 0x0205)
            {
                if (msg == 0x0204) _rightToPage = over && _overUi();
                if (_interactive())
                {
                    if (over && !_rightToPage)
                    {
                        if (msg == 0x0205) _rightClick(m.X, m.Y);
                        return (IntPtr)1; // nicht an den Hintergrund weitergeben
                    }
                }
                // Symbole sichtbar: Rechtsklick auf eine Pille/Karte nicht an den Desktop – die Karte bleibt offen
                else if (over && _rightToPage) return (IntPtr)1;
            }

            if (msg == 0x0201)
            {
                _leftDown = over;
                // Klick auf freie Fläche nicht an den Hintergrund: Der Browser würde sich sonst die Maus holen und
                // Windows bricht das Markieren (blauer Rahmen) ab. Nur Pillen/Karten und das Anordnen bekommen Klicks.
                _clickToPage = over && (_overUi() || _editing());
            }
            else if (msg == 0x0202 && !over) _leftDown = false;
            // Gedrückt auf der Oberfläche, losgelassen woanders (über einem Fenster, dem Dock …): das Loslassen
            // trotzdem melden – sonst hielte eine Karte die Maus fest und bliebe offen. Das Fenster darunter bekommt
            // es nicht: Es hat das Drücken nie gesehen, und das Dock löste sonst beim Loslassen etwas aus.
            if (_swallowUiClicks && !over && msg == 0x0202 && _clickToPage)
            {
                _forward(msg, m.X, m.Y, 0);
                _clickToPage = false;
                _leftDown = false;
                _desktopChanged(false);
                return (IntPtr)1;
            }
            if (_swallowUiClicks && over && (msg is 0x0201 or 0x0202 && _clickToPage || msg == 0x020A && _overUi()))
            {
                _forward(msg, m.X, m.Y, msg == 0x020A ? (short)(m.MouseData >> 16) : 0);
                if (msg == 0x0202) _leftDown = false;
                return (IntPtr)1;
            }
            // Nativer Hintergrund: Klicks auf freie Fläche bekommt er zusätzlich (Leuchten beim Klicken),
            // der Desktop behält sie aber – kein Browser, der sich die Maus holen könnte.
            if (_swallowUiClicks && over && msg is 0x0201 or 0x0202 && !_clickToPage)
                _forward(msg, m.X, m.Y, 0);
            if (msg is 0x0201 or 0x0202 && !_clickToPage)
            {
                if (msg == 0x0202) _leftDown = false;
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            // Auf der Oberfläche gedrückt und dort festgehalten: Bewegungen immer melden, auch außerhalb der Karte.
            // Markieren auf dem Desktop ist nie betroffen – dort gehört das Drücken nicht der Oberfläche.
            if (_swallowUiClicks && msg == 0x0200 && _leftDown && _clickToPage && _captured())
                _forward(msg, m.X, m.Y, 0);
            else if (over && msg is 0x0200 or 0x0201 or 0x0202 or 0x020A) // Bewegung, links runter/hoch, Mausrad
            {
                // Beim Markieren mit gedrückter Taste keine Bewegungen weitergeben: Der Hintergrund müsste sonst
                // bei jeder Bewegung mitrechnen und das Markieren ruckelt. (Außer beim Anordnen.)
                if (!(msg == 0x0200 && _leftDown && !_editing()))
                    _forward(msg, m.X, m.Y, msg == 0x020A ? (short)(m.MouseData >> 16) : 0);
            }
            if (msg == 0x0202) _leftDown = false;
        }
        catch { }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsOverDesktop(int x, int y)
    {
        if (_allScreens)
        {
            // ganzer virtueller Bildschirm (alle Monitore)
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            if (x < vx || y < vy || x >= vx + vw || y >= vy + vh) return false;
        }
        // Hauptbildschirm beginnt immer bei (0, 0)
        else if (x < 0 || y < 0 || x >= GetSystemMetrics(0) || y >= GetSystemMetrics(1)) return false;
        var root = GetAncestor(WindowFromPoint(new Native.POINTPUB { X = x, Y = y }), 2 /* GA_ROOT */);
        _cls.Clear();
        GetClassName(root, _cls, 64);
        var c = _cls.ToString();
        return c is "Progman" or "WorkerW";
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}

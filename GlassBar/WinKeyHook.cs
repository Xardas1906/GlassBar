using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassBar;

/// <summary>
/// Beobachtet die Windows-Taste systemweit.
///
/// Ist das Dock ausgeblendet, wird ein einzelner Druck auf die Windows-Taste abgefangen und zeigt nur das Dock
/// (kein Startmenü). Der nächste Druck – jetzt ist das Dock sichtbar – öffnet ganz normal das Startmenü.
/// Tastenkürzel wie Win+E, Win+D, Win+Tab funktionieren immer: dafür wird die Windows-Taste nachgereicht.
/// </summary>
internal static class WinKeyHook
{
    /// <summary>Windows-Taste wurde gedrückt (true = abgefangen, nur Dock zeigen).</summary>
    public static event Action<bool>? Pressed;

    /// <summary>Soll ein einzelner Druck gerade abgefangen werden? (= Dock ist ausgeblendet)</summary>
    public static Func<bool>? ShouldIntercept;

    /// <summary>Win+1…9 selbst übernehmen? (Einstellung; läuft im Thread des Hooks, nur einfache Felder lesen)</summary>
    public static Func<bool>? HandleNumbers;

    /// <summary>Win+1…9 wurde gedrückt: Index 0–8, true = mit Umschalt (neue Instanz). Kommt aus dem Hook-Thread.</summary>
    public static event Action<int, bool>? NumberPressed;

    /// <summary>Markierung für Tastendrücke, die GlassBar selbst sendet.</summary>
    public static readonly UIntPtr Marker = new(0x47425221);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C, KEYUP = 0x2, EXTENDED = 0x1;

    private static IntPtr _hook;
    private static readonly LowLevelKeyboardProc Proc = Callback; // Referenz halten, sonst räumt der GC sie weg

    private static bool _winDown;        // Windows-Taste ist gerade gedrückt
    private static bool _held;           // ... und wurde von uns zurückgehalten
    private static uint _heldVk;
    private static bool _swallowWinUp;   // Windows hat die Windows-Taste nie gesehen -> auch das Loslassen verschlucken
    private static uint _digitDown;      // gerade gehaltene Ziffer (Tastenwiederholung nicht erneut auslösen)

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static uint _threadId;

    /// <summary>
    /// Hook in einem eigenen Thread: Die Tastatur muss so nie warten, falls GlassBar gerade beschäftigt ist.
    /// </summary>
    public static void Install()
    {
        if (_threadId != 0) return;
        var ready = new System.Threading.ManualResetEventSlim();
        var t = new System.Threading.Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            using (var module = Process.GetCurrentProcess().MainModule)
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, GetModuleHandle(module?.ModuleName), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "GlassBar Tastatur", Priority = System.Threading.ThreadPriority.AboveNormal };
        t.Start();
        ready.Wait(2000);
    }

    public static void Uninstall()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }

    /// <summary>Taste senden, mit GlassBar-Markierung (wird vom Hook durchgelassen).</summary>
    public static void Send(byte vk, bool up) =>
        keybd_event(vk, 0, (up ? KEYUP : 0) | (vk is 0x5B or 0x5C ? EXTENDED : 0), Marker);

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Marker) return CallNextHookEx(_hook, nCode, wParam, lParam); // eigene Eingabe

        int msg = (int)wParam;
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;
        bool isWin = k.vkCode is VK_LWIN or VK_RWIN;

        if (isWin)
        {
            if (down)
            {
                if (_winDown) return _held ? (IntPtr)1 : CallNextHookEx(_hook, nCode, wParam, lParam); // Tastenwiederholung
                _winDown = true;
                bool intercept = ShouldIntercept?.Invoke() == true;
                if (intercept)
                {
                    _held = true;
                    _heldVk = k.vkCode;
                    return (IntPtr)1; // vorerst zurückhalten
                }
                Pressed?.Invoke(false);
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            if (up)
            {
                _winDown = false;
                _digitDown = 0;
                if (_swallowWinUp)
                {
                    _swallowWinUp = false;
                    return (IntPtr)1;
                }
                if (_held)
                {
                    // Nur die Windows-Taste allein gedrückt -> nur das Dock zeigen, kein Startmenü
                    _held = false;
                    Pressed?.Invoke(true);
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // Win+1…9: Programm an dieser Stelle im Dock öffnen (wie bei der Windows-Taskleiste – deren Reihenfolge
        // stimmt nicht mit dem Dock überein, darum übernimmt GlassBar das selbst). Strg/Alt+Win+Ziffer bleibt bei Windows.
        if (_winDown && k.vkCode is >= 0x31 and <= 0x39 && !IsDown(0x11 /* Strg */) && !IsDown(0x12 /* Alt */)
            && HandleNumbers?.Invoke() == true)
        {
            if (up)
            {
                if (_digitDown == k.vkCode) _digitDown = 0;
                return (IntPtr)1;
            }
            if (!down) return CallNextHookEx(_hook, nCode, wParam, lParam);
            if (_digitDown == k.vkCode) return (IntPtr)1; // Tastenwiederholung
            _digitDown = k.vkCode;
            if (_held)
            {
                // Windows-Taste wurde zurückgehalten und kommt nie bei Windows an
                _held = false;
                _swallowWinUp = true;
            }
            else
            {
                // Windows hat die Windows-Taste gesehen: eine unbelegte Taste nachschieben, damit das Loslassen
                // nicht das Startmenü öffnet (Windows hält es sonst für einen einzelnen Druck)
                Send(0xE8, up: false);
                Send(0xE8, up: true);
            }
            NumberPressed?.Invoke((int)(k.vkCode - 0x31), IsDown(0x10 /* Umschalt */));
            return (IntPtr)1;
        }

        // Eine andere Taste, während die Windows-Taste zurückgehalten wird -> es ist ein Kürzel (Win+E …):
        // Windows-Taste nachreichen und die Taste neu senden, damit die Reihenfolge stimmt.
        if (_held && down)
        {
            _held = false;
            Send((byte)_heldVk, up: false);
            keybd_event((byte)k.vkCode, (byte)k.scanCode, (k.flags & 0x1) != 0 ? EXTENDED : 0, Marker);
            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

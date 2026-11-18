using System.Runtime.InteropServices;

namespace GlassBar.NativeStill;

/// <summary>
/// Der native Hintergrund: ein schlichtes Win32-Fenster in der Desktop-Ebene (hinter den Symbolen), in das
/// <see cref="StillRenderer"/> mit Direct3D zeichnet. Kein Browser, kein WPF-Inhalt.
/// </summary>
public sealed class NativeStillWindow : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr h, IntPtr r);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Native.POINTPUB p);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    private const string ClassName = "GlassBarStillNative";
    private static bool _registered;
    private static readonly WndProcDelegate Proc = WndProc; // Referenz halten (GC)

    private IntPtr _hwnd;
    private StillRenderer? _renderer;

    public StillRenderer? Renderer => _renderer;
    public IntPtr Handle => _hwnd;

    private readonly string _screenName;

    // Alle nativen Hintergründe (einer je Bildschirm), für die Maus-Verteilung
    private static readonly List<NativeStillWindow> All = new();
    private static NativeStillWindow? _lastUnderMouse;
    // Hintergrund, auf dessen Oberfläche gedrückt wurde: Er bekommt alles bis zum Loslassen (nur Hook-Thread)
    private static NativeStillWindow? _pressTarget;

    /// <summary>
    /// Maus vom Desktop-Hook (Bildschirmkoordinaten) an den Hintergrund des Bildschirms geben, auf dem sie ist.
    /// Wechselt sie den Bildschirm, bekommt der vorige ein „Maus weg“. Nach einem Druck auf seine Oberfläche bleibt
    /// die Maus bis zum Loslassen bei ihm, auch auf anderen Bildschirmen – sonst bliebe sie dort festgehalten.
    /// </summary>
    public static void Route(uint msg, int x, int y, int wheel)
    {
        NativeStillWindow? target = null;
        lock (All)
            foreach (var w in All)
                if (w._bounds.Contains(x, y)) { target = w; break; }
        var pressed = _pressTarget;
        if (pressed != null && msg != 0x0201)
        {
            target = pressed;
            if (msg == 0x0202) _pressTarget = null;
        }
        else if (msg == 0x0201)
        {
            // Das Loslassen ging verloren: dort sauber beenden (die Stelle liegt außerhalb, es wird nichts getroffen)
            if (pressed != null && !ReferenceEquals(pressed, target))
            {
                pressed.ForwardMouse(0x0201, x, y);
                pressed.ForwardMouse(0x0202, x, y);
            }
            _pressTarget = target?._renderer?.OverUi == true ? target : null;
        }
        var last = _lastUnderMouse;
        if (!ReferenceEquals(last, target)) { last?.MouseLeft(); _lastUnderMouse = target; }
        target?.ForwardMouse(msg, x, y, wheel);
    }

    /// <summary>Maus hat den Desktop ganz verlassen.</summary>
    public static void RouteLeave()
    {
        _lastUnderMouse?.MouseLeft();
        _lastUnderMouse = null;
    }

    /// <summary>Hintergrund für diesen Bildschirm (null = Hauptbildschirm).</summary>
    public NativeStillWindow(System.Windows.Forms.Screen? screen = null)
    {
        _screenName = (screen ?? System.Windows.Forms.Screen.PrimaryScreen!).DeviceName;
        lock (All) All.Add(this);
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Proc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }
        // WS_POPUP; Toolwindow + NoActivate: taucht nirgends auf und holt nie den Fokus
        _hwnd = CreateWindowEx(0x00000080 | 0x08000000, ClassName, "GlassBar Hintergrund (nativ)", 0x80000000,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        AttachToDesktop();
        var b = ScreenBounds;
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        _renderer = new StillRenderer(_hwnd, b.Width, b.Height);
    }

    private static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case 0x0014: return (IntPtr)1;                 // WM_ERASEBKGND: nichts löschen (kein Flackern)
            case 0x000F: ValidateRect(h, IntPtr.Zero); return IntPtr.Zero; // WM_PAINT: zeichnet der Renderer
            case 0x0021: return (IntPtr)3;                 // WM_MOUSEACTIVATE: MA_NOACTIVATE
        }
        return DefWindowProc(h, msg, w, l);
    }

    /// <summary>Wie beim Browser-Hintergrund: hinter die Desktop-Symbole hängen und auf den Hauptbildschirm legen.</summary>
    public void AttachToDesktop()
    {
        var progman = FindWindow("Progman", null);
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        long style = 0x40000000L | 0x10000000L; // WS_CHILD | WS_VISIBLE
        SetWindowLongPtr(_hwnd, -16, new IntPtr(style));

        var innerWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (innerWorker != IntPtr.Zero && defView != IntPtr.Zero)
        {
            SetParent(_hwnd, progman); // Windows 11 24H2+: direkt unter die Symbole
            SetWindowPos(_hwnd, defView, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        else
        {
            IntPtr worker = IntPtr.Zero;
            EnumWindows((hh, _) =>
            {
                if (FindWindowEx(hh, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    worker = FindWindowEx(IntPtr.Zero, hh, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            SetParent(_hwnd, worker != IntPtr.Zero ? worker : progman);
        }
        PlaceOnScreen();
    }

    private System.Drawing.Rectangle ScreenBounds =>
        (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _screenName)
         ?? System.Windows.Forms.Screen.PrimaryScreen!).Bounds;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    /// <summary>Auf den Hauptbildschirm legen (Koordinaten relativ zur Desktop-Ebene).</summary>
    // Zuletzt gesetzte Bildschirmfläche (für die schnelle Maus-Verteilung im Hook-Thread)
    private System.Drawing.Rectangle _bounds; // wird selten geändert; ein kurz veralteter Wert schadet nicht

    public void PlaceOnScreen()
    {
        var p = ScreenBounds;
        _bounds = p;
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        SetWindowPos(_hwnd, IntPtr.Zero, p.X - v.X, p.Y - v.Y, p.Width, p.Height, 0x0004 | 0x0010 | 0x0040);
        _renderer?.Resize(p.Width, p.Height);
    }

    /// <summary>Maus vom Desktop-Hook (Bildschirmkoordinaten): an die Oberfläche und an die Wasser-Strömung.</summary>
    public void ForwardMouse(uint msg, int x, int y, int wheel = 0)
    {
        var r = _renderer;
        if (r == null) return;
        var pt = new Native.POINTPUB { X = x, Y = y };
        ScreenToClient(_hwnd, ref pt);
        var pos = new System.Numerics.Vector2(pt.X, pt.Y);
        switch (msg)
        {
            case 0x0200:
                if (_bounds.Contains(x, y)) r.Flow.Pointer(pt.X, pt.Y); // beim Ziehen kann sie woanders sein
                r.PostPointer(new Ui.PointerEvent(Ui.PointerKind.Move, pos));
                break;
            case 0x0201:
                if (!r.OverUi) r.Flow.Click(pt.X, pt.Y); // Klick auf eine Karte lässt das Licht in Ruhe
                r.PostPointer(new Ui.PointerEvent(Ui.PointerKind.Down, pos));
                break;
            case 0x0202: r.PostPointer(new Ui.PointerEvent(Ui.PointerKind.Up, pos)); break;
            case 0x020A: r.PostPointer(new Ui.PointerEvent(Ui.PointerKind.Wheel, pos, 0, wheel)); break;
        }
    }

    /// <summary>Maus hat den Desktop verlassen.</summary>
    public void MouseLeft() => _renderer?.PostPointer(new Ui.PointerEvent(Ui.PointerKind.Leave, default));

    public void Dispose()
    {
        lock (All) All.Remove(this);
        if (ReferenceEquals(_lastUnderMouse, this)) _lastUnderMouse = null;
        if (ReferenceEquals(_pressTarget, this)) _pressTarget = null;
        _renderer?.Dispose();
        _renderer = null;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}

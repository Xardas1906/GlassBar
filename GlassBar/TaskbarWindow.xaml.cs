using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.Interop;
using ManagedShell.ShellFolders;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace GlassBar;

public partial class TaskbarWindow : Window
{
    private const string DragFormat = "GlassBarApp";

    private readonly ShellManager _shell;
    public TaskbarModel Model { get; }

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Aufklapp-Fenster (Vorschau / Ordner)
    private FlyoutBase? _flyout;
    private string? _flyoutKey;
    private string? _lastClosedKey;
    private DateTime _lastClosedAt;
    private string? _hoverKey;
    private FrameworkElement? _hoverAnchor;
    private Func<FlyoutBase>? _hoverFactory;

    // Ziehen zum Sortieren
    private Point? _dragStart;
    private AppButton? _dragButton;
    private bool _justDragged;

    /// <summary>Ist das Dock auf dem jeweiligen Bildschirm gerade zu sehen? (wird vom Tastatur-Hook gelesen)</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> DockShown = new();

    /// <summary>Gerätename des Bildschirms, auf dem dieses Dock sitzt.</summary>
    public string ScreenName { get; }
    /// <summary>Dock auf dem Hauptbildschirm: Nur dieses steuert Hintergrund, Tastatur und Startmenü.</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Bildschirm, dessen Fenster dieses Dock zeigt (null = alle Fenster).
    /// Das Dock auf dem Hauptbildschirm zeigt immer alle Programme.
    /// </summary>
    private string? AppScreenFilter =>
        !IsPrimary && App.Config.DockOnAllScreens && App.Config.AppsOnOwnScreen ? ScreenName : null;

    public TaskbarWindow(ShellManager shell, System.Windows.Forms.Screen screen)
    {
        _shell = shell;
        ScreenName = screen.DeviceName;
        IsPrimary = screen.Primary;
        InitializeComponent();
        L.Apply(this);

        // Eigenes, schwebendes Fenster mit echtem Windows-Acryl – nur so groß wie das Dock.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            GlassFrameThickness = new Thickness(Native.GlassAvailable ? -1 : 0), CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SizeChanged += (_, _) => PlaceDock();
        // Ohne Acryl rundet Windows die Ecken nicht: dann das Fenster selbst zuschneiden
        Native.KeepRounded(this, () => Root.CornerRadius.TopLeft);
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.WorkArea)) PlaceDock(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceDock);

        // Automatisch ausblenden, wenn ein Fenster das Dock überdeckt (wie beim Mac);
        // einblenden mit der Windows-Taste oder wenn die Maus an den unteren Rand fährt.
        _autoHide.Tick += (_, _) => AutoHideTick();
        _autoHide.Start();

        if (IsPrimary)
        {
            // Startmenü/Suche/Infocenter sofort beim Öffnen über das Dock schieben (die liegen auf dem Hauptbildschirm)
            _winEventProc = (_, _, hwnd, _, _, _, _) => Dispatcher.BeginInvoke(() =>
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                // Ein anderes Programm ist nach vorne gekommen: offenes Dock-Menü (z. B. Sprungliste) schließen
                if (GetWindowThreadProcessId(hwnd, out uint fgPid) != 0 && fgPid != OwnPid) Ui.CloseOpenMenu();
                GetWindowRect(h, out var d);
                LiftShellFlyout(hwnd, d);
            });
            _winEventHook = SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _winEventProc, 0, 0, 0);
            Closed += (_, _) => UnhookWinEvent(_winEventHook);
            // Dock ausgeblendet: 1. Druck zeigt nur das Dock, 2. Druck öffnet das Startmenü
            // läuft im Thread des Tastatur-Hooks -> nur einfache Felder lesen
            WinKeyHook.ShouldIntercept = () => App.Config.WinKeyShowsDockFirst &&
                (App.Config.WinKeyAllDocks ? DockShown.Values.Any(v => !v) : !_shown);
            // Win+1…9: Programm an dieser Stelle im Dock
            WinKeyHook.HandleNumbers = () => App.Config.WinNumberKeys;
            Action<int, bool> number = (i, shift) => Dispatcher.BeginInvoke(() => OpenByNumber(i, shift));
            WinKeyHook.NumberPressed += number;
            Closed += (_, _) => WinKeyHook.NumberPressed -= number;
            WinKeyHook.Install();
            Closed += (_, _) => WinKeyHook.Uninstall();
            FolderFlyout.PinRequested += p => Model!.AddPin(p);
        }
        DockShown[ScreenName] = true;
        Closed += (_, _) => DockShown.TryRemove(ScreenName, out _);
        // Windows-Taste zeigt alle Docks
        Action<bool> peek = held => Dispatcher.BeginInvoke(() =>
        {
            Peek();
            if (held) FreeCapturedMouse();
        });
        WinKeyHook.Pressed += peek;
        Closed += (_, _) => WinKeyHook.Pressed -= peek;

        // Jedes Dock hat seine eigene Liste: angeheftete Programme überall, laufende Fenster nur auf ihrem Bildschirm
        if (IsPrimary) StartTrace.Mark("Dock-Fenster");
        Model = new TaskbarModel(shell) { ScreenFilter = AppScreenFilter };
        if (IsPrimary) StartTrace.Mark("Programmliste");
        Closed += (_, _) => Model.Detach();
        AppList.ItemsSource = Model.Buttons;

        DesktopButton.DataContext = new ShellItem(FolderFlyout.UserDesktop);
        DesktopButton.ToolTip = "Desktop";
        UpdateDesktopButton();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PrewarmDesktop);
        if (IsPrimary) Loaded += (_, _) => StartTrace.Mark("Loaded");
        if (IsPrimary)
            ContentRendered += (_, _) =>
            {
                StartTrace.Mark("Dock sichtbar");
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { StartTrace.Mark("Leerlauf"); StartTrace.Flush(); });
            };


        if (shell.NotificationArea is { } tray)
        {
            TrayList.ItemsSource = TrayView(tray, pinned: true);
        }

        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        Closed += (_, _) =>
        {
            _autoHide.Stop(); _clock.Stop(); _weatherTimer.Stop(); _hover.Stop();
            _overflowWatch?.Stop(); _trayMenuWatch?.Stop();
            CloseFlyout();
            foreach (var f in _folderCache.Values) { f.KeepAlive = false; f.Close(); }
            _mouseHook?.Dispose(); _mouseHook = null;
            _nativeAudio?.Dispose(); _nativeAudio = null;
            _native?.Dispose(); _native = null;
        };
        // Musik (Windows-Medienschnittstelle) und Wetter erst nach dem ersten Zeichnen: spart beim Kaltstart ~0,7 s
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { StartMusicAndWeather(); if (IsPrimary) StartTrace.Mark("Musik und Wetter"); });
        UpdateClock();
        UpdateStatusIcons();

        _hover.Tick += (_, _) =>
        {
            _hover.Stop();
            if (_hoverAnchor is { IsMouseOver: true } && _hoverKey != null && _hoverFactory != null)
                OpenFlyout(_hoverKey, _hoverAnchor, _hoverFactory, sticky: false);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = System.Windows.Media.Colors.Transparent;
        // Die Leiste soll nie den Fokus klauen – sonst weiß sie nicht mehr, welche App aktiv war.
        Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        Theme.ApplyGlass(this, hwnd, App.Config.DockOpacityNow);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    // ---------- Automatisch ausblenden ----------
    private readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _covered;
    private DateTime _peekUntil;
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>Dock kurz nach vorne holen (Windows-Taste).</summary>
    private void Peek()
    {
        _peekUntil = DateTime.Now.AddSeconds(3);
        SetDockShown(true);
        // ganz nach oben, auch über Vollbild-Fenster
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
    }

    // ---------- Maus aus Spielen befreien ----------
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClipCursor(out Native.RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);

    /// <summary>
    /// Erster Druck auf die Windows-Taste in einem Spiel: Das Spiel hält die Maus fest (versteckt oder eingesperrt).
    /// Dann holt das Dock auf diesem Bildschirm sich den Vordergrund, gibt die Maus frei und setzt sie aufs Dock.
    /// </summary>
    private void FreeCapturedMouse()
    {
        var fg = GetForegroundWindow();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (fg == IntPtr.Zero || fg == hwnd) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == OwnPid) return;
        // nur das Dock auf dem Bildschirm des Spiels
        if (MonitorFromWindow(fg, 2) != MonitorFromWindow(hwnd, 2)) return;

        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        bool hidden = GetCursorInfo(ref ci) && ((ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero);
        var screen = ScreenPx;
        bool clipped = GetClipCursor(out var clip) &&
            (clip.Right - clip.Left < screen.Right - screen.Left || clip.Bottom - clip.Top < screen.Bottom - screen.Top);
        if (!hidden && !clipped) return;

        // Vordergrund holen (die Sperre umgehen: an den Eingabe-Thread des Spiels hängen)
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            if (!SetForegroundWindow(hwnd))
            {
                mouse_event(0x0001 /* MOVE */, 0, 0, 0, UIntPtr.Zero); // eigene Eingabe -> Vordergrund erlaubt
                SetForegroundWindow(hwnd);
            }
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }

        ClipCursor(IntPtr.Zero);
        // Maus mitten aufs Dock (über dem Spiel bliebe sie unsichtbar)
        var home = HomeRect(hwnd);
        SetCursorPos((home.Left + home.Right) / 2, (home.Top + home.Bottom) / 2);
        _peekUntil = DateTime.Now.AddSeconds(3);
    }

    // ---------- Startmenü über das Dock schieben ----------
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private WinEventDelegate? _winEventProc;   // Referenz halten (GC)
    private IntPtr _winEventHook;

    private static readonly string[] ShellFlyoutHosts = { "startmenuexperiencehost", "searchhost", "shellexperiencehost" };

    /// <summary>
    /// Windows legt Startmenü, Suche, Kalender usw. an die (versteckte) Windows-Taskleiste – also unter unser Dock.
    /// Ist das Dock sichtbar, rücken wir sie ein Stück nach oben, damit sie direkt über dem Dock sitzen.
    /// </summary>
    private HashSet<uint> _shellPids = new();
    private DateTime _shellPidsAt;

    /// <summary>Prozess-IDs von Startmenü, Suche und Infocenter (alle paar Sekunden neu ermittelt).</summary>
    private HashSet<uint> ShellPids()
    {
        if ((DateTime.Now - _shellPidsAt).TotalSeconds > 5)
        {
            _shellPidsAt = DateTime.Now;
            _shellPids = ShellFlyoutHosts
                .SelectMany(n => System.Diagnostics.Process.GetProcessesByName(n))
                .Select(p => (uint)p.Id).ToHashSet();
        }
        return _shellPids;
    }

    private void LiftShellFlyout(IntPtr fg, Native.RECT dock)
    {
        // Ältere Windows-Versionen: Startmenü-Fenster direkt verschieben (bei neueren legt Windows die Position selbst fest)
        if (Visibility != Visibility.Visible || !App.Config.LiftStartMenu) return;
        try
        {
            // Nur wenn gerade eines davon im Vordergrund ist (Startmenü/Suche/Infocenter offen)
            GetWindowThreadProcessId(fg, out var fgPid);
            var pids = ShellPids();
            if (!pids.Contains(fgPid)) return;

            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            int gap = (int)Math.Round(App.Config.ShellFlyoutGap * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY);
            int wantedBottom = dock.Top - gap;

            // Das Startmenü besteht bei neueren Windows-Versionen aus mehreren Fenstern -> alle sichtbaren verschieben
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;

                GetWindowRect(h, out var r);
                int height = r.Bottom - r.Top;
                if (height <= 0 || height > screen.Height - 60) return true;          // Vollbild-Shellfenster nicht anfassen
                if (r.Right <= screen.Left || r.Left >= screen.Right) return true;     // anderer Bildschirm
                if (r.Bottom <= wantedBottom || r.Top > dock.Bottom) return true;      // liegt schon darüber / gar nicht dort
                // nur verschieben, nicht skalieren
                SetWindowPos(h, IntPtr.Zero, r.Left, r.Top - (r.Bottom - wantedBottom), 0, 0, 0x0001 | 0x0004 | 0x0010);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private void AutoHideTick()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Immer mit der normalen Position des Docks rechnen – auch wenn es gerade unten aus dem Bild gefahren ist
        var dock = HomeRect(hwnd);

        // 1) Liegt ein Fenster über dem Dock? Geprüft wird die tatsächliche Stapelreihenfolge,
        //    nicht das aktive Fenster – ein Klick auf dem zweiten Monitor ändert also nichts.
        // Nur einmal pro Durchlauf suchen (die Suche geht alle Fenster durch)
        var top = TopWindowAtDock(dock) is { } t && !IsShellWindow(t) ? t : IntPtr.Zero;
        _covered = App.Config.AutoHideMode switch
        {
            "Never" => false,
            "FullScreen" => top != IntPtr.Zero && IsFullScreen(top, ScreenPx),
            _ => top != IntPtr.Zero,
        };
        if (IsPrimary) LiftShellFlyout(GetForegroundWindow(), dock);
        // Hintergrund pausieren, wenn man ihn nicht sieht: maximiert, Vollbild oder fast bildschirmfüllend
        {
            // pro Bildschirm: dieses Dock prüft seinen eigenen Bildschirm
            var scr = ScreenPx;
            bool hidden = top != IntPtr.Zero && (IsZoomed(top) || IsFullScreen(top, scr) || CoversMostOfScreen(top, scr));
            if (IsPrimary) _native?.Renderer?.SetPaused(hidden);
        }

        // 2) Maus am unteren Bildschirmrand oder auf dem Dock / einem offenen Fenster davon -> zeigen
        var p = System.Windows.Forms.Cursor.Position;
        var screen = ScreenPx;
        // Über Vollbild-Programmen (Spiele im Fenster-Vollbild, Videos) nicht am Rand einblenden – sonst springt
        // das Dock im Spiel hoch, sobald die Maus nach unten fährt. Die Windows-Taste holt es trotzdem.
        bool fullScreenTop = top != IntPtr.Zero && IsFullScreen(top, screen);
        bool atEdge = App.Config.RevealAtEdge && (!fullScreenTop || App.Config.RevealOverFullScreen)
            && p.Y >= screen.Bottom - 2 && p.Y < screen.Bottom && p.X >= screen.Left && p.X < screen.Right;
        bool overDock = _shown && p.X >= dock.Left && p.X <= dock.Right && p.Y >= dock.Top - 4 && p.Y <= dock.Bottom + 8;
        bool inUse = overDock || _flyout != null || ModernMenu.AnyOpen || Ui.MenuOpen;
        if (atEdge || inUse) _peekUntil = DateTime.Now.AddMilliseconds(700);

        bool show = !_covered || DateTime.Now < _peekUntil || IsFixed;
        SetDockShown(show);
    }

    /// <summary>Fläche des Docks an seinem normalen Platz (Bildschirm-Pixel).</summary>
    private Native.RECT HomeRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var cur);
        int h = cur.Bottom - cur.Top;
        var screen = ScreenPx;
        int top = screen.Bottom - h - (int)Math.Round(App.Config.BottomMargin * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY);
        return new Native.RECT { Left = cur.Left, Right = cur.Right, Top = top, Bottom = top + h };
    }

    // ---------- Rein- und Rausfahren ----------
    private volatile bool _shown = true;      // Ziel: sichtbar?
    private double _slidePos;        // 0 = an seinem Platz, 1 = ganz unten aus dem Bild
    private double _slideFrom;
    private DateTime _slideStart;
    private bool _sliding;

    /// <summary>Dock nach unten aus dem Bild fahren bzw. wieder hereinfahren (mit Animation).</summary>
    private void SetDockShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        DockShown[ScreenName] = show;
        if (!show) Ui.CloseOpenMenu(); // Dock fährt weg: sein Menü mitnehmen
        if (show)
        {
            if (Visibility != Visibility.Visible)
            {
                _slidePos = 1;
                ApplySlide();
                Visibility = Visibility.Visible;
            }
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        }
        _slideFrom = _slidePos;
        _slideStart = DateTime.Now;
        if (!_sliding)
        {
            _sliding = true;
            System.Windows.Media.CompositionTarget.Rendering += SlideFrame;
        }
    }

    private void SlideFrame(object? sender, EventArgs e)
    {
        // Rausfahren etwas schneller als Hereinfahren; ohne Animation sofort am Ziel
        bool animate = App.Config.SlideAnimation && !HasScreenBelow();
        double duration = animate ? Math.Max(1, App.Config.SlideDurationMs) * (_shown ? 1 : 0.8) : 1;
        double t = animate ? Math.Clamp((DateTime.Now - _slideStart).TotalMilliseconds / duration, 0, 1) : 1;
        // Rein: weich abbremsen. Raus: sanft anfahren und beschleunigen.
        double eased = _shown ? 1 - Math.Pow(1 - t, 3) : t * t;
        double target = _shown ? 0 : 1;
        _slidePos = _slideFrom + (target - _slideFrom) * eased;
        ApplySlide();
        if (t < 1) return;

        System.Windows.Media.CompositionTarget.Rendering -= SlideFrame;
        _sliding = false;
        if (!_shown) Visibility = Visibility.Hidden;
    }

    private void ApplySlide()
    {
        var s = ScreenDip;
        double home = s.Bottom - ActualHeight - App.Config.BottomMargin;
        double away = s.Bottom + 6;
        Top = home + (away - home) * _slidePos;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    /// <summary>
    /// Liegt direkt unter diesem Bildschirm ein weiterer? Dann würde das Dock beim Rausfahren dort auftauchen –
    /// es erscheint und verschwindet in dem Fall ohne Fahranimation.
    /// </summary>
    private bool HasScreenBelow()
    {
        var s = ScreenPx;
        GetWindowRect(new WindowInteropHelper(this).Handle, out var d);
        return System.Windows.Forms.Screen.AllScreens.Any(o => o.DeviceName != ScreenName && o.Bounds.Top == s.Bottom
            && o.Bounds.Left < d.Right && o.Bounds.Right > d.Left);
    }

    /// <summary>Bildschirm dieses Docks in Pixeln (fällt auf den Hauptbildschirm zurück).</summary>
    private System.Drawing.Rectangle ScreenPx =>
        (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == ScreenName)
         ?? System.Windows.Forms.Screen.PrimaryScreen!).Bounds;

    /// <summary>Bildschirm dieses Docks in WPF-Einheiten.</summary>
    private Rect ScreenDip
    {
        get
        {
            var b = ScreenPx;
            double k = Native.SystemScale;
            return new Rect(b.X / k, b.Y / k, b.Width / k, b.Height / k);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>
    /// Das oberste echte Fenster, das die Fläche des Docks überdeckt (null = keins, Desktop ist frei).
    /// Unsichtbare, minimierte, „versteckte“ (cloaked) Fenster, Overlays und GlassBar selbst zählen nicht.
    /// </summary>
    private static IntPtr? TopWindowAtDock(Native.RECT dock)
    {
        IntPtr? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            // „Desktop anzeigen“ (Win+D) minimiert unter Windows 11 nichts: Windows hebt den Desktop über alle Fenster.
            // Alles darunter (z. B. ein randloses Spiel) sieht man nicht – also hier aufhören, nichts deckt das Dock.
            if (Native.IsDesktopWindow(h)) return false;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == OwnPid) return true;

            long ex = GetWindowLongPtr(h, -20).ToInt64();
            if ((ex & (0x80 /* TOOLWINDOW */ | 0x20 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */)) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var cloaked, 4) == 0 && cloaked != 0) return true;

            GetWindowRect(h, out var r);
            // Rand von maximierten Fenstern (ragt 8 px über) abziehen, dann Überschneidung prüfen
            bool overlaps = r.Left + 8 < dock.Right && r.Right - 8 > dock.Left && r.Top + 8 < dock.Bottom && r.Bottom - 8 > dock.Top;
            if (!overlaps) return true;

            found = h;
            return false; // oberstes gefunden
        }, IntPtr.Zero);
        return found;
    }

    private static bool CoversMostOfScreen(IntPtr h, System.Drawing.Rectangle s)
    {
        GetWindowRect(h, out var r);
        long w = Math.Max(0, Math.Min(r.Right, s.Right) - Math.Max(r.Left, s.Left));
        long hh = Math.Max(0, Math.Min(r.Bottom, s.Bottom) - Math.Max(r.Top, s.Top));
        return w * hh >= 0.85 * s.Width * s.Height;
    }

    // Ergebnis je Fenster merken: Prozessnamen abzufragen ist teuer und lief sonst mehrmals pro Sekunde
    private static readonly Dictionary<IntPtr, bool> ShellCache = new();
    private static DateTime _shellCacheAt = DateTime.Now;

    /// <summary>Desktop, Hintergrund-Programme, Startmenü, Suche, Infocenter: Dock bleibt sichtbar.</summary>
    private static bool IsShellWindow(IntPtr h)
    {
        if ((DateTime.Now - _shellCacheAt).TotalSeconds > 30) { ShellCache.Clear(); _shellCacheAt = DateTime.Now; }
        if (ShellCache.TryGetValue(h, out var known)) return known;
        return ShellCache[h] = IsShellWindowUncached(h);
    }

    private static bool IsShellWindowUncached(IntPtr h)
    {
        try
        {
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow"
                or "Xaml_WindowedPopupClass" or "TopLevelWindowForOverflowXamlIsland") return true;

            GetWindowThreadProcessId(h, out var pid);
            var name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            return name.Contains("wallpaper") || name.Contains("lively") ||
                   name is "startmenuexperiencehost" or "searchhost" or "shellexperiencehost";
        }
        catch { return false; }
    }

    private static string Describe(IntPtr hwnd)
    {
        try
        {
            var cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, 256);
            GetWindowThreadProcessId(hwnd, out var pid);
            return $"{cls} / {System.Diagnostics.Process.GetProcessById((int)pid).ProcessName}";
        }
        catch { return "?"; }
    }

    internal static void Debug(string msg)
    {
        try { File.AppendAllText(Path.Combine(Config.Folder, "debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private System.Windows.Media.Brush? _startTileBrush;

    /// <summary>Aussehen des Start-Knopfs nach Einstellung (Kachel, Glas, nur Logo oder eigenes Bild).</summary>
    private void ApplyStartIcon()
    {
        _startTileBrush ??= StartTile.Background;
        var mode = App.Config.StartIcon;
        System.Windows.Media.ImageSource? img = null;
        if (mode == "Custom" && File.Exists(App.Config.StartIconPath))
            img = CachedImage(App.Config.StartIconPath);
        if (mode == "Custom" && img == null) mode = "Tile";

        StartImage.Source = img;
        StartImage.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        StartLogo.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
        StartTile.Background = mode switch
        {
            "Glass" => Theme.Get("Chip"),
            "Logo" or "Custom" => System.Windows.Media.Brushes.Transparent,
            _ => _startTileBrush,
        };
        // Eigenes Bild mit runden Ecken wie die anderen Symbole
        StartTile.ClipToBounds = mode == "Custom";
        StartImage.Clip = mode == "Custom"
            ? new System.Windows.Media.RectangleGeometry(new Rect(0, 0, StartTile.ActualWidth > 0 ? StartTile.ActualWidth : 30,
                StartTile.ActualHeight > 0 ? StartTile.ActualHeight : 30), 8, 8)
            : null;
        StartLogo.Width = StartLogo.Height = mode == "Logo" ? 20 : 16;

        // Kachelfarbe
        if (mode == "Tile" && App.Config.StartTileColor != "Blue" && ParseColor(App.Config.StartTileColor) is { } tc)
        {
            var light = System.Windows.Media.Color.FromRgb((byte)(tc.R + (255 - tc.R) * 0.35), (byte)(tc.G + (255 - tc.G) * 0.35), (byte)(tc.B + (255 - tc.B) * 0.35));
            StartTile.Background = new System.Windows.Media.LinearGradientBrush(light, tc, 45);
        }

        // Logo: Stil und Farbe
        var logoColor = ParseColor(App.Config.StartLogoColor) ?? System.Windows.Media.Colors.White;
        // Weißes Logo direkt auf hellem Glas wäre unsichtbar: im hellen Design dunkel zeichnen
        if (Theme.IsLight && mode is "Glass" or "Logo" && App.Config.StartLogoColor == "White")
            logoColor = System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A);
        string[] colorful = { "#F25022", "#7FBA00", "#00A4EF", "#FFB900" };
        int i = 0;
        foreach (var r in StartLogo.Children.OfType<System.Windows.Shapes.Rectangle>())
        {
            var style = App.Config.StartLogoStyle;
            var c = style == "Colorful" ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorful[i]) : logoColor;
            var brush = new System.Windows.Media.SolidColorBrush(c);
            r.Fill = style == "Outline" ? System.Windows.Media.Brushes.Transparent : brush;
            r.Stroke = style == "Outline" ? brush : null;
            r.StrokeThickness = style == "Outline" ? 1.3 : 0;
            r.RadiusX = r.RadiusY = style == "Rounded" ? 2.5 : 0;
            i++;
        }
    }

    // Bilder für Start-Knopf und Desktop-Stapel nur einmal laden (ApplySettings läuft bei jeder Änderung in allen Docks)
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> ImageCache = new();

    private static System.Windows.Media.ImageSource? CachedImage(string path)
    {
        string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (ImageCache.TryGetValue(key, out var cached)) return cached;
        System.Windows.Media.ImageSource? img = null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 128;
            bmp.EndInit();
            bmp.Freeze();
            img = bmp;
        }
        catch (Exception ex) { Log.Error("Eigenes Bild laden", ex); }
        return ImageCache[key] = img;
    }

    private static System.Windows.Media.ImageSource? CachedShellIcon(string spec)
    {
        string key = "shell:" + spec;
        if (!ImageCache.TryGetValue(key, out var icon)) ImageCache[key] = icon = ShellIcons.Load(spec);
        return icon;
    }

    /// <summary>Symbole für den Desktop-Stapel (Segoe Fluent Icons).</summary>
    public static readonly Dictionary<string, string> DesktopGlyphs = new()
    {
        ["Monitor"] = "\uE7F4", ["Folder2"] = "\uE8B7", ["Stack"] = "\uE8F1", ["Home"] = "\uE80F",
        ["Star"] = "\uE734", ["Heart"] = "\uEB51", ["Grid"] = "\uF0E2",
    };

    /// <summary>Desktop-Stapel: Windows-Ordner, ein Symbol auf farbiger Kachel oder ein eigenes Bild.</summary>
    private void ApplyDesktopIcon()
    {
        var c = App.Config;
        System.Windows.Media.ImageSource? img = null;
        if (c.DesktopIcon == "Custom" && File.Exists(c.DesktopIconPath))
            img = CachedImage(c.DesktopIconPath);
        // Windows-Symbol (z. B. aus imageres.dll): ohne Kachel, das Symbol hat seine eigene Form
        System.Windows.Media.ImageSource? sys = c.DesktopIcon == "Sys" && c.DesktopIconSource.Length > 0 ? CachedShellIcon(c.DesktopIconSource) : null;
        if (sys != null)
        {
            DesktopIcon.Visibility = Visibility.Collapsed;
            DesktopTile.Visibility = Visibility.Visible;
            DesktopTile.Background = System.Windows.Media.Brushes.Transparent;
            DesktopGlyph.Text = "";
            DesktopImage.Source = sys;
            DesktopImage.Stretch = System.Windows.Media.Stretch.Uniform;
            DesktopImage.Visibility = Visibility.Visible;
            return;
        }
        DesktopImage.Stretch = System.Windows.Media.Stretch.UniformToFill;
        bool glyph = DesktopGlyphs.TryGetValue(c.DesktopIcon, out var g);
        bool custom = img != null;
        DesktopIcon.Visibility = glyph || custom ? Visibility.Collapsed : Visibility.Visible;
        DesktopTile.Visibility = glyph || custom ? Visibility.Visible : Visibility.Collapsed;
        DesktopImage.Source = img;
        DesktopImage.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        DesktopGlyph.Text = glyph ? g : "";
        // Ohne Kachel darf das Symbol größer sein
        DesktopGlyph.FontSize = Math.Max(12, c.IconSize * (c.DesktopTileColor == "Transparent" ? 0.8 : 0.52));
        if (custom) { DesktopTile.Background = System.Windows.Media.Brushes.Transparent; return; }

        // Ohne farbige Kachel liegt das Symbol direkt auf dem Glas: Schriftfarbe des Designs (sonst weiß auf der Kachel)
        bool onGlass = c.DesktopTileColor is "Transparent" or "Glass";
        DesktopGlyph.Foreground = onGlass ? Theme.Get("Fg") : System.Windows.Media.Brushes.White;
        if (c.DesktopTileColor == "Transparent")
            DesktopTile.Background = System.Windows.Media.Brushes.Transparent;
        else if (c.DesktopTileColor == "Glass")
            DesktopTile.Background = Theme.Get("Chip");
        else
        {
            var tc = ParseColor(c.DesktopTileColor) ?? System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);
            var light = System.Windows.Media.Color.FromRgb((byte)(tc.R + (255 - tc.R) * 0.35), (byte)(tc.G + (255 - tc.G) * 0.35), (byte)(tc.B + (255 - tc.B) * 0.35));
            DesktopTile.Background = new System.Windows.Media.LinearGradientBrush(light, tc, 45);
        }
    }

    /// <summary>"White", "Black", "Accent" (Windows-Akzentfarbe) oder #RRGGBB.</summary>
    private static System.Windows.Media.Color? ParseColor(string value)
    {
        try
        {
            return value switch
            {
                "White" => System.Windows.Media.Colors.White,
                "Black" => System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A),
                "Accent" => AccentColor(),
                _ => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value),
            };
        }
        catch { return null; }
    }

    private static System.Windows.Media.Color AccentColor()
    {
        var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    /// <summary>Unten mittig auf seinem Bildschirm, mit Abstand zum Rand.</summary>
    private void PlaceDock()
    {
        var s = ScreenDip;
        Left = s.Left + (s.Width - ActualWidth) / 2;
        ApplySlide();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);

    // ---------- Vergrößerung beim Drüberfahren (Mac-Dock) ----------
    private const double MaxScale = 1.25, Spread = 60;

    private IEnumerable<FrameworkElement> MagnifiedIcons()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (o is FrameworkElement { Tag: "mag", RenderTransform: System.Windows.Media.ScaleTransform } fe && fe.IsVisible)
                yield return fe;
            for (int i = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o) - 1; i >= 0; i--)
                stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(o, i));
        }
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        double mx = e.GetPosition(Root).X;
        foreach (var fe in MagnifiedIcons())
        {
            double cx;
            try { cx = fe.TransformToAncestor(Root).Transform(new Point(fe.ActualWidth / 2, 0)).X; }
            catch { continue; }
            double d = (mx - cx) / Spread;
            double scale = 1 + (MaxScale - 1) * Math.Exp(-d * d);
            var st = (System.Windows.Media.ScaleTransform)fe.RenderTransform;
            if (st.IsFrozen) fe.RenderTransform = st = st.Clone();
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            st.ScaleX = st.ScaleY = scale;
        }
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
        };
        foreach (var fe in MagnifiedIcons())
        {
            var st = (System.Windows.Media.ScaleTransform)fe.RenderTransform;
            if (st.IsFrozen) fe.RenderTransform = st = st.Clone();
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void UpdateDesktopButton() => ApplySettings();

    /// <summary>Übernimmt alle Einstellungen sofort (wird vom Einstellungsfenster aufgerufen).</summary>
    public void ApplySettings()
    {
        var c = App.Config;
        static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

        // Größe
        Resources["IconSize"] = c.IconSize;
        Resources["SlotSize"] = c.IconSize + 12;
        Resources["DotOpacity"] = c.ShowRunningDots ? 1.0 : 0.0;

        // Elemente
        StartButton.Visibility = Vis(c.ShowStartButton);
        SearchButton.Visibility = Vis(c.ShowSearchButton);
        TaskViewButton.Visibility = Vis(c.ShowTaskViewButton);
        DesktopButton.Visibility = Vis(c.ShowDesktopButton);
        PeekButton.Visibility = Vis(c.ShowPeekButton);
        ApplyDesktopIcon();
        OverflowButton.Visibility = Vis(c.ShowTray);
        TrayList.Visibility = Vis(c.ShowTray);
        QuickSettingsButton.Visibility = Vis(c.ShowQuickSettings);
        ClockButton.Visibility = Vis(c.ShowClock);
        DateText.Visibility = Vis(c.ShowDate);
        bool left = c.ShowStartButton || c.ShowSearchButton || c.ShowTaskViewButton;
        bool right = c.ShowTray || c.ShowQuickSettings || c.ShowClock || c.ShowWeather;
        Sep1.Visibility = Vis(left);
        Sep2.Visibility = Vis(c.ShowDesktopButton);
        Sep3.Visibility = Vis(right);
        RightArea.Visibility = Vis(right);
        UpdateMusicUi();
        if (c.ShowWeather && _weatherCity != WeatherService.EffectiveCity()) RefreshWeather();
        UpdateWeatherUi();
        UpdateWallpaper();

        Model.ScreenFilter = AppScreenFilter;

        ApplyStartIcon();

        // Verhalten
        _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(0, c.HoverDelayMs));
        UpdateClock();

        // Glas
        var hwnd = new WindowInteropHelper(this).Handle;
        Theme.ApplyGlass(this, hwnd, c.DockOpacityNow);
        foreach (var f in _folderCache.Values) f.ApplyGlass();

        PlaceDock();
    }

    private static bool IsFullScreen(IntPtr h) => IsFullScreen(h, System.Windows.Forms.Screen.PrimaryScreen!.Bounds);

    /// <summary>Vollbild: deckt den ganzen Bildschirm ab und ist kein normales maximiertes Fenster.</summary>
    private static bool IsFullScreen(IntPtr h, System.Drawing.Rectangle s)
    {
        GetWindowRect(h, out var r);
        bool coversScreen = r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
        if (!coversScreen) return false;
        long style = GetWindowLongPtr(h, -16).ToInt64();
        bool hasCaption = (style & 0xC00000) == 0xC00000;
        return !(IsZoomed(h) && hasCaption);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);

    // ---------- Infobereich: eigene Ansicht ohne Lautstärke/Netzwerk/Akku (die stecken in den Schnelleinstellungen) ----------
    private static readonly string[] SystemIcons =
        { NotificationArea.VOLUME_GUID, NotificationArea.NETWORK_GUID, NotificationArea.POWER_GUID };

    private static ListCollectionView TrayView(NotificationArea tray, bool pinned)
    {
        var view = new ListCollectionView(tray.TrayIcons)
        {
            Filter = o => o is NotifyIcon n && n.IsPinned == pinned && !n.IsHidden &&
                          !SystemIcons.Any(g => string.Equals(g.Trim('{', '}'), n.GUID.ToString(), StringComparison.OrdinalIgnoreCase)),
            IsLiveFiltering = true,
            IsLiveSorting = true,
        };
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsPinned));
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden)); // z. B. die versteckten Task-Manager-Symbole
        view.LiveSortingProperties.Add(nameof(NotifyIcon.PinOrder));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(NotifyIcon.PinOrder), System.ComponentModel.ListSortDirection.Ascending));
        return view;
    }

    // ---------- Uhr & Statussymbole ----------
    private int _tick;

    // ---------- Musik & Wetter ----------
    private MediaWatcher? _media;
    private readonly WeatherService _weather = new();
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private string? _weatherCity;
    private bool _weatherLoading;

    private void StartMusicAndWeather()
    {
        _media = new MediaWatcher(Dispatcher);
        _media.PropertyChanged += (_, _) => { UpdateMusicUi(); PushMediaToNative(); };
        _media.TimelineChanged += PushMediaToNative;
        _ = _media.StartAsync();

        _weatherTimer.Tick += (_, _) => RefreshWeather();
        _weatherTimer.Start();
        RefreshWeather();
        // läuft erst nach dem ersten Zeichnen des Docks (siehe Konstruktor), ebenso der Hintergrund
        UpdateWallpaper();
    }

    private void UpdateMusicUi()
    {
        bool show = App.Config.ShowMusic && MusicInDock && _media is { HasMedia: true };
        MusicPill.Visibility = SepMusic.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _media == null) return;

        MusicTitle.Text = _media.Title;
        MusicArtist.Text = _media.Artist;
        MusicPlayGlyph.Text = _media.IsPlaying ? "" : ""; // Pause / Abspielen
        MusicCover.Background = _media.Cover != null
            ? new System.Windows.Media.ImageBrush(_media.Cover) { Stretch = System.Windows.Media.Stretch.UniformToFill }
            : Theme.Get("Cover");
        MusicNoCover.Visibility = _media.Cover == null ? Visibility.Visible : Visibility.Collapsed;
        MusicPill.ToolTip = string.IsNullOrEmpty(_media.Artist) ? _media.Title : $"{_media.Title} – {_media.Artist}";
    }

    // ---------- Hintergrund „Still“ (nativ) ----------
    private DesktopMouseHook? _mouseHook;
    private NativeStill.NativeStillWindow? _native;
    /// <summary>Farben des laufenden Hintergrunds (für die Vorschaubilder in den Einstellungen).</summary>
    public NativeStill.StillPalette? NativePalette => _native?.Renderer?.Palette;
    private AudioSpectrum? _nativeAudio;

    /// <summary>Musik bzw. Wetter im Dock? (Nein, wenn der Hintergrund läuft und sie nur dort sein sollen.)</summary>
    private static bool MusicInDock => !App.Config.ShowWallpaper || App.Config.MusicPlacement != "Wallpaper";
    private static bool WeatherInDock => !App.Config.ShowWallpaper || App.Config.WeatherPlacement != "Wallpaper";

    private void UpdateWallpaper()
    {
        if (_media == null) return; // wird nach dem Start von Musik/Wetter erneut aufgerufen
        UpdateNativeWallpaper();
    }

    /// <summary>Den nativen Hintergrund öffnen oder schließen (je nach Einstellung „Darstellung“).</summary>
    private void UpdateNativeWallpaper()
    {
        // Hauptbildschirm: das Hauptdock; Nebenbildschirme: SecondaryWallpapers (auch ohne Dock dort)
        if (!IsPrimary) return;
        bool want = App.Config.ShowWallpaper;
        NativeStill.StillRenderer.ApplyEffect(App.Config);
        if (want && _native == null)
        {
            NativeStill.NativeStillStore.Load();
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == ScreenName);
            try { _native = new NativeStill.NativeStillWindow(screen); }
            catch (Exception ex) { Debug("Still nativ: " + ex.Message); return; }
            var r = _native.Renderer!;
            r.Bind(NativeStill.NativeStillStore.Values);
            _nativeAudio = new AudioSpectrum();
            _nativeAudio.Start();
            var audio = _nativeAudio;
            r.SetAudioSource(() => audio.Read());
            var nw = _native;
            if (IsPrimary)
            {
                // Maus an den Hintergrund: Bewegung immer (Strömung, Hover), Klicks nur über Pillen/Karten –
                // die gehören dann dem Hintergrund und nicht dem Desktop.
                // Ein Hook für alle Bildschirme: Strömung und Leuchten überall, Pillen auf Nebenbildschirmen je nach Einstellung
                _mouseHook = new DesktopMouseHook(NativeStill.NativeStillWindow.Route,
                    () => false, () => NativeStill.NativeStillWindow.AnyOverUi, () => NativeStill.NativeStillWindow.AnyOverUi,
                    (x, y) => Dispatcher.BeginInvoke(() => DesktopShell.ShowDesktopMenu(x, y)),
                    on => { if (!on) NativeStill.NativeStillWindow.RouteLeave(); }, swallowUiClicks: true, allScreens: true,
                    captured: () => NativeStill.NativeStillWindow.AnyCaptured);
                r.Controls = new NativeStill.Ui.MediaControls
                {
                    PlayPause = () => Dispatcher.BeginInvoke(() => _media?.PlayPause()),
                    Next = () => Dispatcher.BeginInvoke(() => _media?.Next()),
                    Previous = () => Dispatcher.BeginInvoke(() => _media?.Previous()),
                    Seek = s => Dispatcher.BeginInvoke(() => _media?.Seek(s)),
                    Volume = (dir, shown) => Dispatcher.BeginInvoke(() => { if (ChangeMusicVolume(dir) is { } v) shown(v); }),
                    ToggleMute = shown => Dispatcher.BeginInvoke(() =>
                    {
                        if (_media is { HasMedia: true } m && AppVolume.ToggleMute(m.App) is { } r) shown(r.Muted, r.Volume);
                    }),
                };
                NativeStill.StillModules.Register(r, Dispatcher);
                PushMediaToNative();
            }
        }
        else if (!want && _native != null)
        {
            _mouseHook?.Dispose(); _mouseHook = null;
            _native.Dispose(); _native = null;
        }
        if (!want) { _nativeAudio?.Dispose(); _nativeAudio = null; }
        var shared = _nativeAudio;
        NativeStill.SecondaryWallpapers.Update(want, shared != null ? () => shared.Read() : null, _native?.Renderer?.Controls);
        PushMediaToNative();
        if (_native?.Renderer is { } rr)
        {
            rr.Fps = (int)Math.Clamp(App.Config.WallpaperFps, 10, 60);
            PushWeatherToNative();
        }
    }

    private int _coverVersion;
    private System.Windows.Media.ImageSource? _lastCover;
    private byte[]? _coverBgra;
    private int _coverW, _coverH;

    /// <summary>Musik an den nativen Hintergrund (Titel, Cover als Pixel, Position) und Albumfarben an die Palette.</summary>
    private void PushMediaToNative()
    {
        if (!IsPrimary || _media is not { } m) return;
        var all = AllNativeRenderers();
        if (all.Count == 0) return;
        if (!ReferenceEquals(m.Cover, _lastCover))
        {
            _lastCover = m.Cover;
            _coverBgra = null; _coverW = _coverH = 0;
            if (m.Cover is System.Windows.Media.Imaging.BitmapSource src)
            {
                try
                {
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                    _coverW = conv.PixelWidth; _coverH = conv.PixelHeight;
                    _coverBgra = new byte[_coverW * _coverH * 4];
                    conv.CopyPixels(_coverBgra, _coverW * 4, 0);
                }
                catch { _coverBgra = null; _coverW = _coverH = 0; }
            }
            _coverVersion++;
        }
        // Musik nur im Dock: Der Hintergrund bekommt keinen Titel und blendet seine Pille aus
        bool onWall = App.Config.ShowMusic && App.Config.MusicPlacement != "Dock";
        var state = new NativeStill.Ui.MediaState
        {
            Title = m.HasMedia && onWall ? m.Title : "", Artist = m.Artist, Album = m.Album, App = m.App,
            CoverBgra = _coverBgra, CoverWidth = _coverW, CoverHeight = _coverH, CoverVersion = _coverVersion,
            Playing = m.IsPlaying, Paused = m.IsPaused, Position = m.Position, Duration = m.Duration, Stamp = m.PositionAt,
            CanSeek = m.HasMedia && m.CanSeek, SeekGeneration = m.SeekGeneration,
        };
        var v = NativeStill.NativeStillStore.Values;
        bool visible = onWall && v.Bool("showPill") && m.HasMedia && (m.IsPlaying || m.IsPaused);
        foreach (var r in all)
        {
            r.SetMedia(state);
            r.Palette.SetAlbum(_coverBgra, _coverW, _coverH, v.Bool("followAlbum"), v.Num("tintStrength"), visible);
        }
    }

    /// <summary>Hauptbildschirm und alle Nebenbildschirme.</summary>
    private List<NativeStill.StillRenderer> AllNativeRenderers()
    {
        var list = new List<NativeStill.StillRenderer>();
        if (_native?.Renderer is { } p) list.Add(p);
        list.AddRange(NativeStill.SecondaryWallpapers.Renderers);
        return list;
    }

    private void PushWeatherToNative()
    {
        if (!IsPrimary) return;
        bool show = App.Config.ShowWeather && App.Config.WeatherPlacement != "Dock" && _weather.HasData;
        if (_weather.HasData) NativeStill.StillModules.SetWeatherCode(_weather.Code);
        var mood = show && NativeStill.NativeStillStore.Values.Bool("weatherMood")
            ? NativeStill.StillPalette.MoodForWeather(_weather.Code, _weather.IsDay) : null;
        foreach (var r in AllNativeRenderers())
        {
            r.SetWeather(show ? $"{Math.Round(_weather.Temperature)}°" : null, show ? _weather.Symbol : null, show ? _weather.Place : null);
            if (mood is { } m) r.Palette.SetMood(m.warm, m.sat, m.light, m.speed);
            else r.Palette.ClearMood();
        }
    }

    /// <summary>Anordnen-Modus von „Still“ öffnen (Pillen und Uhr verschieben).</summary>
    public void EditWallpaperLayout() => NativeStill.StillModules.EditLayout(_native?.Renderer);

    private void Music_Click(object s, MouseButtonEventArgs e) => _media?.PlayPause();
    private void MusicPlay_Click(object s, MouseButtonEventArgs e) { e.Handled = true; _media?.PlayPause(); }
    private void MusicNext_Click(object s, MouseButtonEventArgs e) { e.Handled = true; _media?.Next(); }
    private void MusicPrev_Click(object s, MouseButtonEventArgs e) { e.Handled = true; _media?.Previous(); }

    private async void RefreshWeather()
    {
        if (!App.Config.ShowWeather || _weatherLoading) return;
        _weatherLoading = true;
        _weatherCity = WeatherService.EffectiveCity();
        try { await _weather.RefreshAsync(); }
        finally { _weatherLoading = false; }
        UpdateWeatherUi();
    }

    private void UpdateWeatherUi()
    {
        PushWeatherToNative();
        bool show = App.Config.ShowWeather && WeatherInDock && (_weather.HasData || _weather.Error != null);
        WeatherButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        if (_weather.HasData)
        {
            WeatherGlyph.Text = _weather.Symbol;
            WeatherTemp.Text = $"{Math.Round(_weather.Temperature)}°";
            WeatherButton.ToolTip =
                $"{_weather.Place}: {_weather.Description}, {Math.Round(_weather.Temperature)}°\n" +
                L.F("Gefühlt {0}° · H {1}° · T {2}°", Math.Round(_weather.FeelsLike), Math.Round(_weather.High), Math.Round(_weather.Low)) + "\n" +
                L.F("Regenwahrscheinlichkeit {0} %", _weather.RainChance) + "\n" +
                L.F("Aktualisiert {0} · Klicken zum Aktualisieren", _weather.UpdatedAt.ToString("t", L.Culture));
        }
        else
        {
            WeatherGlyph.Text = "☁";
            WeatherTemp.Text = "–";
            WeatherButton.ToolTip = _weather.Error + (string.IsNullOrWhiteSpace(WeatherService.EffectiveCity())
                ? "\n" + L.T("Stadt in den GlassBar-Einstellungen eintragen.") : "");
        }
    }

    private void Weather_Click(object s, MouseButtonEventArgs e) => RefreshWeather();

    private void UpdateClock()
    {
        var now = DateTime.Now;
        // Uhrzeit, Datum und Tooltip in der Sprache der Oberfläche (24h bei Deutsch, wie Windows bei Englisch)
        TimeText.Text = L.En ? now.ToString(App.Config.ShowSeconds ? "T" : "t", L.Culture) : now.ToString(App.Config.ShowSeconds ? "HH:mm:ss" : "HH:mm");
        DateText.Text = L.En ? now.ToString("d", L.Culture) : now.ToString("dd.MM.yyyy");
        ClockButton.ToolTip = now.ToString(L.En ? "D" : "dddd, d. MMMM yyyy", L.Culture);
        if (++_tick % 10 == 0) UpdateStatusIcons();
        if (_tick % 5 == 1) RemoveDeadTrayIcons();
    }

    private void UpdateStatusIcons()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();
            NetGlyph.Text = up.Count == 0 ? ""                                              // kein Netz
                : up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) ? "" // WLAN
                : "";                                                                     // Ethernet

            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery))
                BatteryGlyph.Visibility = Visibility.Collapsed;
            else
            {
                int level = (int)Math.Round(ps.BatteryLifePercent * 10);
                level = Math.Clamp(level, 0, 10);
                BatteryGlyph.Text = (level == 10 ? '' : (char)(0xE850 + level)).ToString();
                BatteryGlyph.Visibility = Visibility.Visible;
                BatteryGlyph.ToolTip = $"Akku: {ps.BatteryLifePercent:P0}";
            }
        }
        catch { }
    }

    // ---------- Systemknöpfe ----------
    private void Start_Click(object s, MouseButtonEventArgs e)
    {
        // Nebenbildschirm: Startmenü dort öffnen (die Windows-Taste öffnet es immer auf dem Hauptbildschirm)
        if (!IsPrimary && StartMenu.Toggle(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2))) return;
        Native.WinKey();
    }
    private void Start_RightClick(object s, MouseButtonEventArgs e) { e.Handled = true; Native.WinKey('X'); }
    private void Search_Click(object s, MouseButtonEventArgs e) => Native.WinKey('S');
    private void TaskView_Click(object s, MouseButtonEventArgs e) => Native.WinKey('\t');
    private void QuickSettings_Click(object s, MouseButtonEventArgs e) => Native.WinKey('A');
    private void Clock_Click(object s, MouseButtonEventArgs e) => Native.WinKey('N');
    private void ShowDesktop_Click(object s, MouseButtonEventArgs e) => Native.WinKey('D');

    // ---------- Aufklapp-Fenster ----------
    private void BeginHover(string key, FrameworkElement anchor, Func<FlyoutBase> factory)
    {
        _hoverKey = key; _hoverAnchor = anchor; _hoverFactory = factory;
        if (_flyout is { Sticky: true }) return;              // per Klick geöffnetes Fenster nicht stören
        if (_flyout != null) { OpenFlyout(key, anchor, factory, false); return; } // schon offen -> sofort wechseln
        _hover.Start();
    }

    private void EndHover()
    {
        _hover.Stop();
        _hoverAnchor = null;
        _flyout?.AnchorLeft();
    }

    private void OpenFlyout(string key, FrameworkElement anchor, Func<FlyoutBase> factory, bool sticky)
    {
        if (_flyout != null && _flyoutKey == key)
        {
            _flyout.Anchor = anchor;
            if (sticky) _flyout.MakeSticky(); else _flyout.CancelClose();
            return;
        }
        CloseFlyout();

        var f = factory();
        Action? onDismissed = null;
        onDismissed = () =>
        {
            f.Dismissed -= onDismissed;
            if (_flyout != f) return;
            _flyout = null;
            _lastClosedKey = key;
            _lastClosedAt = DateTime.Now;
        };
        f.Dismissed += onDismissed;
        _flyout = f;
        _flyoutKey = key;
        f.ShowAt(anchor);
        if (sticky) f.MakeSticky();
    }

    private void ToggleFlyout(string key, FrameworkElement anchor, Func<FlyoutBase> factory)
    {
        _hover.Stop();
        if (_flyout is { Sticky: true } && _flyoutKey == key) { CloseFlyout(); return; }
        // Klick auf denselben Knopf hat das Fenster gerade erst geschlossen (Fokusverlust) -> nicht neu öffnen
        if (_lastClosedKey == key && (DateTime.Now - _lastClosedAt).TotalMilliseconds < 350) return;
        OpenFlyout(key, anchor, factory, sticky: true);
    }

    private void CloseFlyout()
    {
        _hover.Stop();
        _flyout?.SafeClose();
        _flyout = null;
    }

    // Ordner-Fenster werden nur einmal erzeugt und danach wiederverwendet -> öffnen sofort.
    private readonly Dictionary<string, FolderFlyout> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    private FolderFlyout FolderCache(string? path)
    {
        var key = path ?? "<desktop>";
        if (!_folderCache.TryGetValue(key, out var f))
        {
            f = new FolderFlyout(path) { KeepAlive = true };
            _folderCache[key] = f;
        }
        return f;
    }

    private void PrewarmDesktop()
    {
        // Einmal unsichtbar anzeigen, damit alle Icons schon geladen sind, bevor du drüberfährst.
        var f = FolderCache(null);
        f.Show();
        f.Hide();
    }

    // ---------- Desktop-Knopf ----------
    private void Desktop_MouseEnter(object s, MouseEventArgs e)
    {
        if (App.Config.HoverOpensFolders) BeginHover("desktop", (FrameworkElement)s, () => FolderCache(null));
    }

    private void Desktop_MouseLeave(object s, MouseEventArgs e) => EndHover();

    private void Desktop_Click(object s, MouseButtonEventArgs e) =>
        ToggleFlyout("desktop", (FrameworkElement)s, () => FolderCache(null));

    private void Desktop_RightClick(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseFlyout();
        Menu(
            ("Desktop im Explorer öffnen", "", () => Native.Launch("explorer.exe", $"\"{FolderFlyout.UserDesktop}\""), true),
            ("Desktop anzeigen (alle Fenster minimieren)", "", () => Native.WinKey('D'), true),
            (DesktopShell.IconsHidden() ? "Desktopsymbole einblenden" : "Desktopsymbole ausblenden", "", DesktopShell.ToggleDesktopIcons, true),
            null,
            ("Desktop-Knopf ausblenden", "", () => { App.Config.ShowDesktopButton = false; App.Config.Save(); UpdateDesktopButton(); }, true));
    }

    // ---------- App-Knöpfe ----------
    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void App_MouseEnter(object s, MouseEventArgs e)
    {
        if (Ctx<AppButton>(s) is not { } b) return;
        var el = (FrameworkElement)s;
        if (b.IsFolder && App.Config.HoverOpensFolders)
            BeginHover("folder:" + b.Key, el, () => FolderCache(b.Pin!.Target));
        else if (b.IsRunning && !b.IsFolder && App.Config.WindowPreviews)
            BeginHover("preview:" + b.Key, el, () => new PreviewFlyout(b));
        else
        {
            _hover.Stop();
            _flyout?.AnchorLeft();
        }
    }

    private void App_MouseLeave(object s, MouseEventArgs e) => EndHover();

    private void App_MouseDown(object s, MouseButtonEventArgs e)
    {
        _justDragged = false;
        _dragStart = e.GetPosition(this);
        _dragButton = Ctx<AppButton>(s);
    }

    private void App_MouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragButton is not { } b) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;

        _dragStart = null;
        _dragButton = null;
        _justDragged = true;
        CloseFlyout();
        DragDrop.DoDragDrop((DependencyObject)s, new DataObject(DragFormat, b), DragDropEffects.Move);
    }

    private void App_MouseUp(object s, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (Ctx<AppButton>(s) is not { } b) return;
        var el = (FrameworkElement)s;

        switch (e.ChangedButton)
        {
            case MouseButton.Left when _justDragged:
                _justDragged = false;
                return;

            case MouseButton.Left when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                NewInstance(b);
                return;

            case MouseButton.Left when b.IsFolder:
                ToggleFlyout("folder:" + b.Key, el, () => FolderCache(b.Pin!.Target));
                return;

            case MouseButton.Left:
                CloseFlyout();
                ActivateApp(b);
                return;

            case MouseButton.Middle:
                NewInstance(b);
                return;

            case MouseButton.Right:
                e.Handled = true;
                CloseFlyout();
                _ = AppMenuAsync(b);
                return;
        }
    }

    /// <summary>Klick-Verhalten wie bei Windows.</summary>
    private static void ActivateApp(AppButton b)
    {
        var wins = b.Windows;
        if (wins.Count == 0)
        {
            if (b.LaunchPath is { } path) Native.Launch(path);
            return;
        }
        if (wins.Count == 1)
        {
            var w = wins[0];
            if (w.State == ApplicationWindow.WindowState.Active && !w.IsMinimized) w.Minimize();
            else w.BringToFront();
            return;
        }
        // Mehrere Fenster: reihum durchschalten
        int i = wins.ToList().FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
        wins[(i + 1) % wins.Count].BringToFront();
    }

    private static void NewInstance(AppButton b)
    {
        if (b.LaunchPath is { } path) Native.Launch(path);
    }

    /// <summary>
    /// Win+1…9: das n-te Programm im Dock – im Dock des Bildschirms, auf dem das aktive Fenster liegt.
    /// Wie ein Klick (öffnen, nach vorne holen, minimieren, reihum); mit Umschalt eine neue Instanz.
    /// </summary>
    private static void OpenByNumber(int index, bool newInstance)
    {
        string screen = ScreenOf.Name(GetForegroundWindow());
        var dock = App.Instance.Docks.FirstOrDefault(d => d.ScreenName == screen) ?? App.Instance.Bar;
        if (dock == null || index >= dock.Model.Buttons.Count) return;
        var b = dock.Model.Buttons[index];
        dock.CloseFlyout();
        if (newInstance) NewInstance(b);
        else if (b.IsFolder) Native.Launch(b.Pin!.Target);
        else ActivateApp(b);
    }

    // ---------- Mausrad ----------

    /// <summary>Mausrad über Musik oder Schnelleinstellungen: Lautstärke (Windows zeigt dazu seine Anzeige).</summary>
    private void Volume_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (!App.Config.WheelActions) return;
        e.Handled = true;
        byte key = e.Delta > 0 ? (byte)0xAF /* VK_VOLUME_UP */ : (byte)0xAE /* VK_VOLUME_DOWN */;
        WinKeyHook.Send(key, up: false);
        WinKeyHook.Send(key, up: true);
    }

    /// <summary>
    /// Mausrad über der Musik: Lautstärke des Programms, das gerade spielt (wie im Lautstärkemixer).
    /// Lässt sich keine Audio-Sitzung dazu finden, wie bisher die Systemlautstärke.
    /// </summary>
    private void Music_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (!App.Config.WheelActions) return;
        e.Handled = true;
        int dir = Math.Sign(e.Delta);
        var cv = ChangeMusicVolume(dir);
        if (cv is { } v)
        {
            ShowMusicVolume(v, $"{Math.Round(v * 100)} %");
        }
        else Volume_MouseWheel(s, e);
    }

    /// <summary>Mittelklick auf die Musik: das Musikprogramm stumm schalten bzw. wieder laut.</summary>
    private void Music_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _media is not { HasMedia: true } m) return;
        e.Handled = true;
        var tm = AppVolume.ToggleMute(m.App);
        if (tm is { } r)
            ShowMusicVolume(r.Muted ? 0 : r.Volume, r.Muted ? L.T("Stumm") : $"{Math.Round(r.Volume * 100)} %");
    }

    /// <summary>Kurz statt des Interpreten: Balken mit Prozent bzw. „Stumm“ (der Balken gleitet zum neuen Wert).</summary>
    private void ShowMusicVolume(float fraction, string text)
    {
        MusicArtist.Visibility = Visibility.Hidden;
        MusicVolume.Visibility = Visibility.Visible;
        MusicVolumeText.Text = text;
        MusicVolumeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(fraction, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() });
        _volumeFlash.Stop();
        _volumeFlash.Start();
    }

    private DispatcherTimer? _volumeFlashTimer;
    private DispatcherTimer _volumeFlash => _volumeFlashTimer ??= CreateVolumeFlash();
    private DispatcherTimer CreateVolumeFlash()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            MusicVolume.Visibility = Visibility.Collapsed;
            MusicArtist.Visibility = Visibility.Visible;
            UpdateMusicUi();
        };
        return t;
    }

    /// <summary>Lautstärke des Musikprogramms um eine Stufe (4 %) ändern; null = keine passende Audio-Sitzung.</summary>
    private float? ChangeMusicVolume(int dir) =>
        _media is { HasMedia: true } m && dir != 0 ? AppVolume.Change(m.App, dir * 0.04f) : null;

    /// <summary>Mausrad über einem Programm mit mehreren Fenstern: zwischen seinen Fenstern wechseln.</summary>
    private void App_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (!App.Config.WheelActions || Ctx<AppButton>(s) is not { Windows.Count: > 1 } b) return;
        e.Handled = true;
        var wins = b.Windows;
        int i = wins.ToList().FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
        int next = i < 0 ? 0 : (i + (e.Delta > 0 ? -1 : 1) + wins.Count) % wins.Count;
        wins[next].BringToFront();
    }

    /// <summary>Rechtsklick auf eine App: Sprungliste (zuletzt verwendet, Aufgaben) wie bei Windows, darunter die Dock-Befehle.</summary>
    private async Task AppMenuAsync(AppButton b)
    {
        IReadOnlyList<JumpCategory> cats = Array.Empty<JumpCategory>();
        if (!b.IsFolder)
        {
            var w = b.Windows.FirstOrDefault();
            string? lnk = b.Pin?.Path is { } p && !PackagedApps.IsShellPath(p) && p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? p : null;
            // Store-Apps haben oft keine AUMID am Fenster, und ohne laufendes Fenster gibt es gar keines
            string? aumid = string.IsNullOrEmpty(w?.AppUserModelID) ? b.PackagedAumid : w.AppUserModelID;
            string? exe = b.ExecutablePath;
            IntPtr hwnd = w?.Handle ?? IntPtr.Zero;
            try
            {
                var load = Task.Run(() => JumpList.Load(aumid, exe, lnk, hwnd));
                // Nicht ewig warten: lieber ohne Sprungliste als ein träges Menü
                if (await Task.WhenAny(load, Task.Delay(1200)) == load) cats = load.Result;
            }
            catch { /* ohne Sprungliste weiter */ }
        }

        var menu = new ContextMenu();
        foreach (var c in cats)
        {
            if (c.Items.Count == 0) continue;
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock
                {
                    Text = L.T(c.Name.Length == 0 ? JumpList.TasksDisplayName : c.Name),
                    FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.6,
                },
                IsHitTestVisible = false, Focusable = false,
            });
            foreach (var it in c.Items)
            {
                var mi = new MenuItem
                {
                    Header = new TextBlock { Text = it.Title, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis },
                    // Manche Apps (z. B. Spotify) liefern als Beschreibung nur einen internen Pfad – dann den Titel zeigen
                    ToolTip = string.IsNullOrEmpty(it.Description) || it.Description.Contains(":\\") || it.Description.Contains('!')
                        ? it.Title : it.Description,
                };
                if (it.Icon != null)
                    mi.Icon = new System.Windows.Controls.Image { Source = it.Icon, Width = 16, Height = 16 };
                var item = it;
                mi.Click += (_, _) => { try { item.Launch(); } catch { } };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
        }
        foreach (var entry in AppMenuEntries(b))
        {
            if (entry is not { } x) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = L.T(x.Item1), Tag = x.Item2, IsEnabled = x.Item4 };
            mi.Click += (_, _) => x.Item3();
            menu.Items.Add(mi);
        }
        Ui.Open(menu);
    }

    private List<(string, string, Action, bool)?> AppMenuEntries(AppButton b)
    {
        var items = new List<(string, string, Action, bool)?>();

        if (b.IsFolder)
            items.Add(("Im Explorer öffnen", "", () => Native.Launch(b.Pin!.Path), true));
        else if (b.LaunchPath != null)
            items.Add((b.IsRunning ? "Neues Fenster" : "Öffnen", "", () => NewInstance(b), true));

        if (b.Pin != null)
        {
            if (b.Pin.Aumid == null) // Store-Apps haben keinen zugänglichen Speicherort
                items.Add(("Dateispeicherort öffnen", "",
                    () => Native.Launch("explorer.exe", $"/select,\"{b.Pin.Target}\""), true));
            items.Add(("Von Taskleiste lösen", "", () => Model.Unpin(b.Pin), true));
        }
        else if (b.PinPath is { } pinPath)
            items.Add(("An Taskleiste anheften", "", () => Model.AddPin(pinPath), true));

        if (b.IsRunning)
        {
            items.Add(null);
            items.Add((b.WindowCount > 1 ? "Alle Fenster schließen" : "Fenster schließen", "",
                () => { foreach (var w in b.Windows.ToList()) w.Close(); }, true));
        }
        return items;
    }

    // ---------- Ziehen & Ablegen ----------
    private void App_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move
                  : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link
                  : DragDropEffects.None;
        e.Handled = true;
    }

    private void App_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        if (Ctx<AppButton>(s) is not { } target) return;
        int index = target.Pin != null ? Model.Pins.IndexOf(target.Pin) : Model.Pins.Count;

        if (e.Data.GetData(DragFormat) is AppButton src && src != target)
        {
            if (src.Pin != null) Model.MovePin(src.Pin, index);
            else if (src.PinPath is { } pinPath) Model.AddPin(pinPath, index);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths) Model.AddPin(p, index++);
    }

    private void Root_DragOver(object s, DragEventArgs e) => App_DragOver(s, e);

    private void Root_Drop(object s, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(DragFormat) is AppButton src)
        {
            if (src.Pin != null) Model.MovePin(src.Pin, Model.Pins.Count - 1);
            else if (src.PinPath is { } pinPath) Model.AddPin(pinPath);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths) Model.AddPin(p);
    }

    // ---------- Infobereich (Tray) ----------
    private void Tray_MouseEnter(object s, MouseEventArgs e)
    {
        if (Ctx<NotifyIcon>(s) is not { } icon) return;
        var el = (FrameworkElement)s;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        icon.Placement = new NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        icon.IconMouseEnter(Native.CursorParam());
    }

    private void Tray_MouseLeave(object s, MouseEventArgs e) => Ctx<NotifyIcon>(s)?.IconMouseLeave(Native.CursorParam());

    // Ziehen zum Sortieren: im Dock umstellen, aus den ausgeblendeten Symbolen ins Dock ziehen (anheften) oder zurück (lösen)
    private Point? _trayDragStart;
    private NotifyIcon? _trayDragIcon;

    private void Tray_MouseMove(object s, MouseEventArgs e)
    {
        Ctx<NotifyIcon>(s)?.IconMouseMove(Native.CursorParam());
        if (e.LeftButton != MouseButtonState.Pressed || _trayDragStart is not { } start || _trayDragIcon is not { } icon) return;
        var d = e.GetPosition((IInputElement)s) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _trayDragStart = null;
        _trayDragIcon = null;
        DragDrop.DoDragDrop((DependencyObject)s, new DataObject(typeof(NotifyIcon), icon), DragDropEffects.Move);
    }

    private void Tray_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(NotifyIcon)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tray_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NotifyIcon)) is not NotifyIcon dragged) return;
        e.Handled = true;
        var target = Ctx<NotifyIcon>(s);
        if (target == null || ReferenceEquals(target, dragged)) return;
        if (target.IsPinned)
        {
            // links der Mitte: davor, rechts: dahinter
            var el = (FrameworkElement)s;
            bool after = e.GetPosition(el).X > el.ActualWidth / 2;
            dragged.Pin(target.PinOrder + (after ? 1 : 0));
        }
        else if (dragged.IsPinned) dragged.Unpin(); // ins Fenster der ausgeblendeten Symbole gezogen
        App.Instance.SaveTrayPins();
    }

    /// <summary>Auf freie Fläche des Infobereichs im Dock gezogen: hinten anheften.</summary>
    private void TrayList_Drop(object s, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(typeof(NotifyIcon)) is not NotifyIcon dragged) return;
        e.Handled = true;
        if (!dragged.IsPinned) { dragged.Pin(); App.Instance.SaveTrayPins(); }
    }

    private void Tray_MouseDown(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.ChangedButton == MouseButton.Right) return;
        if (e.ChangedButton == MouseButton.Left && Ctx<NotifyIcon>(s) is { } dragIcon)
        {
            _trayDragStart = e.GetPosition((IInputElement)s);
            _trayDragIcon = dragIcon;
        }
        Ctx<NotifyIcon>(s)?.IconMouseDown(e.ChangedButton, Native.CursorParam(), (int)Native.GetDoubleClickTime());
    }

    private void Tray_MouseUp(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _trayDragStart = null;
        _trayDragIcon = null;
        if (Ctx<NotifyIcon>(s) is not { } icon) return;

        if (Keyboard.Modifiers == ModifierKeys.Shift && e.ChangedButton == MouseButton.Right)
        {
            if (icon.IsPinned) icon.Unpin(); else icon.Pin();
            App.Instance.SaveTrayPins();
            return;
        }
        // Rechtsklick im Fenster der ausgeblendeten Symbole: offen lassen, solange das Menü des Programms offen ist
        if (e.ChangedButton == MouseButton.Right && _flyout is TrayFlyout tf && Window.GetWindow((DependencyObject)s) == tf)
            HoldTrayFlyoutDuringMenu(tf, icon.HWnd);
        icon.IconMouseUp(e.ChangedButton, Native.CursorParam(), (int)Native.GetDoubleClickTime());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private DispatcherTimer? _trayMenuWatch;

    /// <summary>
    /// Das Programm öffnet sein Menü und holt sich dafür den Fokus – das Fenster würde sich sonst schließen.
    /// Solange ein Menü offen ist (bzw. das Programm vorn ist), bleibt es offen.
    /// </summary>
    private void HoldTrayFlyoutDuringMenu(TrayFlyout tf, IntPtr iconHwnd)
    {
        tf.SuppressClose = true;
        GetWindowThreadProcessId(iconHwnd, out var appPid);
        var started = DateTime.Now;
        _trayMenuWatch?.Stop();
        _trayMenuWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (_, _) =>
        {
            var age = (DateTime.Now - started).TotalMilliseconds;
            if (age < 400) return; // dem Programm Zeit lassen, das Menü zu öffnen
            var menu = FindWindow("#32768", null);
            GetWindowThreadProcessId(GetForegroundWindow(), out var fgPid);
            bool menuOpen = (menu != IntPtr.Zero && IsWindowVisible(menu)) || (appPid != 0 && fgPid == appPid && age < 30000);
            if (menuOpen && age < 120000) return;
            _trayMenuWatch!.Stop();
            tf.SuppressClose = false;
        }, Dispatcher);
        _trayMenuWatch.Start();
    }

    private TrayFlyout? _trayFlyout;
    private DispatcherTimer? _overflowWatch;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

    /// <summary>
    /// Pfeil: öffnet bzw. schließt das Glas-Fenster mit den ausgeblendeten Symbolen.
    /// Ein Klick irgendwo anders schließt es ebenfalls.
    /// </summary>
    private void Overflow_Click(object s, MouseButtonEventArgs e)
    {
        if (_shell.NotificationArea is not { } tray) return;
        ToggleFlyout("tray", OverflowButton, () => _trayFlyout ??= new TrayFlyout(
            TrayView(tray, pinned: false), (DataTemplate)FindResource("TrayTemplate")) { KeepAlive = true });

        _overflowWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Input, (_, _) =>
        {
            if (_flyout is not TrayFlyout f) { _overflowWatch!.Stop(); return; }
            bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!pressed || f.SuppressClose) return;
            if (IsCursorOver(OverflowButton) || IsCursorOver(f)) return;
            CloseFlyout();
        }, Dispatcher);
        _overflowWatch.Start();
    }

    /// <summary>
    /// Entfernt „Geister-Symbole“: Programme, die beendet wurden, ohne ihr Symbol abzumelden
    /// (z. B. jeder frühere Task-Manager). Windows räumt diese auch selbst auf.
    /// </summary>
    private void RemoveDeadTrayIcons()
    {
        if (_shell.NotificationArea is not { } tray) return;
        foreach (var icon in tray.TrayIcons.ToList())
            if (icon.HWnd == IntPtr.Zero || !IsWindow(icon.HWnd))
                tray.TrayIcons.Remove(icon);
    }

    private static bool IsCursorOver(FrameworkElement el)
    {
        if (PresentationSource.FromVisual(el) == null) return false;
        var p = System.Windows.Forms.Cursor.Position;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y;
    }

    // ---------- Rechtsklick auf freie Fläche ----------
    private void Root_RightClick(object s, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        CloseFlyout();

        MenuWithUpdate(
            ("Einstellungen …", "", SettingsWindow.ShowSingle, true),
            null,
            ((IsFixed ? "✓ " : "") + (Screens > 1 ? "Dock auf diesem Bildschirm fixieren" : "Dock fixieren"), "", ToggleFixed,
                App.Config.AutoHideMode != "Never"),
            null,
            ("Task-Manager", "", () => Native.Launch("taskmgr.exe"), true),
            null,
            ("Ordner anheften …", "", PickFolder, true),
            ("Desktop-Verknüpfungen einzeln anheften", "", ImportDesktop, true),
            (App.Config.ShowDesktopButton ? "Desktop-Knopf ausblenden" : "Desktop-Knopf anzeigen", "",
                () => { App.Config.ShowDesktopButton = !App.Config.ShowDesktopButton; App.Config.Save(); UpdateDesktopButton(); }, true),
            ((Native.Autostart ? "✓ " : "") + "Mit Windows starten", "", () => Native.Autostart = !Native.Autostart, true),
            null,
            ("GlassBar beenden (Windows-Taskleiste zurück)", "", App.Instance.Quit, true));
    }

    /// <summary>Dock auf diesem Bildschirm fixiert: blendet sich nicht aus (Issue #3).</summary>
    private bool IsFixed => App.Config.FixedScreens.Contains(ScreenName);
    private static int Screens => System.Windows.Forms.Screen.AllScreens.Length;

    private void ToggleFixed()
    {
        if (!App.Config.FixedScreens.Remove(ScreenName)) App.Config.FixedScreens.Add(ScreenName);
        App.Config.Save();
    }

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Ordner für die Taskleiste wählen") };
        if (dlg.ShowDialog() == true) Model.AddPin(dlg.FolderName);
    }

    private void ImportDesktop()
    {
        int count = new[] { FolderFlyout.UserDesktop, FolderFlyout.PublicDesktop }.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .Count(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
        if (count == 0) return;
        var answer = MessageBox.Show(
            L.F("Wirklich alle {0} Verknüpfungen vom Desktop einzeln ans Dock anheften?", count) + "\n\n" +
            L.T("Du kannst sie danach per Rechtsklick → „Von Taskleiste lösen“ wieder entfernen."),
            L.T("Alle Desktop-Icons anheften"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        int before = Model.Pins.Count;
        foreach (var dir in new[] { FolderFlyout.UserDesktop, FolderFlyout.PublicDesktop }.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir)
                         .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName))
                Model.AddPin(f);

        MessageBox.Show(L.F("{0} Verknüpfung(en) angeheftet.", Model.Pins.Count - before), "GlassBar",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Hilfsfunktionen ----------
    /// <summary>Wie <see cref="Menu"/>, aber mit einem gefundenen Update (leise Suche, siehe Updater) ganz oben.</summary>
    private static void MenuWithUpdate(params (string Header, string Icon, Action Action, bool Enabled)?[] entries)
    {
        if (Updater.Available is { } update && update.Version > Updater.Current)
            entries = new (string, string, Action, bool)?[]
            {
                (L.F("Update auf Version {0} installieren …", update.Version.ToString(3)), "", () => SettingsWindow.ShowSingle("SecSystem"), true),
                null,
            }.Concat(entries).ToArray();
        Menu(entries);
    }

    private static void Menu(params (string Header, string Icon, Action Action, bool Enabled)?[] entries)
    {
        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            if (entry is not { } x) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = L.T(x.Header), Tag = x.Icon, IsEnabled = x.Enabled };
            mi.Click += (_, _) => x.Action();
            menu.Items.Add(mi);
        }
        Ui.Open(menu);
    }
}

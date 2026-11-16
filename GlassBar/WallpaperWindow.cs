using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassBar;

/// <summary>
/// Zeigt den Hintergrund „Still“ (deutsche Kopie) selbst an – hinter den Desktop-Symbolen, wie Wallpaper Engine.
/// Stellt dafür dieselben Schnittstellen bereit, die Wallpaper Engine einer Web-Tapete gibt:
/// Eigenschaften, Musikdaten samt Cover und Position, Audio-Pegel und Pausieren.
/// </summary>
public sealed class WallpaperWindow : Window
{
    public const string DefaultFolder =
        @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\projects\myprojects\still-de";

    private readonly WebView2 _web = new();
    private readonly MediaWatcher? _media;
    private AudioSpectrum? _audio;
    private readonly StringBuilder _audioText = new(1024);
    private bool _audioSilent;
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _ready, _paused;
    private IntPtr _hwnd, _renderHwnd;
    private TcpListener? _helper;

    public WallpaperWindow(MediaWatcher? media)
    {
        _media = media;
        Title = "GlassBar Hintergrund";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = System.Windows.Media.Brushes.Black;
        Content = _web;
        Left = Top = -30000; Width = 400; Height = 300;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        Loaded += async (_, _) => { AttachToDesktop(); await InitAsync(); };
        Closed += (_, _) => Cleanup();

        if (_media != null)
        {
            _media.PropertyChanged += (_, _) => PushMedia();
            _media.TimelineChanged += PushTimeline;
        }
        _audioTimer.Tick += (_, _) =>
        {
            if (!_ready || _paused || _audio == null || _web.CoreWebView2 == null) return;
            var v = _audio.Read();
            // Stille: nur einmal senden, danach nichts mehr (spart dem Hintergrund 30 Aufrufe pro Sekunde)
            bool silent = true;
            foreach (var x in v) if (x > 0.002f) { silent = false; break; }
            if (silent && _audioSilent) return;
            _audioSilent = silent;
            _audioText.Clear().Append('A');
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) _audioText.Append(',');
                _audioText.Append(v[i].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            // Nachricht statt Skript: muss nicht jedes Mal übersetzt werden und wartet auf keine Antwort
            try { _web.CoreWebView2.PostWebMessageAsString(_audioText.ToString()); } catch { }
        };
    }

    // ---------- Hinter die Desktop-Symbole ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    private void AttachToDesktop()
    {
        var progman = FindWindow("Progman", null);
        // Bittet den Explorer, die Ebene hinter den Desktop-Symbolen anzulegen
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // Fenster zum Kind machen
        long style = GetWindowLongPtr(_hwnd, -16).ToInt64();
        style = (style & ~0x80000000L & ~0x00C00000L & ~0x00040000L) | 0x40000000L; // -POPUP -CAPTION -THICKFRAME +CHILD
        SetWindowLongPtr(_hwnd, -16, new IntPtr(style));

        var innerWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (innerWorker != IntPtr.Zero && defView != IntPtr.Zero)
        {
            // Windows 11 24H2 und neuer: alles liegt in Progman – direkt unter die Symbole
            SetParent(_hwnd, progman);
            SetWindowPos(_hwnd, defView, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        else
        {
            // Ältere Versionen: das WorkerW hinter dem Fenster mit den Symbolen
            IntPtr worker = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    worker = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            SetParent(_hwnd, worker != IntPtr.Zero ? worker : progman);
        }
        // Desktop-Symbole ausgeblendet? Dann ganz nach vorn in die Desktop-Ebene: Der Hintergrund bekommt die Maus
        // direkt von Windows, Hover und Klicks verhalten sich wie auf einer normalen Webseite.
        _interactive = IconsHidden();
        if (_interactive)
        {
            var host = FindDefViewHost();
            if (host != IntPtr.Zero)
            {
                SetParent(_hwnd, host);
                SetWindowPos(_hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            else _interactive = false;
        }
        PlaceOnPrimary();
    }

    private bool _interactive;

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

    public bool IsInteractive => _interactive;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    /// <summary>Öffnet das normale Windows-Desktopmenü an dieser Stelle (Ansicht, Neu, Anpassen …).</summary>
    public static void ShowDesktopMenu(int x, int y)
    {
        var defView = FindWindowEx(FindDefViewHost(), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) return;
        SetForegroundWindow(defView);
        PostMessage(defView, 0x007B /* WM_CONTEXTMENU */, defView, new IntPtr((y << 16) | (x & 0xFFFF)));
    }

    /// <summary>Wurden die Desktop-Symbole umgeschaltet? Dann neu einhängen.</summary>
    public void CheckIconsChanged()
    {
        if (_hwnd != IntPtr.Zero && IconsHidden() != _interactive)
        {
            AttachToDesktop();
            Exec("window.__gbHold = false");
        }
    }

    /// <summary>Auf den Hauptbildschirm legen (Koordinaten relativ zur Desktop-Ebene).</summary>
    public void PlaceOnPrimary()
    {
        if (_hwnd == IntPtr.Zero) return;
        var p = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        SetWindowPos(_hwnd, IntPtr.Zero, p.X - v.X, p.Y - v.Y, p.Width, p.Height, 0x0004 | 0x0010 | 0x0040);
    }

    // ---------- WebView2 & Schnittstellen ----------

    private static string Shim(bool musicInWallpaper, bool weatherInWallpaper, bool settingsMode = false) => $$"""
        (() => {
          // Wie Wallpaper Engine: Listener für Audio und Medien
          const L = {};
          window.wallpaperRegisterAudioListener = f => L.audio = f;
          window.wallpaperRegisterMediaStatusListener = f => L.status = f;
          window.wallpaperRegisterMediaPropertiesListener = f => L.props = f;
          window.wallpaperRegisterMediaThumbnailListener = f => L.thumb = f;
          window.wallpaperRegisterMediaPlaybackListener = f => L.playback = f;
          window.wallpaperRegisterMediaTimelineListener = f => L.timeline = f;
          window.wallpaperMediaIntegration = { PLAYBACK_STOPPED: 0, PLAYBACK_PLAYING: 1, PLAYBACK_PAUSED: 2 };
          window.__gb = (k, d) => { const f = L[k]; if (f) try { f(d); } catch (e) {} };
          // Audio kommt als Nachricht „A0.1,0.2,…“ (günstiger als ein Skriptaufruf)
          try {
            window.chrome.webview.addEventListener('message', (e) => {
              const d = e.data;
              if (typeof d === 'string' && d.charCodeAt(0) === 65) __gb('audio', d.slice(1).split(',').map(Number));
            });
          } catch (e) {}
          window.__gbProps = p => { const w = window.wallpaperPropertyListener; if (w && w.applyUserProperties) w.applyUserProperties(p); };
          window.__gbFps = n => {
            const w = window.wallpaperPropertyListener; if (w && w.applyGeneralProperties) w.applyGeneralProperties({ fps: n });
          };
          window.__gbPaused = v => { const w = window.wallpaperPropertyListener; if (w && w.setPaused) w.setPaused(v); };
          // GlassBar-Einstellung: Musik-Pille und Wetter im Hintergrund oder im Dock
          try {
            const k = 'still.settings.v2';
            const s = JSON.parse(localStorage.getItem(k) || '{}');
            s.showPill = {{(musicInWallpaper ? "true" : "false")}};
            s.weather = {{(weatherInWallpaper ? "true" : "false")}};
            localStorage.setItem(k, JSON.stringify(s));
          } catch (e) {}
          const css = (t) => document.addEventListener('DOMContentLoaded', () => {
            const st = document.createElement('style'); st.textContent = t; document.head.appendChild(st);
          });
          if ({{(settingsMode ? "true" : "false")}}) {
            // In den GlassBar-Einstellungen: nur das Einstellungs-Panel, immer offen
            css(`html, body { background: transparent !important; }
                 body > :not(.settings-wrap) { opacity: 0 !important; pointer-events: none !important; }
                 .s-trigger { display: none !important; }`);
            setInterval(() => {
              try {
                const p = document.querySelector('.settings');
                if (window.Still && Still.openSettings && (!p || !p.classList.contains('is-open'))) Still.openSettings();
                // Panel so groß wie möglich (es richtet sich sonst nach der kleinen Fensterhöhe)
                if (p) { p.style.zoom = 1; const z = Math.min((innerHeight - 24) / p.offsetHeight, (innerWidth - 24) / p.offsetWidth); if (z > 1.02) p.style.zoom = z.toFixed(3); }
              } catch (e) {}
            }, 400);
          } else {
            // Auf dem Desktop: Einstellungen leben jetzt in GlassBar – Knopf und Zahnräder ausblenden
            css(`.s-trigger, .tp-gear { display: none !important; } .ed-press { display: none !important; }`);
            const post = (m) => { try { window.chrome.webview.postMessage(m); } catch (e) {} };
            const isEmpty = (t) => !t || t === document.body || t === document.documentElement || t.id === 'field'
                                   || (t.classList && t.classList.contains('stage'));
            const editing = () => { try { return !!(window.Still && Still.layout && Still.layout.editing()); } catch (e) { return false; } };

            // Kein Anordnen mehr durch langes Gedrückthalten (nur noch über die GlassBar-Einstellungen):
            // die angefangene Halte-Geste gleich wieder abbrechen.
            window.addEventListener('pointerdown', (e) => {
              if (e.button === 0 && isEmpty(e.target) && !editing())
                setTimeout(() => window.dispatchEvent(new PointerEvent('pointermove', { clientX: e.clientX + 5000, clientY: e.clientY + 5000 })), 0);
            }, true);
            try {
              const g = JSON.parse(localStorage.getItem('still.guide.v1') || 'null') || { seen: {}, ignored: {}, shown: 0, nextAt: 0 };
              g.seen = g.seen || {}; g.seen.edit = 1; // Tipp zum Gedrückthalten nicht mehr zeigen
              localStorage.setItem('still.guide.v1', JSON.stringify(g));
            } catch (e) {}

            // Symbole sichtbar: Die Maus wird von GlassBar weitergereicht, Windows meldet dabei sofort
            // „Maus hat das Fenster verlassen“. Solange sie noch auf dem Desktop ist, diese Meldungen schlucken,
            // sonst klappen Karten (Wasser, Fokus …) gleich wieder zu.
            window.__gbHold = false;
            const held = new Set(); // Elemente, deren „Maus weg“ geschluckt wurde
            for (const t of ['mouseleave', 'mouseout', 'pointerleave', 'pointerout'])
              window.addEventListener(t, (e) => {
                if (!window.__gbHold || e.relatedTarget || !e.isTrusted) return;
                if (t === 'mouseleave' && e.target instanceof Element) held.add(e.target);
                e.stopImmediatePropagation();
              }, true);
            window.addEventListener('mouseenter', (e) => held.delete(e.target), true);
            // Wirklich woanders hin bewegt? Dann das geschluckte „Maus weg“ nachholen.
            const release = (to) => {
              for (const el of [...held]) if (!to || !el.contains(to)) {
                held.delete(el);
                el.dispatchEvent(new MouseEvent('mouseleave', { relatedTarget: to || null }));
                el.dispatchEvent(new PointerEvent('pointerleave', { relatedTarget: to || null }));
              }
            };
            window.addEventListener('mousemove', (e) => { if (held.size) release(e.target); }, true);
            window.__gbRelease = () => release(null);

            // GlassBar wissen lassen, ob die Maus über einer Pille/Karte ist (für den Rechtsklick)
            let ui = null;
            window.addEventListener('pointermove', (e) => {
              const now = !isEmpty(e.target);
              if (now !== ui) { ui = now; post(now ? 'ui:1' : 'ui:0'); }
            }, true);
            let ed = false;
            setInterval(() => { const now = editing(); if (now !== ed) { ed = now; post(now ? 'edit:1' : 'edit:0'); } }, 300);
          }
        })();
        """;

    /// <summary>Gemeinsame WebView2-Umgebung (Hintergrund und Einstellungen teilen sich den Speicher).</summary>
    private static Task<CoreWebView2Environment>? _env;
    public static Task<CoreWebView2Environment> SharedEnvironment() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.Folder, "WebView2"),
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));

    /// <summary>Skript für die Einstellungs-Ansicht in GlassBar.</summary>
    public static string SettingsShim()
    {
        var place = App.Config.InfoPlacement;
        return Shim(App.Config.ShowMusic && place != "Dock", App.Config.ShowWeather && place != "Dock", true);
    }

    /// <summary>Gespeicherte Einstellungen von „Still“ (ohne Zwischenspeicher) als JSON-Liste [[key, value], …].</summary>
    public const string StorageExportJs =
        "JSON.stringify(Object.keys(localStorage).filter(k => !/diag|clock|lyric|cache/i.test(k)).map(k => [k, localStorage.getItem(k)]))";

    public async Task<string> ExportStorageAsync()
    {
        if (_web.CoreWebView2 == null) return "[]";
        try { return JsonSerializer.Deserialize<string>(await _web.CoreWebView2.ExecuteScriptAsync(StorageExportJs)) ?? "[]"; }
        catch { return "[]"; }
    }

    /// <summary>Einstellungen aus dem Einstellungsfenster übernehmen und neu laden.</summary>
    public async void ImportStorageAndReload(string entries)
    {
        if (_web.CoreWebView2 == null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync($"(() => {{ for (const [k, v] of {entries}) localStorage.setItem(k, v); }})()"); }
        catch { }
        Reload();
    }

    /// <summary>Neu laden, damit Änderungen aus den Einstellungen übernommen werden.</summary>
    public void Reload()
    {
        if (_web.CoreWebView2 == null) return;
        _ready = false;
        _web.CoreWebView2.Reload();
    }

    private string? _shimId;

    private async Task InitAsync()
    {
        try
        {
            var folder = DefaultFolder;
            if (!Directory.Exists(folder)) return;

            var env = await SharedEnvironment();
            await _web.EnsureCoreWebView2Async(env);
            var cw = _web.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.AreDevToolsEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.SetVirtualHostNameToFolderMapping("still.glassbar", folder, CoreWebView2HostResourceAccessKind.Allow);
            await ApplyShimAsync();
            cw.WebMessageReceived += (_, e) =>
            {
                string? m = null;
                try { m = e.TryGetWebMessageAsString(); } catch { }
                if (m == "ui:1" || m == "ui:0") _overUi = m == "ui:1";
                else if (m == "edit:1" || m == "edit:0") _editing = m == "edit:1";
            };
            cw.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                FindRenderWindow();
                PushProperties();
                PushMedia();
            };
            cw.Navigate("https://still.glassbar/index.html");

            StartHelper();
            _audio = new AudioSpectrum();
            _audio.Start();
            _audioTimer.Start();
        }
        catch (Exception ex)
        {
            TaskbarWindow.Debug("Hintergrund: " + ex.Message);
        }
    }

    private async Task ApplyShimAsync()
    {
        var cw = _web.CoreWebView2;
        if (_shimId != null) cw.RemoveScriptToExecuteOnDocumentCreated(_shimId);
        var place = App.Config.InfoPlacement;
        _shimId = await cw.AddScriptToExecuteOnDocumentCreatedAsync(
            Shim(App.Config.ShowMusic && place != "Dock", App.Config.ShowWeather && place != "Dock"));
    }

    /// <summary>Nach Änderungen in den Einstellungen: Eigenschaften neu senden, bei Bedarf neu laden.</summary>
    public async void ApplySettings(bool placementChanged)
    {
        if (_web.CoreWebView2 == null) return;
        if (placementChanged)
        {
            await ApplyShimAsync();
            _ready = false;
            _web.CoreWebView2.Reload();
        }
        else PushProperties();
    }

    private static string J(string? s) => JsonSerializer.Serialize(s ?? "");

    /// <summary>
    /// Eine Einstellung von „Still“ live übernehmen (ohne Neuladen). Still speichert sie selbst.
    /// Gibt false zurück, wenn der Hintergrund (noch) nicht bereit ist.
    /// </summary>
    public bool SetStill(string key, object value)
    {
        if (!_ready || _web.CoreWebView2 == null) return false;
        Exec($"window.Still && window.Still.set({J(key)}, {JsonSerializer.Serialize(value)})");
        return true;
    }

    private volatile bool _overUi, _editing;
    /// <summary>Maus ist über einer Pille/Karte des Hintergrunds.</summary>
    public bool OverUi => _overUi;
    /// <summary>Der Hintergrund ist im Anordnen-Modus.</summary>
    public bool Editing => _editing;

    /// <summary>Anordnen-Modus von „Still“ öffnen (Pillen und Uhr verschieben).</summary>
    public void EditLayout() => Exec("window.Still && Still.layout && Still.layout.edit(true)");

    /// <summary>
    /// Die Maus kommt auf den Desktop oder verlässt ihn. Solange sie drauf ist, schluckt der Hintergrund die
    /// falschen „Maus weg“-Meldungen; beim Verlassen bekommt er eine echte, damit offene Karten zuklappen.
    /// </summary>
    public async void SetDesktopHover(bool onDesktop)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        if (_interactive) onDesktop = false;
        try { await _web.CoreWebView2.ExecuteScriptAsync(onDesktop ? "window.__gbHold = true" : "window.__gbHold = false; window.__gbRelease && window.__gbRelease()"); } catch { return; }
        if (!onDesktop && !_interactive && _renderHwnd != IntPtr.Zero)
            PostMessage(_renderHwnd, 0x02A3 /* WM_MOUSELEAVE */, IntPtr.Zero, IntPtr.Zero);
    }

    private void Exec(string js)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(js);
    }

    public void PushProperties()
    {
        var city = WeatherService.EffectiveCity();
        Exec($"__gbProps({{ focus: {{ value: {J(App.Config.WallpaperTasks)} }}, prayerplace: {{ value: {J(city)} }}, settingsbutton: {{ value: 'hidden' }} }}); __gbFps({(int)Math.Clamp(App.Config.WallpaperFps, 10, 60)})");
    }

    private void PushMedia()
    {
        var m = _media;
        if (m == null || !_ready) return;
        Exec($"__gb('status', {{ enabled: true }})");
        Exec($"__gb('props', {{ title: {J(m.Title)}, artist: {J(m.Artist)}, albumTitle: {J(m.Album)} }})");
        Exec($"__gb('thumb', {{ thumbnail: {J(m.CoverDataUrl)} }})");
        int state = m.IsPlaying ? 1 : m.IsPaused ? 2 : 0;
        Exec($"__gb('playback', {{ state: {state} }})");
        PushTimeline();
    }

    private void PushTimeline()
    {
        var m = _media;
        if (m == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Exec($"__gb('timeline', {{ position: {Math.Floor(m.Position).ToString(inv)}, duration: {m.Duration.ToString(inv)} }})");
    }

    /// <summary>Pausieren, wenn ein Fenster den ganzen Bildschirm verdeckt (spart Grafikleistung).</summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;
        Exec($"__gbPaused({(paused ? "true" : "false")})");
        if (paused)
        {
            // Erst nach kurzer Zeit schlafen legen, damit schnelles Hin- und Herwechseln nicht flackert
            _sleepTimer ??= CreateSleepTimer();
            _sleepTimer.Stop();
            _sleepTimer.Start();
        }
        else
        {
            _sleepTimer?.Stop();
            if (_web.Visibility != Visibility.Visible) _web.Visibility = Visibility.Visible;
        }
    }

    private DispatcherTimer? _sleepTimer;

    private DispatcherTimer CreateSleepTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { t.Stop(); Sleep(); };
        return t;
    }

    /// <summary>
    /// Verdeckt: Den Browser ganz schlafen legen. Gemessen sinkt die Last dadurch von ~55 % eines CPU-Kerns auf ~1 %
    /// (die Pause von „Still“ allein spart kaum etwas, weil der Browser trotzdem weiter Bilder erzeugt).
    /// Damit sichtbare Ränder nicht schwarz werden, bleibt das letzte Bild als Standbild stehen.
    /// </summary>
    private async void Sleep()
    {
        if (!_paused || _web.CoreWebView2 == null || _web.Visibility != Visibility.Visible) return;
        try
        {
            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            ms.Position = 0;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            Background = new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.Fill };
        }
        catch { }
        if (_paused) _web.Visibility = Visibility.Hidden;
    }

    // ---------- Wiedergabetasten (dieselbe Schnittstelle wie das Still-Hilfsprogramm) ----------

    private void StartHelper()
    {
        try
        {
            _helper = new TcpListener(IPAddress.Loopback, 39017);
            _helper.Start();
            _ = Task.Run(HelperLoop);
        }
        catch { _helper = null; } // Port belegt (z. B. das originale Hilfsprogramm läuft) – auch gut
    }

    private async Task HelperLoop()
    {
        while (_helper != null)
        {
            TcpClient client;
            try { client = await _helper.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buf = new byte[2048];
                        int n = stream.Read(buf, 0, buf.Length);
                        var first = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0];
                        var parts = first.Split(' ');
                        string method = parts.Length > 0 ? parts[0] : "", path = parts.Length > 1 ? parts[1] : "";
                        if (method == "POST" || method == "GET")
                        {
                            if (path == "/playpause") Dispatcher.BeginInvoke(() => _media?.PlayPause());
                            else if (path == "/next") Dispatcher.BeginInvoke(() => _media?.Next());
                            else if (path == "/prev") Dispatcher.BeginInvoke(() => _media?.Previous());
                        }
                        var body = path == "/ping" ? "still-media" : "ok";
                        var reply = "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                                    "Access-Control-Allow-Headers: *\r\nAccess-Control-Allow-Private-Network: true\r\n" +
                                    $"Content-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        var bytes = Encoding.ASCII.GetBytes(reply);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    catch { }
                }
            });
        }
    }

    // ---------- Mausklicks auf dem Desktop an den Hintergrund weitergeben ----------

    private void FindRenderWindow()
    {
        _renderHwnd = IntPtr.Zero;
        EnumChildWindows(_hwnd, (h, _) =>
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == "Chrome_RenderWidgetHostHWND") { _renderHwnd = h; return false; }
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Native.POINTPUB p);

    /// <summary>Vom globalen Maus-Hook: Ereignis an die Webseite weiterreichen.</summary>
    public void ForwardMouse(uint msg, int x, int y, int wheel)
    {
        if (!_ready || _paused || _interactive) return; // im direkten Modus bekommt der Hintergrund die Maus selbst
        if (_renderHwnd == IntPtr.Zero) FindRenderWindow();
        if (_renderHwnd == IntPtr.Zero) return;
        var pt = new Native.POINTPUB { X = x, Y = y };
        if (msg == 0x020A) // Mausrad: Bildschirmkoordinaten
        {
            PostMessage(_renderHwnd, msg, new IntPtr(wheel << 16), new IntPtr((y << 16) | (x & 0xFFFF)));
            return;
        }
        ScreenToClient(_renderHwnd, ref pt);
        IntPtr wParam = msg == 0x0201 ? new IntPtr(0x0001) : IntPtr.Zero; // MK_LBUTTON beim Drücken
        PostMessage(_renderHwnd, msg, wParam, new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF)));
    }

    private void Cleanup()
    {
        _audioTimer.Stop();
        _audio?.Dispose();
        try { _helper?.Stop(); } catch { }
        _helper = null;
    }
}

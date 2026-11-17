using System.Windows;
using System.Windows.Threading;
using ManagedShell;

namespace GlassBar;

public partial class App : Application
{
    private static Mutex? _mutex;
    private ShellManager? _shell;
    private TaskbarWindow? _bar;
    private bool _cleanedUp;

    public static Config Config { get; private set; } = new();
    public static App Instance => (App)Current;
    /// <summary>Dock auf dem Hauptbildschirm (steuert den Hintergrund).</summary>
    public TaskbarWindow? Bar => _docks.FirstOrDefault(d => d.IsPrimary) ?? _bar;
    /// <summary>Alle Docks (eines pro Bildschirm).</summary>
    public IReadOnlyList<TaskbarWindow> Docks => _docks;
    private readonly List<TaskbarWindow> _docks = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Läuft GlassBar schon? Dann dort die Einstellungen öffnen und selbst gleich wieder beenden.
        var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "GlassBar_OpenSettings");
        _mutex = new Mutex(true, "GlassBar_SingleInstance", out bool isNew);
        if (!isNew) { openSettings.Set(); Shutdown(); return; }
        ThreadPool.RegisterWaitForSingleObject(openSettings,
            (_, _) => Dispatcher.BeginInvoke(SettingsWindow.ShowSingle), null, Timeout.Infinite, false);

        DispatcherUnhandledException += OnCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Cleanup();

        // Höhere Priorität: Dock und Hintergrund sollen gleich nach der Anmeldung stehen und nie stocken
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
        Config = Config.Load();
        Native.EnableDarkMenus();

        var cfg = ShellManager.DefaultShellConfig;
        cfg.EnableTasksService = true;
        cfg.AutoStartTasksService = true;
        cfg.EnableTrayService = true;
        cfg.AutoStartTrayService = true;
        cfg.TaskIconSize = ManagedShell.Common.Enums.IconSize.Large;
        if (Config.PinnedTrayIcons is { Length: > 0 })
            cfg.PinnedNotifyIcons = Config.PinnedTrayIcons;

        _shell = new ShellManager(cfg);

        UpdateDocks();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplySettingsToDocks);

        // Windows-Taskleiste ausblenden (wird beim Beenden wiederhergestellt)
        _shell.ExplorerHelper.HideExplorerTaskbar = true;
    }

    /// <summary>
    /// Für jeden Bildschirm ein Dock (bzw. nur auf dem Hauptbildschirm, je nach Einstellung).
    /// Wird beim Start, bei geänderten Bildschirmen und nach den Einstellungen aufgerufen.
    /// </summary>
    public void UpdateDocks()
    {
        if (_shell == null || _cleanedUp) return;
        var wanted = System.Windows.Forms.Screen.AllScreens
            .Where(s => s.Primary || Config.DockOnAllScreens)
            .OrderByDescending(s => s.Primary) // Hauptdock zuerst
            .ToList();

        // Docks für nicht mehr gewünschte Bildschirme (oder geänderten Hauptbildschirm) schließen
        foreach (var d in _docks.ToList())
        {
            var s = wanted.FirstOrDefault(w => w.DeviceName == d.ScreenName);
            if (s == null || s.Primary != d.IsPrimary) { _docks.Remove(d); d.Close(); }
        }
        // fehlende Docks öffnen
        foreach (var s in wanted)
        {
            if (_docks.Any(d => d.ScreenName == s.DeviceName)) continue;
            var dock = new TaskbarWindow(_shell, s);
            _docks.Add(dock);
            dock.Show();
        }
        _bar = _docks.FirstOrDefault(d => d.IsPrimary);
        if (MainWindow == null || !_docks.Contains(MainWindow as TaskbarWindow)) MainWindow = _bar;
    }

    /// <summary>Einstellungen auf alle Docks anwenden.</summary>
    public void ApplySettingsToDocks()
    {
        UpdateDocks();
        foreach (var d in _docks) d.ApplySettings();
    }

    public void SaveTrayPins()
    {
        if (_shell?.NotificationArea is { } area)
        {
            Config.PinnedTrayIcons = area.PinnedNotifyIcons;
            Config.Save();
        }
    }

    public void Quit()
    {
        Cleanup();
        Shutdown();
    }

    private void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Config.Folder, "error.log"),
                $"[{DateTime.Now}] {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
        MessageBox.Show("GlassBar ist auf einen Fehler gestoßen und wird beendet.\n\n" + e.Exception.Message,
            "GlassBar", MessageBoxButton.OK, MessageBoxImage.Error);
        Quit();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try
        {
            NativeStill.SecondaryWallpapers.Shutdown();
            foreach (var d in _docks.ToList()) d.Close();
            _docks.Clear();
        }
        catch { }
        try
        {
            if (_shell != null)
            {
                _shell.ExplorerHelper.HideExplorerTaskbar = false;
                _shell.Dispose();
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }
}

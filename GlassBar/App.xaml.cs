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
    private bool _afterCrash;
    private int? _taskbarState;   // Zustand der Windows-Taskleiste vor GlassBar (siehe Config.TaskbarStateBeforeGlassBar)
    private readonly Queue<DateTime> _recentErrors = new();

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

        // Wächter-Prozess: wartet nur, bis GlassBar endet, und räumt nach einem Absturz auf (siehe Watchdog)
        if (e.Args.Contains("--watchdog", StringComparer.OrdinalIgnoreCase))
        {
            Config = Config.Load();
            L.Init(Config.Language);
            new Thread(() =>
            {
                string? message = null;
                try { Watchdog.Run(e.Args, m => message = m); } catch (Exception ex) { Log.Error("Wächter", ex); }
                Dispatcher.BeginInvoke(() =>
                {
                    if (message != null) MessageBox.Show(message, "GlassBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                });
            }) { IsBackground = true, Name = "GlassBar Wächter" }.Start();
            return;
        }
        _afterCrash = e.Args.Contains("--after-crash", StringComparer.OrdinalIgnoreCase);

        // Neustart (z. B. nach Sprachwechsel): erst warten, bis die alte Instanz beendet ist
        int wi = Array.FindIndex(e.Args, a => a.Equals("--wait-for", StringComparison.OrdinalIgnoreCase));
        if (wi >= 0 && wi + 1 < e.Args.Length && int.TryParse(e.Args[wi + 1], out int oldPid))
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }

        // Befehle für den Installer: laufendes GlassBar sauber beenden bzw. Autostart ein-/ausschalten
        var quit = new EventWaitHandle(false, EventResetMode.ManualReset, "GlassBar_Quit");
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            quit.Set();
            // warten, bis die laufende Instanz weg ist (sie gibt dabei die Taskleiste zurück)
            for (int i = 0; i < 100 && (Mutex.TryOpenExisting("GlassBar_SingleInstance", out var m) || Mutex.TryOpenExisting("GlassBar_Watchdog", out m)); i++)
            { m.Dispose(); Thread.Sleep(100); }
            quit.Reset();
            Shutdown();
            return;
        }
        int ai = Array.FindIndex(e.Args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            try { Native.Autostart = ai + 1 < e.Args.Length && e.Args[ai + 1].Equals("on", StringComparison.OrdinalIgnoreCase); } catch { }
            Shutdown();
            return;
        }

        // Läuft GlassBar schon? Dann dort die Einstellungen öffnen und selbst gleich wieder beenden.
        var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "GlassBar_OpenSettings");
        _mutex = new Mutex(true, "GlassBar_SingleInstance", out bool isNew);
        if (!isNew) { openSettings.Set(); Shutdown(); return; }
        quit.Reset();
        ThreadPool.RegisterWaitForSingleObject(openSettings,
            (_, _) => Dispatcher.BeginInvoke(SettingsWindow.ShowSingle), null, Timeout.Infinite, false);
        ThreadPool.RegisterWaitForSingleObject(quit,
            (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, true);

        StartTrace.Mark("App");
        DispatcherUnhandledException += OnCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            // Fehler in einem Hintergrund-Thread: das Programm endet in jedem Fall. Taskleiste zurückgeben;
            // der Wächter startet GlassBar danach neu.
            if (ex.ExceptionObject is Exception x) Log.Error("Unbehandelter Fehler (Hintergrund)", x);
            Cleanup();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unbeachteter Fehler in einer Aufgabe", ex.Exception);
            ex.SetObserved();
        };

        // Höhere Priorität: Dock und Hintergrund sollen gleich nach der Anmeldung stehen und nie stocken
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
        bool configExisted = File.Exists(Path.Combine(Config.Folder, "config.json")); // für „Was ist neu“ (Neuinstallation?)
        SettingsTransfer.ApplyPending(); // vorgemerkten Import übernehmen, bevor die Einstellungen geladen werden
        Config = Config.Load();
        L.Init(Config.Language);
        StartTrace.Mark("Einstellungen");
        Theme.Apply(); // helles oder dunkles Design (auch für die Win32-Menüs)
        PackagedApps.MigrateConfig(Config); // vor dem ersten Dock: alte Pins von Store-Apps umstellen
        NativeStill.WallpaperRotation.MigrateMotion(Config); // altes Still-Tempo in das Tempo je Effekt übernehmen
        StartTrace.Mark("Store-Apps");

        var cfg = ShellManager.DefaultShellConfig;
        cfg.EnableTasksService = true;
        cfg.AutoStartTasksService = true;
        cfg.EnableTrayService = true;
        cfg.AutoStartTrayService = true;
        cfg.TaskIconSize = ManagedShell.Common.Enums.IconSize.Large;
        if (Config.PinnedTrayIcons is { Length: > 0 })
            cfg.PinnedNotifyIcons = Config.PinnedTrayIcons;

        // vor dem Ausblenden der Taskleiste, damit der Wächter ihren ursprünglichen Zustand kennt
        // Zustand der Windows-Taskleiste (automatisch ausblenden ja/nein) vor GlassBar dauerhaft merken.
        // Beim Ausblenden stellt GlassBar sie auf „automatisch ausblenden“. Endete GlassBar das letzte Mal nicht
        // sauber, steht sie noch so – dann gilt der gespeicherte Zustand, nicht der jetzige.
        // Steht sie schon ohne gemerkten Wert auf „automatisch ausblenden“, kann das noch vom alten Fehler stammen
        // (bis 1.3.5 blieb es nach einem Absturz hängen): einmal nachfragen, siehe AskAboutAutoHide.
        bool askAutoHide = Config.TaskbarStateBeforeGlassBar == null && !Config.TaskbarAutoHideAsked && (Watchdog.GetTaskbarState() & 1) != 0;
        int taskbarState = Config.TaskbarStateBeforeGlassBar ?? Watchdog.GetTaskbarState();
        _taskbarState = taskbarState;
        if (Config.TaskbarStateBeforeGlassBar != taskbarState) { Config.TaskbarStateBeforeGlassBar = taskbarState; Config.Save(); }
        Watchdog.Start(_afterCrash, taskbarState);

        _shell = new ShellManager(cfg);
        StartTrace.Mark("ShellManager");

        UpdateDocks();
        StartTrace.Mark("Docks");
        NativeStill.WallpaperRotation.Start(); // automatischer Wechsel der Hintergrundeffekte (falls eingeschaltet)
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplySettingsToDocks);

        // Windows-Taskleiste ausblenden (wird beim Beenden wiederhergestellt). Das dauert einige Hundert ms –
        // darum erst nach dem ersten Zeichnen des Docks, damit das Dock nicht darauf wartet.
        var shell = _shell;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_cleanedUp) return;
            shell.ExplorerHelper.HideExplorerTaskbar = true;
            StartTrace.Mark("Taskleiste versteckt");
        });

        Updater.StartBackgroundCheck();
        TrayRescue.Start(_shell); // Symbole von Programmen nachholen, die vor GlassBar gestartet sind
        StartTrace.Mark("Ende OnStartup");
        if (askAutoHide) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, AskAboutAutoHide);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { if (!_cleanedUp) WhatsNew.ShowIfUpdated(configExisted); });
    }

    /// <summary>
    /// Einmalige Rückfrage: Die Windows-Taskleiste stand vor GlassBar auf „automatisch ausblenden“. Bis 1.3.5 konnte
    /// das nach einem Absturz von GlassBar hängen bleiben. Bei „Nein“ stellt GlassBar sie beim Beenden auf normal.
    /// </summary>
    private void AskAboutAutoHide()
    {
        if (_cleanedUp) return;
        Config.TaskbarAutoHideAsked = true;
        var answer = MessageBox.Show(
            L.T("Die Windows-Taskleiste ist auf „automatisch ausblenden“ eingestellt.") + "\n\n" +
            L.T("Hast du das selbst so gewollt? Ältere GlassBar-Versionen konnten diese Einstellung nach einem Absturz versehentlich hinterlassen.") + "\n\n" +
            L.T("Ja: so lassen.  Nein: beim Beenden von GlassBar wieder normal anzeigen."),
            "GlassBar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.No)
        {
            _taskbarState = 0; // normal (Windows 11 meldet 0; RestoreTaskbar setzt dann „immer oben“)
            Config.TaskbarStateBeforeGlassBar = 0;
        }
        Config.Save();
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
            if (s.Primary) StartTrace.Mark("Dock aufgebaut");
            _docks.Add(dock);
            dock.Show();
        }
        _bar = _docks.FirstOrDefault(d => d.IsPrimary);
        if (MainWindow == null || !_docks.Contains(MainWindow as TaskbarWindow)) MainWindow = _bar;
    }

    /// <summary>Einstellungen auf alle Docks anwenden.</summary>
    public void ApplySettingsToDocks()
    {
        Theme.Apply();
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

    /// <summary>GlassBar neu starten (z. B. nach dem Wechsel der Sprache).</summary>
    public void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch { }
        Quit();
    }

    public void Quit()
    {
        Watchdog.SignalCleanExit();
        Cleanup();
        Shutdown();
    }

    /// <summary>
    /// Fehler im UI-Thread: protokollieren und weiterlaufen – ein einzelner Fehler (z. B. ein Symbol, das sich nicht
    /// laden lässt) soll nicht das ganze Dock beenden. Kommen viele Fehler kurz hintereinander, ist GlassBar nicht mehr
    /// in Ordnung: dann Taskleiste zurückgeben und beenden; der Wächter startet GlassBar neu.
    /// </summary>
    private void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unbehandelter Fehler", e.Exception);
        e.Handled = true;

        var now = DateTime.Now;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10)) _recentErrors.Dequeue();
        if (_recentErrors.Count < 5 || _cleanedUp) return;

        Log.Info("Zu viele Fehler hintereinander – GlassBar wird neu gestartet.");
        Cleanup();
        Environment.Exit(Watchdog.RestartExitCode);
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        TrayRescue.Stop();
        try
        {
            NativeStill.SecondaryWallpapers.Shutdown();
            foreach (var d in _docks.ToList()) d.Close();
            _docks.Clear();
        }
        catch (Exception ex) { Log.Error("Beenden: Docks schließen", ex); }
        try
        {
            if (_shell != null)
            {
                _shell.ExplorerHelper.HideExplorerTaskbar = false;
                _shell.Dispose();
                // ManagedShell stellt den Zustand her, den es beim Start vorfand – nach einem früheren Absturz falsch.
                // Darum den gespeicherten ursprünglichen Zustand setzen und die Merkung löschen (sauber zurückgegeben).
                if (_taskbarState is int original)
                {
                    Watchdog.RestoreTaskbar(original);
                    Config.TaskbarStateBeforeGlassBar = null;
                    Config.Save();
                }
            }
        }
        catch (Exception ex) { Log.Error("Beenden: Taskleiste zurückgeben", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Watchdog.SignalCleanExit();
        Cleanup();
        base.OnExit(e);
    }
}

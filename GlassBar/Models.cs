using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.ShellFolders;
using ManagedShell.WindowsTasks;

namespace GlassBar;

/// <summary>Ein angeheftetes Element (Programm, Verknüpfung, Datei oder Ordner).</summary>
public class PinnedItem
{
    public string Path { get; }
    public string Target { get; }
    public ShellItem Item { get; }
    public bool IsFolder { get; }

    /// <summary>Schlüssel, über den laufende Fenster dem Element zugeordnet werden.</summary>
    public string Key { get; }

    public PinnedItem(string path)
    {
        Path = path;
        var (target, args) = Native.ResolveShortcut(path);
        Target = target;
        IsFolder = Directory.Exists(Target);
        Item = new ShellItem(path);

        if (IsFolder)
            Key = "folder:" + Target.ToLowerInvariant();
        else if (Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Squirrel-Apps (Discord, Teams …) starten über Update.exe --processStart X.exe
            var m = System.Text.RegularExpressions.Regex.Match(args, @"--processStart\s+""?([^""\s]+\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Key = (m.Success ? m.Groups[1].Value : System.IO.Path.GetFileName(Target)).ToLowerInvariant();
        }
        else
            Key = "pin:" + path.ToLowerInvariant();
    }
}

/// <summary>Ein Knopf in der Leiste: angeheftete App und/oder ihre laufenden Fenster.</summary>
public class AppButton : INotifyPropertyChanged
{
    public string Key { get; }
    public PinnedItem? Pin { get; private set; }
    public ObservableCollection<ApplicationWindow> Windows { get; } = new();

    public AppButton(string key) => Key = key;

    public bool IsPinned => Pin != null;
    public bool IsFolder => Pin?.IsFolder == true;
    public bool IsRunning => Windows.Count > 0;
    public bool IsActive => Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
    public bool IsFlashing => Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
    public int WindowCount => Windows.Count;

    // ---------- Fortschritt und Badge (ITaskbarList3, z. B. Downloads im Browser, ungelesene Nachrichten) ----------
    // Werte von ManagedShell: ProgressState wie TBPFLAG (0 keiner, 1 unbestimmt, 2 normal, 4 Fehler, 8 Pause),
    // ProgressValue 0–65535 wie bei der Windows-Taskleiste.
    private ApplicationWindow? ProgressWindow => Windows.FirstOrDefault(w => (int)w.ProgressState != 0);
    private int ProgressFlag => ProgressWindow is { } w ? (int)w.ProgressState : 0;

    public bool HasProgress => ProgressFlag != 0;
    public bool IsIndeterminate => ProgressFlag == 1;
    public bool IsProgressError => ProgressFlag == 4;
    public bool IsProgressPaused => ProgressFlag == 8;

    /// <summary>Fortschritt 0–1 (unbestimmt: ganz gefüllt, die Vorlage lässt ihn dann pulsieren).</summary>
    public double ProgressFraction =>
        IsIndeterminate ? 1 : Math.Clamp((ProgressWindow?.ProgressValue ?? 0) / 65535.0, 0, 1);

    /// <summary>Kleines Symbol unten rechts (z. B. Anzahl ungelesener Nachrichten), wie es das Programm setzt.</summary>
    public ImageSource? OverlayIcon => Windows.Select(w => w.OverlayIcon).FirstOrDefault(i => i != null);
    public bool HasOverlay => OverlayIcon != null;

    // 48-px-Icons (statt 32 px), damit sie im Dock und beim Vergrößern scharf bleiben
    private ShellItem? _exeItem;
    private ShellItem? ExeItem
    {
        get
        {
            if (_exeItem == null && Pin == null && Windows.FirstOrDefault(w => !w.IsUWP)?.WinFileName is { Length: > 0 } exe && File.Exists(exe))
            {
                _exeItem = new ShellItem(exe);
                _exeItem.PropertyChanged += OnPinItemChanged;
            }
            return _exeItem;
        }
    }

    public ImageSource? Icon =>
        Pin?.Item.ExtraLargeIcon ?? ExeItem?.ExtraLargeIcon ?? Windows.FirstOrDefault()?.Icon;

    public string Title =>
        Pin?.Item.DisplayName ?? Windows.FirstOrDefault()?.WinFileDescription ?? Windows.FirstOrDefault()?.Title ?? "";

    public string? ExecutablePath =>
        Pin?.Target is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t
        : Windows.FirstOrDefault(w => !w.IsUWP)?.WinFileName;

    internal void SetPin(PinnedItem? pin)
    {
        if (Pin == pin) return;
        if (Pin != null) Pin.Item.PropertyChanged -= OnPinItemChanged;
        Pin = pin;
        if (Pin != null) Pin.Item.PropertyChanged += OnPinItemChanged;
    }

    private void OnPinItemChanged(object? s, PropertyChangedEventArgs e) => Refresh();

    internal void SetWindows(IReadOnlyList<ApplicationWindow> wins)
    {
        if (!Windows.SequenceEqual(wins))
        {
            for (int i = Windows.Count - 1; i >= 0; i--)
                if (!wins.Contains(Windows[i])) Windows.RemoveAt(i);
            foreach (var w in wins)
                if (!Windows.Contains(w)) Windows.Add(w);
        }
        Refresh();
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public event PropertyChangedEventHandler? PropertyChanged;

    public static string KeyOf(ApplicationWindow w)
    {
        if (!w.IsUWP && !string.IsNullOrEmpty(w.WinFileName))
            return System.IO.Path.GetFileName(w.WinFileName).ToLowerInvariant();
        return "aumid:" + (w.AppUserModelID ?? w.Handle.ToString());
    }
}

/// <summary>Führt angeheftete Elemente und laufende Fenster zu Knöpfen zusammen – wie bei Windows.</summary>
public class TaskbarModel
{
    private static readonly string PinnedFolder = System.IO.Path.Combine(Config.Folder, "Pinned");

    private readonly ICollectionView _view;
    private readonly Dictionary<string, AppButton> _byKey = new();
    private readonly List<string> _runOrder = new();
    private readonly HashSet<ApplicationWindow> _hooked = new();
    private bool _pending;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher; // UI-Thread (Ereignisse kommen teils aus Hintergrund-Threads)

    public ObservableCollection<PinnedItem> Pins { get; } = new();
    public ObservableCollection<AppButton> Buttons { get; } = new();

    /// <summary>Angeheftete Elemente wurden in einem Dock geändert -> alle anderen übernehmen das.</summary>
    private static event Action<TaskbarModel>? PinsChanged;

    // Nur Fenster auf diesem Bildschirm zeigen (null = alle)
    private string? _screenFilter;
    private readonly DispatcherTimer _screenWatch = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private string _screenSignature = "";

    /// <summary>Gerätename des Bildschirms, dessen Fenster gezeigt werden (null = alle Fenster).</summary>
    public string? ScreenFilter
    {
        get => _screenFilter;
        set
        {
            if (_screenFilter == value) return;
            _screenFilter = value;
            if (value != null) _screenWatch.Start(); else _screenWatch.Stop();
            Recompute();
        }
    }

    public TaskbarModel(ShellManager shell)
    {
        LoadPins();

        _view = shell.Tasks.GroupedWindows;
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => Schedule();
        PinsChanged += OnPinsChangedElsewhere;
        // Fenster, die auf einen anderen Bildschirm gezogen werden, wechseln ins dortige Dock
        _screenWatch.Tick += (_, _) =>
        {
            var sig = string.Join("|", _hooked.Select(w => w.Handle + "@" + ScreenOf.Name(w.Handle)));
            if (sig != _screenSignature) Schedule();
        };
        Recompute();
    }

    /// <summary>Dock wird geschlossen.</summary>
    public void Detach()
    {
        PinsChanged -= OnPinsChangedElsewhere;
        _screenWatch.Stop();
    }

    private void LoadPins()
    {
        var paths = App.Config.Pinned.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        // vorhandene Einträge behalten (Symbole müssen nicht neu geladen werden)
        var keep = Pins.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        Pins.Clear();
        foreach (var p in paths)
            Pins.Add(keep.TryGetValue(p, out var old) ? old : new PinnedItem(p));
    }

    private void OnPinsChangedElsewhere(TaskbarModel source)
    {
        if (source == this) return;
        LoadPins();
        Recompute();
    }

    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Recompute);
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

    private void Recompute()
    {
        _pending = false;
        var wins = _view.Cast<object>().OfType<ApplicationWindow>().ToList();

        foreach (var w in wins)
            if (_hooked.Add(w)) w.PropertyChanged += OnWindowChanged;
        foreach (var w in _hooked.Where(w => !wins.Contains(w)).ToList())
        {
            w.PropertyChanged -= OnWindowChanged;
            _hooked.Remove(w);
        }

        // Nur die Fenster auf dem Bildschirm dieses Docks
        if (_screenFilter != null)
        {
            var screens = wins.ToDictionary(w => w, w => ScreenOf.Name(w.Handle));
            _screenSignature = string.Join("|", wins.Select(w => w.Handle + "@" + screens[w]));
            wins = wins.Where(w => string.Equals(screens[w], _screenFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var groups = new Dictionary<string, List<ApplicationWindow>>();
        foreach (var w in wins)
        {
            var k = AppButton.KeyOf(w);
            if (!groups.TryGetValue(k, out var list)) groups[k] = list = new();
            list.Add(w);
            if (!_runOrder.Contains(k)) _runOrder.Add(k);
        }
        _runOrder.RemoveAll(k => !groups.ContainsKey(k));

        var desired = new List<AppButton>();
        var pinKeys = new HashSet<string>();
        foreach (var pin in Pins)
        {
            if (!pinKeys.Add(pin.Key)) continue;
            var b = Get(pin.Key);
            b.SetPin(pin);
            b.SetWindows(groups.TryGetValue(pin.Key, out var l) ? l : new());
            desired.Add(b);
        }
        foreach (var k in _runOrder)
        {
            if (pinKeys.Contains(k)) continue;
            var b = Get(k);
            b.SetPin(null);
            b.SetWindows(groups[k]);
            desired.Add(b);
        }

        foreach (var k in _byKey.Keys.Except(desired.Select(d => d.Key)).ToList())
            _byKey.Remove(k);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Buttons.Count && Buttons[i] == desired[i]) continue;
            int idx = Buttons.IndexOf(desired[i]);
            if (idx >= 0) Buttons.Move(idx, i);
            else Buttons.Insert(i, desired[i]);
        }
        while (Buttons.Count > desired.Count) Buttons.RemoveAt(Buttons.Count - 1);

    }

    private AppButton Get(string key)
    {
        if (!_byKey.TryGetValue(key, out var b)) _byKey[key] = b = new AppButton(key);
        return b;
    }

    // ---------- Anheften ----------

    public void AddPin(string path, int index = -1)
    {
        // Verknüpfungen werden kopiert, damit sie weiter funktionieren, wenn du sie vom Desktop löschst.
        if ((path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            && !path.StartsWith(PinnedFolder, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(PinnedFolder);
            string name = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
            string dest = System.IO.Path.Combine(PinnedFolder, name + ext);
            if (File.Exists(dest) && Pins.Any(p => Same(p.Path, dest))) return;
            for (int i = 2; File.Exists(dest); i++) dest = System.IO.Path.Combine(PinnedFolder, $"{name} ({i}){ext}");
            File.Copy(path, dest);
            path = dest;
        }

        if (Pins.Any(p => Same(p.Path, path))) return;
        var item = new PinnedItem(path);
        if (Pins.Any(p => p.Key == item.Key)) return; // gleiche App schon angeheftet
        if (index < 0 || index > Pins.Count) Pins.Add(item); else Pins.Insert(index, item);
        Save();
    }

    public void Unpin(PinnedItem item)
    {
        Pins.Remove(item);
        Save();
        if (item.Path.StartsWith(PinnedFolder, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(item.Path); } catch { }
    }

    public void MovePin(PinnedItem item, int index)
    {
        int from = Pins.IndexOf(item);
        if (from < 0) return;
        index = Math.Clamp(index, 0, Pins.Count - 1);
        if (from != index) { Pins.Move(from, index); Save(); }
    }

    private void Save()
    {
        App.Config.Pinned = Pins.Select(p => p.Path).ToList();
        App.Config.Save();
        Recompute();
        PinsChanged?.Invoke(this);
    }

    private static bool Same(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Auf welchem Bildschirm ein Fenster liegt (Gerätename wie „\\.\DISPLAY1“).</summary>
internal static class ScreenOf
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public int Length, Flags, ShowCmd; public POINT Min, Max; public RECT Normal; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor, Work; public int Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);

    private static readonly Dictionary<IntPtr, string> Names = new();

    public static string Name(IntPtr hwnd)
    {
        IntPtr mon;
        if (IsIconic(hwnd))
        {
            // Minimiert: dort, wo das Fenster wieder erscheint
            var p = new WINDOWPLACEMENT { Length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref p);
            mon = MonitorFromRect(ref p.Normal, 2 /* NEAREST */);
        }
        else mon = MonitorFromWindow(hwnd, 2 /* NEAREST */);
        if (Names.TryGetValue(mon, out var n)) return n;
        var mi = new MONITORINFOEX { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        n = GetMonitorInfo(mon, ref mi) ? mi.Device : "";
        if (Names.Count > 32) Names.Clear();
        return Names[mon] = n;
    }
}

/// <summary>Ein Eintrag im Ordner-Fenster.</summary>
public record FolderEntry(ShellItem Item, ShellFolder Parent);

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ManagedShell.ShellFolders;

namespace GlassBar;

/// <summary>Ein Abschnitt im Ordner-Fenster: eine Gruppe oder L.T("Weitere").</summary>
public class FolderSection
{
    public FolderGroup? Group { get; init; }
    public string Header { get; init; } = "";
    public bool ShowHeader { get; init; }
    public bool Collapsed { get; init; }
    public List<FolderEntry> Items { get; init; } = new();

    public int Count => Items.Count;
    public string Chevron => Collapsed ? "" : "";
    public Visibility HeaderVisibility => ShowHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ItemsVisibility => Collapsed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyHintVisibility => !Collapsed && Group != null && Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Zeigt den Inhalt eines Ordners (oder des Desktops) als Raster mit Gruppen.
/// Das Fenster wächst und schrumpft mit dem Inhalt und aktualisiert sich live.
/// </summary>
public partial class FolderFlyout : FlyoutBase
{
    private const double ItemWidth = 100;
    private const string DragFormat = "GlassBarEntry";

    /// <summary>"An Taskleiste anheften" aus dem Ordner-Fenster.</summary>
    public static event Action<string>? PinRequested;

    private readonly Stack<string?> _history = new();
    private readonly List<ShellFolder> _sources = new();
    private string? _current;   // null = Desktop
    private bool _rebuildPending;

    private Point? _dragStart;
    private FolderEntry? _dragEntry;
    private bool _justDragged;

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <param name="path">Ordnerpfad oder null für den Desktop.</param>
    public FolderFlyout(string? path)
    {
        InitializeComponent();
        L.Apply(this);
        Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.75;
        Load(path);
        Closed += (_, _) => DisposeSources();
    }

    // ---------- Laden ----------

    private void Load(string? path)
    {
        DisposeSources();
        _current = path;
        _rebuildPending = false;
        SearchBox.Text = "";

        // Der Desktop-Ordner der Shell enthält bereits auch den öffentlichen Desktop.
        var dir = path ?? UserDesktop;
        if (Directory.Exists(dir))
        {
            var f = new ShellFolder(dir, IntPtr.Zero, true, true);
            ((INotifyCollectionChanged)f.Files).CollectionChanged += (_, _) => ScheduleRebuild();
            _sources.Add(f);
        }

        TitleText.Text = path == null ? L.T("Desktop") : new DirectoryInfo(path).Name;
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    private void DisposeSources()
    {
        Sections.ItemsSource = null;
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
    }

    private void ScheduleRebuild()
    {
        // Ereignisse kommen teils aus Hintergrund-Threads -> auf den UI-Thread dieses Fensters
        if (_rebuildPending) return;
        _rebuildPending = true;
        // Datei gelöscht/hinzugefügt: dabei auch den Papierkorb (voll/leer) nachziehen
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { Rebuild(); RefreshRecycleBin(); });
    }

    private string GroupKey => _current ?? "<desktop>";

    private List<FolderGroup> Groups
    {
        get
        {
            if (!App.Config.FolderGroups.TryGetValue(GroupKey, out var g))
                App.Config.FolderGroups[GroupKey] = g = new();
            return g;
        }
    }

    private void Rebuild()
    {
        _rebuildPending = false;
        string q = SearchBox.Text.Trim();

        var entries = new List<FolderEntry>();
        foreach (var src in _sources)
        {
            List<ShellItem> files;
            try { files = src.Files.Cast<ShellItem>().ToList(); }
            catch { ScheduleRebuild(); return; }   // Liste wurde gerade im Hintergrund geändert
            entries.AddRange(files.Select(f => new FolderEntry(f, src)));
        }

        bool desktop = _current == null;
        var all = entries
            .Where(e => !string.IsNullOrEmpty(e.Item.Path) && (!desktop || ShownOnDesktop(e.Item)))
            .GroupBy(e => e.Item.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Where(e => q.Length == 0 || (e.Item.DisplayName ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => IsDir(e.Item))
            .ThenBy(e => e.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = Groups;
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<FolderSection>();

        foreach (var g in groups)
        {
            var set = new HashSet<string>(g.Items, StringComparer.OrdinalIgnoreCase);
            var items = all.Where(e => set.Contains(e.Item.Path)).ToList();
            foreach (var i in items) grouped.Add(i.Item.Path);
            if (q.Length > 0 && items.Count == 0) continue; // bei Suche leere Gruppen ausblenden
            sections.Add(new FolderSection
            {
                Group = g, Header = g.Name, ShowHeader = true,
                Collapsed = g.Collapsed && q.Length == 0, Items = items,
            });
        }

        var rest = all.Where(e => !grouped.Contains(e.Item.Path)).ToList();
        if (rest.Count > 0 || sections.Count == 0)
            sections.Add(new FolderSection
            {
                Header = L.T("Weitere"), ShowHeader = sections.Count > 0, Items = rest,
                Collapsed = sections.Count > 0 && q.Length == 0 && App.Config.CollapsedRest.Contains(GroupKey),
            });

        Sections.ItemsSource = sections;

        // Spaltenzahl passt sich der Menge an: wenige Icons -> kleines Fenster, viele -> breiter
        int cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(all.Count, 1) * 1.6)), 4, 8);
        Sections.Width = cols * (ItemWidth + 4);

        EmptyText.Text = q.Length > 0 ? L.T("Keine Treffer.") : L.T("Dieser Ordner ist leer.");
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsDir(ShellItem i) => i.IsFileSystem && Directory.Exists(i.Path);

    /// <summary>
    /// Der Shell-Desktop enthält auch Bibliotheken, Benutzerordner usw. Gezeigt wird nur, was auch
    /// wirklich auf deinem Desktop liegt, plus die Systemsymbole, die du dort eingeschaltet hast.
    /// </summary>
    private static bool ShownOnDesktop(ShellItem i)
    {
        var p = i.Path ?? "";
        if (p.StartsWith("::{"))
            return EnabledDesktopIcons.Value.Any(c => p.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (!i.IsFileSystem) return false;
        var parent = Path.GetDirectoryName(p);
        return string.Equals(parent, UserDesktop, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parent, PublicDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Lazy<HashSet<string>> EnabledDesktopIcons = new(() =>
    {
        // HideDesktopIcons\NewStartPanel: Wert 0 = Symbol wird auf dem Desktop angezeigt
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
            if (k != null)
                foreach (var name in k.GetValueNames())
                    if (k.GetValue(name) is int v && v == 0) set.Add(name);
        }
        catch { }
        if (set.Count == 0) set.Add("{645FF040-5081-101B-9F08-00AA002F954E}"); // Papierkorb (Standard)
        return set;
    });

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);
    private bool? _binFull;

    /// <summary>
    /// Der Desktop-Ordner bleibt vorgeladen; das Papierkorb-Symbol (leer/voll) ändert sich aber.
    /// Beim Öffnen prüfen, ob sich der Füllstand geändert hat, und dann das Symbol neu laden.
    /// </summary>
    private void RefreshRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
            bool full = SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
            if (_binFull == full) return;
            bool first = _binFull == null;
            _binFull = full;
            if (first) return; // beim ersten Mal sind die Symbole frisch geladen
            foreach (var s in _sources)
                foreach (var item in s.Files.ToList())
                    if ((item.Path ?? "").Contains("645FF040-5081-101B-9F08-00AA002F954E", StringComparison.OrdinalIgnoreCase))
                        item.Refresh();
        }
        catch { }
    }

    /// <summary>Beim erneuten Öffnen wieder beim Startordner beginnen.</summary>
    public void Reset()
    {
        if (_history.Count == 0 && SearchBox.Text.Length == 0) return;
        string? root = _current;
        while (_history.Count > 0) root = _history.Pop();
        Load(root);
    }

    public override void ShowAt(FrameworkElement anchor)
    {
        Reset();
        RefreshRecycleBin();
        base.ShowAt(anchor);
    }

    // ---------- Gruppen ----------

    private FolderGroup? GroupOf(string path) =>
        Groups.FirstOrDefault(g => g.Items.Contains(path, StringComparer.OrdinalIgnoreCase));

    private void Assign(string path, FolderGroup? group)
    {
        foreach (var g in Groups) g.Items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        group?.Items.Add(path);
        App.Config.Save();
        Rebuild();
    }

    private FolderGroup? CreateGroup(string? firstItem = null)
    {
        SuppressClose = true;
        string? name;
        try { name = Ui.Ask(this, "Neue Gruppe", L.T("Neue Gruppe")); }
        finally { SuppressClose = false; Activate(); }
        if (name == null) return null;

        var g = new FolderGroup { Name = name };
        Groups.Add(g);
        if (firstItem != null) Assign(firstItem, g);
        else { App.Config.Save(); Rebuild(); }
        return g;
    }

    private void NewGroup_Click(object sender, MouseButtonEventArgs e) => CreateGroup();

    private void Header_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderSection s) return;
        if (s.Group is { } g) g.Collapsed = !g.Collapsed;
        else if (!App.Config.CollapsedRest.Remove(GroupKey)) App.Config.CollapsedRest.Add(GroupKey); // L.T("Weitere")
        App.Config.Save();
        Rebuild();
    }

    private void Header_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FolderSection { Group: { } g }) return;
        var groups = Groups;
        int i = groups.IndexOf(g);

        SuppressClose = true;
        Ui.Show(new object?[]
        {
            Ui.Item("Umbenennen", "", () =>
            {
                SuppressClose = true;
                try { if (Ui.Ask(this, "Gruppe umbenennen", g.Name) is { } n) { g.Name = n; App.Config.Save(); Rebuild(); } }
                finally { SuppressClose = false; Activate(); }
            }),
            Ui.Item(g.Collapsed ? "Aufklappen" : "Zuklappen", g.Collapsed ? "" : "",
                () => { g.Collapsed = !g.Collapsed; App.Config.Save(); Rebuild(); }),
            null,
            Ui.Item("Nach oben", "", () => { groups.RemoveAt(i); groups.Insert(i - 1, g); App.Config.Save(); Rebuild(); }, i > 0),
            Ui.Item("Nach unten", "", () => { groups.RemoveAt(i); groups.Insert(i + 1, g); App.Config.Save(); Rebuild(); }, i < groups.Count - 1),
            null,
            Ui.Item("Gruppe auflösen (Symbole bleiben)", "", () => { groups.Remove(g); App.Config.Save(); Rebuild(); }),
        }, () => SuppressClose = false);
    }

    private void Section_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Section_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is not string path) return;
        if ((sender as FrameworkElement)?.DataContext is not FolderSection s) return;
        Assign(path, s.Group);
    }

    // ---------- Symbole ----------

    private static FolderEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as FolderEntry;

    private void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _justDragged = false;
        _dragStart = e.GetPosition(this);
        _dragEntry = EntryOf(sender);
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragEntry is not { } entry) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;

        _dragStart = null;
        _dragEntry = null;
        _justDragged = true;

        var data = new DataObject(DragFormat, entry.Item.Path);
        if (entry.Item.IsFileSystem)
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { entry.Item.Path });

        SuppressClose = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); }
        finally { SuppressClose = false; }
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (_justDragged) { _justDragged = false; return; }
        if (EntryOf(sender) is { } entry) Open(entry);
    }

    private void Open(FolderEntry entry)
    {
        if (IsDir(entry.Item))
        {
            _history.Push(_current);
            Load(entry.Item.Path);
        }
        else
        {
            Native.Launch(entry.Item.Path);
            SafeClose();
        }
    }

    private void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (EntryOf(sender) is not { } entry) return;

        ShowModernMenu(entry);
    }

    /// <summary>Kontextmenü im Stil von Windows 11. Die Befehle selbst führt Windows aus.</summary>
    private void ShowModernMenu(FolderEntry entry)
    {
        var item = entry.Item;
        var path = item.Path;
        bool fs = item.IsFileSystem && (File.Exists(path) || Directory.Exists(path));
        bool dir = IsDir(item);
        bool runnable = fs && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
        bool isLink = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        var current = GroupOf(path);
        var hwnd = Handle;

        void Verb(string verb) { if (!ShellVerbs.Invoke(path, verb, hwnd)) ShowShellMenu(entry); }

        var buttons = new List<MenuButton>
        {
            new("Ausschneiden", "", () => Verb("cut"), fs),
            new("Kopieren", "", () => Verb("copy"), fs),
            new("Umbenennen", "", () => Rename(entry), fs),
            new("Freigabe", "", () => Verb("Windows.ModernShare"), fs),
            new("Löschen", "", () => Verb("delete"), fs),
        };

        var groupMenu = new List<MenuEntry>();
        foreach (var g in Groups)
        {
            var target = g;
            groupMenu.Add(new MenuEntry(g.Name, "", () => Assign(path, target == current ? null : target), IsChecked: g == current));
        }
        if (groupMenu.Count > 0) groupMenu.Add(MenuEntry.Separator);
        groupMenu.Add(new MenuEntry("Neue Gruppe …", "", () => CreateGroup(path)));
        if (current != null) groupMenu.Add(new MenuEntry("Aus Gruppe entfernen", "", () => Assign(path, null)));

        var entries = new List<MenuEntry>
        {
            new("Öffnen", null, () => Open(entry), "Eingabetaste", Image: item.ExtraLargeIcon),
        };
        if (fs)
            entries.Add(new MenuEntry(isLink ? "Dateipfad öffnen" : "Speicherort öffnen", "", () =>
            {
                var target = Native.ResolveShortcut(path).Target;
                Native.Launch("explorer.exe", $"/select,\"{(File.Exists(target) || Directory.Exists(target) ? target : path)}\"");
                SafeClose();
            }));
        if (runnable) entries.Add(new MenuEntry("Als Administrator ausführen", "", () => { Ui.RunAsAdmin(path); SafeClose(); }));
        if (fs) entries.Add(new MenuEntry("Als Pfad kopieren", "", () => Clipboard.SetText(path), "Strg+Umschalt+C"));
        if (fs) entries.Add(new MenuEntry("Komprimieren in", "\uE7B8", Children: new[]
        {
            new MenuEntry("ZIP-Datei", "\uE7B8", () => Zip(path)),
        }));
        if (fs) entries.Add(new MenuEntry("An \"Start\" anheften", "", () => Verb("pintostartscreen")));
        if (fs) entries.Add(new MenuEntry("An Taskleiste anheften", "", () => PinRequested?.Invoke(path)));
        if (dir) entries.Add(new MenuEntry("Zu Favoriten hinzufügen", "", () => Verb("pintohome")));
        entries.Add(new MenuEntry("Eigenschaften", "", () => { if (fs) Ui.ShowProperties(path); else Verb("properties"); }, "Alt+Eingabetaste"));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("Gruppe", "", Children: groupMenu));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("Weitere Optionen anzeigen", "", () => ShowShellMenu(entry)));

        SuppressClose = true;
        ModernMenu.ShowAtCursor(buttons, entries, () => SuppressClose = false);
    }

    private void Rename(FolderEntry entry)
    {
        var path = entry.Item.Path;
        bool dir = Directory.Exists(path);
        string ext = dir ? "" : Path.GetExtension(path);
        // Wie im Explorer: bei Verknüpfungen die Endung nicht zeigen
        bool hideExt = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
        string current = hideExt ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

        SuppressClose = true;
        string? name;
        try { name = Ui.Ask(this, "Umbenennen", current); }
        finally { SuppressClose = false; }
        if (name == null || name == current) return;
        if (hideExt) name += ext;

        var target = Path.Combine(Path.GetDirectoryName(path)!, name);
        try
        {
            if (dir) Directory.Move(path, target); else File.Move(path, target);
            // Gruppenzuordnung mitnehmen
            foreach (var g in Groups)
                for (int i = 0; i < g.Items.Count; i++)
                    if (string.Equals(g.Items[i], path, StringComparison.OrdinalIgnoreCase)) g.Items[i] = target;
            App.Config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Umbenennen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Zip(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var baseName = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            var zip = Path.Combine(dir, baseName + ".zip");
            for (int i = 2; File.Exists(zip); i++) zip = Path.Combine(dir, $"{baseName} ({i}).zip");

            if (Directory.Exists(path))
                System.IO.Compression.ZipFile.CreateFromDirectory(path, zip, System.IO.Compression.CompressionLevel.Optimal, true);
            else
            {
                using var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, path, Path.GetFileName(path));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Komprimieren", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Das echte Windows-Kontextmenü (dunkel), unten ergänzt um GlassBar-Einträge:
    /// anheften und Gruppen.
    /// </summary>
    private void ShowShellMenu(FolderEntry entry)
    {
        var path = entry.Item.Path;
        bool fs = entry.Item.IsFileSystem && (File.Exists(path) || Directory.Exists(path));
        var current = GroupOf(path);

        var actions = new Dictionary<uint, Action>();
        var post = new ShellMenuCommandBuilder();
        uint uid = 40000;
        void Add(string label, Action action, bool check = false)
        {
            uid++;
            actions[uid] = action;
            post.AddCommand(new ShellMenuCommand
            {
                UID = uid, Label = label,
                Flags = check ? ManagedShell.ShellFolders.Enums.MFT.CHECKED : ManagedShell.ShellFolders.Enums.MFT.BYCOMMAND,
            });
        }

        post.AddSeparator();
        if (fs) Add("An Taskleiste anheften", () => PinRequested?.Invoke(path));
        foreach (var g in Groups)
        {
            var target = g;
            Add($"Gruppe: {g.Name}", () => Assign(path, target == current ? null : target), g == current);
        }
        Add("Neue Gruppe …", () => CreateGroup(path));
        if (current != null) Add("Aus Gruppe entfernen", () => Assign(path, null));

        SuppressClose = true;
        try
        {
            _ = new ShellItemContextMenu(new[] { entry.Item }, entry.Parent, Handle,
                (command, _, _) =>
                {
                    if (uint.TryParse(command, out var id) && actions.TryGetValue(id, out var act))
                    {
                        // Nach dem Schließen des Menüs ausführen (z. B. Dialog "Neue Gruppe")
                        Dispatcher.BeginInvoke(act);
                        return true;
                    }
                    if (command == "open") { Dispatcher.BeginInvoke(() => Open(entry)); return true; }
                    return false; // alles andere erledigt Windows selbst
                },
                true, new ShellMenuCommandBuilder(), post);
        }
        catch (Exception ex) { Log.Error("Kontextmenü im Ordner", ex); }
        finally { SuppressClose = false; }
    }

    // ---------- Kopfzeile ----------

    private void Back_Click(object sender, MouseButtonEventArgs e)
    {
        if (_history.Count > 0) Load(_history.Pop());
    }

    private void Explorer_Click(object sender, MouseButtonEventArgs e)
    {
        Native.Launch("explorer.exe", $"\"{_current ?? UserDesktop}\"");
        SafeClose();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) Rebuild();
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        // Einfach lostippen = suchen
        if (!SearchBox.IsKeyboardFocused) SearchBox.Focus();
        base.OnPreviewTextInput(e);
    }
}

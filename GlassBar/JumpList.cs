using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBar;

/// <summary>Ein Eintrag der Sprungliste (Aufgabe, zuletzt verwendete Datei, angeheftetes Element …).</summary>
public sealed record JumpItem(string Title, string? Description, ImageSource? Icon, Action Launch);

/// <summary>Ein Abschnitt der Sprungliste. Name "" = Aufgaben.</summary>
public sealed record JumpCategory(string Name, IReadOnlyList<JumpItem> Items);

/// <summary>
/// Liest die Sprungliste einer fremden App – so, wie die Windows-Taskleiste sie zeigt.
/// Dafür gibt es keine öffentliche Schnittstelle, also lesen wir die Dateien, die Windows selbst führt:
/// <list type="bullet">
/// <item>CustomDestinations\&lt;hash&gt;.customDestinations-ms – eigene Kategorien und Aufgaben der App</item>
/// <item>AutomaticDestinations\&lt;hash&gt;.automaticDestinations-ms – zuletzt/häufig verwendet und angeheftet</item>
/// </list>
/// &lt;hash&gt; ist die CRC-64 der AppUserModelID (in Großbuchstaben, UTF-16).
/// </summary>
public static class JumpList
{
    public const string PinnedName = "Angeheftet";
    public const string RecentName = "Zuletzt verwendet";
    public const string FrequentName = "Häufig verwendet";
    public const string TasksDisplayName = "Aufgaben";

    private const int MaxPerCategory = 10;
    private const int MaxPinned = 30;

    private static readonly string RecentFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");

    /// <summary>
    /// Liest die Sprungliste einer App. Alles optional: AUMID (falls bekannt), Programmpfad,
    /// angeheftete Verknüpfung, irgendein Fenster der App. Wirft nie; ohne Treffer eine leere Liste.
    /// Darf aus jedem Thread aufgerufen werden (COM läuft intern in einem STA-Thread).
    /// </summary>
    public static IReadOnlyList<JumpCategory> Load(string? appUserModelId, string? exePath, string? shortcutPath, IntPtr anyWindow)
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return LoadCore(appUserModelId, exePath, shortcutPath, anyWindow);

            IReadOnlyList<JumpCategory> result = Array.Empty<JumpCategory>();
            var t = new Thread(() =>
            {
                try { result = LoadCore(appUserModelId, exePath, shortcutPath, anyWindow); } catch (Exception ex) { Log.Error("Sprungliste laden", ex); }
            }) { IsBackground = true, Name = "GlassBar JumpList" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t.Join(TimeSpan.FromSeconds(8)) ? result : Array.Empty<JumpCategory>();
        }
        catch { return Array.Empty<JumpCategory>(); }
    }

    /// <summary>Dateiname, unter dem Windows die Sprungliste dieser AppID ablegt (ohne Endung).</summary>
    public static string AppIdHash(string appId)
    {
        // CRC-64 (Jones, gespiegelt), Startwert ~0, ohne Abschluss-XOR – Hex ohne führende Nullen.
        const ulong Poly = 0x92C64265D32139A4UL;
        ulong crc = ulong.MaxValue;
        foreach (byte b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ Poly : crc >> 1;
        }
        return crc.ToString("x");
    }

    /// <summary>Alle AppIDs, die für diese App in Frage kommen – die wahrscheinlichste zuerst.</summary>
    public static IReadOnlyList<string> ResolveAppIds(string? appUserModelId, string? exePath, string? shortcutPath, IntPtr anyWindow)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id.Trim());
        }

        Add(appUserModelId);
        if (anyWindow != IntPtr.Zero)
        {
            Add(Try(() => ResolverForWindow(anyWindow)));
            Add(Try(() => WindowProperty(anyWindow)));
        }
        if (!string.IsNullOrEmpty(shortcutPath) && File.Exists(shortcutPath))
        {
            Add(Try(() => ResolverForShortcut(shortcutPath)));
            Add(Try(() => FileProperty(shortcutPath)));
        }
        if (!string.IsNullOrEmpty(exePath))
            Add(Try(() => DefaultAppId(exePath)));
        return ids;
    }

    // ---------- Zusammenbau ----------

    private static IReadOnlyList<JumpCategory> LoadCore(string? aumid, string? exe, string? lnk, IntPtr hwnd)
    {
        foreach (var id in ResolveAppIds(aumid, exe, lnk, hwnd))
        {
            string hash = AppIdHash(id);
            string custom = Path.Combine(RecentFolder, "CustomDestinations", hash + ".customDestinations-ms");
            string auto = Path.Combine(RecentFolder, "AutomaticDestinations", hash + ".automaticDestinations-ms");
            bool hasCustom = File.Exists(custom), hasAuto = File.Exists(auto);
            if (!hasCustom && !hasAuto) continue;
            return Compose(hasCustom ? Cached(custom, ParseCustom) : null, hasAuto ? Cached(auto, ParseAutomatic) : null);
        }
        return Array.Empty<JumpCategory>();
    }

    private static IReadOnlyList<JumpCategory> Compose(List<RawCategory>? custom, AutoList? auto)
    {
        var result = new List<JumpCategory>();
        var pinnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Angeheftet steht bei Windows immer oben
        if (auto != null && auto.Pinned.Count > 0)
        {
            foreach (var e in auto.Pinned) pinnedKeys.Add(e.Key);
            result.Add(new JumpCategory(PinnedName, auto.Pinned.Take(MaxPinned).Select(ToItem).ToList()));
        }

        IEnumerable<Entry> Unpinned(IEnumerable<Entry> list) => list.Where(e => !pinnedKeys.Contains(e.Key)).Take(MaxPerCategory);
        void AddCategory(string name, IEnumerable<Entry> entries)
        {
            var items = entries.Select(ToItem).ToList();
            if (items.Count > 0) result.Add(new JumpCategory(name, items));
        }

        RawCategory? tasks = null;
        bool hasCategories = custom?.Any(c => c.Kind != CategoryKind.Tasks) == true;
        if (custom != null)
        {
            foreach (var c in custom)
            {
                switch (c.Kind)
                {
                    case CategoryKind.Custom:
                        AddCategory(c.Name, Unpinned(c.Items.Where(e => !e.IsSeparator)));
                        break;
                    case CategoryKind.Known when auto != null:
                        if (c.KnownId == 1) AddCategory(FrequentName, Unpinned(auto.Frequent));
                        else if (c.KnownId == 2) AddCategory(RecentName, Unpinned(auto.Recent));
                        break;
                    case CategoryKind.Tasks:
                        tasks = c;
                        break;
                }
            }
        }
        // Apps ohne eigene Kategorien bekommen von Windows die Liste "Zuletzt verwendet"
        if (!hasCategories && auto != null) AddCategory(RecentName, Unpinned(auto.Recent));

        if (tasks != null) AddCategory("", tasks.Items.Where(e => !e.IsSeparator));
        return result;
    }

    private static JumpItem ToItem(Entry e) => new(e.Title, e.Description, e.Icon, () => Execute(e.Launch));

    // ---------- Zwischenspeicher (pro Datei + Änderungszeit) ----------

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, long Length, object Data)> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static T Cached<T>(string path, Func<byte[], T> parse) where T : class
    {
        var info = new FileInfo(path);
        DateTime stamp = info.LastWriteTimeUtc;
        long length = info.Length;
        lock (Gate)
            if (FileCache.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.Length == length && hit.Data is T data)
                return data;

        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        var parsed = parse(bytes);
        lock (Gate) FileCache[path] = (stamp, length, parsed);
        return parsed;
    }

    // ---------- Gelesene Einträge ----------

    private enum CategoryKind { Custom, Known, Tasks }

    private sealed record RawCategory(CategoryKind Kind, string Name, int KnownId, List<Entry> Items);

    /// <summary>Alles, was zum Starten nötig ist – ohne COM-Objekte, damit es jeder Thread benutzen kann.</summary>
    private sealed record LaunchInfo(string? File, string? Arguments, string? Directory, int ShowCmd, byte[]? IdList);

    private sealed record Entry(string Title, string? Description, ImageSource? Icon, LaunchInfo Launch, string Key, bool IsSeparator);

    private sealed record AutoList(List<Entry> Pinned, List<Entry> Recent, List<Entry> Frequent);

    // ---------- customDestinations-ms ----------

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private const uint CategoryFooter = 0xBABFFBAB;

    /// <summary>
    /// Aufbau: Version, Anzahl Kategorien, 0 – dann je Kategorie ein Typ (0 = eigene, 1 = bekannte, 2 = Aufgaben),
    /// Name/Kennung, Einträge (CLSID + per IPersistStream gespeichertes Objekt) und die Endmarke 0xBABFFBAB.
    /// </summary>
    private static List<RawCategory> ParseCustom(byte[] d)
    {
        var cats = new List<RawCategory>();
        if (d.Length < 12) return cats;
        uint count = BitConverter.ToUInt32(d, 4);
        int pos = 12;
        try
        {
            for (uint c = 0; c < count && pos + 4 <= d.Length; c++)
            {
                uint type = BitConverter.ToUInt32(d, pos); pos += 4;
                switch (type)
                {
                    case 0:
                    {
                        int len = BitConverter.ToUInt16(d, pos); pos += 2;
                        string name = Encoding.Unicode.GetString(d, pos, len * 2); pos += len * 2;
                        uint n = BitConverter.ToUInt32(d, pos); pos += 4;
                        var items = ReadObjects(d, ref pos, n);
                        cats.Add(new RawCategory(CategoryKind.Custom, IndirectString(name), 0, items));
                        break;
                    }
                    case 1:
                    {
                        int id = BitConverter.ToInt32(d, pos); pos += 4;
                        cats.Add(new RawCategory(CategoryKind.Known, "", id, new()));
                        break;
                    }
                    case 2:
                    {
                        uint n = BitConverter.ToUInt32(d, pos); pos += 4;
                        cats.Add(new RawCategory(CategoryKind.Tasks, "", 0, ReadObjects(d, ref pos, n)));
                        break;
                    }
                    default:
                        return cats;
                }
                if (pos + 4 > d.Length || BitConverter.ToUInt32(d, pos) != CategoryFooter) return cats;
                pos += 4;
            }
        }
        catch { /* beschädigt oder unbekannt: was wir haben, reicht */ }
        return cats;
    }

    private static List<Entry> ReadObjects(byte[] d, ref int pos, uint count)
    {
        var list = new List<Entry>();
        for (uint i = 0; i < count; i++)
        {
            var clsid = new Guid(d.AsSpan(pos, 16)); pos += 16;
            object? obj = null;
            try
            {
                var iid = typeof(IPersistStream).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, IntPtr.Zero, 1 /* INPROC_SERVER */, ref iid, out obj));
                var stream = new ReadStream(d, pos);
                ((IPersistStream)obj).Load(stream);
                pos = (int)stream.Position; // Länge des Objekts kennt nur das Objekt selbst
                if (FromObject(obj) is { } e) list.Add(e);
            }
            finally { Release(obj); }
        }
        return list;
    }

    private static Entry? FromObject(object obj)
    {
        if (obj is IShellLinkW link) return FromLink(link);

        // Ein reines Shell-Element (IShellItem o. ä.): öffnen wie im Explorer
        if (SHGetIDListFromObject(obj, out var pidl) != 0 || pidl == IntPtr.Zero) return null;
        try { return FromIdList(pidl, null); }
        finally { CoTaskMemFree(pidl); }
    }

    // ---------- automaticDestinations-ms ----------

    /// <summary>
    /// Ein OLE-Verbunddokument: Stream "DestList" mit den Metadaten (angeheftet, Zugriffe, Zeit),
    /// dazu je Eintrag ein Stream (Name = Eintragsnummer in Hex) mit einer gespeicherten Verknüpfung.
    /// </summary>
    private static AutoList ParseAutomatic(byte[] file)
    {
        var empty = new AutoList(new(), new(), new());
        var streams = CompoundFile.ReadStreams(file);
        if (!streams.TryGetValue("DestList", out var dl) || dl.Length < 32) return empty;

        int version = BitConverter.ToInt32(dl, 0);
        int count = BitConverter.ToInt32(dl, 4);
        var raw = new List<(uint Number, float Score, long Time, int Pin, string Path)>();
        int pos = 32;
        for (int i = 0; i < count; i++)
        {
            int fixedSize = version >= 2 ? 130 : 114;
            if (pos + fixedSize > dl.Length) break;
            uint number = BitConverter.ToUInt32(dl, pos + 88);
            float score = BitConverter.ToSingle(dl, pos + 96);
            long time = BitConverter.ToInt64(dl, pos + 100);
            int pin = BitConverter.ToInt32(dl, pos + 108);
            int len = BitConverter.ToUInt16(dl, pos + fixedSize - 2);
            if (pos + fixedSize + len * 2 > dl.Length) break;
            string path = Encoding.Unicode.GetString(dl, pos + fixedSize, len * 2);
            pos += fixedSize + len * 2 + (version >= 2 ? 4 : 0);
            raw.Add((number, score, time, pin, path));
        }

        // Nur laden, was gezeigt werden kann (Explorer hat oft Hunderte Einträge)
        var pinned = raw.Where(r => r.Pin >= 0).OrderBy(r => r.Pin).Take(MaxPinned).ToList();
        int reserve = MaxPerCategory + pinned.Count;
        var recent = raw.OrderByDescending(r => r.Time).Take(reserve).ToList();
        var frequent = raw.OrderByDescending(r => r.Score).ThenByDescending(r => r.Time).Take(reserve).ToList();

        var loaded = new Dictionary<uint, Entry?>();
        Entry? Get((uint Number, float Score, long Time, int Pin, string Path) r)
        {
            if (loaded.TryGetValue(r.Number, out var e)) return e;
            e = null;
            if (streams.TryGetValue(r.Number.ToString("x"), out var data))
                try { e = LoadLink(data, r.Path); } catch { }
            return loaded[r.Number] = e;
        }
        List<Entry> Resolve(IEnumerable<(uint, float, long, int, string)> list) =>
            list.Select(Get).OfType<Entry>().ToList();

        return new AutoList(Resolve(pinned), Resolve(recent), Resolve(frequent));
    }

    private static Entry? LoadLink(byte[] data, string fallbackPath)
    {
        object? obj = null;
        try
        {
            var iid = typeof(IPersistStream).GUID;
            var clsid = ShellLinkClsid;
            if (CoCreateInstance(clsid, IntPtr.Zero, 1, ref iid, out obj) != 0 || obj == null) return null;
            ((IPersistStream)obj).Load(new ReadStream(data, 0));
            var e = FromLink((IShellLinkW)obj);
            if (e != null && string.IsNullOrEmpty(e.Title) && fallbackPath.Length > 0)
                e = e with { Title = Path.GetFileName(fallbackPath.TrimEnd('\\')) is { Length: > 0 } n ? n : fallbackPath };
            return e;
        }
        finally { Release(obj); }
    }

    // ---------- Verknüpfungen und Shell-Elemente auswerten ----------

    private static Entry? FromLink(IShellLinkW link)
    {
        var sb = new StringBuilder(2048);
        string? Read(Func<StringBuilder, int> f)
        {
            sb.Clear();
            try { return f(sb) == 0 && sb.Length > 0 ? sb.ToString() : null; } catch { return null; }
        }

        string? file = Read(b => link.GetPath(b, b.Capacity, IntPtr.Zero, 0));
        string? args = Read(b => link.GetArguments(b, b.Capacity));
        string? dir = Read(b => link.GetWorkingDirectory(b, b.Capacity));
        string? desc = Read(b => link.GetDescription(b, b.Capacity));
        int iconIndex = 0;
        string? iconFile = Read(b => link.GetIconLocation(b, b.Capacity, out iconIndex));
        int show = 1;
        try { link.GetShowCmd(out show); } catch { }

        string? title = null;
        bool separator = false;
        if (link is IPropertyStore props)
        {
            title = ReadString(props, PKEY_Title);
            separator = ReadBool(props, PKEY_AppUserModel_IsDestListSeparator);
        }
        if (separator)
            return new Entry("-", null, null, new LaunchInfo(null, null, null, 1, null), "-", true);

        byte[]? idList = null;
        string? displayName = null, parsingName = null;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (link.GetIDList(out pidl) == 0 && pidl != IntPtr.Zero)
            {
                idList = CopyIdList(pidl);
                (displayName, parsingName) = Names(pidl);
            }
        }
        catch { }

        if (file == null && idList == null) return null;

        title = IndirectString(title);
        desc = IndirectString(desc);
        if (string.IsNullOrWhiteSpace(title)) title = !string.IsNullOrWhiteSpace(desc) && args == null ? desc : null;
        if (string.IsNullOrWhiteSpace(title)) title = displayName ?? (file != null ? Path.GetFileNameWithoutExtension(file) : "");
        // Tooltip: Beschreibung der Verknüpfung; bei Dateien/Ordnern der Pfad (bei Aufgaben nicht die Exe)
        string? tip = !string.IsNullOrWhiteSpace(desc) && desc != title ? desc
                    : string.IsNullOrEmpty(args) ? file ?? parsingName : null;

        ImageSource? icon = null;
        if (!string.IsNullOrEmpty(iconFile)) icon = IconFromFile(Environment.ExpandEnvironmentVariables(iconFile), iconIndex);
        if (icon == null && pidl != IntPtr.Zero) icon = IconFromIdList(pidl, parsingName ?? file);
        else if (icon == null && file != null) icon = IconFromPath(file);
        if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);

        string key = ((file ?? parsingName ?? title) + "|" + args).ToLowerInvariant();
        return new Entry(title, tip, icon, new LaunchInfo(file, args, dir, show == 0 ? 1 : show, idList), key, false);
    }

    private static Entry? FromIdList(IntPtr pidl, string? fallbackTitle)
    {
        var (display, parsing) = Names(pidl);
        string title = display ?? fallbackTitle ?? parsing ?? "";
        if (title.Length == 0) return null;
        var icon = IconFromIdList(pidl, parsing);
        return new Entry(title, parsing, icon, new LaunchInfo(null, null, null, 1, CopyIdList(pidl)),
            (parsing ?? title).ToLowerInvariant() + "|", false);
    }

    private static (string? Display, string? Parsing) Names(IntPtr pidl)
    {
        string? display = null, parsing = null;
        if (SHGetNameFromIDList(pidl, SIGDN_NORMALDISPLAY, out var p) == 0) { display = Marshal.PtrToStringUni(p); CoTaskMemFree(p); }
        if (SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out p) == 0) { parsing = Marshal.PtrToStringUni(p); CoTaskMemFree(p); }
        return (display, parsing);
    }

    private static byte[] CopyIdList(IntPtr pidl)
    {
        var bytes = new byte[ILGetSize(pidl)];
        Marshal.Copy(pidl, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Texte wie "@shell32.dll,-21787" oder "@{Paket?ms-resource://…}" auflösen.</summary>
    private static string IndirectString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s[0] != '@') return s;
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s;
    }

    // ---------- Starten (wie die Taskleiste) ----------

    private static void Execute(LaunchInfo li)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var sei = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_DOENVSUBST | SEE_MASK_FLAG_LOG_USAGE,
                lpParameters = string.IsNullOrEmpty(li.Arguments) ? null : li.Arguments,
                lpDirectory = string.IsNullOrEmpty(li.Directory) ? null : Environment.ExpandEnvironmentVariables(li.Directory),
                nShow = li.ShowCmd,
            };
            // Programme in Paketordnern lassen sich nicht direkt starten und wechseln mit Updates den Ordner:
            // Start über den App-Eintrag (die Argumente kommen dort an), die ID-Liste nur als letzter Ausweg
            if (PackagedApps.LaunchFile(li.File, PackagedApps.AumidForFamily) is { } file)
            {
                sei.lpFile = file;
                // Das Arbeitsverzeichnis zeigt sonst womöglich auf den alten Versionsordner
                if (PackagedApps.IsPackageExe(li.File)) sei.lpDirectory = null;
            }
            else if (li.IdList is { Length: > 0 } ids)
            {
                pidl = Marshal.AllocCoTaskMem(ids.Length);
                Marshal.Copy(ids, 0, pidl, ids.Length);
                sei.lpIDList = pidl;
                sei.fMask |= SEE_MASK_INVOKEIDLIST;
            }
            else return;
            ShellExecuteEx(ref sei);
        }
        catch { }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    // ---------- Symbole ----------

    /// <summary>Kantenlänge in Pixeln für ein 16-DIP-Symbol bei der aktuellen Skalierung.</summary>
    private static int IconPixels
    {
        get
        {
            uint dpi = 96;
            try { dpi = GetDpiForSystem(); } catch { }
            return Math.Clamp((int)Math.Round(16 * dpi / 96.0), 16, 32);
        }
    }

    private static ImageSource? CachedIcon(string key, Func<ImageSource?> make)
    {
        lock (Gate)
            if (IconCache.TryGetValue(key, out var hit)) return hit;
        ImageSource? img = null;
        try { img = make(); } catch { }
        lock (Gate)
        {
            if (IconCache.Count > 500) IconCache.Clear();
            IconCache[key] = img;
        }
        return img;
    }

    private static ImageSource? IconFromFile(string file, int index)
    {
        int size = IconPixels;
        return CachedIcon($"ico|{size}|{file}|{index}", () =>
        {
            if (SHDefExtractIcon(file, index, 0, out var hIcon, IntPtr.Zero, (uint)size) != 0 || hIcon == IntPtr.Zero) return null;
            try { return FromHIcon(hIcon, size); }
            finally { DestroyIcon(hIcon); }
        });
    }

    private static ImageSource? IconFromPath(string path)
    {
        if (path.StartsWith(@"\\")) return IconForOfflinePath(path);
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj) != 0 || obj == null) return null;
        try { return CachedIcon($"item|{IconPixels}|{path}", () => FromFactory((IShellItemImageFactory)obj)); }
        finally { Release(obj); }
    }

    private static ImageSource? IconFromIdList(IntPtr pidl, string? name)
    {
        // Netzwerkpfade nicht anfassen – ein schlafender Server würde das Menü blockieren
        if (name != null && name.StartsWith(@"\\")) return IconForOfflinePath(name);
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromIDList(pidl, ref iid, out var obj) != 0 || obj == null) return null;
        try
        {
            return name != null
                ? CachedIcon($"item|{IconPixels}|{name}", () => FromFactory((IShellItemImageFactory)obj))
                : FromFactory((IShellItemImageFactory)obj);
        }
        finally { Release(obj); }
    }

    private static ImageSource? IconForOfflinePath(string path)
    {
        bool folder = !Path.HasExtension(path);
        return CachedIcon($"offline|{folder}|{Path.GetExtension(path)}", () =>
        {
            var fi = new SHFILEINFO();
            uint attr = folder ? 0x10u /* DIRECTORY */ : 0x80u /* NORMAL */;
            if (SHGetFileInfo(path, attr, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1 | 0x10 /* ICON|SMALLICON|USEFILEATTRIBUTES */) == IntPtr.Zero
                || fi.hIcon == IntPtr.Zero) return null;
            try { return FromHIcon(fi.hIcon, 16); }
            finally { DestroyIcon(fi.hIcon); }
        });
    }

    private static ImageSource? FromFactory(IShellItemImageFactory factory)
    {
        int size = IconPixels;
        if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
        try { return FromHBitmap(hbmp, size); }
        finally { DeleteObject(hbmp); }
    }

    private static ImageSource? FromHIcon(IntPtr hIcon, int size)
    {
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        // Kopie ohne Bindung an diesen Thread – eingefroren, damit der UI-Thread sie benutzen darf
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0).CopyPixels(px, stride, 0);
        return Frozen(w, h, px, size);
    }

    /// <summary>Pixel einer Bitmap als BGRA, oberste Zeile zuerst.</summary>
    private static byte[]? ReadHBitmap(IntPtr hbmp, out int w, out int h)
    {
        w = h = 0;
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        w = bm.bmWidth;
        h = Math.Abs(bm.bmHeight);
        var px = new byte[w * 4 * h];
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = GetDC(IntPtr.Zero);
        try { return GetDIBits(dc, hbmp, 0, (uint)h, px, ref bi, 0) == 0 ? null : px; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>Nur das Symbol eines Shell-Eintrags (Datei oder shell:-Pfad) in dieser Größe; null, wenn es keines gibt.</summary>
    internal static byte[]? RenderShellItem(string parsingName, int size, out int w, out int h)
    {
        w = h = 0;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) != 0 || obj == null) return null;
        try
        {
            if (((IShellItemImageFactory)obj).GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try { return ReadHBitmap(hbmp, out w, out h); }
            finally { DeleteObject(hbmp); }
        }
        finally { Release(obj); }
    }

    /// <summary>Lässt sich der Shell-Pfad auflösen?</summary>
    internal static bool ShellItemExists(string parsingName)
    {
        var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
        if (SHCreateItemFromParsingNameRaw(parsingName, IntPtr.Zero, ref iid, out var psi) != 0 || psi == IntPtr.Zero) return false;
        Marshal.Release(psi);
        return true;
    }

    /// <summary>In einer Verknüpfung eingetragene AppUserModelID.</summary>
    internal static string? ShortcutAppId(string lnkPath) => Try(() => FileProperty(lnkPath));

    private static ImageSource? FromHBitmap(IntPtr hbmp, int size)
    {
        if (ReadHBitmap(hbmp, out int w, out int h) is not { } px) return null;

        // Bitmaps ohne Alphakanal wären sonst unsichtbar
        bool anyAlpha = false;
        for (int i = 3; i < px.Length && !anyAlpha; i += 4) anyAlpha = px[i] != 0;
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 0xFF;
        return Frozen(w, h, px, size);
    }

    private static ImageSource Frozen(int w, int h, byte[] px, int size)
    {
        // DPI so wählen, dass das Bild 16 DIPs groß ist
        double dpi = 96.0 * Math.Max(w, size) / 16;
        var bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------- AppID ermitteln ----------

    private static string? Try(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    private static readonly Guid ApplicationResolverClsid = new("660b90c8-73a9-4b58-8cae-355b7f55341b");

    private static IApplicationResolver8? Resolver()
    {
        var iid = typeof(IApplicationResolver8).GUID;
        return CoCreateInstance(ApplicationResolverClsid, IntPtr.Zero, 1, ref iid, out var obj) == 0 ? obj as IApplicationResolver8 : null;
    }

    /// <summary>Die AppID, die auch die Taskleiste für dieses Fenster benutzt (inkl. Standard-AppID ohne AUMID).</summary>
    private static string? ResolverForWindow(IntPtr hwnd)
    {
        var r = Resolver();
        if (r == null) return null;
        try { return r.GetAppIDForWindow(hwnd, out var p, out _, out _, out _) == 0 ? TakeString(p) : null; }
        finally { Release(r); }
    }

    private static string? ResolverForShortcut(string path)
    {
        var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
        if (SHCreateItemFromParsingNameRaw(path, IntPtr.Zero, ref iid, out var psi) != 0 || psi == IntPtr.Zero) return null;
        var r = Resolver();
        try { return r != null && r.GetAppIDForShortcut(psi, out var p) == 0 ? TakeString(p) : null; }
        finally
        {
            Marshal.Release(psi);
            Release(r);
        }
    }

    private static string? WindowProperty(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try { return ReadString(store, PKEY_AppUserModel_ID); }
        finally { Release(store); }
    }

    private static string? FileProperty(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0 /* GPS_DEFAULT */, ref iid, out var store) != 0 || store == null) return null;
        try { return ReadString(store, PKEY_AppUserModel_ID); }
        finally { Release(store); }
    }

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { CoTaskMemFree(p); }
    }

    /// <summary>
    /// Standard-AppID eines Programms ohne eigene AUMID: der Pfad, bei dem der bekannte Ordner
    /// durch seine GUID ersetzt ist – z. B. "{6D809377-6AF0-444B-8957-A3773F02200E}\Notepad++\notepad++.exe".
    /// </summary>
    public static string DefaultAppId(string exePath)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(exePath));
        string? bestGuid = null, bestPath = null;
        foreach (var id in KnownFolders)
        {
            var g = id;
            if (SHGetKnownFolderPath(ref g, 0x8000 /* KF_FLAG_DONT_VERIFY */, IntPtr.Zero, out var p) != 0) continue;
            string? folder = TakeString(p)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(folder)) continue;
            if (!full.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (bestPath == null || folder.Length > bestPath.Length) { bestPath = folder; bestGuid = id.ToString("B").ToUpperInvariant(); }
        }
        return bestPath == null ? full : bestGuid + full[bestPath.Length..];
    }

    private static readonly Guid[] KnownFolders =
    [
        new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), // System
        new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), // SystemX86
        new("F38BF404-1D43-42F2-9305-67DE0B28FC23"), // Windows
        new("6D809377-6AF0-444B-8957-A3773F02200E"), // ProgramFilesX64
        new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), // ProgramFilesX86
        new("6365D5A7-0F0D-45E5-87F6-0DA56B6A4F7D"), // ProgramFilesCommonX64
        new("DE974D24-D9C6-4D3E-BF91-F4455120B917"), // ProgramFilesCommonX86
        new("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"), // UserProgramFiles
        new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), // LocalAppData
        new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), // RoamingAppData
        new("5E6C858F-0E22-4760-9AFE-EA3317B67173"), // Profile
    ];

    // ---------- Eigenschaften ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

    private static readonly PROPERTYKEY PKEY_Title = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    private static readonly PROPERTYKEY PKEY_AppUserModel_IsDestListSeparator = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 6);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public short boolVal;
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, out pv) != 0) return null;
            return pv.vt is 31 /* LPWSTR */ or 8 /* BSTR */ ? Marshal.PtrToStringUni(pv.ptr) : null;
        }
        catch { return null; }
        finally { PropVariantClear(ref pv); }
    }

    private static bool ReadBool(IPropertyStore store, PROPERTYKEY key)
    {
        var pv = new PROPVARIANT();
        try { return store.GetValue(ref key, out pv) == 0 && pv.vt == 11 /* BOOL */ && pv.boolVal != 0; }
        catch { return false; }
        finally { PropVariantClear(ref pv); }
    }

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o))
            try { Marshal.ReleaseComObject(o); } catch { }
    }

    // ---------- Stream über ein Byte-Array (für IPersistStream.Load) ----------

    private sealed class ReadStream(byte[] data, long start) : IStream
    {
        public long Position { get; private set; } = start;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n = (int)Math.Max(0, Math.Min(cb, data.Length - Position));
            Array.Copy(data, Position, pv, 0, n);
            Position += n;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }

        public void Seek(long offset, int origin, IntPtr plibNewPosition)
        {
            long basePos = origin switch { 0 => 0, 1 => Position, _ => data.Length };
            Position = Math.Clamp(basePos + offset, 0, data.Length);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, Position);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { type = 2, cbSize = data.Length };
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("read only", unchecked((int)0x80030005));
        public void SetSize(long libNewSize) => throw new COMException("read only", unchecked((int)0x80030005));
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Clone(out IStream ppstm) => ppstm = new ReadStream(data, Position);
    }

    // ---------- OLE-Verbunddokument (nur lesen, flach) ----------

    private static class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE, MaxRegular = 0xFFFFFFFA;

        public static Dictionary<string, byte[]> ReadStreams(byte[] d)
        {
            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (d.Length < 512 || BitConverter.ToUInt64(d, 0) != 0xE11AB1A1E011CFD0UL) return res;
            try
            {
                int ss = 1 << BitConverter.ToUInt16(d, 0x1E);
                int ms = 1 << BitConverter.ToUInt16(d, 0x20);
                int fatCount = BitConverter.ToInt32(d, 0x2C);
                uint dirStart = BitConverter.ToUInt32(d, 0x30);
                uint cutoff = BitConverter.ToUInt32(d, 0x38);
                uint miniFatStart = BitConverter.ToUInt32(d, 0x3C);
                uint difat = BitConverter.ToUInt32(d, 0x44);
                int difatCount = BitConverter.ToInt32(d, 0x48);

                // FAT-Sektoren: 109 im Kopf, der Rest in DIFAT-Sektoren
                var fatSectors = new List<uint>();
                for (int i = 0; i < 109; i++)
                    if (BitConverter.ToUInt32(d, 0x4C + i * 4) is var s && s < MaxRegular) fatSectors.Add(s);
                for (int k = 0; k < difatCount && difat < MaxRegular; k++)
                {
                    long off = (difat + 1L) * ss;
                    if (off + ss > d.Length) break;
                    for (int j = 0; j < ss / 4 - 1; j++)
                        if (BitConverter.ToUInt32(d, (int)off + j * 4) is var s && s < MaxRegular) fatSectors.Add(s);
                    difat = BitConverter.ToUInt32(d, (int)off + ss - 4);
                }

                var fat = new List<uint>();
                foreach (var s in fatSectors.Take(fatCount))
                {
                    long off = (s + 1L) * ss;
                    if (off + ss > d.Length) break;
                    for (int j = 0; j < ss / 4; j++) fat.Add(BitConverter.ToUInt32(d, (int)off + j * 4));
                }

                byte[] Regular(uint start, long length) =>
                    Chain(start, fat, ss, length, s => (s + 1L) * ss, d);

                var dir = Regular(dirStart, long.MaxValue);
                if (dir.Length < 128) return res;

                // Wurzel-Eintrag: Start und Größe des Mini-Streams
                uint rootStart = BitConverter.ToUInt32(dir, 0x74);
                long rootSize = BitConverter.ToInt64(dir, 0x78);
                if (ss == 512) rootSize &= 0xFFFFFFFF;
                var mini = Regular(rootStart, rootSize);
                var miniFatBytes = Regular(miniFatStart, long.MaxValue);
                var miniFat = new List<uint>();
                for (int j = 0; j + 4 <= miniFatBytes.Length; j += 4) miniFat.Add(BitConverter.ToUInt32(miniFatBytes, j));

                for (int e = 128; e + 128 <= dir.Length; e += 128)
                {
                    if (dir[e + 0x42] != 2) continue; // nur Streams
                    int nameLen = BitConverter.ToUInt16(dir, e + 0x40);
                    if (nameLen < 2 || nameLen > 64) continue;
                    string name = Encoding.Unicode.GetString(dir, e, nameLen - 2);
                    uint start = BitConverter.ToUInt32(dir, e + 0x74);
                    long size = BitConverter.ToInt64(dir, e + 0x78);
                    if (ss == 512) size &= 0xFFFFFFFF;
                    res[name] = size < cutoff
                        ? Chain(start, miniFat, ms, size, s => (long)s * ms, mini)
                        : Regular(start, size);
                }
            }
            catch { }
            return res;
        }

        private static byte[] Chain(uint start, List<uint> table, int sectorSize, long length, Func<uint, long> offsetOf, byte[] source)
        {
            using var ms = new MemoryStream();
            uint s = start;
            for (int guard = 0; s < MaxRegular && s != EndOfChain && guard <= table.Count && ms.Length < length; guard++)
            {
                long off = offsetOf(s);
                if (off < 0 || off >= source.Length) break;
                int n = (int)Math.Min(sectorSize, source.Length - off);
                ms.Write(source, (int)off, n);
                if (s >= table.Count) break;
                s = table[(int)s];
            }
            if (length != long.MaxValue && ms.Length > length) ms.SetLength(length);
            return ms.ToArray();
        }
    }

    // ---------- Win32 / COM ----------

    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const int SIIGBF_ICONONLY = 0x4;
    private const uint SEE_MASK_INVOKEIDLIST = 0xC, SEE_MASK_DOENVSUBST = 0x200, SEE_MASK_FLAG_LOG_USAGE = 0x04000000;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.Interface)] IStream pStm);
        void Save([MarshalAs(UnmanagedType.Interface)] IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);
        void GetSizeMax(out long pcbSize);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    /// <summary>Nicht dokumentiert (Windows 8+): damit ermittelt der Explorer die AppID von Fenstern und Verknüpfungen.</summary>
    [ComImport, Guid("de25675a-72de-44b4-9373-05170450c140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationResolver8
    {
        [PreserveSig] int GetAppIDForShortcut(IntPtr psi, out IntPtr ppszAppID);
        [PreserveSig] int GetAppIDForShortcutObject(IntPtr psl, IntPtr psi, out IntPtr ppszAppID);
        [PreserveSig] int GetAppIDForWindow(IntPtr hwnd, out IntPtr ppszAppID, out int pfPinningPrevented, out int pfExplicitAppID, out int pfEmbeddedShortcutValid);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, IntPtr outer, uint ctx, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object obj);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr ppidl);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr ppszName);
    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingNameRaw(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid, out IPropertyStore ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr hiconLarge, IntPtr hiconSmall, uint iconSize);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO sei);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string src, StringBuilder buf, int cch, IntPtr reserved);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr h, int cb, out BITMAP bm);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
}

using System.Windows;
using System.Windows.Media;

namespace GlassBar;

/// <summary>
/// Helles oder dunkles Design für Dock, Aufklapp-Fenster und Menüs (Einstellung „Design“: Dunkel, Hell, wie Windows).
/// Die Farben liegen als Ressourcen in App.xaml und werden hier ausgetauscht; alles, was sie über
/// {DynamicResource …} bzw. SetResourceReference nutzt, färbt sich sofort um.
/// Das Einstellungsfenster bleibt dunkel (es überschreibt die Schlüssel in seinen eigenen Ressourcen).
/// </summary>
internal static class Theme
{
    /// <summary>Gerade aktiv: helles Design.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>
    /// Wie weit die gewählte Glasfarbe im hellen Design Richtung Weiß gemischt wird (0 = gar nicht, 1 = weiß).
    /// So bleibt der Farbton erhalten (Blaugrau wird hellblaugrau), und dunkle Schrift ist lesbar.
    /// </summary>
    private const double LightMix = 0.75;

    private static bool _watching;

    /// <summary>Design aus der Einstellung bestimmen und anwenden. Gibt zurück, ob sich etwas geändert hat.</summary>
    public static bool Apply()
    {
        bool light = App.Config.Theme switch
        {
            "Light" => true,
            "Auto" => WindowsUsesLight(),
            _ => false,
        };
        bool changed = light != IsLight || !_applied;
        _applied = true;
        IsLight = light;

        var r = Application.Current.Resources;
        r["Fg"] = Brush(light ? 0xFF1A1A1A : 0xFFFFFFFF);
        r["FgSub"] = Brush(light ? 0xB0000000 : 0xD0FFFFFF);
        r["FgDim"] = Brush(light ? 0x80000000 : 0x80FFFFFF);
        r["FgDisabled"] = Brush(light ? 0x60000000 : 0x60FFFFFF);
        r["Hover"] = Brush(light ? 0x14000000 : 0x22FFFFFF);
        r["Pressed"] = Brush(light ? 0x22000000 : 0x33FFFFFF);
        r["SlotHover"] = Brush(light ? 0x12000000 : 0x1EFFFFFF);
        r["Line"] = Brush(light ? 0x24000000 : 0x30FFFFFF);
        r["Chip"] = Brush(light ? 0x14000000 : 0x3AFFFFFF);
        r["PillBg"] = Brush(light ? 0x0C000000 : 0x12FFFFFF);
        r["PillBorder"] = Brush(light ? 0x14000000 : 0x18FFFFFF);
        r["Cover"] = Brush(light ? 0x1A000000 : 0x30FFFFFF);
        r["Track"] = Brush(light ? 0x30000000 : 0x40FFFFFF);
        r["Dot"] = Brush(light ? 0xB0000000 : 0xD0FFFFFF);
        r["PeekHover"] = Brush(light ? 0x1A000000 : 0x26FFFFFF);
        r["PeekLine"] = Brush(light ? 0x40000000 : 0x50FFFFFF);
        r["MenuBg"] = Brush(light ? 0xF8F9F9F9 : 0xF82B2B2B);
        r["MenuBorder"] = Brush(light ? 0x1F000000 : 0x30FFFFFF);
        r["MenuHover"] = Brush(light ? 0x0F000000 : 0x18FFFFFF);
        r["MenuLine"] = Brush(light ? 0x1F000000 : 0x28FFFFFF);
        r["Field"] = Brush(light ? 0x0C000000 : 0x18FFFFFF);
        r["FieldBorder"] = Brush(light ? 0x1A000000 : 0x20FFFFFF);
        r["DockSheen"] = Gradient(light ? new uint[] { 0x40FFFFFF, 0x18FFFFFF, 0x10FFFFFF } : new uint[] { 0x14FFFFFF, 0x06FFFFFF, 0x08FFFFFF });
        r["DockEdge"] = Gradient(light ? new uint[] { 0x50FFFFFF, 0x1A000000 } : new uint[] { 0x40FFFFFF, 0x14FFFFFF });

        // Win32-Menüs (Kontextmenüs der Shell, z. B. im Desktop-Ordner) passend dazu
        Native.SetMenuTheme(light);

        if (!_watching)
        {
            _watching = true;
            // „Wie Windows“: auf Änderungen in den Windows-Einstellungen hören
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || App.Config.Theme != "Auto") return;
                Application.Current.Dispatcher.BeginInvoke(() => { if (Apply()) App.Instance.ApplySettingsToDocks(); });
            };
        }
        return changed;
    }

    private static bool _applied;

    /// <summary>
    /// Glasfarbe (ARGB) für das aktuelle Design. Deckkraft und Farbe kommen in beiden Designs aus den Einstellungen;
    /// im hellen Design wird die Farbe nur aufgehellt (siehe <see cref="LightMix"/>).
    /// </summary>
    public static uint Tint(Config c, double opacityPercent)
    {
        uint argb = c.Tint(opacityPercent);
        if (!IsLight) return argb;
        static uint Lighten(uint ch) => (uint)Math.Round(ch + (255 - ch) * LightMix);
        uint r = Lighten((argb >> 16) & 0xFF), g = Lighten((argb >> 8) & 0xFF), b = Lighten(argb & 0xFF);
        return (argb & 0xFF000000) | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Glas für ein Fenster von Dock, Aufklapp-Fenstern oder Menüs.
    /// Dunkel: Acryl von Windows mit der Tönung. Hell: Acryl ginge nicht – Windows legt dort aus der Glasfarbe eine
    /// fast deckende helle Schicht darüber, unabhängig von der Deckkraft. Darum hell nur die Unschärfe von Windows
    /// und die Tönung als Fensterhintergrund in WPF: so wirken Deckkraft und Farbe genau wie eingestellt.
    /// </summary>
    public static void ApplyGlass(Window w, IntPtr hwnd, double opacityPercent)
    {
        uint tint = Tint(App.Config, opacityPercent);
        if (hwnd != IntPtr.Zero) Native.ApplyGlass(hwnd, tint, plainBlur: IsLight);
        // Ohne Acryl (VM, Remotedesktop …): deckend in der Glasfarbe, sonst bliebe die Fläche schwarz
        w.Background = !Native.GlassAvailable ? Brush(tint | 0xFF000000) : IsLight ? Brush(tint) : Brushes.Transparent;
    }

    /// <summary>Pinsel aus den Design-Ressourcen (für Elemente, die im Code entstehen).</summary>
    public static Brush Get(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.White;

    /// <summary>Taskleiste/Startmenü in Windows auf hell gestellt? (Einstellungen › Personalisierung › Farben)</summary>
    private static bool WindowsUsesLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private static SolidColorBrush Brush(long argb) // long: Literale wie 0x14000000 sind int, 0xFF1A1A1A ist uint
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Gradient(uint[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i < stops.Length; i++)
        {
            uint c = stops[i];
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c),
                stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
        }
        g.Freeze();
        return g;
    }
}

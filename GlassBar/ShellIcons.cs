using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBar;

/// <summary>Symbole aus Windows-Dateien (z. B. imageres.dll) laden und auswählen.</summary>
public static class ShellIcons
{
    /// <summary>Auswahl schöner Windows-Symbole für den Desktop-Stapel (Datei,Index).</summary>
    public static readonly (string Spec, string Name)[] Gallery =
    {
        (@"%SystemRoot%\System32\imageres.dll,105", "Desktop"),
        (@"%SystemRoot%\System32\imageres.dll,104", "Bildschirm"),
        (@"%SystemRoot%\System32\imageres.dll,3", "Ordner"),
        (@"%SystemRoot%\System32\imageres.dll,265", "Explorer"),
        (@"%SystemRoot%\System32\imageres.dll,9", "Ordner (bunt)"),
        (@"%SystemRoot%\System32\imageres.dll,174", "Ordner (blau)"),
        (@"%SystemRoot%\System32\imageres.dll,179", "Ordner (türkis)"),
        (@"%SystemRoot%\System32\imageres.dll,178", "Videos"),
        (@"%SystemRoot%\System32\imageres.dll,108", "Bilder"),
        (@"%SystemRoot%\System32\imageres.dll,103", "Musik"),
        (@"%SystemRoot%\System32\imageres.dll,187", "3D-Objekte"),
        (@"%SystemRoot%\System32\imageres.dll,142", "Stapel"),
        (@"%SystemRoot%\System32\imageres.dll,204", "Stern"),
        (@"%SystemRoot%\System32\imageres.dll,218", "OneDrive"),
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[]? ids, int count, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int PickIconDlg(IntPtr owner, System.Text.StringBuilder path, int size, ref int index);

    /// <summary>"Datei,Index" laden (Umgebungsvariablen erlaubt). null, wenn es das Symbol nicht gibt.</summary>
    public static ImageSource? Load(string spec, int size = 256)
    {
        try
        {
            int comma = spec.LastIndexOf(',');
            string file = Environment.ExpandEnvironmentVariables(comma > 0 ? spec[..comma] : spec);
            int index = comma > 0 && int.TryParse(spec[(comma + 1)..], out var i) ? i : 0;
            var icons = new IntPtr[1];
            if (PrivateExtractIcons(file, index, size, size, icons, null, 1, 0) <= 0 || icons[0] == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(icons[0]); }
        }
        catch { return null; }
    }

    /// <summary>Der Windows-Dialog „Anderes Symbol“. Liefert "Datei,Index" oder null bei Abbruch.</summary>
    public static string? Pick(IntPtr owner, string? current)
    {
        string file = @"%SystemRoot%\System32\imageres.dll";
        int index = 0;
        if (!string.IsNullOrEmpty(current))
        {
            int comma = current.LastIndexOf(',');
            if (comma > 0) { file = current[..comma]; int.TryParse(current[(comma + 1)..], out index); }
        }
        var sb = new System.Text.StringBuilder(file, 1024);
        if (PickIconDlg(owner, sb, sb.Capacity, ref index) == 0) return null;
        return $"{sb},{index}";
    }
}

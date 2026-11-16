using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar;

/// <summary>Kleine Helfer für dunkle Menüs und Eingabedialoge.</summary>
public static class Ui
{
    public static MenuItem Item(string header, string glyph, Action action, bool enabled = true, bool isChecked = false)
    {
        var mi = new MenuItem { Header = header, Tag = glyph, IsEnabled = enabled, IsChecked = isChecked };
        mi.Click += (_, _) => action();
        return mi;
    }

    public static MenuItem Sub(string header, string glyph, IEnumerable<object?> children)
    {
        var mi = new MenuItem { Header = header, Tag = glyph };
        foreach (var c in children) mi.Items.Add(c ?? new Separator());
        return mi;
    }

    /// <summary>Zeigt ein Kontextmenü an der Maus. null = Trennlinie.</summary>
    public static ContextMenu Show(IEnumerable<object?> items, Action? onClosed = null)
    {
        var menu = new ContextMenu();
        foreach (var i in items) menu.Items.Add(i ?? new Separator());
        if (onClosed != null) menu.Closed += (_, _) => onClosed();
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>Einfacher dunkler Eingabedialog. Gibt null zurück, wenn abgebrochen.</summary>
    public static string? Ask(Window? owner, string title, string initial = "")
    {
        var box = new TextBox
        {
            Text = initial, FontSize = 13, Padding = new Thickness(8, 6, 8, 6), Foreground = Brushes.White,
            CaretBrush = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)Application.Current.Resources["Accent"], BorderThickness = new Thickness(0, 0, 0, 2),
        };
        Border Btn(string text, bool primary) => new()
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
            Background = primary ? new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0xC2, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13 },
        };
        var ok = Btn("OK", true);
        var cancel = Btn("Abbrechen", false);

        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Topmost = true,
            ShowInTaskbar = false, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Content = new Border
            {
                Width = 340, Padding = new Thickness(18), CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x2B, 0x2B, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                        box,
                        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, ok } },
                    },
                },
            },
        };
        string? result = null;
        ok.MouseLeftButtonUp += (_, _) => { result = box.Text.Trim(); win.Close(); };
        cancel.MouseLeftButtonUp += (_, _) => win.Close();
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Text.Trim(); win.Close(); }
            else if (e.Key == Key.Escape) win.Close();
        };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ---------- Shell-Helfer ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    public static void ShowProperties(string path) => SHObjectProperties(IntPtr.Zero, 2 /* SHOP_FILEPATH */, path, null);

    public static void RunAsAdmin(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); }
        catch { /* Abgebrochen (UAC) */ }
    }

    public static void Recycle(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GlassBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

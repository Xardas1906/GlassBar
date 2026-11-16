using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace GlassBar;

/// <summary>Ein Eintrag im Menü. <see cref="Separator"/> = Trennlinie.</summary>
public record MenuEntry(string Text, string? Glyph = null, Action? Action = null, string? Shortcut = null,
                        bool Enabled = true, IReadOnlyList<MenuEntry>? Children = null, ImageSource? Image = null,
                        bool IsChecked = false)
{
    public static readonly MenuEntry Separator = new("-");
    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>Ein Knopf in der Symbolleiste oben (Ausschneiden, Kopieren …).</summary>
public record MenuButton(string Text, string Glyph, Action Action, bool Enabled = true);

/// <summary>
/// Kontextmenü im Stil von Windows 11: Glas-Hintergrund, Symbolleiste oben, Einträge mit Symbol,
/// Tastenkürzel und Untermenüs. Nimmt keinen Fokus an und schließt sich bei Klick daneben oder Esc.
/// </summary>
public class ModernMenu : Window
{
    private static readonly List<ModernMenu> Open = new();
    public static bool AnyOpen => Open.Count > 0;
    private static DispatcherTimer? _watch;
    private static Action? _onAllClosed;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Line = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    private readonly int _level;
    private Point _anchor;          // Bildschirmposition in DIPs
    private Rect? _parentRow;       // bei Untermenüs: Zeile im Elternmenü (DIPs)
    private ModernMenu? _child;
    private readonly DispatcherTimer _subTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Zeigt das Menü an der Mausposition.</summary>
    public static void ShowAtCursor(IReadOnlyList<MenuButton>? buttons, IReadOnlyList<MenuEntry> entries, Action? onClosed = null)
    {
        CloseAll();
        _onAllClosed = onClosed;
        var p = System.Windows.Forms.Cursor.Position;
        var menu = new ModernMenu(0, buttons, entries);
        menu.ShowAt(PixelsToDips(new Point(p.X, p.Y)), null);
        StartWatch();
    }

    public static void CloseAll()
    {
        foreach (var m in Open.ToList()) m.Close();
        Open.Clear();
        _watch?.Stop();
        var cb = _onAllClosed;
        _onAllClosed = null;
        cb?.Invoke();
    }

    private ModernMenu(int level, IReadOnlyList<MenuButton>? buttons, IReadOnlyList<MenuEntry> entries)
    {
        _level = level;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        Foreground = Brushes.White;
        FontSize = 13;
        UseLayoutRounding = true;
        Left = Top = -30000;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1), CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(h) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
            Native.AddExStyle(h, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Native.ApplyGlass(h, App.Config.Tint(App.Config.MenuOpacity));
        };
        SizeChanged += (_, _) => Place();
        _subTimer.Tick += (_, _) => _subTimer.Stop();

        var root = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        if (buttons is { Count: > 0 })
        {
            var bar = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var b in buttons) bar.Children.Add(MakeButton(b));
            root.Children.Add(bar);
            root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(-4, 0, -4, 4) });
        }
        foreach (var e in entries)
            root.Children.Add(e.IsSeparator
                ? new Border { Height = 1, Background = Line, Margin = new Thickness(-4, 4, -4, 4) }
                : MakeRow(e));
        Content = root;
    }

    // ---------- Aufbau ----------

    private FrameworkElement MakeButton(MenuButton b)
    {
        var el = new Border
        {
            CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Padding = new Thickness(6, 8, 6, 6),
            Margin = new Thickness(1, 0, 1, 0), IsEnabled = b.Enabled, Opacity = b.Enabled ? 1 : 0.4, ToolTip = b.Text,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = b.Glyph, FontFamily = IconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = b.Text, FontSize = 11, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        el.MouseEnter += (_, _) => { el.Background = Hover; CloseChild(); };
        el.MouseLeave += (_, _) => el.Background = Brushes.Transparent;
        el.MouseLeftButtonUp += (_, _) => Run(b.Action);
        return el;
    }

    private FrameworkElement MakeRow(MenuEntry e)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = e.Image != null
            ? new Image { Source = e.Image, Width = 16, Height = 16 }
            : new TextBlock { Text = e.IsChecked ? "" : e.Glyph ?? "", FontFamily = IconFont, FontSize = 15 };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var text = new TextBlock { Text = e.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 24, 0) };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var right = new TextBlock
        {
            Text = e.Children != null ? "\u203A" : e.Shortcut ?? "",
            FontSize = e.Children != null ? 18 : 12,
            Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = new Border
        {
            CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = grid,
            IsEnabled = e.Enabled, Opacity = e.Enabled ? 1 : 0.4,
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = Hover;
            if (e.Children != null) OpenChild(e.Children, row);
            else CloseChild();
        };
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (e.Children != null) OpenChild(e.Children, row);
            else if (e.Action != null) Run(e.Action);
        };
        return row;
    }

    private static void Run(Action action)
    {
        CloseAll();
        Dispatcher.CurrentDispatcher.BeginInvoke(action);
    }

    // ---------- Untermenüs ----------

    private void OpenChild(IReadOnlyList<MenuEntry> entries, FrameworkElement row)
    {
        CloseChild();
        var tl = PixelsToDips(row.PointToScreen(new Point(0, 0)));
        var br = PixelsToDips(row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight)));
        _child = new ModernMenu(_level + 1, null, entries);
        _child.ShowAt(new Point(br.X + 2, tl.Y - 5), new Rect(tl, br));
    }

    private void CloseChild()
    {
        _child?.CloseChild();
        if (_child != null) { Open.Remove(_child); _child.Close(); }
        _child = null;
    }

    // ---------- Platzierung ----------

    private void ShowAt(Point anchor, Rect? parentRow)
    {
        _anchor = anchor;
        _parentRow = parentRow;
        Open.Add(this);
        Show();
        Place();
    }

    private void Place()
    {
        if (ActualWidth <= 0) return;
        var wa = Native.ScreenAt(_anchor);
        double x = _anchor.X, y = _anchor.Y;
        if (x + ActualWidth > wa.Right)
            x = _parentRow is { } r ? r.Left - ActualWidth - 2 : wa.Right - ActualWidth - 4;
        if (y + ActualHeight > wa.Bottom)
            y = _parentRow != null ? wa.Bottom - ActualHeight - 4 : _anchor.Y - ActualHeight;
        Left = Math.Max(wa.Left + 4, x);
        Top = Math.Max(wa.Top + 4, y);
    }

    private static Point PixelsToDips(Point px)
    {
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? new Window());
        return new Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY);
    }

    // ---------- Schließen bei Klick daneben / Esc ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private static void StartWatch()
    {
        _watch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) =>
        {
            if (Open.Count == 0) { _watch!.Stop(); return; }
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { CloseAll(); return; } // Esc
            bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0
                        || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            if (!pressed) return;
            var p = System.Windows.Forms.Cursor.Position;
            foreach (var m in Open)
            {
                var tl = m.PointToScreen(new Point(0, 0));
                var br = m.PointToScreen(new Point(m.ActualWidth, m.ActualHeight));
                if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y) return;
            }
            CloseAll();
        }, Dispatcher.CurrentDispatcher);
        _watch.Start();
    }
}


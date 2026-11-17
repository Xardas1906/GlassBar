using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;

namespace GlassBar;

/// <summary>
/// Basis für alle Aufklapp-Fenster über der Leiste: Acryl-Hintergrund, passt sich
/// dem Inhalt an, sitzt über dem Knopf und schließt sich, wenn die Maus weggeht.
/// </summary>
public class FlyoutBase : Window
{
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public FrameworkElement? Anchor { get; set; }

    /// <summary>Tönung des Glases (ARGB). Kleinerer Alpha-Wert = durchsichtiger.</summary>
    public uint GlassTint => App.Config.Tint(App.Config.FlyoutOpacity);

    /// <summary>Glas neu anwenden (nach Änderung in den Einstellungen).</summary>
    public void ApplyGlass() { if (Handle != IntPtr.Zero) Native.ApplyGlass(Handle, GlassTint); }

    /// <summary>Per Klick geöffnet: bleibt offen, bis man woanders hinklickt.</summary>
    public bool Sticky { get; private set; }

    /// <summary>Während eines Kontextmenüs nicht schließen.</summary>
    public bool SuppressClose { get; set; }

    protected IntPtr Handle { get; private set; }

    public FlyoutBase()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        Foreground = Brushes.White;
        Left = Top = -30000;
        UseLayoutRounding = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(Handle) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
            Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW);
            Native.ApplyGlass(Handle, GlassTint);
        };

        SizeChanged += (_, _) => Reposition();
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => { if (!Sticky) _closeTimer.Start(); };
        Activated += (_, _) => { Sticky = true; _closeTimer.Stop(); };
        Deactivated += (_, _) => { if (Sticky && !SuppressClose && IsVisible) SafeClose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };

        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (Sticky || SuppressClose || IsMouseOver || Anchor?.IsMouseOver == true) return;
            SafeClose();
        };
    }

    /// <summary>Statt schließen nur ausblenden (für schnelles Wiederöffnen).</summary>
    public bool KeepAlive { get; set; }

    /// <summary>Fenster wurde geschlossen oder ausgeblendet.</summary>
    public event Action? Dismissed;

    private bool _closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { _closing = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); Dismissed?.Invoke(); }

    public void SafeClose()
    {
        _closeTimer.Stop();
        if (KeepAlive)
        {
            if (!IsVisible) return;
            Sticky = false;
            Hide();
            Dismissed?.Invoke();
            return;
        }
        if (!_closing) { _closing = true; Close(); }
    }

    /// <summary>Zeigt das Fenster (erneut) über dem Knopf an.</summary>
    public virtual void ShowAt(FrameworkElement anchor)
    {
        Anchor = anchor;
        Sticky = false;
        if (!IsVisible) Show();
        Reposition();
    }

    /// <summary>Maus hat den Knopf verlassen.</summary>
    public void AnchorLeft() { if (!Sticky) _closeTimer.Start(); }
    public void CancelClose() => _closeTimer.Stop();

    public void MakeSticky()
    {
        Sticky = true;
        _closeTimer.Stop();
        Activate();
    }

    protected void Reposition()
    {
        if (Anchor == null || PresentationSource.FromVisual(Anchor) is not { CompositionTarget: { } ct }) return;
        var m = ct.TransformFromDevice;
        var tl = m.Transform(Anchor.PointToScreen(new Point(0, 0)));
        var br = m.Transform(Anchor.PointToScreen(new Point(Anchor.ActualWidth, Anchor.ActualHeight)));
        // Bildschirm des Docks, auf dem der Knopf liegt (es gibt ein Dock pro Bildschirm)
        var wa = Native.ScreenAt(new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2));

        double x = (tl.X + br.X) / 2 - ActualWidth / 2;
        Left = Math.Max(wa.Left + 8, Math.Min(x, wa.Right - ActualWidth - 8));
        Top = Math.Min(wa.Bottom, tl.Y) - ActualHeight - App.Config.FlyoutGap;
    }

    protected static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>Live-Vorschau aller Fenster einer App (wie beim Drüberfahren in Windows).</summary>
public class PreviewFlyout : FlyoutBase
{
    private readonly AppButton _app;
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
    private readonly List<(ApplicationWindow Win, Border Host, FrameworkElement Fallback, IntPtr Thumb)> _cards = new();

    public PreviewFlyout(AppButton app)
    {
        _app = app;
        Content = _panel;
        Title = "GlassBar Vorschau";
        SourceInitialized += (_, _) => Build();
        LayoutUpdated += (_, _) => UpdateThumbs();
        app.Windows.CollectionChanged += OnWindowsChanged;
        Closed += (_, _) =>
        {
            app.Windows.CollectionChanged -= OnWindowsChanged;
            Unregister();
        };
    }

    private void OnWindowsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (_app.Windows.Count == 0) SafeClose();
        else Build();
    }

    private void Unregister()
    {
        foreach (var c in _cards)
            if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
        _cards.Clear();
    }

    private void Build()
    {
        if (Handle == IntPtr.Zero) return;
        Unregister();
        _panel.Children.Clear();

        foreach (var w in _app.Windows.ToList())
        {
            var title = new TextBlock
            {
                FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
            };
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(ApplicationWindow.Title)) { Source = w });

            var icon = new Image { Width = 16, Height = 16 };
            icon.SetBinding(Image.SourceProperty, new Binding(nameof(ApplicationWindow.Icon)) { Source = w });

            var close = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                Visibility = Visibility.Hidden, ToolTip = L.T("Schließen"),
                Child = new TextBlock
                {
                    Text = "", FontFamily = IconFont, FontSize = 10, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; w.Close(); };

            var header = new DockPanel { Height = 26 };
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(close);
            header.Children.Add(icon);
            header.Children.Add(title);

            var fallback = new Image { Source = w.Icon, Width = 48, Height = 48, Opacity = 0.8 };
            var host = new Border { Height = 130, Margin = new Thickness(0, 8, 0, 0), Child = fallback };

            var card = new Border
            {
                Width = 236, Padding = new Thickness(8), Margin = new Thickness(2), CornerRadius = new CornerRadius(8),
                Background = w.State == ApplicationWindow.WindowState.Active
                    ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                Child = new StackPanel { Children = { header, host } },
            };
            var normalBg = card.Background;
            card.MouseEnter += (_, _) => { card.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); close.Visibility = Visibility.Visible; };
            card.MouseLeave += (_, _) => { card.Background = normalBg; close.Visibility = Visibility.Hidden; };
            card.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) { w.BringToFront(); SafeClose(); }
                else if (e.ChangedButton == MouseButton.Middle) w.Close();
            };

            IntPtr thumb = IntPtr.Zero;
            // Auch minimierte Fenster: Windows hält das letzte Bild vor dem Minimieren bereit (wie die Taskleiste).
            // Liefert es nichts, bleibt das Programmsymbol darunter sichtbar.
            if (Native.DwmRegisterThumbnail(Handle, w.Handle, out var t) == 0)
                thumb = t;

            _cards.Add((w, host, fallback, thumb));
            _panel.Children.Add(card);
        }
        _lastRects.Clear();
    }

    private readonly Dictionary<IntPtr, Native.RECT> _lastRects = new();

    private void UpdateThumbs()
    {
        if (_cards.Count == 0 || PresentationSource.FromVisual(this) is not { CompositionTarget: { } ct }) return;
        var toDevice = ct.TransformToDevice;

        foreach (var (win, host, fallback, thumb) in _cards)
        {
            if (thumb == IntPtr.Zero || !host.IsLoaded) continue;
            Rect r;
            try { r = host.TransformToAncestor(this).TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight)); }
            catch { continue; }
            r = Rect.Transform(r, toDevice);

            if (Native.DwmQueryThumbnailSourceSize(thumb, out var src) != 0 || src.X <= 0 || src.Y <= 0) continue;
            double scale = Math.Min(r.Width / src.X, r.Height / src.Y);
            double w = src.X * scale, h = src.Y * scale;
            var dest = new Native.RECT
            {
                Left = (int)(r.Left + (r.Width - w) / 2), Top = (int)(r.Top + (r.Height - h) / 2),
            };
            dest.Right = dest.Left + (int)w;
            dest.Bottom = dest.Top + (int)h;

            if (_lastRects.TryGetValue(thumb, out var last) && last.Equals(dest)) continue;
            _lastRects[thumb] = dest;

            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_SOURCECLIENTAREAONLY | Native.DWM_TNP_OPACITY,
                rcDestination = dest, fVisible = true, fSourceClientAreaOnly = false, opacity = 255,
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
            // Die Vorschau liegt über dem Symbol. Bei minimierten Fenstern bleibt das Symbol darunter stehen,
            // falls Windows kein letztes Bild mehr hat.
            if (!win.IsMinimized) fallback.Visibility = Visibility.Hidden;
        }
    }
}

/// <summary>Glas-Fenster mit den ausgeblendeten Symbolen des Infobereichs.</summary>
public class TrayFlyout : FlyoutBase
{
    public TrayFlyout(System.Collections.IEnumerable icons, DataTemplate itemTemplate)
    {
        Title = "GlassBar Infobereich";

        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        panel.SetValue(FrameworkElement.WidthProperty, 7 * 30.0);
        var list = new ItemsControl
        {
            ItemsSource = icons,
            ItemTemplate = itemTemplate,
            ItemsPanel = new ItemsPanelTemplate(panel),
        };

        var hint = new TextBlock
        {
            Text = L.T("Umschalt + Rechtsklick: im Dock anzeigen / ausblenden"),
            Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Width = 7 * 30.0, Margin = new Thickness(2, 8, 2, 0),
        };

        Content = new StackPanel { Margin = new Thickness(10), Children = { list, hint } };
    }
}

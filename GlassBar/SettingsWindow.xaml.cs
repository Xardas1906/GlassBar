using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassBar;

/// <summary>Einstellungsfenster: alle Änderungen wirken sofort und werden gespeichert.</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private bool _applyPending;

    /// <summary>Öffnet das Fenster (oder holt ein schon offenes nach vorne).</summary>
    public static void ShowSingle()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        BuildStillNav();
        DataContext = App.Config;
        AutostartBox.IsChecked = Native.Autostart;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(h) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
            ApplyGlass();
        };

        // Jede Änderung (Schalter, Regler, Auswahl) -> speichern und sofort anwenden
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleApply()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleApply()));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => ScheduleApply()));

        CloseButton.MouseEnter += (_, _) => CloseButton.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        CloseButton.MouseLeave += (_, _) => CloseButton.Background = Brushes.Transparent;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>
    /// Gleiches Glas wie das Dock (Farbe aus den Einstellungen). Damit Text gut lesbar bleibt,
    /// ist das Fenster mindestens zu 55 % getönt.
    /// </summary>
    private void ApplyGlass()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Native.ApplyGlass(h, App.Config.Tint(Math.Max(App.Config.DockOpacity, 55)));
    }

    // ---------- Navigation links ----------
    private bool _syncingNav;
    private DateTime _navClickAt;

    /// <summary>Beim Scrollen die Kategorie links markieren, in der man gerade ist.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsLoaded || e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if ((DateTime.Now - _navClickAt).TotalMilliseconds < 400) return; // Sprung durch Klick links

        if (StillPage.Visibility == Visibility.Visible) return;
        var navItems = Nav.Children.OfType<RadioButton>().Where(r => !((r.Tag as string) ?? "").StartsWith("Still")).ToList();
        RadioButton? current = navItems.FirstOrDefault();
        bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;

        foreach (var item in navItems)
        {
            if (item.Tag is not string name || FindName(name) is not FrameworkElement section) continue;
            double y = section.TransformToAncestor(Content_).Transform(new Point(0, 0)).Y;
            if (y <= Scroller.VerticalOffset + Scroller.ViewportHeight * 0.35) current = item;
        }
        if (atBottom) current = navItems.LastOrDefault(); // ganz unten: letzte Kategorie

        if (current != null && current.IsChecked != true)
        {
            _syncingNav = true;
            current.IsChecked = true;
            _syncingNav = false;
            current.BringIntoView(); // Seitenleiste mitscrollen
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return; // vom Scrollen ausgelöst -> nicht erneut springen
        if (!IsLoaded || (sender as FrameworkElement)?.Tag is not string name) return;
        _navClickAt = DateTime.Now;

        // Eigene Seite: Einstellungs-Panel des Hintergrunds
        bool still = name.StartsWith("Still");
        if (name.StartsWith("Still:")) _stillTab = name.Substring(6);
        else if (name == "StillPage") _stillTab = StillSettings.Tabs[0];
        foreach (var sub in _stillNav) sub.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        StillPage.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = still ? Visibility.Collapsed : Visibility.Visible;
        if (still && sender is FrameworkElement fe) Dispatcher.BeginInvoke(() => fe.BringIntoView(new Rect(0, 0, 1, name == "StillPage" ? 420 : 38)), System.Windows.Threading.DispatcherPriority.Loaded);
        if (still) { ShowStillSettings(); Dispatcher.BeginInvoke(PlaceStillOverlay, System.Windows.Threading.DispatcherPriority.Loaded); return; }
        PlaceStillOverlay();

        if (FindName(name) is FrameworkElement target)
        {
            var pos = target.TransformToAncestor(Content_).Transform(new Point(0, 0));
            Scroller.ScrollToVerticalOffset(pos.Y - 8);
        }
    }

    // ---------- Einstellungen des Hintergrunds „Still“ (nachgebaut) ----------
    private Dictionary<string, object>? _still;          // aktuelle Werte
    private System.Text.Json.Nodes.JsonObject? _stillRaw; // gespeicherte Werte inkl. unbekannter Schlüssel
    private string _stillTab = StillSettings.Tabs[0];
    private readonly System.Windows.Threading.DispatcherTimer _stillSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _stillSaveHooked;

    private void PlaceStillOverlay() { }

    private async void ShowStillSettings()
    {
        // Nativ braucht die Browser-Dateien nicht, um die Einstellungen zu bearbeiten
        bool available = App.Config.ShowWallpaper && (IsNativeStill || Directory.Exists(WallpaperWindow.DefaultFolder));
        StillMissing.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        StillScroll.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) return;

        if (!_stillSaveHooked)
        {
            _stillSaveHooked = true;
            _stillSave.Tick += (_, _) => { _stillSave.Stop(); SaveStill(); };
            Closed += (_, _) => { if (_stillSave.IsEnabled) { _stillSave.Stop(); SaveStill(); } };
        }

        if (_still == null && IsNativeStill)
            LoadNativeStill();
        else if (_still == null)
        {
            // aktuellen Stand aus dem Hintergrund lesen
            _still = new Dictionary<string, object>(StillSettings.Defaults);
            _stillRaw = new System.Text.Json.Nodes.JsonObject();
            try
            {
                var entries = App.Instance.Bar != null ? await App.Instance.Bar.ExportWallpaperStorageAsync() : "[]";
                foreach (var e in System.Text.Json.JsonDocument.Parse(entries).RootElement.EnumerateArray())
                    if (e[0].GetString() == "still.settings.v2" && e[1].GetString() is { } json)
                        _stillRaw = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject ?? new();
                foreach (var kv in _stillRaw)
                {
                    if (kv.Value is not System.Text.Json.Nodes.JsonValue v) continue;
                    if (v.TryGetValue<bool>(out var b)) _still[kv.Key] = b;
                    else if (v.TryGetValue<double>(out var d)) _still[kv.Key] = d;
                    else if (v.TryGetValue<string>(out var s)) _still[kv.Key] = s;
                }
            }
            catch { }
        }
        RenderStillTab();
    }

    private bool IsNativeStill => string.Equals(App.Config.StillEngine, "Native", StringComparison.OrdinalIgnoreCase);

    /// <summary>Aktuelle Werte für den nativen Hintergrund (Standardwerte wie bisher ergänzt).</summary>
    private void LoadNativeStill()
    {
        NativeStill.NativeStillStore.Load();
        _still = new Dictionary<string, object>(StillSettings.Defaults);
        _stillRaw = null; // nativ wird über NativeStillStore gespeichert
        foreach (var key in StillSettings.Defaults.Keys.ToList())
            _still[key] = NativeStill.NativeStillStore.Values.Get(key);
        foreach (var kv in NativeStill.NativeStillStore.Values.Snapshot())
            _still[kv.Key] = kv.Value;
    }

    private int _engineSwitch; // zählt Umschaltungen, damit eine ältere nicht nachträglich gewinnt

    /// <summary>
    /// Darstellung von „Still“ umschalten. Beim ersten Wechsel auf „Nativ“ werden vorher die Werte aus dem
    /// Browser übernommen – solange der Browser-Hintergrund noch läuft.
    /// </summary>
    private async void StillEngine_Checked(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // nicht sofort anwenden (siehe unten)
        if (!IsLoaded || (sender as FrameworkElement)?.Tag is not string engine) return;
        if (string.Equals(App.Config.StillEngine, engine, StringComparison.OrdinalIgnoreCase)) return;
        int id = ++_engineSwitch;

        if (engine == "Native" && !NativeStill.NativeStillStore.Exists)
        {
            try
            {
                var entries = App.Instance.Bar != null ? await App.Instance.Bar.ExportWallpaperStorageAsync() : "[]";
                string? json = null;
                foreach (var entry in System.Text.Json.JsonDocument.Parse(entries).RootElement.EnumerateArray())
                    if (entry[0].GetString() == "still.settings.v2" && entry[1].GetString() is { } s) json = s;
                NativeStill.NativeStillStore.ImportFromBrowserJson(json ?? "{}");

                // Noch nicht beim Browser angekommene Änderungen aus dieser Seite mitnehmen
                if (_still != null && _stillSave.IsEnabled)
                {
                    _stillSave.Stop();
                    foreach (var kv in _still) NativeStill.NativeStillStore.Set(kv.Key, kv.Value);
                }
            }
            catch { }
            if (id != _engineSwitch) return; // inzwischen wieder umgeschaltet
        }

        // Ausstehendes Speichern gehört noch zur alten Darstellung
        if (_stillSave.IsEnabled) { _stillSave.Stop(); if (engine == "Native") SaveStill(); }

        App.Config.StillEngine = engine;
        _still = null; // Werte beim nächsten Öffnen der Seite neu lesen
        if (StillPage.Visibility == Visibility.Visible) ShowStillSettings();
        ScheduleApply();
    }

    private void StillEditLayout_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Bar?.EditWallpaperLayout();
        WindowState = WindowState.Minimized; // den Desktop freigeben
    }

    private void StillChanged(string key, object value)
    {
        _still![key] = value;
        // Nativ: direkt in den eigenen Speicher, der Hintergrund liest die Werte live
        if (IsNativeStill) { NativeStill.NativeStillStore.Set(key, value); return; }
        // Sofort und ohne Neuladen im Hintergrund übernehmen; nur wenn das nicht geht, neu laden
        if (App.Instance.Bar?.SetStill(key, value) == true) return;
        _stillSave.Stop();
        _stillSave.Start();
    }

    /// <summary>Werte in den Speicher von „Still“ schreiben und den Hintergrund neu laden.</summary>
    private void SaveStill()
    {
        if (_still == null || _stillRaw == null) return;
        foreach (var kv in _still)
            _stillRaw[kv.Key] = kv.Value switch
            {
                bool b => System.Text.Json.Nodes.JsonValue.Create(b),
                double d => System.Text.Json.Nodes.JsonValue.Create(d),
                string s => System.Text.Json.Nodes.JsonValue.Create(s),
                _ => null,
            };
        var json = _stillRaw.ToJsonString();
        var entries = System.Text.Json.JsonSerializer.Serialize(new[] { new[] { "still.settings.v2", json } });
        App.Instance.Bar?.ImportWallpaperStorage(entries);
    }

    private readonly List<RadioButton> _stillNav = new();

    /// <summary>Unterpunkte von „Still“ (Aussehen, Schrift, …) eingerückt in der Seitenleiste.</summary>
    private void BuildStillNav()
    {
        var parent = Nav.Children.OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == "StillPage");
        if (parent == null) return;
        int index = Nav.Children.IndexOf(parent);
        foreach (var tab in StillSettings.Tabs)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("NavItem"), GroupName = "Nav", Tag = "Still:" + tab, Height = 32,
                Visibility = Visibility.Collapsed,
                Content = new TextBlock { Text = tab, FontSize = 12.5, Margin = new Thickness(34, 0, 0, 0), Opacity = 0.9 },
            };
            rb.Checked += Nav_Checked;
            Nav.Children.Insert(++index, rb);
            _stillNav.Add(rb);
        }
    }

    private void RenderStillTab()
    {
        if (_still == null) return;
        StillTitle.Text = $"Still · {_stillTab}";
        StillContent.Children.Clear();
        StillScroll.ScrollToTop();
        var card = new StackPanel();
        StillContent.Children.Add(new Border { Style = (Style)FindResource("CardBox"), Child = card });

        bool first = true;
        foreach (var s in StillSettings.All.Where(x => x.Tab == _stillTab))
        {
            if (s.Section != null)
                card.Children.Add(new TextBlock { Text = s.Section, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
            else if (!first)
                card.Children.Add(new Border { Style = (Style)FindResource("RowLine") });
            first = false;
            card.Children.Add(BuildStillRow(s));
        }
    }

    private FrameworkElement BuildStillRow(StillSetting s)
    {
        var label = new TextBlock { Style = (Style)FindResource("Label") };
        label.Inlines.Add(new System.Windows.Documents.Run(s.Label));
        if (s.Note != null)
        {
            label.Inlines.Add(new System.Windows.Documents.LineBreak());
            label.Inlines.Add(new System.Windows.Documents.Run(s.Note) { Foreground = (Brush)FindResource("Sub"), FontSize = 12 });
        }
        var row = new DockPanel { Style = (Style)FindResource("Row") };
        object value = _still![s.Key];

        switch (s.Kind)
        {
            case "toggle":
            {
                var cb = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = value is true };
                cb.Click += (_, _) => StillChanged(s.Key, cb.IsChecked == true);
                row.Children.Add(cb);
                break;
            }
            case "slider":
            {
                double v = value is double d ? d : 0;
                var text = new TextBlock { Width = 84, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                           Foreground = (Brush)FindResource("Sub"), Text = StillSettings.Format(s, v) };
                DockPanel.SetDock(text, Dock.Right);
                var sl = new Slider { Style = (Style)FindResource("Slide"), Minimum = s.Min, Maximum = s.Max, Value = v,
                                      SmallChange = s.Step, LargeChange = s.Step * 5 };
                if (s.Step >= 0.05) { sl.TickFrequency = s.Step; sl.IsSnapToTickEnabled = true; }
                DockPanel.SetDock(sl, Dock.Right);
                sl.ValueChanged += (_, e) =>
                {
                    var nv = Math.Round(e.NewValue, 3);
                    text.Text = StillSettings.Format(s, nv);
                    StillChanged(s.Key, nv);
                };
                row.Children.Add(text);
                row.Children.Add(sl);
                break;
            }
            case "choice" when s.Key == "font":
                return BuildFontTiles(s, label, value);
            case "choice":
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                foreach (var (optValue, optLabel) in s.Options!)
                {
                    var v = Normalize(optValue);
                    var rb = new RadioButton { Content = optLabel, GroupName = "still_" + s.Key, Style = (Style)FindResource("Segment"),
                                               Margin = new Thickness(0, 0, 0, 6), IsChecked = Equals(v, Normalize(value)) };
                    rb.Checked += (_, _) => StillChanged(s.Key, v);
                    wrap.Children.Add(rb);
                }
                var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                stack.Children.Add(label);
                stack.Children.Add(wrap);
                return stack;
            }
            case "palette":
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(panel, Dock.Right);
                foreach (var (key, name, color) in StillSettings.Palettes)
                {
                    var rb = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "still_theme", ToolTip = name,
                                               Background = (Brush)new BrushConverter().ConvertFromString(color)!, IsChecked = (value as string) == key };
                    var k = key;
                    rb.Checked += (_, _) => StillChanged("theme", k);
                    panel.Children.Add(rb);
                }
                row.Children.Add(panel);
                break;
            }
        }
        row.Children.Add(label);
        return row;
    }

    /// <summary>Schriftarten als Kacheln mit Vorschau der Uhrzeit (wie im Original).</summary>
    private FrameworkElement BuildFontTiles(StillSetting s, TextBlock label, object value)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var (optValue, optLabel) in s.Options!)
        {
            var key = (string)optValue;
            var time = new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"), FontSize = 34, FontWeight = FontPreview.WeightOf(key),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 2),
            };
            // Buchstaben zeigen die Unterschiede viel deutlicher als Ziffern
            var sample = new TextBlock
            {
                Text = DateTime.Now.ToString("ddd, d. MMMM", new System.Globalization.CultureInfo("de-DE")), FontSize = 13.5,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            };
            var name = new TextBlock { Text = optLabel, FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
            var rb = new RadioButton
            {
                GroupName = "still_font", Style = (Style)FindResource("Segment"), Width = 168, Height = 106,
                Margin = new Thickness(0, 0, 2, 8), IsChecked = Equals(key, value),
                Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { time, sample, name } },
            };
            rb.Checked += (_, _) => StillChanged("font", key);
            wrap.Children.Add(rb);

            // Schrift nachladen und in der Vorschau einsetzen
            _ = FontPreview.GetAsync(key).ContinueWith(t =>
            {
                if (t.Result != null) Dispatcher.BeginInvoke(() => time.FontFamily = sample.FontFamily = t.Result);
            });
        }
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(label);
        stack.Children.Add(wrap);
        return stack;
    }

    /// <summary>Zahlen als double vergleichen (5 und 5.0 sind gleich).</summary>
    private static object Normalize(object o) => o switch { int i => (double)i, float f => (double)f, _ => o };

    private void ScheduleApply()
    {
        if (_applyPending || !IsLoaded) return;
        _applyPending = true;
        // erst nachdem die Bindung den neuen Wert geschrieben hat
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _applyPending = false;
            App.Config.Save();
            App.Instance.ApplySettingsToDocks();
            ApplyGlass();
        });
    }

    private void Close_Click(object sender, MouseButtonEventArgs e) => Close();

    // ---------- Stadt fürs Wetter ----------
    private void City_TextChanged(object sender, TextChangedEventArgs e)
    {
        var fallback = WeatherService.WallpaperCity();
        CityHint.Text = CityBox.Text.Length > 0 ? "" :
            string.IsNullOrEmpty(fallback) ? "z. B. Berlin" : $"{fallback} (aus „Still“)";
    }

    private void City_LostFocus(object sender, RoutedEventArgs e) => ScheduleApply();

    private void Tasks_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TasksBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ScheduleApply();
        Keyboard.ClearFocus();
    }

    private void City_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CityBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ScheduleApply();
        Keyboard.ClearFocus();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e) => Native.Autostart = AutostartBox.IsChecked == true;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Config.ResetAppearanceAndBehavior();
        _still = null; // Darstellung kann sich geändert haben -> Werte neu lesen
        DataContext = null;
        DataContext = App.Config;
        App.Config.Save();
        App.Instance.ApplySettingsToDocks();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Native.Launch("explorer.exe", $"\"{Config.Folder}\"");

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Quit();
}

/// <summary>Für Auswahlknöpfe: true, wenn der Wert dem Parameter entspricht.</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() ?? "" : Binding.DoNothing;
}

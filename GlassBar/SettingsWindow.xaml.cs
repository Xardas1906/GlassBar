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
        SliderEdit.AttachAll(Content_);
        BuildDesktopGallery();
        CheckUpdateQuietly();

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
    private string _stillTab = StillSettings.Tabs[0];

    private void PlaceStillOverlay() { }

    private void ShowStillSettings()
    {
        bool available = App.Config.ShowWallpaper;
        StillMissing.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        StillScroll.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) return;
        if (_still == null) LoadNativeStill();
        RenderStillTab();
    }

    /// <summary>Aktuelle Werte des Hintergrunds (Standardwerte ergänzt).</summary>
    private void LoadNativeStill()
    {
        NativeStill.NativeStillStore.Load();
        _still = new Dictionary<string, object>(StillSettings.Defaults);
        foreach (var key in StillSettings.Defaults.Keys.ToList())
            _still[key] = NativeStill.NativeStillStore.Values.Get(key);
        foreach (var kv in NativeStill.NativeStillStore.Values.Snapshot())
            _still[kv.Key] = kv.Value;
    }

    private void StillEditLayout_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.Bar?.EditWallpaperLayout();
        WindowState = WindowState.Minimized; // den Desktop freigeben
    }

    /// <summary>Wert direkt in den Speicher; der Hintergrund liest die Werte live.</summary>
    private void StillChanged(string key, object value)
    {
        _still![key] = value;
        NativeStill.NativeStillStore.Set(key, value);
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
                SliderEdit.Attach(sl, text, s.Fmt is "pct" or "pos" ? 100 : 1);
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

    /// <summary>Eigenes Bild für den Start-Knopf wählen (wird nach %AppData%\GlassBar kopiert).</summary>
    private void StartIconCustom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bild für den Start-Knopf",
            Filter = "Bilder (*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif",
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var dest = Path.Combine(Config.Folder, "start-icon" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
                foreach (var old in Directory.GetFiles(Config.Folder, "start-icon.*")) File.Delete(old);
                File.Copy(dlg.FileName, dest, true);
                App.Config.StartIconPath = dest;
                App.Config.StartIcon = "Custom";
            }
            catch (Exception ex) { MessageBox.Show(this, "Das Bild konnte nicht übernommen werden:\n" + ex.Message, "GlassBar"); }
        }
        // Abgebrochen: Auswahl wieder auf den gespeicherten Stand
        StartIconCustom.IsChecked = App.Config.StartIcon == "Custom";
        ScheduleApply();
    }

    // ---------- Updates über GitHub ----------
    private Updater.Release? _update;
    private bool _updateBusy;

    private void ShowVersion(string? extra = null) =>
        UpdateStatus.Text = $"Installiert: Version {Updater.Current.ToString(3)}" + (extra != null ? " · " + extra : "");

    /// <summary>Beim Öffnen leise nachsehen, ob es etwas Neues gibt.</summary>
    private async void CheckUpdateQuietly()
    {
        ShowVersion();
        try
        {
            var r = await Updater.GetLatestAsync();
            if (r != null && r.Version > Updater.Current) OfferUpdate(r);
        }
        catch { /* offline o. Ä.: nur der Knopf bleibt */ }
    }

    private void OfferUpdate(Updater.Release r)
    {
        _update = r;
        ShowVersion($"Neu: Version {r.Version.ToString(3)}");
        UpdateButton.Content = $"Auf {r.Version.ToString(3)} aktualisieren";
        UpdateButton.ToolTip = string.IsNullOrWhiteSpace(r.Notes) ? r.Name : r.Notes;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (_update == null)
            {
                UpdateButton.Content = "Suche …";
                var r = await Updater.GetLatestAsync();
                if (r == null || r.Version <= Updater.Current)
                {
                    ShowVersion("Du hast die neueste Version.");
                    UpdateButton.Content = "Nach Updates suchen";
                    return;
                }
                OfferUpdate(r);
                return; // erst anzeigen, beim nächsten Klick installieren
            }

            var release = _update;
            var progress = new Progress<double>(p => UpdateButton.Content = $"Lade … {p * 100:0} %");
            await Updater.PrepareAsync(release, progress);
            UpdateButton.Content = "Starte neu …";
            App.Instance.Quit(); // das Update-Skript ersetzt die Dateien und startet GlassBar wieder
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowVersion("Update abgebrochen (keine Administratorrechte).");
            UpdateButton.Content = _update != null ? $"Auf {_update.Version.ToString(3)} aktualisieren" : "Nach Updates suchen";
        }
        catch (Exception ex)
        {
            ShowVersion("Update fehlgeschlagen: " + ex.Message);
            UpdateButton.Content = _update != null ? $"Auf {_update.Version.ToString(3)} aktualisieren" : "Nach Updates suchen";
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }

    /// <summary>Galerie mit Windows-Symbolen für den Desktop-Stapel, dazu „Anderes Windows-Symbol …“.</summary>
    private void BuildDesktopGallery()
    {
        DesktopGallery.Children.Clear();
        foreach (var (spec, name) in ShellIcons.Gallery)
        {
            var icon = ShellIcons.Load(spec, 48);
            if (icon == null) continue; // gibt es auf diesem Windows nicht
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "DesktopIcon", ToolTip = name, Tag = spec,
                Content = new Image { Source = icon, Width = 22, Height = 22 },
                IsChecked = App.Config.DesktopIcon == "Sys" && App.Config.DesktopIconSource == spec,
            };
            rb.Checked += (_, _) => { App.Config.DesktopIcon = "Sys"; App.Config.DesktopIconSource = spec; ScheduleApply(); };
            DesktopGallery.Children.Add(rb);
        }
        var other = new RadioButton
        {
            Style = (Style)FindResource("Segment"), GroupName = "DesktopIcon", Content = "Anderes Windows-Symbol …",
            IsChecked = App.Config.DesktopIcon == "Sys" && !ShellIcons.Gallery.Any(g => g.Spec == App.Config.DesktopIconSource),
        };
        other.Click += (_, _) =>
        {
            var picked = ShellIcons.Pick(new WindowInteropHelper(this).Handle, App.Config.DesktopIconSource);
            if (picked != null) { App.Config.DesktopIcon = "Sys"; App.Config.DesktopIconSource = picked; }
            BuildDesktopGallery(); // Auswahl neu anzeigen
            ScheduleApply();
        };
        DesktopGallery.Children.Add(other);
    }

    /// <summary>Eigenes Bild für den Desktop-Stapel wählen (wird nach %AppData%\GlassBar kopiert).</summary>
    private void DesktopIconCustom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bild für den Desktop-Stapel",
            Filter = "Bilder (*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif",
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var dest = Path.Combine(Config.Folder, "desktop-icon" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
                foreach (var old in Directory.GetFiles(Config.Folder, "desktop-icon.*")) File.Delete(old);
                File.Copy(dlg.FileName, dest, true);
                App.Config.DesktopIconPath = dest;
                App.Config.DesktopIcon = "Custom";
            }
            catch (Exception ex) { MessageBox.Show(this, "Das Bild konnte nicht übernommen werden:\n" + ex.Message, "GlassBar"); }
        }
        DesktopIconCustom.IsChecked = App.Config.DesktopIcon == "Custom";
        ScheduleApply();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        Native.Autostart = AutostartBox.IsChecked == true;
        AutostartBox.IsChecked = Native.Autostart; // Rückfrage abgelehnt? Dann den echten Stand zeigen
    }

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

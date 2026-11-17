using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassBar;

/// <summary>Suche über alle Einstellungen (GlassBar und „Still“) in der Seitenleiste.</summary>
public partial class SettingsWindow
{
    private sealed record SearchEntry(string Title, string Where, string Haystack, Action Go);
    private List<SearchEntry>? _searchIndex;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Nav.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchResults.Children.Clear();
        NavScroll.ScrollToTop();
        if (q.Length == 0) return;

        _searchIndex ??= BuildSearchIndex();
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _searchIndex.Where(x => words.All(w => x.Haystack.Contains(w)))
                               // Treffer im Titel zuerst
                               .OrderByDescending(x => words.All(w => x.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
                               .Take(50).ToList();

        if (hits.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock { Text = "Nichts gefunden.", Foreground = (Brush)FindResource("Sub"), Margin = new Thickness(14, 8, 0, 0) });
            return;
        }
        foreach (var hit in hits) SearchResults.Children.Add(ResultItem(hit));
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (SearchResults.Children.OfType<Border>().FirstOrDefault()?.Tag is SearchEntry first) first.Go();
        e.Handled = true;
    }

    private void SearchClear_Click(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private Border ResultItem(SearchEntry entry)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
        text.Children.Add(new TextBlock { Text = entry.Where, FontSize = 11.5, Foreground = (Brush)FindResource("Sub"), TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new Border
        {
            Tag = entry, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = text, ToolTip = entry.Title,
        };
        item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => entry.Go();
        return item;
    }

    // ---------- Index ----------
    private List<SearchEntry> BuildSearchIndex()
    {
        var list = new List<SearchEntry>();
        var sub = FindResource("Sub");
        var labelStyle = FindResource("Label");
        var sectionStyle = FindResource("Section");

        // GlassBar: Abschnitte und Zeilen der Hauptseite
        TextBlock? section = null;
        foreach (var child in Content_.Children.OfType<FrameworkElement>())
        {
            if (child is TextBlock { Style: var st } head && st == sectionStyle)
            {
                section = head;
                var target = head;
                list.Add(Entry(head.Text, "Kategorie", "", () => GoToMain(target, null)));
                continue;
            }
            if (section == null) continue;
            var sec = section;
            foreach (var el in Descendants(child))
            {
                string? text = null, extra = null;
                if (el is TextBlock tb && !BindingOperations.IsDataBound(tb, TextBlock.TextProperty)
                    && (tb.Style == labelStyle || tb.Style == null && tb.Foreground != sub))
                    text = tb.Text;
                else if (el is RadioButton { Content: string option } rb && rb.Style == FindResource("Segment"))
                {
                    // Auswahl-Option: unter dem Titel der Zeile finden
                    extra = option;
                    text = RowOf(rb) is { } row ? Descendants(row).OfType<TextBlock>().FirstOrDefault()?.Text : null;
                    if (text == null) continue;
                    text = $"{text}: {option}";
                }
                else if (el is Button { Content: string caption }) text = caption;
                if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 3) continue;
                var lines = text.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                var target = el;
                list.Add(Entry(lines[0], sec.Text, string.Join(" ", lines.Skip(1)) + " " + extra, () => GoToMain(sec, target)));
            }
        }

        // Still: Kategorien und alle Einstellungen
        foreach (var tab in StillSettings.Tabs)
            list.Add(Entry(tab, "Still", "hintergrund", () => GoToStill(tab, null)));
        foreach (var s in StillSettings.All)
        {
            var opts = s.Options == null ? "" : string.Join(" ", s.Options.Select(o => o.Label));
            list.Add(Entry(s.Label, $"Still › {s.Tab}", $"{s.Note} {s.Section} {opts} hintergrund", () => GoToStill(s.Tab, s.Label)));
        }
        return list;
    }

    /// <summary>Zusätzliche Suchbegriffe für Einstellungen, die anders heißen, als man sucht.</summary>
    private static readonly (string Part, string Words)[] Synonyms =
    {
        ("Mit Windows starten", "autostart hochfahren anmelden"),
        ("Deckkraft", "transparenz durchsichtig opazität"),
        ("Farbe des Glases", "tönung farbe theme"),
        ("Icon-Größe", "symbolgröße symbole"),
        ("ausblenden", "verstecken auto-hide automatisch"),
        ("Windows-Taste", "win taste startmenü"),
        ("Zurücksetzen", "standard reset"),
        ("Stadt", "ort wetter"),
        ("Desktop anzeigen", "minimieren alle fenster win+d knopf rechts"),
        ("Musik anzeigen", "musik pille hintergrund dock platz"),
        ("Wetter anzeigen", "wetter hintergrund dock platz"),
        ("Start-Symbol", "windows logo startknopf start icon symbol bild personalisieren"),
        ("Farbe der Kachel", "start windows icon farbe personalisieren akzent"),
        ("Windows-Logo", "start icon logo quadrate bunt umriss abgerundet personalisieren"),
        ("Farbe des Logos", "start windows icon logo farbe"),
        ("Nebenbildschirmen", "monitor zweiter bildschirm hintergrund mehrere"),
        ("allen Bildschirmen", "monitor mehrere zweiter bildschirm taskleiste"),
        ("Bildrate","fps leistung performance ruckeln lag cpu grafikkarte"),
        ("aus dem Bild fahren", "animation rausfahren reinfahren gleiten"),
        ("Dauer der Animation", "geschwindigkeit schnell langsam"),
    };

    private static SearchEntry Entry(string title, string where, string extra, Action go)
    {
        var syn = string.Join(" ", Synonyms.Where(x => title.Contains(x.Part, StringComparison.OrdinalIgnoreCase)).Select(x => x.Words));
        return new(title, where, $"{title} {where} {extra} {syn}".ToLowerInvariant(), go);
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (c is FrameworkElement fe) yield return fe;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Die Zeile in der Karte, zu der ein Element gehört (direktes Kind der Karte).</summary>
    private FrameworkElement? RowOf(FrameworkElement el)
    {
        DependencyObject? cur = el;
        while (cur != null)
        {
            var parent = LogicalTreeHelper.GetParent(cur);
            if (parent is Panel p && LogicalTreeHelper.GetParent(p) is Border { Style: var st } && st == FindResource("CardBox"))
                return cur as FrameworkElement;
            cur = parent;
        }
        return null;
    }

    // ---------- Springen ----------
    private void GoToMain(FrameworkElement section, FrameworkElement? target)
    {
        var nav = Nav.Children.OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == section.Name);
        if (nav != null && nav.IsChecked != true) nav.IsChecked = true; // wechselt ggf. von „Still“ zurück
        Dispatcher.BeginInvoke(() =>
        {
            var el = target ?? section;
            var y = el.TransformToAncestor(Content_).Transform(new Point(0, 0)).Y;
            _navClickAt = DateTime.Now;
            Scroller.ScrollToVerticalOffset(Math.Max(0, y - (target == null ? 8 : 90)));
            if (target != null) Flash(RowOf(target));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void GoToStill(string tab, string? label)
    {
        var nav = _stillNav.FirstOrDefault(r => (r.Tag as string) == "Still:" + tab);
        if (nav == null) return;
        if (nav.IsChecked == true) RenderStillTab(); else nav.IsChecked = true;
        if (label == null) return;
        // Die Seite wird ggf. erst nach dem Laden der Werte aufgebaut -> kurz warten
        int tries = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            var tb = Descendants(StillContent).OfType<TextBlock>().FirstOrDefault(t => t.Text.StartsWith(label));
            if (tb == null && ++tries < 25) return;
            timer.Stop();
            if (tb == null) return;
            StillScroll.UpdateLayout();
            var y = tb.TransformToAncestor(StillContent).Transform(new Point(0, 0)).Y;
            StillScroll.ScrollToVerticalOffset(Math.Max(0, y - 90));
            Flash(RowOf(tb) ?? tb);
        };
        timer.Start();
    }

    /// <summary>Gefundene Zeile kurz blau aufleuchten lassen.</summary>
    private static void Flash(FrameworkElement? row)
    {
        if (row is not Panel panel) return;
        var brush = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xC2, 0xFF));
        panel.Background = brush;
        var anim = new ColorAnimation(Color.FromArgb(0, 0x4C, 0xC2, 0xFF), TimeSpan.FromMilliseconds(1800))
        {
            BeginTime = TimeSpan.FromMilliseconds(500),
        };
        anim.Completed += (_, _) => panel.Background = null;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}

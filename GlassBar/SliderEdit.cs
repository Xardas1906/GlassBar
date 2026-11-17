using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar;

/// <summary>
/// Wert neben einem Regler anklicken und direkt eintippen (z. B. „37“ für 37 %).
/// Die Zahl wird auf den Bereich des Reglers begrenzt; Enter übernimmt, Esc bricht ab.
/// </summary>
public static class SliderEdit
{
    /// <param name="scale">Anzeige = Wert × scale (z. B. 100, wenn der Regler 0–1 speichert, aber Prozent zeigt).</param>
    public static void Attach(Slider slider, TextBlock display, double scale = 1)
    {
        display.Cursor = Cursors.IBeam;
        display.ToolTip = "Klicken, um einen Wert einzutippen";
        display.Background = Brushes.Transparent; // ganze Fläche anklickbar
        display.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Begin(slider, display, scale);
        };
    }

    /// <summary>Alle Regler-Zeilen (Regler + Wert rechts daneben) unterhalb von root.</summary>
    public static void AttachAll(DependencyObject root)
    {
        foreach (var dock in Descendants(root).OfType<DockPanel>())
        {
            var slider = dock.Children.OfType<Slider>().FirstOrDefault();
            var text = dock.Children.OfType<TextBlock>()
                .FirstOrDefault(t => DockPanel.GetDock(t) == Dock.Right);
            if (slider != null && text != null) Attach(slider, text);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void Begin(Slider slider, TextBlock display, double scale)
    {
        if (display.Parent is not Panel panel) return;
        int index = panel.Children.IndexOf(display);
        double shown = slider.Value * scale;
        int decimals = scale == 1 && slider.TickFrequency > 0 && slider.TickFrequency < 1 ? 2 : 0;
        var box = new TextBox
        {
            Text = Math.Round(shown, decimals).ToString(CultureInfo.CurrentCulture),
            Width = Math.Max(display.ActualWidth, 48), Margin = display.Margin,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            FontSize = display.FontSize, Padding = new Thickness(4, 2, 4, 2),
            Foreground = Brushes.White, CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Application.Current.Resources["Accent"] as Brush ?? Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(0, 0, 0, 2),
        };
        if (panel is DockPanel) DockPanel.SetDock(box, DockPanel.GetDock(display));
        display.Visibility = Visibility.Collapsed;
        panel.Children.Insert(index, box);

        bool done = false;
        void Finish(bool apply)
        {
            if (done) return;
            done = true;
            if (apply && TryParse(box.Text, out double typed))
            {
                double v = Math.Clamp(typed / scale, slider.Minimum, slider.Maximum);
                if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
                    v = Math.Clamp(slider.Minimum + Math.Round((v - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency,
                                   slider.Minimum, slider.Maximum);
                slider.Value = v;
            }
            panel.Children.Remove(box);
            display.Visibility = Visibility.Visible;
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    /// <summary>Erste Zahl im Text („37 %“, „-20“, „1,5“).</summary>
    private static bool TryParse(string text, out double value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        value = 0;
        return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace GlassBar;

/// <summary>
/// Übersetzung der Oberfläche. Geschrieben ist GlassBar auf Deutsch; der deutsche Text ist zugleich der Schlüssel
/// für die Übersetzung (siehe Strings.En.cs). Fehlt eine Übersetzung, bleibt der deutsche Text stehen.
/// </summary>
public static class L
{
    /// <summary>Englische Oberfläche?</summary>
    public static bool En { get; private set; }

    /// <summary>Kultur für Datum und Zahlen passend zur Sprache.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>"Auto" = wie Windows (Deutsch, wenn Windows Deutsch spricht, sonst Englisch), "de" oder "en".</summary>
    public static void Init(string? setting)
    {
        En = setting switch
        {
            "de" => false,
            "en" => true,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de",
        };
        Culture = En
            ? (CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "en" ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo("en-US"))
            : CultureInfo.GetCultureInfo("de-DE");
    }

    /// <summary>Text übersetzen (Leerraum am Rand und ein vorangestelltes „✓ “ bleiben erhalten).</summary>
    public static string T(string? de)
    {
        if (!En || string.IsNullOrWhiteSpace(de)) return de ?? "";
        // Der Hintergrund fragt dieselben Texte bei jedem Bild ab (eigener Thread) -> Ergebnis merken
        if (Cache.TryGetValue(de, out var hit)) return hit;
        var result = Translate(de);
        if (Cache.Count < 20000) Cache[de] = result;
        return result;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

    private static string Translate(string de)
    {
        string core = de.Trim();
        string lead = de[..de.IndexOf(core[0])], trail = de[(de.LastIndexOf(core[^1]) + 1)..];
        string check = "";
        if (core.StartsWith("✓ ")) { check = "✓ "; core = core[2..]; }
        if (Strings.En.TryGetValue(core, out var en)) return lead + check + en + trail;
        NoteMissing(core);
        return de;
    }

    /// <summary>Formattext übersetzen und füllen: F("{0} Verknüpfung(en) angeheftet.", n).</summary>
    public static string F(string de, params object?[] args) => string.Format(Culture, T(de), args);

    /// <summary>Alle festen Texte eines Fensters übersetzen (Beschriftungen, Knöpfe, Tooltips, Titel).</summary>
    public static void Apply(DependencyObject root)
    {
        if (!En) return;
        if (root is Window w) w.Title = T(w.Title);
        Walk(root);
    }

    private static void Walk(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb when !BindingOperations.IsDataBound(tb, TextBlock.TextProperty):
                if (tb.Inlines.Count > 0) foreach (var i in tb.Inlines.ToList()) Inline(i);
                else tb.Text = T(tb.Text);
                break;
            case ContentControl cc when cc.Content is string s && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty):
                cc.Content = T(s);
                break;
            case HeaderedItemsControl hc when hc.Header is string h:
                hc.Header = T(h);
                break;
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip && !BindingOperations.IsDataBound(fe, FrameworkElement.ToolTipProperty))
            fe.ToolTip = T(tip);
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList())
            Walk(child);
    }

    private static void Inline(Inline i)
    {
        switch (i)
        {
            case Run r when !BindingOperations.IsDataBound(r, Run.TextProperty): r.Text = T(r.Text); break;
            case Span s: foreach (var c in s.Inlines.ToList()) Inline(c); break;
        }
    }

    // ---------- fehlende Übersetzungen sammeln (hilft beim Nachpflegen) ----------
    private static readonly HashSet<string> Missing = new();

    private static void NoteMissing(string de)
    {
        // nur echte Wörter, keine Zahlen/Symbole/Programmnamen aus Windows
        if (!de.Any(char.IsLetter)) return;
        lock (Missing) { if (!Missing.Add(de)) return; }
        try { File.AppendAllText(Path.Combine(Config.Folder, "missing-en.txt"), de + Environment.NewLine); } catch { }
    }
}

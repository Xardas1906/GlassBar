using System.Diagnostics;

namespace GlassBar;

/// <summary>
/// Zeitmarken beim Start ins debug.log („Start: Dock sichtbar nach 640 ms“) – zeigt, wo die Startzeit bleibt.
/// Nur in den ersten 30 Sekunden nach dem Programmstart, danach tut Mark nichts mehr.
/// </summary>
internal static class StartTrace
{
    private static readonly DateTime ProcessStart = GetStart();
    private static readonly List<string> Marks = new();
    private static bool _written;

    private static DateTime GetStart()
    {
        try { return Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; }
    }

    public static void Mark(string what)
    {
        double ms = (DateTime.Now - ProcessStart).TotalMilliseconds;
        if (ms > 30000) return;
        lock (Marks) Marks.Add($"{what} {ms:0} ms");
    }

    /// <summary>Alle Marken in einer Zeile ins debug.log (einmal, wenn das Dock steht).</summary>
    public static void Flush()
    {
        lock (Marks)
        {
            if (_written || Marks.Count == 0) return;
            _written = true;
            TaskbarWindow.Debug("Start: " + string.Join(" | ", Marks));
        }
    }
}

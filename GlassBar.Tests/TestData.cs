using System.Runtime.CompilerServices;

namespace GlassBar.Tests;

/// <summary>
/// Lenkt alle Dateien von GlassBar (Config.Folder) in einen eigenen Temp-Ordner um,
/// bevor irgendein Test läuft – die echten Einstellungen in %AppData%\GlassBar bleiben unberührt.
/// </summary>
internal static class TestData
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "GlassBar.Tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Folder);
        Environment.SetEnvironmentVariable("GLASSBAR_DATA", Folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Folder, true); } catch { } };
    }

    /// <summary>Frischer Unterordner für einen Test.</summary>
    public static string NewDir()
    {
        string dir = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Tests, die Config.Folder (config.json, error.log) benutzen, laufen nacheinander.</summary>
[CollectionDefinition("Datenordner", DisableParallelization = true)]
public class DataFolderCollection { }

using System.IO.Compression;
using System.Text.Json.Nodes;
using GlassBar;
using Xunit;

[Collection("Datenordner")]
public class SettingsTransferTests
{
    [Fact]
    public void ExportThenImport_RestoresSettings_KeepsLocalValues()
    {
        Directory.CreateDirectory(Config.Folder);
        string cfg = Path.Combine(Config.Folder, "config.json");
        var c = new Config { DockOpacity = 42, WallpaperField = "Bokeh", TaskbarStateBeforeGlassBar = 2 };
        c.Save();
        File.WriteAllText(Path.Combine(Config.Folder, "still.json"), "{\"a\":1}");
        string file = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}.glassbar");
        try
        {
            SettingsTransfer.Export(file);
            using (var zip = ZipFile.OpenRead(file))
                Assert.Equal(new[] { "config.json", "still.json" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray());

            // anderer Stand auf „diesem PC“
            new Config { DockOpacity = 10, WallpaperField = "Light", TaskbarStateBeforeGlassBar = 1 }.Save();
            Assert.Null(SettingsTransfer.Prepare(file));
            SettingsTransfer.ApplyPending();

            var loaded = Config.Load();
            Assert.Equal(42, loaded.DockOpacity);
            Assert.Equal("Bokeh", loaded.WallpaperField);
            Assert.Equal(1, loaded.TaskbarStateBeforeGlassBar); // gehört zu diesem PC
            Assert.True(File.Exists(cfg + ".vor-import.bak"));
            Assert.False(File.Exists(Path.Combine(Config.Folder, "import.zip")));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Prepare_RejectsOtherFiles()
    {
        string file = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllText(file, "kein zip");
            Assert.NotNull(SettingsTransfer.Prepare(file));
            File.Delete(file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) zip.CreateEntry("other.txt");
            Assert.NotNull(SettingsTransfer.Prepare(file));
        }
        finally { File.Delete(file); }
    }
}

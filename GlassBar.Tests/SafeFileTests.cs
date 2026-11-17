using System.Text.Json;

namespace GlassBar.Tests;

[Collection("Datenordner")]
public class SafeFileTests
{
    private static Dictionary<string, int>? Parse(string s) => JsonSerializer.Deserialize<Dictionary<string, int>>(s);

    [Fact]
    public void FehlendeDatei_GibtNull()
    {
        string f = Path.Combine(TestData.NewDir(), "config.json");
        Assert.Null(SafeFile.Read(f, Parse));
    }

    [Fact]
    public void Speichern_BehältVorigeFassungAlsSicherung()
    {
        string f = Path.Combine(TestData.NewDir(), "config.json");
        SafeFile.WriteAllText(f, """{"a":1}""");
        Assert.False(File.Exists(f + ".bak"));

        SafeFile.WriteAllText(f, """{"a":2}""");
        Assert.Equal(2, SafeFile.Read(f, Parse)!["a"]);
        Assert.Equal("""{"a":1}""", File.ReadAllText(f + ".bak"));
        Assert.False(File.Exists(f + ".tmp"));
    }

    [Fact]
    public void KaputteDatei_LädtSicherungUndLegtSieBeiseite()
    {
        string dir = TestData.NewDir();
        string f = Path.Combine(dir, "config.json");
        SafeFile.WriteAllText(f, """{"a":1}""");
        SafeFile.WriteAllText(f, """{"a":2}""");
        File.WriteAllText(f, """{"a":2, halb geschrieb""");

        Assert.Equal(1, SafeFile.Read(f, Parse)!["a"]);
        Assert.False(File.Exists(f));
        Assert.Single(Directory.GetFiles(dir, "config.json.broken-*"));

        // Danach speichern: neue Datei, die gute Sicherung bleibt
        SafeFile.WriteAllText(f, """{"a":3}""");
        Assert.Equal(3, SafeFile.Read(f, Parse)!["a"]);
        Assert.Equal("""{"a":1}""", File.ReadAllText(f + ".bak"));
    }

    [Fact]
    public void LeereDatei_GiltAlsKaputt()
    {
        string f = Path.Combine(TestData.NewDir(), "config.json");
        File.WriteAllBytes(f, Array.Empty<byte>());
        File.WriteAllText(f + ".bak", "");
        Assert.Null(SafeFile.Read(f, Parse));
    }
}

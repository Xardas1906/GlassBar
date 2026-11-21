using GlassBar.NativeStill;
using Vortice.D3DCompiler;

namespace GlassBar.Tests;

/// <summary>Die Shader werden erst zur Laufzeit übersetzt – hier fällt ein Fehler im HLSL schon beim Testen auf.</summary>
public class ShaderTests
{
    public static TheoryData<string, string, string> All => new()
    {
        { "Vertex", Shaders.Vertex, "vs_5_0" },
        { "Lichtfeld", Shaders.Field, "ps_5_0" },
        { "Polarlicht", Shaders.Aurora, "ps_5_0" },
        { "Kaustik", Shaders.Caustics, "ps_5_0" },
        { "Bokeh", Shaders.Bokeh, "ps_5_0" },
        { "Nebel", Shaders.Smoke, "ps_5_0" },
        { "Hochziehen", Shaders.Upscale, "ps_5_0" },
    };

    [Theory]
    [MemberData(nameof(All))]
    public void ShaderLässtSichÜbersetzen(string name, string source, string profile)
    {
        var code = Compiler.Compile(source, "main", name + ".hlsl", profile, ShaderFlags.OptimizationLevel3);
        Assert.True(code.Length > 0, name);
    }
}

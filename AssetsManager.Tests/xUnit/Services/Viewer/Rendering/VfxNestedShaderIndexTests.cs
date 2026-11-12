using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxNestedShaderIndexTests
{
    public static IEnumerable<object[]> Indices()
    {
        foreach (bool mesh in new[] { false, true })
        foreach (bool pixel in new[] { false, true })
        foreach (string index in new[] { "0u", "uint(indices.m[0u].x)", "uint(globals.m[0u].x)",
                     "uint(indices.m[uint(indices.m[0u].x)].x)" })
            yield return new object[] { mesh, pixel, index };
    }

    [Theory]
    [MemberData(nameof(Indices))]
    public void ParticleProgramLinksWithNestedBufferIndices(bool mesh, bool pixel, string index)
    {
        const string header = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        const string declarations = "layout(std140) uniform Globals\n{\n    vec4 m[2];\n} globals;\n" +
            "layout(std140) uniform Indices\n{\n    uvec4 m[2];\n} indices;\n";
        string expression = "globals.m[" + index + "]";
        var blocks = new[]
        {
            new GameShaderTranslator.UniformBlock("$Globals", "Globals", 32,
                new[] { new GameShaderTranslator.BlockMember("kColorFactor", 0, 16, true,
                    GameShaderTranslator.MemberScalar.Float, 1, 4, 0, false) }),
            new GameShaderTranslator.UniformBlock("Indices", "Indices", 32,
                Array.Empty<GameShaderTranslator.BlockMember>())
        };
        GameShaderTranslator.TranslatedStage Stage(string source, bool reads) =>
            new(source, new GameShaderTranslator.ShaderSidecar(reads ? blocks : Array.Empty<GameShaderTranslator.UniformBlock>(),
                Array.Empty<GameShaderTranslator.TextureBinding>(), Array.Empty<GameShaderTranslator.AttributeBinding>()),
                Array.Empty<GameShaderTranslator.AppliedPatch>(), Array.Empty<(string, string)>(), Array.Empty<uint>());
        var translated = new GameShaderTranslator.TranslatedProgram(
            Stage(header + (pixel ? "" : declarations) + "in vec4 a_POSITION;\nout vec4 probe;\n" +
                "void main() { gl_Position = a_POSITION; probe = " + (pixel ? "vec4(1.0)" : expression) + "; }", !pixel),
            Stage(header + (pixel ? declarations : "") + "in vec4 probe;\nout vec4 result;\n" +
                "void main() { result = " + (pixel ? expression : "probe") + "; }", pixel));
        var composed = GameParticleShaderPrelude.Compose(translated, mesh);
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        uint program = GlShaderCompiler.CreateRawProgram(gl, Desktop(composed.Vertex.Glsl), Desktop(composed.Pixel.Glsl));
        try
        {
            Assert.NotEqual(0u, program);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally { gl.DeleteProgram(program); }
    }

    private static string Desktop(string source) => GameShaderRuntime.ToDesktopGlsl(source);
}

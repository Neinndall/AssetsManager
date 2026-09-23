using System;
using System.Linq;
using AssetsManager.Shaders;
using Xunit;

namespace AssetsManager.Tests.xUnit.Shaders
{
    public sealed class GameShaderPatchTests
    {
        private const string Preamble = "#version 300 es\nprecision highp float;\nprecision highp int;\n";

        [Theory]
        [InlineData("Diffuse_Texture__TX", "Diffuse_Texture_TX")]
        [InlineData("$Globals", "Globals")]
        [InlineData("3d", "_3d")]
        [InlineData("__", "unnamed")]
        public void IdentMatchesHexshadeRules(string input, string expected)
        {
            Assert.Equal(expected, GameShaderTranslator.Ident(input));
        }

        [Fact]
        public void CbufferExtentShrinksToReflectionSize()
        {
            string source = Preamble + "\nlayout(std140) uniform BonesCB\n{\n    vec4 m[4096];\n} BonesCB_i;\n";
            (string output, _) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Vertex,
                new[] { ("BonesCB", 768u) });

            Assert.Contains("vec4 m[768];", output, StringComparison.Ordinal);
            Assert.Contains("uniform BonesCB_vs\n", output, StringComparison.Ordinal);
        }

        [Fact]
        public void CbufferNarrowerThanReflectionIsLeftAlone()
        {
            string source = "layout(std140) uniform Globals\n{\n    vec4 m[1];\n} Globals_i;\n";
            (string output, _) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                new[] { ("Globals", 4u) });

            Assert.Contains("vec4 m[1];", output, StringComparison.Ordinal);
        }

        [Fact]
        public void BitBuiltinGetsPolyfillAfterVersionLine()
        {
            string source = Preamble + "void main() { int n = bitCount(3u); }\n";
            (string output, var applied) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                Array.Empty<(string, uint)>());

            Assert.Contains(GameShaderTranslator.AppliedPatch.BitBuiltin, applied);
            int version = output.IndexOf("#version", StringComparison.Ordinal);
            int polyfill = output.IndexOf("int bitCount(uint v)", StringComparison.Ordinal);
            int main = output.IndexOf("void main", StringComparison.Ordinal);
            Assert.True(version < polyfill && polyfill < main);
        }

        [Fact]
        public void TexelBufferBecomesDataTextureWithFetchHelper()
        {
            string source = "#version 300 es\n#extension GL_EXT_texture_buffer : require\nprecision highp float;\n" +
                            "uniform highp usamplerBuffer CLUSTER_DATA;\n" +
                            "void main() { uvec4 w = texelFetch(CLUSTER_DATA, 7); }\n";
            (string output, var applied) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                Array.Empty<(string, uint)>());

            Assert.Contains(GameShaderTranslator.AppliedPatch.TexelBuffer, applied);
            Assert.Contains("uniform highp usampler2D CLUSTER_DATA;", output, StringComparison.Ordinal);
            Assert.Contains("dxbcBufferFetch(CLUSTER_DATA, 7)", output, StringComparison.Ordinal);
            Assert.Contains("uvec4 dxbcBufferFetch(highp usampler2D t, int i)", output, StringComparison.Ordinal);
            Assert.DoesNotContain("#extension", output, StringComparison.Ordinal);
        }

        [Fact]
        public void CubeArrayBecomesSixLayersPerCube()
        {
            string source = "#version 300 es\n#extension GL_EXT_texture_cube_map_array : require\nprecision highp float;\n" +
                            "uniform highp samplerCubeArray IBL_CUBEMAP;\n" +
                            "void main() { vec4 c = textureLod(IBL_CUBEMAP, vec4(1.0, 0.0, 0.0, 2.0), 3.0); }\n";
            (string output, var applied) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                Array.Empty<(string, uint)>());

            Assert.Contains(GameShaderTranslator.AppliedPatch.CubeArray, applied);
            Assert.Contains("uniform highp sampler2DArray IBL_CUBEMAP;", output, StringComparison.Ordinal);
            Assert.Contains("dxbcCubeArrayLod(IBL_CUBEMAP, vec4(1.0, 0.0, 0.0, 2.0), 3.0)", output, StringComparison.Ordinal);
        }

        [Fact]
        public void ShadowLevelZeroBecomesZeroGradientSample()
        {
            string source = Preamble + "uniform highp sampler2DShadow sShadowMap;\n" +
                            "void main() { float s = textureLod(sShadowMap, vec3(uv, d), 0.0) + textureLod(sShadowMap, vec3(uv, d), 1.0); }\n";
            (string output, var applied) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                Array.Empty<(string, uint)>());

            Assert.Contains(GameShaderTranslator.AppliedPatch.ShadowLevelZero, applied);
            Assert.Contains("textureGrad(sShadowMap, vec3(uv, d), vec2(0.0), vec2(0.0))", output, StringComparison.Ordinal);
            Assert.Contains("textureLod(sShadowMap, vec3(uv, d), 1.0)", output, StringComparison.Ordinal);
        }

        [Fact]
        public void NarrowVertexVaryingWidensAndWholeWritesPad()
        {
            string source = "out vec2 v_TEXCOORD;\nout float v_FOG;\nvoid main()\n{\n    v_TEXCOORD = a_TEXCOORD.xy;\n    v_FOG.x = 1.0;\n}\n";
            (string output, _) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Vertex,
                Array.Empty<(string, uint)>());

            Assert.Contains("out vec4 v_TEXCOORD;", output, StringComparison.Ordinal);
            Assert.Contains("out vec4 v_FOG;", output, StringComparison.Ordinal);
            Assert.Contains("v_TEXCOORD = vec4(a_TEXCOORD.xy, 0.0, 0.0);", output, StringComparison.Ordinal);
            Assert.Contains("v_FOG.x = 1.0;", output, StringComparison.Ordinal);
        }

        [Fact]
        public void NarrowFragmentVaryingWidensAndReadsSwizzle()
        {
            string source = "in vec3 v_NORMAL;\nin vec2 v_TEXCOORD;\nvoid main()\n{\n    vec3 n = normalize(v_NORMAL);\n    vec4 t = texture(tex, v_TEXCOORD);\n    float u = v_TEXCOORD.x;\n}\n";
            (string output, _) = GameShaderTranslator.PatchGlsl(
                source,
                GameShaderTranslator.Stage.Pixel,
                Array.Empty<(string, uint)>());

            Assert.Contains("in vec4 v_NORMAL;", output, StringComparison.Ordinal);
            Assert.Contains("in vec4 v_TEXCOORD;", output, StringComparison.Ordinal);
            Assert.Contains("normalize(v_NORMAL.xyz)", output, StringComparison.Ordinal);
            Assert.Contains("texture(tex, v_TEXCOORD.xy)", output, StringComparison.Ordinal);
            Assert.Contains("float u = v_TEXCOORD.x;", output, StringComparison.Ordinal);
        }

        [Fact]
        public void DeclareOutputsAddsOnlyMissingVarying()
        {
            string source = "out vec4 v_TEXCOORD;\n\nvoid main()\n{\n    v_TEXCOORD = vec4(1.0);\n}\n";
            string output = GameShaderTranslator.DeclareOutputs(source, new[] { "TEXCOORD", "COLOR" });

            Assert.Contains("out vec4 v_TEXCOORD;\n", output, StringComparison.Ordinal);
            Assert.Contains("out vec4 v_COLOR;\nvoid main()", output, StringComparison.Ordinal);
            Assert.Equal(1, output.Split("v_TEXCOORD;", StringSplitOptions.None).Length - 1);
        }
    }
}

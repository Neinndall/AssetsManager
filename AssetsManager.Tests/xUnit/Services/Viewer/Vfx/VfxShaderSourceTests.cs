using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxShaderSourceTests
    {
        [Fact]
        public void MissingTexturesFollowLtkFallbackSemantics()
        {
            Assert.Contains(": vec4(1.0);", VfxShaderSource.MeshFragment);
            Assert.Contains("t = vec4(1.0);", VfxShaderSource.ParticleFragment);
            Assert.Contains("1.0 - smoothstep(0.0, 0.5", VfxShaderSource.ParticleFragment);
            Assert.Contains("if (!ribbonPrimitive)", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("uDeriveAlphaFromRgb", VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("uDeriveAlphaFromRgb", VfxShaderSource.ParticleFragment);
        }

        [Fact]
        public void ParticleVertexFitsThePortableAttributeLimit()
        {
            int[] locations = Regex.Matches(VfxShaderSource.ParticleVertex, @"layout\(location=(\d+)\)")
                .Select(match => int.Parse(match.Groups[1].Value))
                .ToArray();

            Assert.NotEmpty(locations);
            Assert.True(locations.Max() <= 15, $"Particle shader uses vertex attribute {locations.Max()}, above OpenGL's guaranteed 0..15 range.");
            Assert.Contains("layout(location=12) in vec3 aBasisX;", VfxShaderSource.ParticleVertex);
            Assert.Contains("layout(location=14) in vec3 aBasisZ;", VfxShaderSource.ParticleVertex);
        }

        [Fact]
        public void TextureMultiplierSharesTheBaseFlipbookFrame()
        {
            Assert.DoesNotContain("uTextureMultFrame", VfxShaderSource.MeshVertex);
            Assert.Contains("float multFrame = floor(uFrame + 0.0001);", VfxShaderSource.MeshVertex);
            Assert.Contains("float multFrame = floor(aRotFrame.y + 0.0001);", VfxShaderSource.ParticleVertex);
        }

        [Fact]
        public void PaletteColoringPreservesAuthoredTextureCoverage()
        {
            Assert.Contains("float paletteCoverage = t.a;", VfxShaderSource.ParticleFragment);
            Assert.Contains("t.a = paletteCoverage;", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("t.a = max(t.a, palette.a)", VfxShaderSource.ParticleFragment);

            Assert.Contains("float paletteCoverage = texel.a;", VfxShaderSource.MeshFragment);
            Assert.Contains("texel.a = paletteCoverage;", VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("texel.a = max(texel.a, palette.a)", VfxShaderSource.MeshFragment);
        }

        [Fact]
        public void OnlyAddAndSubtractPremultiplyAuthoredAlpha()
        {
            const string coverageExpression =
                "if (uIsAdditive == 1 || uIsMultiply != 0)";

            Assert.Contains("uniform int uIsMultiply;", VfxShaderSource.ParticleFragment);
            Assert.Contains(coverageExpression, VfxShaderSource.ParticleFragment);
            Assert.Contains("uniform int uIsMultiply;", VfxShaderSource.MeshFragment);
            Assert.Contains(coverageExpression, VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("mix(vec3(1.0), fragColor.rgb", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("mix(vec3(1.0), fragColor.rgb", VfxShaderSource.MeshFragment);
        }
    }
}

using AssetsManager.Services.Viewer.Vfx.Rendering;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxShaderSourceTests
    {
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
        public void MultiplyNeutralizesTransparentRgbBeforeFramebufferComposition()
        {
            const string coverageExpression =
                "fragColor.rgb = mix(vec3(1.0), fragColor.rgb, effectiveAlpha);";

            Assert.Contains("uniform int uIsMultiply;", VfxShaderSource.ParticleFragment);
            Assert.Contains(coverageExpression, VfxShaderSource.ParticleFragment);
            Assert.Contains("uniform int uIsMultiply;", VfxShaderSource.MeshFragment);
            Assert.Contains(coverageExpression, VfxShaderSource.MeshFragment);
        }
    }
}

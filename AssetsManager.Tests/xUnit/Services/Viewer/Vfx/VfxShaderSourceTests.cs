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
        [Fact]
        public void ArbitraryQuadUsesAuthoredLtkUvOrientationForBothLayers()
        {
            Assert.Contains("vec2 quadUv = uArbitraryQuad != 0", VfxShaderSource.ParticleVertex);
            Assert.Contains("? vec2(aCorner.y + 0.5, aCorner.x + 0.5)", VfxShaderSource.ParticleVertex);
            Assert.Contains("vec2 localUv = trailPrimitive", VfxShaderSource.ParticleVertex);
            Assert.Contains("vec2 multUv = trailPrimitive", VfxShaderSource.ParticleVertex);
            Assert.Equal(2, Regex.Matches(VfxShaderSource.ParticleVertex, @"\s:\squadUv;").Count);
        }

        [Fact]
        public void MeshCameraAlignmentMatchesLtkAxisSelection()
        {
            Assert.Contains("uniform int uAlignPitchToCamera;", VfxShaderSource.MeshVertex);
            Assert.Contains("uniform int uAlignYawToCamera;", VfxShaderSource.MeshVertex);
            Assert.Contains("uAlignYawToCamera != 0 ? uCamPos.x - uWorldPos.x : 0.0", VfxShaderSource.MeshVertex);
            Assert.Contains("uAlignPitchToCamera != 0 ? uCamPos.y - uWorldPos.y : 0.0", VfxShaderSource.MeshVertex);
            Assert.Contains("vec3 aside = cross(uCamUp, facing);", VfxShaderSource.MeshVertex);
            Assert.Contains("if (uMeshSkinned == 0)", VfxShaderSource.MeshVertex);
            Assert.Contains("aside = -aside;", VfxShaderSource.MeshVertex);
            Assert.Contains("facing = -facing;", VfxShaderSource.MeshVertex);
        }

        [Fact]
        public void MeshDepthPushPullMatchesParticleEyeRaySemantics()
        {
            Assert.Contains("uniform vec3 uCamPos;", VfxShaderSource.MeshVertex);
            Assert.Contains("uniform float uDepthPushPull;", VfxShaderSource.MeshVertex);
            Assert.Contains("vec3 eyeRay = p - uCamPos;", VfxShaderSource.MeshVertex);
            Assert.Contains("p += normalize(eyeRay) * uDepthPushPull;", VfxShaderSource.MeshVertex);
        }
    }
}

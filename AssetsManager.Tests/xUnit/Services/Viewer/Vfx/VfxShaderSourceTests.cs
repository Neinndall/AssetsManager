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
            Assert.Contains("float frame = floor(uFrame + 0.0001);", VfxShaderSource.MeshVertex);
            Assert.Contains("float multFrame = mod(mod(frame, multDiv.x * multDiv.y)", VfxShaderSource.MeshVertex);
            Assert.Contains("float frame = floor(aRotFrame.y + 0.0001);", VfxShaderSource.ParticleVertex);
            Assert.Contains("float multFrame = mod(mod(frame, multDiv.x * multDiv.y)", VfxShaderSource.ParticleVertex);
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
        public void FragmentShadersKeepPremultiplicationInTheRuntimeLikeLtk()
        {
            // LTK premultiplies ADD/SUBTRACT tint before it enters the draw buffers. Keeping
            // a second shader-side multiply would incorrectly affect a zero-warp distortion,
            // whose authored alpha must remain the distortion mask/fallback coverage.
            Assert.DoesNotContain("uIsMultiply", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("uIsAdditive", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("fragColor.rgb *= authoredColor.a", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("uIsMultiply", VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("uIsAdditive", VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("fragColor.rgb *= authoredColor.a", VfxShaderSource.MeshFragment);
        }

        [Fact]
        public void InspectorOnlyModulationFactorDoesNotAlterLtkMaterials()
        {
            Assert.DoesNotContain("uModulationFactor", VfxShaderSource.ParticleFragment);
            Assert.DoesNotContain("uModulationFactor", VfxShaderSource.MeshFragment);
            Assert.Contains("vec4 authoredColor = vColor;", VfxShaderSource.ParticleFragment);
            Assert.Contains(
                "vec4 authoredColor = uColor * (uAttachedMesh != 0 ? vec4(1.0) : vMeshColor);",
                VfxShaderSource.MeshFragment);
        }
        [Fact]
        public void ArbitraryQuadUsesAuthoredLtkUvOrientationForBothLayers()
        {
            Assert.Contains("vec2 quadUv = uArbitraryQuad != 0", VfxShaderSource.ParticleVertex);
            Assert.Contains("? vec2(aCorner.y + 0.5, aCorner.x + 0.5)", VfxShaderSource.ParticleVertex);
            Assert.Contains("vec2 localUv = quadUv;", VfxShaderSource.ParticleVertex);
            Assert.Contains("vec2 multUv = quadUv;", VfxShaderSource.ParticleVertex);
            Assert.Contains("if (trailPrimitive)", VfxShaderSource.ParticleVertex);
            Assert.Contains("vCornerUv = aRotFrame;", VfxShaderSource.ParticleVertex);
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
        public void MeshReflectionUsesRealNormalsAndLtkCubeFacingTerms()
        {
            Assert.Contains("layout(location=3) in vec3 aNormal;", VfxShaderSource.MeshVertex);
            Assert.Contains("vec3 ray = normalize(p - uCamPos);", VfxShaderSource.MeshVertex);
            Assert.Contains("vRim = (1.0 - pow(facing, uFresnel.w)) * uFresnel.rgb;", VfxShaderSource.MeshVertex);
            Assert.Contains("reflect(ray, normal) * vec3(-1.0, 1.0, 1.0)", VfxShaderSource.MeshVertex);
            Assert.DoesNotContain("uDepthPushPull", VfxShaderSource.MeshVertex);
            Assert.Contains("uniform samplerCube uReflectionTex;", VfxShaderSource.MeshFragment);
            Assert.Contains("texture(uReflectionTex, vReflect.xyz)", VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("length(vLocalUv - vec2(0.5))", VfxShaderSource.MeshFragment);
        }

        [Fact]
        public void AttachedMeshUsesOwnerSkinningAndParticleTintLikeLtk()
        {
            Assert.Contains("layout(location=4) in vec4 aBoneIndices;", VfxShaderSource.MeshVertex);
            Assert.Contains("layout(location=5) in vec4 aBoneWeights;", VfxShaderSource.MeshVertex);
            Assert.Contains("layout(std140) uniform VfxBoneTransforms", VfxShaderSource.MeshVertex);
            Assert.Contains("if (uUseOwnerSkinning != 0)", VfxShaderSource.MeshVertex);
            Assert.Contains("sourcePosition = (skinMatrix * vec4(aPos, 1.0)).xyz;", VfxShaderSource.MeshVertex);
            Assert.Contains("vec3 scaled = sourcePosition * uScale;", VfxShaderSource.MeshVertex);
            Assert.Contains("if (uAttachedMesh != 0)", VfxShaderSource.MeshVertex);
            Assert.Contains("uColor * (uAttachedMesh != 0 ? vec4(1.0) : vMeshColor)", VfxShaderSource.MeshFragment);
        }

        [Fact]
        public void LockAlphaUsesTheLtkCoordinateForMeshesAndRibbons()
        {
            Assert.Contains("vec2 alphaUv = baseUv * uUvScale;", VfxShaderSource.MeshVertex);
            Assert.Contains("vCornerUv = alphaUv;", VfxShaderSource.MeshVertex);
            Assert.Contains("sampleAddressed(uTex, vCornerUv, uAddressMode).a", VfxShaderSource.MeshFragment);
            Assert.Contains("vCornerUv = aRotFrame;", VfxShaderSource.ParticleVertex);
            Assert.Contains("sampleAddressed(uTex, vCornerUv, uAddressMode).a", VfxShaderSource.ParticleFragment);
        }

        [Fact]
        public void RampAndErosionUseUnfoldedAtlasCoordinatesLikeLtk()
        {
            const string rampAtMult = "colorUv = atlasUvRaw(vLocalUvMult, vCellMult, uTexDivMult);";
            const string erosionAtBase = "atlasUvRaw(vLocalUv, vCell, uTexDiv)";

            Assert.Contains(rampAtMult, VfxShaderSource.MeshFragment);
            Assert.Contains(rampAtMult, VfxShaderSource.ParticleFragment);
            Assert.Contains(erosionAtBase, VfxShaderSource.MeshFragment);
            Assert.Contains(erosionAtBase, VfxShaderSource.ParticleFragment);
        }

        [Fact]
        public void GroundLayerFlattensFinalWorldPositionAtZeroLikeLtk()
        {
            Assert.Contains("if (uIsGroundLayer != 0) world.y = 0.0;", VfxShaderSource.ParticleVertex);
            Assert.Contains("if (uIsGroundLayer != 0) p.y = 0.0;", VfxShaderSource.MeshVertex);
            Assert.DoesNotContain("groundForward", VfxShaderSource.ParticleVertex);
            Assert.DoesNotContain("0.02", VfxShaderSource.ParticleVertex);
        }

        [Fact]
        public void ZeroStrengthDistortionKeepsTheLitParticlePath()
        {
            Assert.Contains("uIsDistortion != 0 && uDistortionStrength != 0.0", VfxShaderSource.MeshFragment);
            Assert.Contains("uIsDistortion != 0 && uDistortionStrength != 0.0", VfxShaderSource.ParticleFragment);
        }
    }
}

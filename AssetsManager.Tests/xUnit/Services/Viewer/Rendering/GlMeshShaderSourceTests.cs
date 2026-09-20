using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GlMeshShaderSourceTests
    {
        [Fact]
        public void AuthoredMaterialCullingKeepsTheExpectedFace()
        {
            Assert.Equal(
                TriangleFace.Back,
                GlMeshRenderer.MaterialCullFace(ModelMaterialRenderState.Default));
            Assert.Equal(
                TriangleFace.Front,
                GlMeshRenderer.MaterialCullFace(ModelMaterialRenderState.Default with { Inverted = true }));
        }

        [Theory]
        [InlineData(
            ModelMaterialBlendMode.Normal,
            false,
            BlendingFactor.SrcAlpha,
            BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One,
            BlendingFactor.OneMinusSrcAlpha)]
        [InlineData(
            ModelMaterialBlendMode.Normal,
            true,
            BlendingFactor.One,
            BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One,
            BlendingFactor.OneMinusSrcAlpha)]
        [InlineData(
            ModelMaterialBlendMode.Additive,
            false,
            BlendingFactor.SrcAlpha,
            BlendingFactor.One,
            BlendingFactor.One,
            BlendingFactor.One)]
        [InlineData(
            ModelMaterialBlendMode.Additive,
            true,
            BlendingFactor.One,
            BlendingFactor.One,
            BlendingFactor.One,
            BlendingFactor.One)]
        public void AuthoredMaterialBlendingMatchesThreeJs(
            ModelMaterialBlendMode blending,
            bool premultipliedAlpha,
            BlendingFactor sourceRgb,
            BlendingFactor destinationRgb,
            BlendingFactor sourceAlpha,
            BlendingFactor destinationAlpha)
        {
            var factors = GlMeshRenderer.MaterialBlendFactors(blending, premultipliedAlpha);

            Assert.Equal(sourceRgb, factors.SourceRgb);
            Assert.Equal(destinationRgb, factors.DestinationRgb);
            Assert.Equal(sourceAlpha, factors.SourceAlpha);
            Assert.Equal(destinationAlpha, factors.DestinationAlpha);
        }

        [Fact]
        public void ReferenceCharacterLightingUsesTheAuthoredSunSplit()
        {
            var lighting = GlMeshRenderer.ReferenceCharacterLighting();
            Vector3 expectedDirection = Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));

            Assert.Equal(expectedDirection.X, lighting.LightDirection.X, 6);
            Assert.Equal(expectedDirection.Y, lighting.LightDirection.Y, 6);
            Assert.Equal(expectedDirection.Z, lighting.LightDirection.Z, 6);
            Assert.Equal(new Vector3(0.4f), lighting.LightColor);
            Assert.Equal(Vector3.Zero, lighting.FillColor);
            Assert.Equal(new Vector3(0.6f), lighting.AmbientColor);
        }

        [Fact]
        public void Fragment_AdvancesBaseUvFromModelLifetime()
        {
            Assert.Contains(
                "vec2 materialUv = vUv * uMaterialUvRepeat + uMaterialUvScroll * uEffectTime;",
                GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Fragment_UsesSrgbCharacterMaterialPath()
        {
            Assert.Contains("uniform int uMaterialSrgb;", GlMeshShaderSource.Fragment);
            Assert.Contains("vec4 texColor = readBaseTexture(materialUv);", GlMeshShaderSource.Fragment);
            Assert.Contains("return texture(uTex, uv);", GlMeshShaderSource.Fragment);
            Assert.DoesNotContain("sampleValue.rgb = srgbToLinear(sampleValue.rgb);", GlMeshShaderSource.Fragment);
            Assert.Contains("? srgbToLinear(uColorTint.rgb)", GlMeshShaderSource.Fragment);
            Assert.Contains("finalColor = linearToSrgb(finalColor);", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void CharacterBaseTexturesUseSrgbGpuStorageWhileRawTexturesStayLinear()
        {
            Assert.Equal(InternalFormat.Srgb8Alpha8, GlMeshResourceCache.BaseTextureInternalFormat(true));
            Assert.Equal(InternalFormat.Rgba8, GlMeshResourceCache.BaseTextureInternalFormat(false));
        }

        [Fact]
        public void Fragment_OnlyUsesCharacterTextureAlphaWhenMaterialReadsCoverage()
        {
            Assert.Contains("uniform int uMaterialUsesTextureAlpha;", GlMeshShaderSource.Fragment);
            Assert.Contains(
                "float coverageAlpha = uMaterialUsesTextureAlpha != 0 ? texColor.a : 1.0;",
                GlMeshShaderSource.Fragment);
            Assert.Contains("if (uMaterialUsesTextureAlpha != 0)", GlMeshShaderSource.Fragment);
            Assert.Contains("texColor.a *= mix(1.0, fresnelAlpha, fadeMask);", GlMeshShaderSource.Fragment);
            Assert.DoesNotContain("texColor.a <= 0.0001", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Fragment_ReusesDecodedBaseTextureForFlowSampling()
        {
            Assert.Contains("vec3 flowColor = readBaseTexture(flowUv).rgb", GlMeshShaderSource.Fragment);
            Assert.DoesNotContain("vec3 flowColor = texture(uTex, flowUv).rgb", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Fragment_UsesIndependentComposableMaterialLayers()
        {
            Assert.Contains("if ((uEffectKind & 1) != 0 && uAdditiveTexIndex >= 0)", GlMeshShaderSource.Fragment);
            Assert.Contains("if ((uEffectKind & 2) != 0 && uFlowTexIndex >= 0)", GlMeshShaderSource.Fragment);
            Assert.Contains("if ((uEffectKind & 8) != 0 && uDissolvePatternIndex >= 0)", GlMeshShaderSource.Fragment);
            Assert.Contains("if ((uEffectKind & 128) != 0 && uEmissionTexIndex >= 0)", GlMeshShaderSource.Fragment);
            Assert.DoesNotContain("else if ((uEffectKind & 2)", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Fragment_UsesAuthoredStateDistortionAndChannels()
        {
            Assert.Contains("uDissolveStateIndex >= 0", GlMeshShaderSource.Fragment);
            Assert.Contains("uDistortionTexIndex >= 0", GlMeshShaderSource.Fragment);
            Assert.Contains("float channelValue(vec4 value, int channel)", GlMeshShaderSource.Fragment);
            Assert.Contains("sampleAux(uFresnelNoiseIndex, uv)", GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void Vertex_UsesAuthoredComplexDeformationTextures()
        {
            Assert.Contains("(uEffectKind & 2048) != 0", GlMeshShaderSource.Vertex);
            Assert.Contains("sampleAux(uDeformNoiseIndex, deformUv)", GlMeshShaderSource.Vertex);
            Assert.Contains("sampleAux(uDeformMaskIndex, aUv)", GlMeshShaderSource.Vertex);
        }

        [Fact]
        public void ShaderFactory_ExpandsAuxiliaryTextureSlotsForCapableHardware()
        {
            string vertex = GlMeshShaderSource.CreateVertex(GlMeshShaderSource.MaximumAuxiliaryTextureCount);
            string fragment = GlMeshShaderSource.CreateFragment(GlMeshShaderSource.MaximumAuxiliaryTextureCount);

            Assert.Contains("uniform sampler2D uAuxTex19;", vertex);
            Assert.Contains("if (index == 19) return texture(uAuxTex19, uv);", vertex);
            Assert.Contains("uniform sampler2D uAuxTex19;", fragment);
            Assert.Contains("if (index == 19) return texture(uAuxTex19, uv);", fragment);
        }

        [Fact]
        public void ShaderFactory_ShrinksAuxiliaryTextureSlotsForConstrainedHardware()
        {
            string vertex = GlMeshShaderSource.CreateVertex(4);
            string fragment = GlMeshShaderSource.CreateFragment(4);

            Assert.Contains("uniform sampler2D uAuxTex3;", vertex);
            Assert.DoesNotContain("uniform sampler2D uAuxTex4;", vertex);
            Assert.Contains("if (index == 3) return texture(uAuxTex3, uv);", vertex);
            Assert.DoesNotContain("if (index == 4) return texture(uAuxTex4, uv);", vertex);
            Assert.Contains("uniform sampler2D uAuxTex3;", fragment);
            Assert.DoesNotContain("uniform sampler2D uAuxTex4;", fragment);
        }

        [Theory]
        [InlineData(16, 16, 48, 14)]
        [InlineData(32, 32, 192, 20)]
        [InlineData(64, 64, 256, 20)]
        public void Renderer_CapsAuxiliaryTexturesToContextLimits(
            int fragmentUnits,
            int vertexUnits,
            int combinedUnits,
            int expected)
        {
            Assert.Equal(
                expected,
                GlMeshRenderer.CalculateAuxiliaryTextureCapacity(
                    fragmentUnits,
                    vertexUnits,
                    combinedUnits));
        }
    }
}

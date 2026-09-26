using System.Numerics;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils;
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
        public void DefaultStudioLightingMatchesReferenceCharacterLighting()
        {
            var studio = GlMeshRenderer.StudioCharacterLighting(
                ViewerViewportModel.DefaultAmbientIntensity,
                ViewerViewportModel.DefaultLightRotation,
                ViewerViewportModel.DefaultLightHeight);
            var reference = GlMeshRenderer.ReferenceCharacterLighting();

            Assert.Equal(reference.LightDirection.X, studio.LightDirection.X, 6);
            Assert.Equal(reference.LightDirection.Y, studio.LightDirection.Y, 6);
            Assert.Equal(reference.LightDirection.Z, studio.LightDirection.Z, 6);
            Assert.Equal(reference.LightColor.X, studio.LightColor.X, 6);
            Assert.Equal(reference.LightColor.Y, studio.LightColor.Y, 6);
            Assert.Equal(reference.LightColor.Z, studio.LightColor.Z, 6);
            Assert.Equal(reference.FillColor, studio.FillColor);
            Assert.Equal(reference.AmbientColor.X, studio.AmbientColor.X, 6);
            Assert.Equal(reference.AmbientColor.Y, studio.AmbientColor.Y, 6);
            Assert.Equal(reference.AmbientColor.Z, studio.AmbientColor.Z, 6);
        }

        [Fact]
        public void ScenePartsCanUseUnlitSrgbTextureSemanticsWithoutAStaticMaterial()
        {
            var part = new ModelPart
            {
                ForceUnlit = true,
                TreatBaseTextureAsSrgb = true,
                UseBaseTextureAlpha = true
            };

            Assert.True(part.UsesUnlitShading);
            Assert.True(part.UsesSrgbBaseTexture);
            Assert.True(part.UseBaseTextureAlpha);
            Assert.Null(part.MaterialDefinition);
        }

        [Fact]
        public void GameShadersOnlyRunForTheLitMaterialSurface()
        {
            Assert.False(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Lit, shadersEnabled: false));
            Assert.True(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Lit, shadersEnabled: true));
            Assert.False(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Unshaded, shadersEnabled: true));
            Assert.False(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Untextured, shadersEnabled: true));
            Assert.False(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Wireframe, shadersEnabled: true));
            Assert.False(GlMeshRenderer.UsesGameShaders(VfxPreviewViewMode.Lit, shadersEnabled: true, wireframePass: true));
        }

        [Fact]
        public void VfxStudioDisplayDefaultsMatchReferenceGameShadersOptIn()
        {
            AppSettings settings = AppSettings.GetDefaultSettings();

            Assert.Equal("Lit", settings.VfxStudio.ViewMode);
            Assert.False(settings.VfxStudio.WireOverlay);
            Assert.False(settings.VfxStudio.ShadersEnabled);
            Assert.False(new VfxInspectorModel().PreviewShaders);
        }

        [Fact]
        public void VfxStudioCharacterSpaceMirrorsXWithoutChangingTheNormalViewerSpace()
        {
            var model = new SceneModel
            {
                Scale = 2d,
                PositionX = 3d,
                PositionY = 4d,
                PositionZ = 5d
            };

            Matrix4x4 viewerWorld = GlMeshRenderer.CreateWorldMatrix(model);
            Matrix4x4 interactionWorld = ViewerInteractionService.CreateWorldMatrix(model);
            Matrix4x4 vfxWorld = GlMeshRenderer.CreateWorldMatrix(model, mirrorCharacterX: true);
            Vector3 viewerXAxis = Vector3.TransformNormal(Vector3.UnitX, viewerWorld);
            Vector3 vfxXAxis = Vector3.TransformNormal(Vector3.UnitX, vfxWorld);

            Assert.Equal(viewerWorld, interactionWorld);
            Assert.Equal(2f, viewerXAxis.X, 6);
            Assert.Equal(-2f, vfxXAxis.X, 6);
            Assert.True(viewerWorld.GetDeterminant() > 0f);
            Assert.True(vfxWorld.GetDeterminant() < 0f);
        }

        [Fact]
        public void VfxStudioViewModeKeepsWireOverlayIndependent()
        {
            var model = new VfxInspectorModel
            {
                PreviewWireOverlay = true,
                PreviewViewMode = VfxPreviewViewMode.Unshaded
            };

            Assert.False(model.CanPreviewWireOverlay);
            Assert.False(model.EffectivePreviewWireOverlay);

            model.PreviewViewMode = VfxPreviewViewMode.Untextured;
            Assert.True(model.CanPreviewWireOverlay);
            Assert.True(model.EffectivePreviewWireOverlay);
        }

        [Fact]
        public void Fragment_WireframeBypassesTheStockMaterialPath()
        {
            int wire = GlMeshShaderSource.Fragment.IndexOf("if (uWireframePass != 0)");
            int materialUv = GlMeshShaderSource.Fragment.IndexOf(
                "vec2 materialUv = vUv * uMaterialUvRepeat + uMaterialUvScroll * uEffectTime;");

            Assert.Contains("uniform vec4 uWireframeColor;", GlMeshShaderSource.Fragment);
            Assert.True(wire >= 0 && wire < materialUv);
            Assert.Contains("fragColor = uWireframeColor;", GlMeshShaderSource.Fragment);
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
        public void Fragment_PremultipliesBeforeSrgbOutputEncoding()
        {
            int finalAlphaTest = GlMeshShaderSource.Fragment.LastIndexOf(
                "if (uAlphaCutoff > 0.0 && texColor.a < uAlphaCutoff) discard;");
            int premultiply = GlMeshShaderSource.Fragment.IndexOf("finalColor *= texColor.a;");
            int outputEncoding = GlMeshShaderSource.Fragment.IndexOf(
                "finalColor = linearToSrgb(finalColor);");

            Assert.True(finalAlphaTest >= 0);
            Assert.True(premultiply > finalAlphaTest);
            Assert.True(outputEncoding > premultiply);
        }

        [Fact]
        public void CharacterBaseTexturesUseSrgbGpuStorageWhileRawTexturesStayLinear()
        {
            Assert.Equal(InternalFormat.Srgb8Alpha8, GlMeshResourceCache.BaseTextureInternalFormat(true));
            Assert.Equal(InternalFormat.Rgba8, GlMeshResourceCache.BaseTextureInternalFormat(false));
        }

        [Fact]
        public void Fragment_UsesBaseTextureAlphaWhenConfigured()
        {
            Assert.Contains("uniform int uMaterialUsesTextureAlpha;", GlMeshShaderSource.Fragment);
            Assert.Contains(
                "coverageAlpha = (uMaterialUsesTextureAlpha != 0 ? texColor.a : 1.0) * uColorTint.a;",
                GlMeshShaderSource.Fragment);
        }

        [Fact]
        public void VfxMeshFragment_UsesUniformErosionMixerWithoutUndefinedVariables()
        {
            Assert.Contains("uniform vec4 uErosionMixer;", AssetsManager.Services.Viewer.Vfx.Rendering.VfxShaderSource.MeshFragment);
            Assert.Contains("clamp(dot(erosionTexel, uErosionMixer), 0.0, 1.0)", AssetsManager.Services.Viewer.Vfx.Rendering.VfxShaderSource.MeshFragment);
            Assert.DoesNotContain("vErosionMixer", AssetsManager.Services.Viewer.Vfx.Rendering.VfxShaderSource.MeshFragment);
        }
    }
}

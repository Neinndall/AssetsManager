using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils.Rendering
{
    public sealed class VfxRenderingUtilsTests
    {
        [Theory]
        [InlineData(0f, 1, 0.5f)]
        [InlineData(0f, 4, 0.125f)]
        [InlineData(1f, 4, 0.375f)]
        [InlineData(2f, 4, 0.625f)]
        [InlineData(3f, 4, 0.875f)]
        public void PaletteRowNormalizedAlignsWithTexelCenters(float row, int count, float expected)
        {
            var palette = new VfxPaletteDefinition(count, VfxCurve3.Const(new Vector3(row, 0f, 0f)));
            float actual = VfxShaderParameterUtils.ResolvePaletteRowNormalized(palette);
            Assert.Equal(expected, actual, 5);
        }

        [Fact]
        public void PaletteSelectMainPackagesRowAndScrollCoordinates()
        {
            var palette = new VfxPaletteDefinition(
                PaletteCount: 2,
                PaletteSelector: VfxCurve3.Const(new Vector3(1f, 0f, 0f)),
                ScrollU: VfxCurveF.Const(0.25f),
                ScrollV: VfxCurveF.Const(0.75f));

            Vector4 selectMain = VfxShaderParameterUtils.ResolvePaletteSelectMain(palette, phase: 0.5f);
            Assert.Equal(0.75f, selectMain.X, 5); // (1 + 0.5) / 2 = 0.75
            Assert.Equal(0f, selectMain.Y);
            Assert.Equal(0.25f, selectMain.Z);
            Assert.Equal(0.75f, selectMain.W);
        }

        [Fact]
        public void SoftParticleControlDistinguishesAdditiveAndBlendModes()
        {
            // Modes 1 and 4 (Additive)
            Assert.Equal(new Vector4(1f, 0f, 0f, 1f), VfxShaderParameterUtils.ResolveSoftParticleControl(1));
            Assert.Equal(new Vector4(1f, 0f, 0f, 1f), VfxShaderParameterUtils.ResolveSoftParticleControl(4));

            // Mode 5 (Premultiplied / Blend)
            Assert.Equal(new Vector4(0f, 1f, 0f, 1f), VfxShaderParameterUtils.ResolveSoftParticleControl(5));

            // Other modes (Default Alpha Blend)
            Assert.Equal(new Vector4(0f, 1f, 1f, 0f), VfxShaderParameterUtils.ResolveSoftParticleControl(0));
            Assert.Equal(new Vector4(0f, 1f, 1f, 0f), VfxShaderParameterUtils.ResolveSoftParticleControl(2));
        }

        [Fact]
        public void AttachedMeshReceivesOverlayBiasByDefault()
        {
            var attached = CreateDefinition() with { PrimitiveKind = VfxPrimitiveKind.AttachedMesh };
            Vector2? offset = VfxGeometryUtils.ResolvePolygonOffset(attached);
            Assert.NotNull(offset);
            Assert.Equal(new Vector2(-1f, -1f), offset.Value);

            var quad = CreateDefinition() with { PrimitiveKind = VfxPrimitiveKind.CameraQuad };
            Assert.Null(VfxGeometryUtils.ResolvePolygonOffset(quad));

            var customBias = CreateDefinition() with
            {
                PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                DepthBiasFactors = new Vector2(2f, 4f)
            };
            Assert.Equal(new Vector2(2f, 4f), VfxGeometryUtils.ResolvePolygonOffset(customBias));
        }

        [Fact]
        public void SoftParticleDisabledForAttachedMeshOrFixedAlphaUv()
        {
            var soft = new VfxSoftParticleDefinition(0f, 1f, 0f, 1f);
            var attached = CreateDefinition() with
            {
                SoftParticle = soft,
                PrimitiveKind = VfxPrimitiveKind.AttachedMesh
            };
            Assert.False(VfxGeometryUtils.ShouldUseSoftParticles(attached, hasSceneDepth: true));

            var fixedAlphaQuad = CreateDefinition() with
            {
                SoftParticle = soft,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                UvMode = 2
            };
            Assert.False(VfxGeometryUtils.ShouldUseSoftParticles(fixedAlphaQuad, hasSceneDepth: true));

            var standardQuad = CreateDefinition() with
            {
                SoftParticle = soft,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                UvMode = 0
            };
            Assert.True(VfxGeometryUtils.ShouldUseSoftParticles(standardQuad, hasSceneDepth: true));
            Assert.False(VfxGeometryUtils.ShouldUseSoftParticles(standardQuad, hasSceneDepth: false));
        }

        [Fact]
        public void CameraForwardEvaluatesRightHandCrossProduct()
        {
            Vector3 forward = VfxGeometryUtils.ResolveCameraForward(Vector3.UnitX, Vector3.UnitY);
            Assert.Equal(-Vector3.UnitZ, forward);
        }

        [Fact]
        public void PopulateNativeParametersFillsAllCoreShaderBindings()
        {
            var def = CreateDefinition() with
            {
                SoftParticle = new VfxSoftParticleDefinition(0f, 1f, 0f, 1f),
                BlendMode = 1
            };
            var map = new Dictionary<string, Vector4>();
            VfxShaderParameterUtils.PopulateNativeParameters(map, def, 0.5f);

            Assert.True(map.ContainsKey("TEXTURE_INFO"));
            Assert.True(map.ContainsKey("TEXTURE_INFO_2"));
            Assert.True(map.ContainsKey("PARTICLE_DEPTH_PUSH_PULL"));
            Assert.True(map.ContainsKey("cSoftParticleParams"));
            Assert.True(map.ContainsKey("cSoftParticleControl"));
            Assert.True(map.ContainsKey("cPaletteSelectMain"));
            Assert.True(map.ContainsKey("cAlphaErosionParams"));
            Assert.True(map.ContainsKey("vFresnel"));
            Assert.True(map.ContainsKey("vReflection"));
        }

        [Fact]
        public void TextureTransformResolvesSpriteCellAndPeriodicOffset()
        {
            Vector2 cell = VfxTextureTransformUtils.ResolveGridCell(5f, new Vector2(4f, 4f));
            Assert.Equal(new Vector2(1f, 1f), cell); // 5th frame in 4x4 grid: col 1, row 1

            Vector2 cellSize = VfxTextureTransformUtils.ResolveCellSize(new Vector2(4f, 2f));
            Assert.Equal(new Vector2(0.25f, 0.5f), cellSize);

            Assert.Equal(0.3f, VfxTextureTransformUtils.Wrap(1.3f, 1f), 4);
            Assert.Equal(0.7f, VfxTextureTransformUtils.Wrap(-0.3f, 1f), 4);

            Assert.Equal(1f, VfxTextureTransformUtils.Ramp(2.5f, clamped: true, maxReach: 1f));
            Assert.Equal(0.5f, VfxTextureTransformUtils.Ramp(2.5f, clamped: false));

            Assert.Equal(0.2f, VfxTextureTransformUtils.Periodic(1.2f, addressMode: 0), 4); // Wrap
            Assert.Equal(0.8f, VfxTextureTransformUtils.Periodic(2.8f, addressMode: 2), 4); // Mirror (period 2)
            Assert.Equal(3.5f, VfxTextureTransformUtils.Periodic(3.5f, addressMode: 1));    // Clamp
        }

        [Fact]
        public void ColorEvaluationEvaluatesLookupAxisAndPremultiplication()
        {
            // Lifetime
            Assert.Equal(3.5f, VfxColorEvaluationUtils.ResolveLookupAxis(1, scale: 2f, offset: 1.5f, age01: 1f, speed: 10f, birthRandom: 0.5f));
            // Velocity
            Assert.Equal(21.5f, VfxColorEvaluationUtils.ResolveLookupAxis(2, scale: 2f, offset: 1.5f, age01: 1f, speed: 10f, birthRandom: 0.5f));
            // Birth Random
            Assert.Equal(2.5f, VfxColorEvaluationUtils.ResolveLookupAxis(3, scale: 2f, offset: 1.5f, age01: 1f, speed: 10f, birthRandom: 0.5f));
            // Constant fallback
            Assert.Equal(2f, VfxColorEvaluationUtils.ResolveLookupAxis(99, scale: 2f, offset: 1.5f, age01: 1f, speed: 10f, birthRandom: 0.5f));

            Vector4 baseColor = new(0.5f, 0.25f, 0.1f, 0.5f);
            // Additive (0): rgb *= alpha, alpha = 1
            Vector4 premultipliedAdd = VfxColorEvaluationUtils.PremultiplyVertexColor(baseColor, blendMode: 0, isDistortion: false, hasCustomMaterial: false);
            Assert.Equal(new Vector4(0.25f, 0.125f, 0.05f, 1.0f), premultipliedAdd);

            // Alpha Blend (1): unchanged
            Vector4 blendAlpha = VfxColorEvaluationUtils.PremultiplyVertexColor(baseColor, blendMode: 1, isDistortion: false, hasCustomMaterial: false);
            Assert.Equal(baseColor, blendAlpha);

            // Distortion: unchanged
            Vector4 distortionColor = VfxColorEvaluationUtils.PremultiplyVertexColor(baseColor, blendMode: 0, isDistortion: true, hasCustomMaterial: false);
            Assert.Equal(baseColor, distortionColor);
        }

        [Fact]
        public void DrawOrderPrioritizesGroundLayersAndOpaquePasses()
        {
            Assert.Equal(0, VfxDrawOrderUtils.ResolveBlendRank(3)); // Opaque = rank 0
            Assert.Equal(1, VfxDrawOrderUtils.ResolveBlendRank(0)); // Add = rank 1
            Assert.Equal(2, VfxDrawOrderUtils.ResolveBlendRank(1)); // Alpha = rank 2
            Assert.Equal(3, VfxDrawOrderUtils.ResolveBlendRank(8)); // TargetAlpha = rank 3

            var ground = CreateDefinition() with { IsGroundLayer = true };
            var nonGround = CreateDefinition() with { IsGroundLayer = false };
            Assert.True(VfxDrawOrderUtils.CompareEmitters(ground, 0, nonGround, 0) < 0);
            Assert.True(VfxDrawOrderUtils.CompareEmitters(nonGround, 0, ground, 0) > 0);
        }

        [Fact]
        public void BufferGrowthCalculatesExponentialPowerOfTwo()
        {
            Assert.Equal(64, VfxBufferGrowthUtils.CalculateNextCapacity(0, 32, minimumLength: 64));
            Assert.Equal(128, VfxBufferGrowthUtils.CalculateNextCapacity(64, 65));
            Assert.Equal(256, VfxBufferGrowthUtils.CalculateNextCapacity(64, 150));
            Assert.Equal(64, VfxBufferGrowthUtils.CalculateNextCapacity(64, 60)); // No growth needed
        }

        private static VfxEmitterDefinition CreateDefinition() =>
            new(
                Name: "test_emitter",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 0,
                BirthScale: VfxCurve3.Const(Vector3.One),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "sample.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false,
                RenderState: VfxEmitterRenderState.Default);
    }
}

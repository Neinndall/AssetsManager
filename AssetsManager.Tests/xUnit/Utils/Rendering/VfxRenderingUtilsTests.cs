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

using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxCompatibilityAnalyzerTests
    {
        [Fact]
        public void ReportsReadyForStandaloneSupportedEmitter()
        {
            VfxCompatibilityReport report = VfxCompatibilityAnalyzer.Analyze(
                SystemWith(CreateEmitter()));

            Assert.Equal(VfxCompatibilityLevel.Ready, report.Level);
            Assert.Empty(report.Issues);
        }

        [Fact]
        public void ReportsOwnerContextForAttachedMesh()
        {
            VfxCompatibilityReport report = VfxCompatibilityAnalyzer.Analyze(
                SystemWith(CreateEmitter() with
                {
                    PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                    IsMeshPrimitive = true
                }));

            Assert.Equal(VfxCompatibilityLevel.ContextRequired, report.Level);
            Assert.Equal("OWNER_ATTACHED_MESH", Assert.Single(report.Issues).Code);
        }

        [Fact]
        public void ReportsBindPoseWhenAttachedOwnerSubmeshCanBeResolved()
        {
            VfxCompatibilityReport report = VfxCompatibilityAnalyzer.Analyze(
                SystemWith(CreateEmitter() with
                {
                    PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                    IsMeshPrimitive = true,
                    AttachedSubmeshHashes = new uint[] { 11 }
                }),
                new VfxOwnerSceneContext("Characters/Test/Test.skn", "Characters/Test/Test.skl", 1.25f));

            Assert.Equal(VfxCompatibilityLevel.Approximate, report.Level);
            Assert.Equal("OWNER_BIND_POSE", Assert.Single(report.Issues).Code);
        }

        [Fact]
        public void UnsupportedAuthoredFeaturesTakePriorityOverApproximations()
        {
            VfxEmitterDefinition emitter = CreateEmitter() with
            {
                IsFollowingTerrain = true,
                AuthoredFeatures = new VfxEmitterAuthoredFeatures(
                    HasCustomMaterial: true,
                    HasStencil: true)
            };

            VfxCompatibilityReport report = VfxCompatibilityAnalyzer.Analyze(SystemWith(emitter));

            Assert.Equal(VfxCompatibilityLevel.Limited, report.Level);
            Assert.Equal(1, report.ApproximationCount);
            Assert.Equal(1, report.UnsupportedCount);
        }

        [Fact]
        public void ReportsOnlyUnknownStencilModesAsUnsupported()
        {
            VfxEmitterDefinition supported = CreateEmitter() with
            {
                RenderState = VfxEmitterRenderState.Default with { StencilMode = 2, StencilReference = 7 },
                AuthoredFeatures = new VfxEmitterAuthoredFeatures(HasStencil: true)
            };
            VfxEmitterDefinition unknown = CreateEmitter() with
            {
                Name = "Unknown",
                RenderState = VfxEmitterRenderState.Default with { StencilMode = 9 },
                AuthoredFeatures = new VfxEmitterAuthoredFeatures(HasStencil: true)
            };

            Assert.Equal(VfxCompatibilityLevel.Ready, VfxCompatibilityAnalyzer.Analyze(SystemWith(supported)).Level);
            VfxCompatibilityIssue issue = Assert.Single(VfxCompatibilityAnalyzer.Analyze(SystemWith(unknown)).Issues);
            Assert.Equal("STENCIL_MODE", issue.Code);
            Assert.Equal(VfxCompatibilitySeverity.Unsupported, issue.Severity);
        }

        [Fact]
        public void SystemMaterialOverridesAreReportedWithoutChampionRules()
        {
            var system = new VfxSystemDefinition(
                1,
                "material",
                "material",
                new[] { CreateEmitter() },
                AuthoredFeatures: new VfxSystemAuthoredFeatures(HasMaterialOverrides: true));

            VfxCompatibilityReport report = VfxCompatibilityAnalyzer.Analyze(system);

            Assert.Equal(VfxCompatibilityLevel.Limited, report.Level);
            Assert.Equal("SYSTEM_MATERIAL_OVERRIDES", Assert.Single(report.Issues).Code);
        }

        private static VfxSystemDefinition SystemWith(VfxEmitterDefinition emitter)
            => new(1, "test", "test", new[] { emitter });

        private static VfxEmitterDefinition CreateEmitter()
            => new(
                Name: "Basic",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: 1f,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(Vector3.One),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "effects/test.dds",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false);
    }
}

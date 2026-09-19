using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxRuntimeTests
    {
        [Theory]
        [InlineData(0f, 1f, 0f)]
        [InlineData(0.25f, 1f, 0.25f)]
        [InlineData(1f, 1f, 0f)]
        [InlineData(1.25f, 1f, 0.25f)]
        [InlineData(-0.25f, 1f, 0.75f)]
        public void AnimationPoseTimeLoopsLikeLtk(float time, float duration, float expected)
        {
            Assert.Equal(expected, AnimationService.FoldAnimationTime(time, duration), precision: 5);
        }

        [Theory]
        [InlineData(1f, 0f)]
        [InlineData(float.NaN, 1f)]
        [InlineData(1f, float.PositiveInfinity)]
        public void AnimationPoseTimeFallsBackToZeroForInvalidClocks(float time, float duration)
        {
            Assert.Equal(0f, AnimationService.FoldAnimationTime(time, duration));
        }

        [Theory]
        [InlineData(-1d, 0d)]
        [InlineData(0d, 0d)]
        [InlineData(0.62d, 37d / 60d)]
        [InlineData(0.625d, 38d / 60d)]
        [InlineData(61d, 60d)]
        public void VfxSeekQuantizesToTheNearestLtkFrame(double time, double expected)
        {
            Assert.Equal(expected, VfxRenderSession.QuantizeLtkSeek(time), precision: 10);
        }

        [Fact]
        public void AnimationHierarchyPlacesParentsBeforeChildren()
        {
            (int[] order, int[] parents) = AnimationService.BuildHierarchy(new[] { 1, -1, 1 });

            Assert.Equal(new[] { 1, 2, 0 }, order);
            Assert.Equal(new[] { 1, -1, 1 }, parents);
        }

        [Fact]
        public void AnimationHierarchyBreaksCyclesAtFirstUnplacedJointLikeLtk()
        {
            (int[] order, int[] parents) = AnimationService.BuildHierarchy(new[] { 1, 0 });

            Assert.Equal(new[] { 0, 1 }, order);
            Assert.Equal(new[] { -1, 0 }, parents);
        }

        [Fact]
        public void MeshInstancesPreserveAuthoredNonUniformScale()
        {
            var emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(1, "test", "test", new[] { emitter });
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(system, Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(1, state.InstanceCount);
            Assert.Equal(2f, state.Instances[3]);
            Assert.Equal(3f, state.Instances[4]);
            Assert.Equal(4f, state.Instances[18]);
        }

        [Fact]
        public void UniformScaleCurveUsesItsAuthoredScalarForDynamicScale()
        {
            var emitter = CreateEmitter(new Vector3(8f, 1f, 1f), VfxEmitterRenderState.Default) with
            {
                ScaleOverLife = VfxCurve3.Const(new Vector3(0.6f, 0f, 0f)),
                IsUniformScale = true
            };
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "confetti", "confetti", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(4.8f, state.Instances[3], precision: 5);
            Assert.Equal(4.8f, state.Instances[4], precision: 5);
            Assert.Equal(4.8f, state.Instances[18], precision: 5);
        }

        [Fact]
        public void WorldTransformScaleAppliesToParticleDimensions()
        {
            var emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default);
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(
                new VfxSystemDefinition(1, "scaled", "scaled", new[] { emitter }),
                Matrix4x4.CreateScale(2f));

            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(4f, state.Instances[3]);
            Assert.Equal(6f, state.Instances[4]);
            Assert.Equal(8f, state.Instances[18]);
        }

        [Fact]
        public void ViewerVfxTransformMatchesTheActiveSceneTransform()
        {
            var model = new SceneModel
            {
                PositionX = 10,
                PositionY = 20,
                PositionZ = 30,
                RotationY = 90,
                Scale = 2
            };

            Matrix4x4 transform = ViewerInteractionService.CreateWorldMatrix(model);
            Vector3 origin = Vector3.Transform(Vector3.Zero, transform);
            Vector3 scaledAxis = Vector3.TransformNormal(Vector3.UnitX, transform);

            Assert.Equal(new Vector3(10f, 20f, 30f), origin);
            Assert.Equal(2f, scaledAxis.Length(), 3);
        }

        [Fact]
        public void EmitterSpaceParticlesFollowAnimatedEmitterPosition()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(10f),
                EmitterLifetime = 1f,
                IsEmitterSpace = true,
                EmitterPosition = new VfxCurve3(
                    Vector3.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) })
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "emitter-space", "emitter-space", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.1f);
            runtime.Update(0.1f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(2f, state.Particles[0].Pos.X, precision: 5);
        }

        [Fact]
        public void EmitterSpacePositionDeltaUsesEachParticlesBirthFrameLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsEmitterSpace = true,
                IsSingleParticle = true,
                ParticleLifetime = VfxCurveF.Const(10f),
                EmitterLifetime = 1f,
                EmitterPosition = new VfxCurve3(
                    Vector3.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) })
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "emitter-space-frame", "emitter-space-frame", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.1f);
            Assert.Equal(1f, Assert.Single(Assert.Single(runtime.Emitters).Particles).Pos.X, precision: 5);

            Matrix4x4 currentRig = Matrix4x4.CreateRotationY(MathF.PI * 0.5f);
            runtime.SetTransform(currentRig, Matrix4x4.Identity);
            runtime.Update(0.1f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.Equal(2f, particle.Pos.X, precision: 5);
            Assert.InRange(MathF.Abs(particle.Pos.Z), 0f, 1e-5f);
        }

        [Fact]
        public void EmitterSpaceDoesNotCarryRootMotionWithoutBindWeightLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsEmitterSpace = true,
                BindWeight = VfxCurveF.Const(0f),
                ParticleLifetime = VfxCurveF.Const(10f),
                EmitterPosition = VfxCurve3.Const(Vector3.Zero)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "emitter-space-root", "emitter-space-root", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.02f);
            Vector3 bornAt = Assert.Single(Assert.Single(runtime.Emitters).Particles).Pos;

            runtime.SetTransform(Matrix4x4.CreateTranslation(100f, 0f, 0f));
            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.Equal(bornAt, particle.Pos);
        }

        [Fact]
        public void BindWeightCarriesAnyNonZeroRootDeltaLikeLtk()
        {
            const float tiny = 1e-7f;
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                BindWeight = VfxCurveF.Const(1f),
                ParticleLifetime = VfxCurveF.Const(10f)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "tiny-bind", "tiny-bind", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.02f);

            runtime.SetTransform(Matrix4x4.CreateTranslation(tiny, 0f, 0f));
            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.InRange(particle.Pos.X, tiny * 0.999f, tiny * 1.001f);
        }

        [Fact]
        public void ForceFieldsReadThePreBindParticlePositionLikeLtk()
        {
            var attraction = new VfxAttractionField(
                VfxCurveF.Const(10f),
                VfxCurve3.Const(Vector3.Zero),
                VfxCurveF.Const(100f));
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                new[] { attraction },
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                Array.Empty<VfxNoiseField>());
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                BindWeight = VfxCurveF.Const(1f),
                ParticleLifetime = VfxCurveF.Const(10f),
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "field-before-bind", "field-before-bind", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.02f);

            runtime.SetTransform(Matrix4x4.CreateTranslation(10f, 0f, 0f));
            runtime.Update(0.1f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.Equal(10f, particle.Pos.X, precision: 5);
            Assert.Equal(Vector3.Zero, particle.Vel);
        }

        [Fact]
        public void ParticleLocalOrientationUsesCurrentRigFrameWithoutMovingParticleLikeLtk()
        {
            static VfxPlaybackRuntime.ParticleLifecycleInfo DeathFrame(bool localOrientation)
            {
                VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
                {
                    IsSingleParticle = true,
                    ParticleLifetime = VfxCurveF.Const(0.5f),
                    ParticleIsLocalOrientation = localOrientation
                };
                var runtime = new VfxPlaybackRuntime(7);
                VfxPlaybackRuntime.ParticleLifecycleInfo died = default;
                bool sawDeath = false;
                runtime.ParticleLifecycle += (_, _, particle) =>
                {
                    if (!particle.Died) return;
                    died = particle;
                    sawDeath = true;
                };
                runtime.SetSystem(new VfxSystemDefinition(1, "particle-local", "particle-local", new[] { emitter }), Vector3.Zero);
                runtime.Update(0.01f);

                Matrix4x4 current = Matrix4x4.CreateRotationY(MathF.PI * 0.5f);
                runtime.SetTransform(current);
                runtime.Update(0.5f);
                Assert.True(sawDeath);
                return died;
            }

            VfxPlaybackRuntime.ParticleLifecycleInfo local = DeathFrame(localOrientation: true);
            VfxPlaybackRuntime.ParticleLifecycleInfo born = DeathFrame(localOrientation: false);

            Vector3 expectedCurrent = Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.CreateRotationY(MathF.PI * 0.5f));
            Vector3 localForward = Vector3.TransformNormal(Vector3.UnitZ, local.Frame);
            Vector3 bornForward = Vector3.TransformNormal(Vector3.UnitZ, born.Frame);
            Assert.True(Vector3.Distance(expectedCurrent, localForward) < 1e-5f);
            Assert.True(Vector3.Distance(Vector3.UnitZ, bornForward) < 1e-5f);
        }

        [Fact]
        public void AttachedMeshIsNotStandaloneVisual()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                IsMeshPrimitive = true
            };

            Assert.False(emitter.IsVisual);
        }

        [Fact]
        public void UnsupportedPrimitiveDoesNotFallThroughToBillboardRendering()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.Unsupported,
                TexturePath = "visible.dds"
            };

            Assert.False(emitter.DrawsAsQuad);
            Assert.False(emitter.IsVisual);
        }

        [Fact]
        public void TrailPrimitiveWithoutTrailDefinitionDoesNotDraw()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                TexturePath = "trail.dds",
                Trail = null
            };

            Assert.False(emitter.DrawsAsTrail);
            Assert.False(emitter.IsVisual);
        }

        [Fact]
        public void MeshInstancesPreserveAuthoredZeroScaleComponents()
        {
            var emitter = CreateEmitter(new Vector3(7f, 0f, 7f), VfxEmitterRenderState.Default);
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "ground", "ground", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(7f, state.Instances[3]);
            Assert.Equal(0f, state.Instances[4]);
            Assert.Equal(7f, state.Instances[18]);
        }

        [Fact]
        public void UniformMeshBirthScaleUsesAuthoredScalarOnEveryAxis()
        {
            var emitter = CreateEmitter(new Vector3(1f, 45f, 40f), VfxEmitterRenderState.Default) with
            {
                IsUniformScale = true
            };
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "jumbotron", "jumbotron", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(1f, state.Instances[3]);
            Assert.Equal(1f, state.Instances[4]);
            Assert.Equal(1f, state.Instances[18]);
        }

        [Fact]
        public void UniformBillboardBirthScaleUsesFirstAuthoredAxisOnBothQuadAxes()
        {
            var emitter = CreateEmitter(new Vector3(100f, 230f, 0f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsUniformScale = true
            };
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "stars", "stars", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(100f, state.Instances[3]);
            Assert.Equal(100f, state.Instances[4]);
        }

        [Fact]
        public void UniformArbitraryQuadScaleUsesFirstAuthoredAxisOnBothQuadAxes()
        {
            var emitter = CreateEmitter(new Vector3(345f, 400f, 50f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsArbitraryQuad = true,
                IsUniformScale = true
            };
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "ring", "ring", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(690f, state.Instances[3]);
            Assert.Equal(690f, state.Instances[4]);
        }

        [Fact]
        public void UniformGroundArbitraryQuadUsesCircularBirthScale()
        {
            var emitter = CreateEmitter(new Vector3(345f, 550f, 1f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsArbitraryQuad = true,
                IsGroundLayer = true,
                IsUniformScale = true
            };
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(new VfxSystemDefinition(1, "ground-ring", "ground-ring", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(690f, state.Instances[3]);
            Assert.Equal(690f, state.Instances[4]);
        }

        [Fact]
        public void MeshInterleavingPreservesPositionUvVertexColorNormalAndSkinningSlots()
        {
            float[] interleaved = VfxMeshResourceCache.BuildInterleaved(
                new[] { 1f, 2f, 3f },
                new[] { 0f, 1f, 0f },
                new[] { 0.25f, 0.75f },
                new[] { 0.1f, 0.2f, 0.3f, 0.4f });

            Assert.Equal(VfxMeshResourceCache.VertexStride, interleaved.Length);
            Assert.Equal(
                new[]
                {
                    1f, 2f, 3f,
                    0.25f, 0.75f,
                    0.1f, 0.2f, 0.3f, 0.4f,
                    0f, 1f, 0f,
                    0f, 0f, 0f, 0f,
                    0f, 0f, 0f, 0f
                },
                interleaved);
        }

        [Fact]
        public void MeshInterleavingPreservesAttachedOwnerBoneIndicesAndWeights()
        {
            float[] interleaved = VfxMeshResourceCache.BuildInterleaved(
                new[] { 1f, 2f, 3f },
                new[] { 0f, 1f, 0f },
                new[] { 0.25f, 0.75f },
                new[] { 1f, 1f, 1f, 1f },
                new[] { 4f, 8f, 15f, 16f },
                new[] { 0.4f, 0.3f, 0.2f, 0.1f });

            Assert.Equal(new[] { 4f, 8f, 15f, 16f }, interleaved[12..16]);
            Assert.Equal(new[] { 0.4f, 0.3f, 0.2f, 0.1f }, interleaved[16..20]);
        }

        [Fact]
        public void BirthColorDynamicsUseEmitterTimeAtParticleCreation()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(10f),
                EmitterLifetime = 1f,
                BirthColor = new VfxCurve4(
                    Vector4.Zero,
                    new[] { 0f, 1f },
                    new[] { new Vector4(1f, 1f, 1f, 0f), Vector4.One })
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "color", "color", new[] { emitter }), Vector3.Zero);

            for (int i = 0; i < 5; i++) simulator.Update(0.1f);

            var state = Assert.Single(simulator.Emitters);
            // LTK truncates the exact floating-point debt without an epsilon. At 0.4s the
            // accumulated 0.1 step is fractionally below one particle, so it is paid at 0.5s.
            Assert.Equal(4, state.InstanceCount);
            int last = (state.InstanceCount - 1) * VfxPlaybackRuntime.InstanceStride;
            Assert.InRange(state.Instances[last + 8], 0.499f, 0.501f);
        }

        [Fact]
        public void CurveSamplingUsesLtksLastDuplicateAndPositiveTinySpanRules()
        {
            var duplicate = new VfxCurveF(
                0f,
                new[] { 0f, 0f, 1f },
                new[] { 1f, 2f, 4f });
            var tinySpan = new VfxCurveF(
                0f,
                new[] { 0f, 0.0000005f },
                new[] { 0f, 1f });

            Assert.Equal(2f, duplicate.Sample(0f));
            Assert.InRange(tinySpan.Sample(0.00000025f), 0.4999f, 0.5001f);
        }

        [Fact]
        public void BirthProbabilityTablesShareOneChanceAcrossAllChannelsLikeLtk()
        {
            var curve = new VfxCurve3(
                Vector3.One,
                null,
                null,
                new[]
                {
                    new VfxProbTable(new[] { 0f, 1f }, new[] { 0f, 1f }),
                    new VfxProbTable(new[] { 0f, 1f }, new[] { 0f, 2f }),
                    new VfxProbTable(new[] { 0f, 1f }, new[] { 0f, 4f })
                });
            var rng = new VfxLtkRandom(42);
            VfxLtkRandom expected = rng.Clone();
            float chance = expected.NextUnitFloat();

            Vector3 drawn = curve.SampleBirth(rng);

            Assert.Equal(chance, drawn.X, precision: 6);
            Assert.Equal(chance * 2f, drawn.Y, precision: 6);
            Assert.Equal(chance * 4f, drawn.Z, precision: 6);
            Assert.Equal(expected.State, rng.State);
        }

        [Fact]
        public void CameraTrailPreservesPointWidthsAndBuildsConnectedGeometry()
        {
            var emitter = CreateEmitter(new Vector3(2f, 9f, 1f), VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(10f),
                EmitterLifetime = 1f,
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                IsMeshPrimitive = false,
                EmitterPosition = new VfxCurve3(
                    Vector3.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) }),
                Trail = new VfxTrailDefinition(VfxCurve3.Const(new Vector3(2f, 0f, 0f)), 2, 1, 30, 10000f)
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "trail", "trail", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);
            simulator.Update(0.1f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(2, state.InstanceCount);
            var geometry = new VfxTrailGeometry();
            Assert.Equal(6, geometry.Build(state, Vector3.UnitZ));
            int stride = VfxTrailGeometry.VertexStride;
            // Birth X is a half-width, and both triangles reuse exactly the same joint.
            Assert.Equal(4f, MathF.Abs(geometry.Vertices[3] - geometry.Vertices[4 * stride + 3]), 3);
            Assert.Equal(geometry.Vertices[0], geometry.Vertices[3 * stride]);
            Assert.Equal(0.5f, MathF.Abs(geometry.Vertices[0] - geometry.Vertices[2 * stride]), 3);
        }

        [Fact]
        public void StaticTrailDoesNotRenderDegenerateParticleQuads()
        {
            var emitter = CreateEmitter(new Vector3(20f, 150f, 2f), VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(120f),
                EmitterLifetime = 1f,
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                IsMeshPrimitive = false
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "trail", "trail", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);

            Assert.Equal(0, new VfxTrailGeometry().Build(Assert.Single(simulator.Emitters), Vector3.UnitZ));
        }

        [Fact]
        public void BeamNamingMeshSuppressesItsRibbonLikeLtk()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.Beam,
                MeshPath = "Effects/BeamMesh.scb",
                Beam = new VfxBeamDefinition(
                    0,
                    0,
                    0,
                    VfxCurve3.Const(Vector3.Zero),
                    VfxCurve4.Const(Vector4.One),
                    false,
                    Vector3.Zero,
                    Vector3.Zero)
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "beam-mesh", "beam-mesh", new[] { emitter }), Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.True(state.Def.SuppressesBeamRibbon);
            Assert.False(state.Def.IsVisual);
            Assert.Equal(0, new VfxBeamGeometry().Build(state, Vector3.UnitZ));
        }

        [Fact]
        public void ShortRateEmitterSpawnsAtActivation()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(1f),
                EmitterLifetime = 0.2f,
                ParticleLifetime = VfxCurveF.Const(0.2f),
                IsMeshPrimitive = true,
                PrimitiveKind = VfxPrimitiveKind.Mesh
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "beam", "beam", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.02f);

            Assert.Equal(1, Assert.Single(simulator.Emitters).InstanceCount);
        }

        [Fact]
        public void TrailHonorsMaximumSamplesAddedPerSimulationStep()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(100f),
                EmitterLifetime = 1f,
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                EmitterPosition = new VfxCurve3(
                    Vector3.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) }),
                Trail = new VfxTrailDefinition(VfxCurve3.Const(Vector3.Zero), 0, 0, 2, 10000f)
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "trail", "trail", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(simulator.Emitters);
            Assert.Equal(2, state.Particles.Count);
            Assert.Equal(0, new VfxTrailGeometry().Build(state, Vector3.UnitZ));
        }

        [Fact]
        public void TrailRejectsSegmentsBeyondAuthoredCutoff()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(10f),
                EmitterLifetime = 1f,
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                EmitterPosition = new VfxCurve3(
                    Vector3.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) }),
                Trail = new VfxTrailDefinition(VfxCurve3.Const(Vector3.Zero), 0, 0, 30, 0.5f)
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "trail", "trail", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);
            simulator.Update(0.1f);

            Assert.Equal(0, new VfxTrailGeometry().Build(Assert.Single(simulator.Emitters), Vector3.UnitZ));
        }

        [Fact]
        public void ErosionChannelMixerIsPreservedPerParticle()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                AlphaErosion = new VfxAlphaErosionDefinition(
                    "erosion.tex",
                    VfxCurveF.Zero,
                    0f,
                    0f,
                    0,
                    VfxCurve4.Const(new Vector4(0f, 1f, 0f, 0f)))
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "erosion", "erosion", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(0f, state.Instances[25]);
            Assert.Equal(1f, state.Instances[26]);
            Assert.Equal(0f, state.Instances[27]);
            Assert.Equal(0f, state.Instances[28]);
        }

        [Fact]
        public void ParticleUvTransformsEvolveAcrossLifetimeForBaseAndMultiplierTextures()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                BirthUvOffset = VfxCurve2.Const(new Vector2(0.1f, 0.2f)),
                BirthUvScrollRateCurve = VfxCurve2.Const(new Vector2(2f, 0f)),
                ParticleUvScrollRate = VfxCurve2.Const(new Vector2(4f, 0f)),
                BirthUvRotateRate = VfxCurveF.Const(90f),
                ParticleUvRotateRate = VfxCurveF.Const(30f),
                TextureMultBirthUvOffset = VfxCurve2.Const(new Vector2(0.1f, 0f)),
                TextureMultBirthUvScrollRate = VfxCurve2.Const(new Vector2(0.2f, 0f)),
                TextureMultParticleUvScroll = VfxCurve2.Const(new Vector2(0.4f, 0f)),
                TextureMultUvScale = VfxCurve2.Const(new Vector2(0.5f, 0.75f)),
                TextureMultUvRotation = VfxCurveF.Const(10f),
                TextureMultBirthUvRotateRate = VfxCurveF.Const(20f),
                TextureMultParticleUvRotate = VfxCurveF.Const(40f)
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "uv", "uv", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);
            simulator.Update(0.1f);
            simulator.Update(0.05f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(0.15f, state.Instances[11], 3);
            // LTK folds wrap-mode offsets by one cell at draw time, so exactly 1.0 becomes 0.0.
            Assert.Equal(0.0f, state.Instances[19], 3);
            Assert.Equal(0.2f, state.Instances[20], 3);
            Assert.Equal(18f * MathF.PI / 180f, state.Instances[23], 3);
            Assert.Equal(0.19f, state.Instances[29], 3);
            Assert.Equal(0.5f, state.Instances[31], 3);
            Assert.Equal(0.75f, state.Instances[32], 3);
            Assert.Equal(19f * MathF.PI / 180f, state.Instances[33], 3);
        }

        [Fact]
        public void IntegratedParticleUvRateAccumulatesTheAuthoredCurve()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = VfxCurveF.Const(2f),
                ParticleUvScrollRate = new VfxCurve2(
                    Vector2.Zero,
                    new[] { 0f, 1f },
                    new[] { Vector2.Zero, new Vector2(4f, 0f) })
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "integrated-uv", "integrated-uv", new[] { emitter }), Vector3.Zero);

            for (int step = 0; step < 5; step++) simulator.Update(0.1f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(0.2f, state.Instances[19], 3);
        }

        [Fact]
        public void BirthRandomIsDeterministicAndPackedForColorLookup()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var first = new VfxPlaybackRuntime(37);
            var second = new VfxPlaybackRuntime(37);
            var system = new VfxSystemDefinition(1, "birth-random", "birth-random", new[] { emitter });
            first.SetSystem(system, Vector3.Zero);
            second.SetSystem(system, Vector3.Zero);

            first.Update(0.02f);
            second.Update(0.02f);

            VfxPlaybackRuntime.EmitterState firstState = Assert.Single(first.Emitters);
            VfxPlaybackRuntime.EmitterState secondState = Assert.Single(second.Emitters);
            float firstRoll = firstState.Instances[34];
            float secondRoll = secondState.Instances[34];
            Assert.Equal(firstRoll, secondRoll);
            Assert.Equal(firstState.Particles[0].RangeRandom, firstRoll);
            Assert.InRange(firstRoll, 0f, 1f);
            Assert.Equal(45, VfxPlaybackRuntime.InstanceStride);
        }

        [Fact]
        public void PlaybackGraphResolvesAndCreatesChildSystem()
        {
            var childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            var parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TexturePath = string.Empty,
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(1f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var systems = new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child };
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                systems,
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);

            Assert.Equal(2, graph.Runtimes.Count);
            Assert.Same(childEmitter, graph.Runtimes[1].Emitters[0].Def);
            Assert.Equal(graph.Root.CurrentTime, graph.Runtimes[1].Emitters[0].RenderTime, precision: 5);
            Assert.True(graph.Runtimes[1].CurrentTime < graph.Runtimes[1].Emitters[0].RenderTime);

            graph.Update(0.02f);
            Assert.Equal(graph.Root.CurrentTime, graph.Runtimes[1].Emitters[0].RenderTime, precision: 5);
            Assert.True(graph.Runtimes[1].CurrentTime < graph.Runtimes[1].Emitters[0].RenderTime);
        }

        [Fact]
        public void ChildResolutionUsesTheParentSystemsDocumentResolverScope()
        {
            const uint effectKey = 77;
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            VfxSystemDefinition globalTarget = new(2, "global", "global", new[] { childEmitter });
            VfxSystemDefinition localTarget = new(3, "local", "local", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference(string.Empty, 0, effectKey) },
                    false,
                    VfxCurveF.Zero,
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            VfxSystemDefinition parent = new(
                1,
                "parent",
                "parent",
                new[] { parentEmitter },
                ResourceMap: new Dictionary<uint, uint> { [effectKey] = localTarget.PathHash });
            var systems = new Dictionary<uint, VfxSystemDefinition>
            {
                [parent.PathHash] = parent,
                [globalTarget.PathHash] = globalTarget,
                [localTarget.PathHash] = localTarget
            };
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                systems,
                new Dictionary<uint, uint> { [effectKey] = globalTarget.PathHash },
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);

            Assert.Equal(2, graph.Runtimes.Count);
            Assert.Same(localTarget, graph.Runtimes[1].Definition);
        }

        [Fact]
        public void UnmappedEffectKeyDoesNotFallbackToSystemHashOrName()
        {
            const uint effectKey = 77;
            const string namedPath = "Effects/NamedFallback";
            var byKey = new VfxSystemDefinition(effectKey, "by-key", "by-key", Array.Empty<VfxEmitterDefinition>());
            var byName = new VfxSystemDefinition(
                Fnv1a.HashLower(namedPath),
                "by-name",
                namedPath,
                Array.Empty<VfxEmitterDefinition>());
            var systems = new Dictionary<uint, VfxSystemDefinition>
            {
                [byKey.PathHash] = byKey,
                [byName.PathHash] = byName
            };

            Assert.Null(VfxPlaybackGraphRuntime.ResolveSystem(
                new VfxChildSystemReference(namedPath, 0, effectKey),
                systems,
                new Dictionary<uint, uint>()));
        }

        [Fact]
        public void ChildPoolCapacityMatchesLtkPeakDemandBuckets()
        {
            VfxEmitterDefinition regular = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = new VfxCurveF(1f, new[] { 0f, 1f }, new[] { 1f, 20f }),
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            var regularSystem = new VfxSystemDefinition(2, "regular", "regular", new[] { regular });
            Assert.Equal(64, VfxPlaybackGraphRuntime.ChildCapacityOf(regularSystem));

            VfxEmitterDefinition burst = regular with
            {
                IsSingleParticle = true,
                Rate = VfxCurveF.Const(70_000f)
            };
            var burstSystem = new VfxSystemDefinition(3, "burst", "burst", new[] { burst });
            Assert.Equal(4096, VfxPlaybackGraphRuntime.ChildCapacityOf(burstSystem));

            VfxEmitterDefinition tiny = burst with { Rate = VfxCurveF.Const(1f) };
            var tinySystem = new VfxSystemDefinition(4, "tiny", "tiny", new[] { tiny });
            Assert.Equal(16, VfxPlaybackGraphRuntime.ChildCapacityOf(tinySystem));
        }

        [Fact]
        public void ChildStartsEmptyWithoutApplyingRootBuildUpTime()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter }, BuildUpTime: 0.2f);
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) }, false,
                    VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = CreateGraph(parent, child);

            graph.Update(0.02f);

            Assert.Equal(2, graph.Runtimes.Count);
            VfxPlaybackRuntime childRuntime = graph.Runtimes[1];
            Assert.Equal(0f, childRuntime.CurrentTime);
            Assert.Equal(0f, Assert.Single(childRuntime.Emitters).EmitterAge);
            Assert.Equal(0, childRuntime.LiveParticleCount);
        }

        [Fact]
        public void ChildProbabilityConsumesLineageDrawBeforeChildStarts()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            var probability = new VfxCurveF(
                1f,
                null,
                null,
                new[] { new VfxProbTable(null, null, 0f, IsPresent: true) });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[]
                    {
                        new VfxChildSystemReference("child", 2, 0),
                        new VfxChildSystemReference("child", 2, 0)
                    },
                    false,
                    probability,
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            const int seed = 7;
            VfxPlaybackGraphRuntime graph = CreateGraph(parent, child, seed);

            graph.Update(0.02f);

            VfxPlaybackRuntime childRuntime = Assert.Single(graph.Runtimes.Skip(1));
            uint lineage = unchecked(1u * 0x9e3779b1u);
            int lineageSeed = unchecked(seed ^ (int)Fnv1a.HashLower("0") ^ (int)lineage);
            var expected = new VfxLtkRandom(unchecked((uint)lineageSeed));
            _ = expected.NextUnitFloat();
            Assert.Equal(expected.State, childRuntime.InitialRandomState);
        }

        [Fact]
        public void ChildLineageEnforcesLtkCapacityBudgetAndLiveChildLimit()
        {
            VfxPlaybackGraphRuntime CreateFor(VfxSystemDefinition child, int births)
            {
                VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
                {
                    Rate = VfxCurveF.Const(births),
                    IsSingleParticle = true,
                    ParticleLifetime = VfxCurveF.Const(10f),
                    ChildParticleSet = new VfxChildParticleSetDefinition(
                        new[] { new VfxChildSystemReference("child", 2, 0) }, false,
                        VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0)
                };
                var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
                return CreateGraph(parent, child);
            }

            VfxEmitterDefinition largeChildEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(5000f)
            };
            var largeChild = new VfxSystemDefinition(2, "child", "child", new[] { largeChildEmitter });
            VfxPlaybackGraphRuntime budgeted = CreateFor(largeChild, 100);
            budgeted.Update(0.02f);
            Assert.Equal(32, budgeted.LiveChildSystemCount);
            Assert.Equal(131_072, budgeted.HeldChildParticleCapacity);
            Assert.All(budgeted.Runtimes.Skip(1), runtime => Assert.Equal(4096, runtime.ParticleCapacity));

            VfxEmitterDefinition smallChildEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var smallChild = new VfxSystemDefinition(2, "child", "child", new[] { smallChildEmitter });
            VfxPlaybackGraphRuntime capped = CreateFor(smallChild, 600);
            capped.Update(0.02f);
            Assert.Equal(512, capped.LiveChildSystemCount);
            Assert.Equal(512 * 16, capped.HeldChildParticleCapacity);
        }

        [Fact]
        public void DeathSpawnedChildSoftStopsAfterItsSystemSpan()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Zero,
                EmitterLifetime = null
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = VfxCurveF.Const(0.05f),
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) }, true,
                    VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = CreateGraph(parent, child);

            graph.Update(0.1f);
            graph.Update(0.1f);
            Assert.Equal(2, graph.Runtimes.Count);
            float stopAfter = (float)VfxDurationCalculator.SystemSpan(child);
            Assert.Equal(6f, stopAfter);
            graph.Update(stopAfter + 0.2f);

            Assert.Single(graph.Runtimes);
            Assert.Equal(0, graph.HeldChildParticleCapacity);
        }

        [Fact]
        public void CarriedChildFollowsItsParentParticleAndStopsWhenItDies()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 10f,
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = VfxCurveF.Const(0.15f),
                BirthVelocity = VfxCurve3.Const(new Vector3(0f, 100f, 0f)),
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(0f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);
            VfxPlaybackRuntime childRuntime = graph.Runtimes[1];
            float bornAt = childRuntime.Emitters[0].BasePos.Y;

            graph.Update(0.08f);
            Assert.True(childRuntime.Emitters[0].BasePos.Y > bornAt + 1f);
            Assert.False(childRuntime.IsStopped);

            graph.Update(0.08f);
            Assert.True(childRuntime.IsStopped);
        }

        [Fact]
        public void GraphSnapshotRestoresCarriedChildLineageDeterministically()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 10f,
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = VfxCurveF.Const(0.15f),
                BirthVelocity = VfxCurve3.Const(new Vector3(0f, 100f, 0f)),
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(0f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.10f);
            Assert.Equal(2, graph.Runtimes.Count);
            int checkpointCapacity = graph.Runtimes[1].ParticleCapacity;
            int checkpointHeld = graph.HeldChildParticleCapacity;
            VfxPlaybackGraphRuntime.Snapshot checkpoint = graph.CaptureSnapshot();

            graph.Update(0.08f);
            VfxPlaybackRuntime expectedChild = graph.Runtimes[1];
            bool expectedStopped = expectedChild.IsStopped;
            Vector3 expectedPosition = expectedChild.Emitters[0].BasePos;
            int expectedLive = expectedChild.LiveParticleCount;

            graph.RestoreSnapshot(checkpoint);
            Assert.Equal(2, graph.Runtimes.Count);
            Assert.Equal(checkpointCapacity, graph.Runtimes[1].ParticleCapacity);
            Assert.Equal(checkpointHeld, graph.HeldChildParticleCapacity);
            Assert.False(graph.Runtimes[1].IsStopped);
            graph.Update(0.08f);

            VfxPlaybackRuntime restoredChild = graph.Runtimes[1];
            Assert.Equal(expectedStopped, restoredChild.IsStopped);
            Assert.Equal(expectedPosition, restoredChild.Emitters[0].BasePos);
            Assert.Equal(expectedLive, restoredChild.LiveParticleCount);
        }

        [Fact]
        public void GraphSnapshotDoesNotRestoreStalePreviewVisibility()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var definition = new VfxSystemDefinition(1, "visibility-checkpoint", "visibility-checkpoint", new[] { emitter });
            var graph = new VfxPlaybackGraphRuntime(
                definition,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = definition },
                new Dictionary<uint, uint>(),
                (system, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(system, transform);
                    return runtime;
                });

            graph.Update(0.02f);
            VfxPlaybackGraphRuntime.Snapshot checkpoint = graph.CaptureSnapshot();

            graph.SetAllEmittersVisible(false);
            graph.RestoreSnapshot(checkpoint);

            Assert.False(graph.Root.Emitters[0].IsVisible);
        }

        [Fact]
        public void BoneChildUsesTheLiveJointTransform()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(0f),
                    VfxCurve3.Const(Vector3.Zero),
                    0,
                    new[] { "R_Hand" })
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });
            graph.SetJointTransformProvider(name =>
                name == "R_Hand" ? Matrix4x4.CreateTranslation(10f, 0f, 0f) : null);

            graph.Update(0.02f);

            Assert.Equal(2, graph.Runtimes.Count);
            Assert.Equal(10f, graph.Runtimes[1].Emitters[0].BasePos.X, precision: 4);
        }

        [Fact]
        public void UnresolvedChildSlotKeepsItsProbabilityIndex()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });

            VfxPlaybackGraphRuntime Create(float selectedSlot)
            {
                VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
                {
                    ChildParticleSet = new VfxChildParticleSetDefinition(
                        new VfxChildSystemReference[] { null, new("child", 2, 0) },
                        false,
                        VfxCurveF.Const(selectedSlot),
                        VfxCurve3.Const(Vector3.Zero),
                        0)
                };
                var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
                return new VfxPlaybackGraphRuntime(
                    parent,
                    Matrix4x4.Identity,
                    7,
                    new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                    new Dictionary<uint, uint>(),
                    (definition, transform, seed) =>
                    {
                        var runtime = new VfxPlaybackRuntime(seed);
                        runtime.SetSystem(definition, transform);
                        return runtime;
                    });
            }

            VfxPlaybackGraphRuntime unresolved = Create(0f);
            unresolved.Update(0.02f);
            Assert.Single(unresolved.Runtimes);

            VfxPlaybackGraphRuntime resolved = Create(1f);
            resolved.Update(0.02f);
            Assert.Equal(2, resolved.Runtimes.Count);
        }

        [Fact]
        public void LivePoolsKeepHiddenRootAndChildParticlesInTheSimulationTally()
        {
            var childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            var parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = VfxCurveF.Const(1f),
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(0f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);
            Assert.Equal(2, graph.Runtimes.Count);
            graph.Update(0.02f);

            int before = 0;
            foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                before += runtime.LiveParticleCount;
            Assert.True(before >= 2);

            graph.SetAllEmittersVisible(false);

            Assert.All(graph.Runtimes, runtime =>
                Assert.All(runtime.Emitters, emitter => Assert.False(emitter.IsVisible)));
            int after = 0;
            foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                after += runtime.LiveParticleCount;
            Assert.Equal(before, after);
        }

        [Fact]
        public void PlaybackGraphGlobalVisibilityCoversCurrentAndFutureChildEmitters()
        {
            var childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            var parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TexturePath = string.Empty,
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(1f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.SetAllEmittersVisible(false);
            Assert.False(Assert.Single(graph.Root.Emitters).IsVisible);

            graph.Update(0.02f);

            Assert.Equal(2, graph.Runtimes.Count);
            Assert.All(graph.Runtimes, runtime => Assert.All(runtime.Emitters, emitter => Assert.False(emitter.IsVisible)));

            graph.SetAllEmittersVisible(true);
            Assert.All(graph.Runtimes, runtime => Assert.All(runtime.Emitters, emitter => Assert.True(emitter.IsVisible)));
        }

        [Fact]
        public void PlaybackGraphPreservesChildPlacementWhenRootTransformChanges()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterPosition = VfxCurve3.Const(new Vector3(2f, 0f, 0f)),
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false,
                    VfxCurveF.Const(1f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);
            VfxPlaybackRuntime childRuntime = graph.Runtimes[1];
            Assert.Equal(2f, childRuntime.Emitters[0].BasePos.X, precision: 4);

            graph.SetTransform(Matrix4x4.CreateTranslation(10f, 0f, 0f));

            Assert.Equal(12f, childRuntime.Emitters[0].BasePos.X, precision: 4);
        }

        [Fact]
        public void LocalOrientationControlsRigYawWithoutDroppingRigOrigin()
        {
            VfxEmitterDefinition local = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterPosition = VfxCurve3.Const(Vector3.UnitX),
                BirthVelocity = VfxCurve3.Const(Vector3.UnitX),
                IsLocalOrientation = true
            };
            VfxEmitterDefinition worldOriented = local with { IsLocalOrientation = false };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "orientation", "orientation", new[] { local, worldOriented }), Vector3.Zero);

            Matrix4x4 origin = Matrix4x4.CreateTranslation(10f, 20f, 30f);
            Matrix4x4 rig = Matrix4x4.CreateRotationY(MathF.PI / 2f) * origin;
            runtime.SetTransform(rig, origin);
            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle localParticle = Assert.Single(runtime.Emitters[0].Particles);
            VfxPlaybackRuntime.Particle worldParticle = Assert.Single(runtime.Emitters[1].Particles);
            Vector3 expectedLocalPosition = Vector3.Transform(Vector3.UnitX, rig);
            Vector3 expectedWorldPosition = Vector3.Transform(Vector3.UnitX, origin);
            Vector3 expectedLocalVelocity = Vector3.TransformNormal(Vector3.UnitX, rig);
            Vector3 expectedWorldVelocity = Vector3.UnitX;

            Assert.Equal(expectedLocalPosition.X, localParticle.Pos.X, precision: 5);
            Assert.Equal(expectedLocalPosition.Y, localParticle.Pos.Y, precision: 5);
            Assert.Equal(expectedLocalPosition.Z, localParticle.Pos.Z, precision: 5);
            Assert.Equal(expectedWorldPosition.X, worldParticle.Pos.X, precision: 5);
            Assert.Equal(expectedWorldPosition.Y, worldParticle.Pos.Y, precision: 5);
            Assert.Equal(expectedWorldPosition.Z, worldParticle.Pos.Z, precision: 5);
            Assert.Equal(expectedLocalVelocity.X, localParticle.Vel.X, precision: 5);
            Assert.Equal(expectedLocalVelocity.Z, localParticle.Vel.Z, precision: 5);
            Assert.Equal(expectedWorldVelocity, worldParticle.Vel);
        }

        [Fact]
        public void RuntimeConsumesTheCompleteVariableDriverStepLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(10f),
                AccelerationOverLife = VfxCurve3.Const(Vector3.UnitX)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "timing", "timing", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.01f); // birth step
            runtime.Update(0.2f);  // one variable driver step, not two 0.1 s substeps

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.Equal(0.21f, runtime.Emitters[0].EmitterAge, precision: 4);
            Assert.Equal(0.2f, particle.Vel.X, precision: 5);
            Assert.Equal(0.04f, particle.Pos.X, precision: 5);
        }

        [Fact]
        public void SessionPlaybackKeepsOneVariableStepAfterSpeedLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(10f),
                AccelerationOverLife = VfxCurve3.Const(Vector3.UnitX)
            };
            var definition = new VfxSystemDefinition(1, "timing-session", "timing-session", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "timing-session",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [1] = definition },
                ResourceMap = new Dictionary<uint, uint>(),
                SearchDirectory = Path.GetTempPath(),
                TotalDuration = 10d
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.Play();
            session.Update(0.01f); // birth step at 1x
            model.Speed = 2d;
            session.Update(0.1f);  // one 0.2 s driver step after speed

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(root.Emitters).Particles);
            Assert.Equal(0.21d, model.CurrentTime, precision: 5);
            Assert.Equal(0.2f, particle.Vel.X, precision: 5);
            Assert.Equal(0.04f, particle.Pos.X, precision: 5);
        }

        [Fact]
        public void LtkRandomZeroSeedAndCloneMatchDriverContracts()
        {
            var zero = new VfxLtkRandom(0);
            var seen = new HashSet<float>();
            for (int index = 0; index < 8; index++)
            {
                float value = zero.NextUnitFloat();
                Assert.InRange(value, 0f, MathF.BitDecrement(1f));
                Assert.True(seen.Add(value));
            }

            var original = new VfxLtkRandom(42);
            original.NextUnitFloat();
            original.NextUnitFloat();
            VfxLtkRandom clone = original.Clone();
            for (int index = 0; index < 8; index++)
                Assert.Equal(original.NextUnitFloat(), clone.NextUnitFloat());
        }

        [Fact]
        public void LtkRandomMatchesGoldenXorshift32Sequence()
        {
            var rng = new VfxLtkRandom(7);
            float[] expected =
            {
                0.0004405975341796875f,
                0.10952103137969971f,
                0.9038963913917542f,
                0.7147876024246216f,
                0.6646947264671326f,
                0.48851478099823f,
                0.060258567333221436f,
                0.1849934458732605f
            };

            foreach (float value in expected)
                Assert.Equal(value, rng.NextUnitFloat());
        }

        [Fact]
        public void ParticleSerialKeepsCountingAfterRetirement()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(10f),
                ParticleLifetime = VfxCurveF.Const(0.05f),
                EmitterLifetime = 1f
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "serial", "serial", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.1f);
            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            uint first = Assert.Single(state.Particles).Serial;

            runtime.Update(0.1f);
            uint second = Assert.Single(state.Particles).Serial;

            Assert.Equal(first + 1u, second);
        }

        [Fact]
        public void RuntimeResetRestoresTheInitialRandomSequence()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                NumFrames = 8,
                RandomStartFrame = true
            };
            var runtime = new VfxPlaybackRuntime(17);
            runtime.SetSystem(new VfxSystemDefinition(1, "deterministic", "deterministic", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);
            float firstFrame = runtime.Emitters[0].Instances[10];
            runtime.Reset();
            runtime.Update(0.02f);

            Assert.Equal(firstFrame, runtime.Emitters[0].Instances[10]);
        }

        [Fact]
        public void RigLoopReplayPreservesRandomStreamAndParticleSerial()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(20f),
                ParticleLifetime = VfxCurveF.Const(1f),
                NumFrames = 4,
                RandomStartFrame = true
            };
            var runtime = new VfxPlaybackRuntime(17);
            runtime.SetSystem(new VfxSystemDefinition(1, "loop-stream", "loop-stream", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.2f);

            VfxPlaybackRuntime.Snapshot before = runtime.CaptureSnapshot();
            Assert.True(before.ParticleSerial > 0);

            runtime.ReplayLoop();
            VfxPlaybackRuntime.Snapshot replayed = runtime.CaptureSnapshot();

            Assert.Equal(before.RandomState, replayed.RandomState);
            Assert.Equal(before.ParticleSerial, replayed.ParticleSerial);
            Assert.Equal(0f, replayed.CurrentTime);

            runtime.Reset();
            VfxPlaybackRuntime.Snapshot reset = runtime.CaptureSnapshot();
            Assert.Equal(reset.InitialRandomState, reset.RandomState);
            Assert.Equal(0u, reset.ParticleSerial);
        }

        [Fact]
        public void RuntimeSnapshotRestoreReplaysTheSameDeterministicState()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(20f),
                EmitterLifetime = 2f,
                ParticleLifetime = VfxCurveF.Const(2f),
                NumFrames = 8,
                RandomStartFrame = true,
                BirthVelocity = VfxCurve3.Const(new Vector3(2f, 3f, 4f))
            };
            var runtime = new VfxPlaybackRuntime(17);
            runtime.SetSystem(new VfxSystemDefinition(1, "snapshot", "snapshot", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.5f);
            VfxPlaybackRuntime.Snapshot checkpoint = runtime.CaptureSnapshot();
            runtime.Update(0.4f);
            float expectedTime = runtime.CurrentTime;
            int expectedLive = runtime.LiveParticleCount;
            int expectedCount = runtime.Emitters[0].InstanceCount;
            float[] expectedInstances = (float[])runtime.Emitters[0].Instances.Clone();

            runtime.RestoreSnapshot(checkpoint);
            runtime.Update(0.4f);

            Assert.Equal(expectedTime, runtime.CurrentTime);
            Assert.Equal(expectedLive, runtime.LiveParticleCount);
            Assert.Equal(expectedCount, runtime.Emitters[0].InstanceCount);
            Assert.Equal(expectedInstances, runtime.Emitters[0].Instances);
        }

        [Fact]
        public void RuntimeKillClearsParticlesAndResetRestoresPlayback()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "kill", "kill", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.02f);
            Assert.True(runtime.LiveParticleCount > 0);

            runtime.Kill();

            Assert.True(runtime.IsComplete);
            Assert.Equal(0, runtime.LiveParticleCount);
            Assert.Empty(runtime.Emitters[0].Particles);

            runtime.Reset();
            runtime.Update(0.02f);
            Assert.True(runtime.LiveParticleCount > 0);
        }

        [Fact]
        public void DurationIncludesParticleAndChildLifeButNotExternalLinger()
        {
            VfxEmitterDefinition childEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 0.1f,
                ParticleLifetime = VfxCurveF.Const(0.4f)
            };
            var child = new VfxSystemDefinition(2, "child", "child", new[] { childEmitter });
            VfxEmitterDefinition parentEmitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(0.5f),
                ParticleLinger = 10.75f,
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    true,
                    VfxCurveF.Const(1f),
                    VfxCurve3.Const(Vector3.Zero),
                    0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });

            double duration = VfxDurationCalculator.Calculate(
                parent,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>());

            Assert.Equal(1.9, duration, precision: 3);

            VfxEmitterDefinition externalLifetime = parentEmitter with
            {
                EmitterLifetime = null,
                ParticleLifetime = VfxCurveF.Const(0.3f),
                ParticleLinger = 10.3f,
                ChildParticleSet = null
            };
            Assert.Equal(
                0.3,
                VfxDurationCalculator.Calculate(new VfxSystemDefinition(3, "external", "external", new[] { externalLifetime })),
                precision: 3);
            Assert.Equal(
                0.3,
                VfxDurationCalculator.Calculate(
                    new VfxSystemDefinition(4, "loop", "loop", new[] { externalLifetime with { IsLoop = true } })),
                precision: 3);
        }

        [Fact]
        public void DurationHandlesSingleValueProbabilityTablesWithoutKeys()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                ParticleLifetime = new VfxCurveF(
                    2f,
                    null,
                    null,
                    new[] { new VfxProbTable(null, null, 0.5f, true) })
            };

            Assert.Equal(1d, VfxDurationCalculator.GetMaximumParticleLifetime(emitter), precision: 5);

            emitter = emitter with
            {
                ParticleLifetime = new VfxCurveF(
                    2f,
                    null,
                    null,
                    new[] { new VfxProbTable(null, new[] { 0.5f, 2f }, 1f, true) })
            };
            Assert.Equal(4d, VfxDurationCalculator.GetMaximumParticleLifetime(emitter), precision: 5);

            emitter = emitter with { ParticleLifetime = VfxCurveF.Const(2f) };
            Assert.Equal(2d, VfxDurationCalculator.GetMaximumParticleLifetime(emitter), precision: 5);
        }

        [Fact]
        public void SystemSpanUsesAuthoredEmissionAndParticleWindows()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TimeBeforeFirstEmission = 0.25f,
                EmitterLifetime = 0.5f,
                ParticleLifetime = VfxCurveF.Const(5f)
            };
            var system = new VfxSystemDefinition(1, "finite", "finite", new[] { emitter });

            Assert.Equal(5.75d, VfxDurationCalculator.SystemSpan(system), precision: 5);
        }

        [Fact]
        public void SystemSpanUsesFiveSecondWindowForEndlessEmitter()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = null,
                ParticleLifetime = VfxCurveF.Const(0.3f)
            };
            var system = new VfxSystemDefinition(1, "endless", "endless", new[] { emitter });

            Assert.Equal(5.3d, VfxDurationCalculator.SystemSpan(system), precision: 5);
        }

        [Fact]
        public void RenderStateNormalizesAlphaReference()
        {
            var state = new VfxEmitterRenderState(3, 128, 1, true, true, false, true);

            Assert.Equal(3, state.RenderPass);
            Assert.InRange(state.AlphaCutoff, 0.5019f, 0.5020f);
            Assert.True(state.ClampUvScroll);
            Assert.True(state.FlipU);
            Assert.True(state.DisableBackfaceCull);
        }

        [Fact]
        public void AttachedMeshLiveVisibilityMatchesLtkDrawAlwaysAndHiddenPrecedence()
        {
            uint body = Fnv1a.HashLower("Body");
            uint cape = Fnv1a.HashLower("Cape");
            uint hair = Fnv1a.HashLower("Hair");
            var draw = new[] { cape };
            var always = new[] { hair };
            var hidden = new HashSet<uint> { cape, hair };

            Assert.False(VfxOpenGlRenderer.ShouldDrawAttachedRange(body, narrowed: true, draw, always, hidden));
            Assert.False(VfxOpenGlRenderer.ShouldDrawAttachedRange(cape, narrowed: true, draw, always, hidden));
            Assert.True(VfxOpenGlRenderer.ShouldDrawAttachedRange(hair, narrowed: true, draw, always, hidden));

            Assert.True(VfxOpenGlRenderer.ShouldDrawAttachedRange(body, narrowed: false, draw, always, new HashSet<uint>()));
            Assert.False(VfxOpenGlRenderer.ShouldDrawAttachedRange(body, narrowed: false, draw, always, new HashSet<uint> { body }));
        }

        [Fact]
        public void AttachedMeshDrawBudgetMatchesLtkEightSlotsAcrossSources()
        {
            Assert.Equal(8, VfxOpenGlRenderer.ResolveAttachedMeshDrawCount(0, 20));
            Assert.Equal(3, VfxOpenGlRenderer.ResolveAttachedMeshDrawCount(5, 20));
            Assert.Equal(0, VfxOpenGlRenderer.ResolveAttachedMeshDrawCount(8, 20));
            Assert.Equal(0, VfxOpenGlRenderer.ResolveAttachedMeshDrawCount(12, 20));
        }

        [Fact]
        public void QuadDrawBudgetMatchesLtk4096AcrossLiveSources()
        {
            VfxEmitterDefinition quad = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsMeshPrimitive = false
            };

            int first = VfxOpenGlRenderer.ResolveEmitterDrawCount(quad, 0, 3000);
            int second = VfxOpenGlRenderer.ResolveEmitterDrawCount(quad, first, 3000);

            Assert.Equal(3000, first);
            Assert.Equal(1096, second);
            Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(quad, first + second, 1));
        }

        [Fact]
        public void MeshDrawBudgetMatchesLtk512AcrossLiveSources()
        {
            VfxEmitterDefinition mesh = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                IsMeshPrimitive = true
            };

            int first = VfxOpenGlRenderer.ResolveEmitterDrawCount(mesh, 0, 400);
            int second = VfxOpenGlRenderer.ResolveEmitterDrawCount(mesh, first, 400);

            Assert.Equal(400, first);
            Assert.Equal(112, second);
            Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(mesh, first + second, 1));
        }

        [Fact]
        public void BeamDrawBudgetMatchesLtk256AcrossLiveSources()
        {
            VfxEmitterDefinition beam = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.Beam,
                IsMeshPrimitive = false,
                Beam = new VfxBeamDefinition(
                    0,
                    0,
                    0,
                    VfxCurve3.Const(Vector3.One),
                    VfxCurve4.Const(Vector4.One),
                    false,
                    Vector3.Zero,
                    Vector3.Zero)
            };

            int first = VfxOpenGlRenderer.ResolveEmitterDrawCount(beam, 0, 200);
            int second = VfxOpenGlRenderer.ResolveEmitterDrawCount(beam, first, 200);

            Assert.Equal(200, first);
            Assert.Equal(56, second);
            Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(beam, first + second, 1));
        }

        [Fact]
        public void TrailPointBudgetMatchesLtk1024PerSource()
        {
            Assert.Equal(0, VfxTrailGeometry.ResolvePointCount(-4));
            Assert.Equal(1000, VfxTrailGeometry.ResolvePointCount(1000));
            Assert.Equal(1024, VfxTrailGeometry.ResolvePointCount(1024));
            Assert.Equal(1024, VfxTrailGeometry.ResolvePointCount(5000));
        }

        [Fact]
        public void AttachedMeshUsesItsEightSlotBudgetBeforeGenericMeshBudget()
        {
            VfxEmitterDefinition attached = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                IsMeshPrimitive = true
            };

            Assert.Equal(8, VfxOpenGlRenderer.ResolveEmitterDrawCount(attached, 0, 512));
            Assert.Equal(3, VfxOpenGlRenderer.ResolveEmitterDrawCount(attached, 5, 512));
            Assert.Equal(0, VfxOpenGlRenderer.ResolveEmitterDrawCount(attached, 8, 512));
        }

        [Fact]
        public void OnlyQuadDrawsUseBackToFrontParticleSorting()
        {
            VfxEmitterDefinition attached = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.AttachedMesh,
                IsMeshPrimitive = true,
                BlendMode = 1
            };
            VfxEmitterDefinition mesh = attached with { PrimitiveKind = VfxPrimitiveKind.Mesh };
            VfxEmitterDefinition quad = attached with
            {
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsMeshPrimitive = false
            };

            Assert.False(VfxOpenGlRenderer.ShouldSortInstances(attached, 4));
            Assert.False(VfxOpenGlRenderer.ShouldSortInstances(mesh, 4));
            Assert.True(VfxOpenGlRenderer.ShouldSortInstances(quad, 4));
        }

        [Fact]
        public void PolygonOffsetMatchesLtkZeroAndAttachedOverlaySemantics()
        {
            VfxEmitterDefinition mesh = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                DepthBiasFactors = Vector2.Zero
            };
            VfxEmitterDefinition attached = mesh with { PrimitiveKind = VfxPrimitiveKind.AttachedMesh };

            Assert.Null(VfxOpenGlRenderer.ResolvePolygonOffset(mesh));
            Assert.Equal(new Vector2(-1f, -1f), VfxOpenGlRenderer.ResolvePolygonOffset(attached));
            Assert.Equal(
                new Vector2(2f, -3f),
                VfxOpenGlRenderer.ResolvePolygonOffset(attached with { DepthBiasFactors = new Vector2(2f, -3f) }));
        }

        [Fact]
        public void UnnamedBaseTextureSamplesTransparentBlackWhileMissingNamedTextureStaysUntextured()
        {
            VfxEmitterDefinition unnamed = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TexturePath = null,
                TextureMultPath = "mult.tex"
            };
            VfxEmitterDefinition named = unnamed with { TexturePath = "base.tex" };

            Assert.True(VfxOpenGlRenderer.ShouldSampleBaseTexture(unnamed, 0));
            Assert.False(VfxOpenGlRenderer.ShouldSampleBaseTexture(named, 0));
            Assert.True(VfxOpenGlRenderer.ShouldSampleBaseTexture(named, 7));
        }

        [Fact]
        public void LockAlphaQuadRestoresColorRampWhenErosionIsCompiledOut()
        {
            var erosion = new VfxAlphaErosionDefinition(
                "erosion.tex",
                VfxCurveF.Zero,
                0f,
                0f,
                0,
                VfxCurve4.Const(Vector4.One));
            VfxEmitterDefinition fixedAlpha = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                UvMode = 2,
                AlphaErosion = erosion,
                TextureMultPath = null
            };

            Assert.True(VfxOpenGlRenderer.ShouldUseColorRamp(fixedAlpha, hasColorRampTexture: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseColorRamp(
                fixedAlpha with { TextureMultPath = "mult.tex" },
                hasColorRampTexture: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseColorRamp(
                fixedAlpha with { UvMode = 0 },
                hasColorRampTexture: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseColorRamp(fixedAlpha, hasColorRampTexture: false));
        }

        [Fact]
        public void FollowingTerrainAloneDoesNotUseGroundLayerProjection()
        {
            VfxEmitterDefinition regular = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            VfxEmitterDefinition terrain = regular with { IsFollowingTerrain = true };
            VfxEmitterDefinition ground = regular with { IsGroundLayer = true };
            VfxEmitterDefinition projection = regular with { PrimitiveKind = VfxPrimitiveKind.PlanarProjection };

            Assert.False(VfxOpenGlRenderer.ShouldProjectToGround(terrain));
            Assert.True(VfxOpenGlRenderer.ShouldProjectToGround(ground));
            Assert.False(VfxOpenGlRenderer.ShouldProjectToGround(projection));
            Assert.False(projection.IsVisual);
        }

        [Fact]
        public void DrawnQuadsUseAuthoredSoftParticleFadeButPlanarProjectionStaysUndrawn()
        {
            var soft = new VfxSoftParticleDefinition(0f, 80f, 0f, 0f);
            VfxEmitterDefinition regular = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                SoftParticle = soft
            };
            VfxEmitterDefinition ground = regular with { IsGroundLayer = true };
            VfxEmitterDefinition terrain = regular with { IsFollowingTerrain = true };
            VfxEmitterDefinition projection = regular with { PrimitiveKind = VfxPrimitiveKind.PlanarProjection };

            Assert.True(VfxOpenGlRenderer.ShouldUseSoftParticles(regular, hasSceneDepth: true));
            Assert.True(VfxOpenGlRenderer.ShouldUseSoftParticles(ground, hasSceneDepth: true));
            Assert.True(VfxOpenGlRenderer.ShouldUseSoftParticles(terrain, hasSceneDepth: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(projection, hasSceneDepth: true));
            Assert.False(projection.IsVisual);
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(regular, hasSceneDepth: false));

            VfxEmitterDefinition groundRotation = regular with
            {
                BirthRotation = VfxCurve3.Const(new Vector3(-90f, -90f, 0f))
            };
            Assert.False(VfxOpenGlRenderer.ShouldProjectToGround(groundRotation));
            Assert.True(VfxOpenGlRenderer.ShouldUseSoftParticles(groundRotation, hasSceneDepth: true));
        }

        [Fact]
        public void ParticleColorLookupAloneDoesNotCreateABillboard()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                TexturePath = null,
                TextureMultPath = null,
                ParticleColorTexturePath = "color-lookup.tex"
            };

            Assert.False(emitter.IsVisual);
        }

        [Fact]
        public void MeshParticlesAdvanceAuthoredRotationOnAllThreeAxes()
        {
            VfxEmitterDefinition emitter = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                BirthRotation = VfxCurve3.Const(new Vector3(10f, 20f, 30f)),
                BirthRotationalVelocity = VfxCurve3.Const(new Vector3(40f, 50f, 60f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "rotation", "rotation", new[] { emitter }), Vector3.Zero);

            for (int step = 0; step < 4; step++)
                runtime.Update(0.0625f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(1, state.InstanceCount);
            Assert.Equal(17.5f * MathF.PI / 180f, state.Instances[15], precision: 5);
            Assert.Equal(29.375f * MathF.PI / 180f, state.Instances[16], precision: 5);
            Assert.Equal(41.25f * MathF.PI / 180f, state.Instances[17], precision: 5);
        }

        [Fact]
        public void GroundProjectionRequiresTheAuthoredGroundLayerFlag()
        {
            VfxEmitterDefinition tiltedMesh = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = true,
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                BirthRotation = VfxCurve3.Const(new Vector3(-90f, 0f, 0f))
            };

            Assert.False(VfxOpenGlRenderer.ShouldProjectToGround(tiltedMesh));
            Assert.True(VfxOpenGlRenderer.ShouldProjectToGround(tiltedMesh with { IsGroundLayer = true }));
        }

        [Fact]
        public void BirthScaleRangeAndRotationRateUseTheAuthoredSecondEndpoint()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                BirthScale = VfxCurve3.Const(new Vector3(2f, 4f, 6f)),
                BirthScale1 = VfxCurve3.Const(new Vector3(4f, 8f, 10f)),
                RotationOverLife = VfxCurve3.Const(new Vector3(10f, 20f, 30f)),
                Rotation1 = VfxCurve3.Const(new Vector3(20f, 40f, 50f)),
                ParticleLifetime = VfxCurveF.Const(2f)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "ranges", "ranges", new[] { emitter }), Vector3.Zero);

            runtime.Update(1f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            VfxPlaybackRuntime.Particle particle = state.Particles[0];
            float range = particle.RangeRandom;
            float rotationRate = 10f + (20f - 10f) * range;
            Assert.Equal(2f + (4f - 2f) * range, state.Instances[3], precision: 5);
            Assert.Equal(rotationRate * 60f * particle.Age * MathF.PI / 180f, state.Instances[15], precision: 5);
        }

        [Fact]
        public void FlexShapeUsesTheLargestAttachedObjectExtentForScaleAndOffset()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                BirthScale = VfxCurve3.Const(new Vector3(10f, 20f, 30f)),
                EmitterPosition = VfxCurve3.Const(Vector3.Zero),
                SpawnShape = new VfxSpawnShape(
                    VfxSpawnShapeKind.Point,
                    VfxCurve3.Const(new Vector3(1f, 0f, 0f)),
                    Array.Empty<Vector3>(),
                    Array.Empty<VfxCurveF>()),
                FlexShape = new VfxFlexShapeDefinition(0.01f, 0.02f)
            };
            var runtime = new VfxPlaybackRuntime(7)
            {
                BoundObjectSize = new Vector3(10f, 25f, 5f)
            };
            runtime.SetSystem(new VfxSystemDefinition(1, "flex", "flex", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(new Vector3(12.5f, 25f, 37.5f), new Vector3(state.Instances[3], state.Instances[4], state.Instances[18]));
            Assert.Equal(1.5f, state.Instances[0]);
        }

        [Fact]
        public void PlaybackFinishesAtTimelineBoundaryWithoutImplicitLooping()
        {
            Assert.False(VfxRenderSession.ShouldFinishPlayback(
                hasFiniteDuration: true,
                currentTime: 0.83,
                totalDuration: 1.25,
                graphIsComplete: true));
            Assert.True(VfxRenderSession.ShouldFinishPlayback(
                hasFiniteDuration: true,
                currentTime: 1.25,
                totalDuration: 1.25,
                graphIsComplete: true));
            Assert.True(VfxRenderSession.ShouldFinishPlayback(
                hasFiniteDuration: false,
                currentTime: 0.83,
                totalDuration: 0,
                graphIsComplete: true));
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(float.NaN, 0f)]
        [InlineData(1f / 60f, 1f / 60f)]
        [InlineData(0.1f, 0.1f)]
        [InlineData(0.25f, 0.1f)]
        public void PlaybackFrameTimeMatchesTheRunClockCap(float deltaTime, float expected)
        {
            Assert.Equal(expected, VfxRenderSession.NormalizeFrameTime(deltaTime), precision: 6);
        }

        [Theory]
        [InlineData(0.01d, 0.05f)]
        [InlineData(0.05d, 0.05f)]
        [InlineData(1d, 1f)]
        [InlineData(2d, 2f)]
        [InlineData(5d, 2f)]
        [InlineData(double.NaN, 1f)]
        public void PlaybackSpeedUsesTheFullTransportRange(double speed, float expected)
        {
            Assert.Equal(expected, VfxRenderSession.NormalizePlaybackSpeed(speed), precision: 6);
        }

        [Theory]
        [InlineData(1d, -1, 2d, 59d / 60d)]
        [InlineData(1d, 1, 2d, 61d / 60d)]
        [InlineData(0d, -1, 2d, 0d)]
        [InlineData(2d, 1, 2d, 2d)]
        [InlineData(1d, 6, 2d, 1.1d)]
        [InlineData(1d, -6, 2d, 0.9d)]
        public void TransportStepMovesWholeSixtyHertzFrames(
            double currentTime,
            int frames,
            double span,
            double expected)
        {
            Assert.Equal(
                expected,
                VfxInspectorControl.PlaybackStepTarget(currentTime, frames, span),
                precision: 10);
        }

        [Theory]
        [InlineData(1d, -1, 0.5d)]
        [InlineData(1d, 1, 1.5d)]
        [InlineData(0.05d, -1, 0.05d)]
        [InlineData(2d, 1, 2d)]
        [InlineData(0.6d, -1, 0.5d)]
        [InlineData(0.6d, 1, 1d)]
        public void TransportSpeedDetentsMatchThePlaybackShortcuts(
            double speed,
            int direction,
            double expected)
        {
            Assert.Equal(expected, VfxInspectorControl.PlaybackSpeedDetent(speed, direction), precision: 6);
        }

        [Fact]
        public void StandaloneRunStartsFromTheDeterministicSeed()
        {
            Assert.Equal(1337, VfxInspectorControl.StandalonePlaybackSeed);
            Assert.Equal(1338, VfxInspectorControl.NextPlaybackSeed(1337));
        }

        [Theory]
        [InlineData(-1d, 5d, 0d)]
        [InlineData(2d, 5d, 2d)]
        [InlineData(7d, 5d, 5d)]
        [InlineData(double.NaN, 5d, 0d)]
        public void RememberedRunPlayheadStaysInsideTheCurrentSpan(
            double playhead,
            double span,
            double expected)
        {
            Assert.Equal(
                expected,
                VfxInspectorControl.RememberedPlayhead(playhead, span),
                precision: 6);
        }

        [Fact]
        public void PreviewLoopDoesNotRestartGraphUnlessExplicitlyEnabled()
        {
            Assert.False(VfxInspectorControl.ShouldRestartPreview(
                enabled: false,
                currentTime: 0.30,
                boundary: 0.30));
            Assert.False(VfxInspectorControl.ShouldRestartPreview(
                enabled: true,
                currentTime: 0.29,
                boundary: 0.30));
            Assert.True(VfxInspectorControl.ShouldRestartPreview(
                enabled: true,
                currentTime: 0.30,
                boundary: 0.30));
        }

        [Fact]
        public void PreviewLoopKeepsAnAuthoredRangeInsideTheCurrentSpan()
        {
            (double from, double to) = VfxInspectorControl.ClampPreviewLoop(0.5, 1.0, 2.0);
            Assert.Equal(0.5, from, 6);
            Assert.Equal(1.0, to, 6);
            Assert.Equal(0.5, VfxInspectorControl.ResolvePreviewLoopRestart(from, to, 2.0), 6);

            (from, to) = VfxInspectorControl.ClampPreviewLoop(1.0, 0.5, 2.0);
            Assert.Equal(0.5 - VfxInspectorControl.PreviewLoopMinimumSpan, from, 6);
            Assert.Equal(0.5, to, 6);

            (from, to) = VfxInspectorControl.ClampPreviewLoop(-1.0, 5.0, 2.0);
            Assert.Equal(0.0, from, 6);
            Assert.Equal(2.0, to, 6);
        }

        [Fact]
        public void TimelineUsesTheRealPlaybackDurationInsteadOfAnArtificialMinimum()
        {
            Assert.Equal(0.30, VfxInspectorControl.ResolveTimelineDuration(0.30), 6);
            Assert.Equal(10.0, VfxInspectorControl.ResolveTimelineDuration(double.PositiveInfinity), 6);
        }

        [Fact]
        public void TransparentInstanceDataIsCopiedBackToFrontForTheCurrentCamera()
        {
            const int stride = VfxPlaybackRuntime.InstanceStride;
            var source = new float[stride * 3];
            source[2] = -2f;
            source[stride + 2] = -10f;
            source[stride * 2 + 2] = -5f;
            source[3] = 2f;
            source[stride + 3] = 10f;
            source[stride * 2 + 3] = 5f;
            var destination = new float[source.Length];

            VfxRenderQueue.CopyInstancesBackToFront(
                source,
                3,
                stride,
                Matrix4x4.Identity,
                destination,
                new float[3],
                new int[3]);

            Assert.Equal(new[] { -10f, -5f, -2f }, new[]
            {
                destination[2],
                destination[stride + 2],
                destination[stride * 2 + 2]
            });
            Assert.Equal(new[] { 10f, 5f, 2f }, new[]
            {
                destination[3],
                destination[stride + 3],
                destination[stride * 2 + 3]
            });
        }

        [Fact]
        public void QuadSortingUsesSquaredEyeDistanceInsteadOfViewDepth()
        {
            const int stride = VfxPlaybackRuntime.InstanceStride;
            var source = new float[stride * 2];
            source[2] = -10f;
            source[3] = 10f;
            source[stride] = 100f;
            source[stride + 2] = -9f;
            source[stride + 3] = 9f;
            var destination = new float[source.Length];

            VfxRenderQueue.CopyInstancesBackToFront(
                source,
                2,
                stride,
                Matrix4x4.Identity,
                destination,
                new float[2],
                new int[2]);

            Assert.Equal(100f, destination[0]);
            Assert.Equal(9f, destination[3]);
            Assert.Equal(-10f, destination[stride + 2]);
            Assert.Equal(10f, destination[stride + 3]);
        }

        [Fact]
        public void QuadSourcesAreGatheredIntoOneSharedBudgetBeforeGlobalSorting()
        {
            const int stride = VfxPlaybackRuntime.InstanceStride;
            VfxEmitterDefinition definition = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsMeshPrimitive = false
            };
            var first = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition,
                Instances = new float[stride * 2],
                InstanceCount = 2
            };
            first.Instances[2] = -5f;
            first.Instances[3] = 1f;
            first.Instances[stride + 2] = -1f;
            first.Instances[stride + 3] = 2f;
            var second = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition,
                Instances = new float[stride * 2],
                InstanceCount = 2
            };
            second.Instances[2] = -10f;
            second.Instances[3] = 3f;
            second.Instances[stride + 2] = -20f;
            second.Instances[stride + 3] = 4f;

            var grouped = new float[stride * 3];
            var sorted = new float[stride * 3];
            int held = VfxRenderQueue.CopyQuadSourcesBackToFront(
                new[] { first, second },
                3,
                stride,
                Matrix4x4.Identity,
                grouped,
                sorted,
                new float[3],
                new int[3]);

            Assert.Equal(3, held);
            Assert.Equal(new[] { 3f, 1f, 2f }, new[]
            {
                sorted[3],
                sorted[stride + 3],
                sorted[stride * 2 + 3]
            });
        }

        [Fact]
        public void AbilityCompositionSchedulesResolvedSystemFromAuthoredClipFrames()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(100, "event", "event", new[] { emitter });
            var particleEvent = new VfxParticleEventDefinition(
                EventHash: 1,
                NameHash: 0,
                StartFrame: 30f,
                EndFrame: 60f,
                EffectKey: 10,
                EnemyEffectKey: 0,
                EffectName: string.Empty,
                IsLoop: false,
                IsKillEvent: false,
                IsDetachable: false,
                IsSelfOnly: false,
                FireIfAnimationEndsEarly: false,
                SkipIfPastEndFrame: false,
                ScalePlaySpeedWithAnimation: false,
                Scale: 1f,
                Attachments: Array.Empty<VfxParticleEventAttachment>());
            var composition = new VfxAbilityComposition(
                SequencePathHash: 5,
                SequenceClassHash: 6,
                TickDuration: 1f / 30f,
                StartFrame: 0f,
                EndFrame: 60f,
                Events: new[] { new VfxCompositionEvent(particleEvent, 100, system, false) })
            {
                ResolvedCount = 1
            };
            using var session = new VfxRenderSession();

            Assert.True(session.SetAbilityComposition(
                composition,
                new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                new Dictionary<uint, uint>(),
                Path.GetTempPath(),
                seed: 7));
            session.Play();
            for (int frame = 0; frame < 5; frame++) session.Update(0.1f);
            Assert.Equal(0, session.LiveParticleCount);

            for (int frame = 0; frame < 6; frame++) session.Update(0.1f);
            Assert.True(session.LiveParticleCount > 0);
        }

        [Fact]
        public void AnimationClipParticleCuesUseLtkDeterministicSeed()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(100, "event", "event", new[] { emitter });
            var particleEvent = new VfxParticleEventDefinition(
                EventHash: 1,
                NameHash: 0,
                StartFrame: 0f,
                EndFrame: 30f,
                EffectKey: 10,
                EnemyEffectKey: 0,
                EffectName: string.Empty,
                IsLoop: false,
                IsKillEvent: false,
                IsDetachable: false,
                IsSelfOnly: false,
                FireIfAnimationEndsEarly: false,
                SkipIfPastEndFrame: false,
                ScalePlaySpeedWithAnimation: false,
                Scale: 1f,
                Attachments: new[]
                {
                    new VfxParticleEventAttachment(1, 0),
                    new VfxParticleEventAttachment(2, 0)
                });
            var composition = new VfxAbilityComposition(
                SequencePathHash: 5,
                SequenceClassHash: 6,
                TickDuration: 1f / 30f,
                StartFrame: 0f,
                EndFrame: 30f,
                Events: new[] { new VfxCompositionEvent(particleEvent, 100, system, false) })
            {
                ResolvedCount = 1
            };
            using var session = new VfxRenderSession();

            Assert.True(session.SetAbilityComposition(
                composition,
                new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                new Dictionary<uint, uint>(),
                Path.GetTempPath(),
                seed: 123456));

            Assert.Equal(2, session.Graphs.Count);
            Assert.All(session.Graphs, graph =>
                Assert.Equal(VfxRenderSession.AnimationClipCueSeed, graph.InitialSeed));
        }

        [Fact]
        public void IdleEffectsUseLtkDeterministicSeed()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(100, "idle", "idle", new[] { emitter });
            var idle = new VfxIdleEffectDefinition(
                EffectKey: 100,
                EffectName: "idle",
                BoneName: "R_Hand",
                BoneNameHash: 0,
                TargetBoneName: string.Empty,
                TargetBoneNameHash: 0,
                Position: Vector3.Zero);
            using var session = new VfxRenderSession();

            Assert.True(session.SetAnimationSession(
                composition: null,
                idleEffects: new[] { idle },
                systems: new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                resourceMap: new Dictionary<uint, uint>(),
                searchDirectory: Path.GetTempPath(),
                seed: 987654,
                animationDuration: 1d));

            VfxPlaybackGraphRuntime graph = Assert.Single(session.Graphs);
            Assert.Equal(VfxRenderSession.IdleEffectSeed, graph.InitialSeed);
            Assert.Equal(1337, graph.InitialSeed);
        }

        [Fact]
        public void MissingIdleBoneKeepsScaledAuthoredPositionAtSkeletonOrigin()
        {
            Matrix4x4 result = VfxRenderSession.IdleFallbackTransform(
                Matrix4x4.Identity,
                new Vector3(1f, 2f, 3f),
                skinScale: 2f,
                Matrix4x4.CreateTranslation(10f, 20f, 30f));

            Assert.Equal(new Vector3(12f, 24f, 36f), result.Translation);
        }

        [Fact]
        public void BoneRigKeepsSourceBasisAndUsesTargetBoneOnlyAsAimLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(100, "bone-rig", "bone-rig", new[] { emitter });
            var idle = new VfxIdleEffectDefinition(
                EffectKey: 100,
                EffectName: "bone-rig",
                BoneName: "source",
                BoneNameHash: 1,
                TargetBoneName: "target",
                TargetBoneNameHash: 2,
                Position: Vector3.Zero);
            using var session = new VfxRenderSession();
            Assert.True(session.SetAnimationSession(
                composition: null,
                idleEffects: new[] { idle },
                systems: new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                resourceMap: new Dictionary<uint, uint>(),
                searchDirectory: Path.GetTempPath(),
                seed: 7,
                animationDuration: 1d));

            Matrix4x4 source =
                Matrix4x4.CreateRotationY(MathF.PI * 0.5f) *
                Matrix4x4.CreateTranslation(10f, 0f, 0f);
            Matrix4x4 target = Matrix4x4.CreateTranslation(10f, 0f, 20f);
            session.UpdateBoneTransforms((name, hash) =>
            {
                if (name == "source" || hash == 1) return source;
                if (hash == 2) return target;
                return null;
            });

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Vector3 expectedForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, source));
            Vector3 actualForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, root.WorldTransform));
            Assert.Equal(expectedForward.X, actualForward.X, precision: 5);
            Assert.Equal(expectedForward.Y, actualForward.Y, precision: 5);
            Assert.Equal(expectedForward.Z, actualForward.Z, precision: 5);
            Assert.Equal(new Vector3(10f, 0f, 20f), Assert.Single(root.Emitters).SystemTarget);
        }

        [Fact]
        public void BoneRigMissingSourceStillUsesTargetBoneLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(100, "bone-fallback", "bone-fallback", new[] { emitter });
            var idle = new VfxIdleEffectDefinition(
                EffectKey: 100,
                EffectName: "bone-fallback",
                BoneName: "missing",
                BoneNameHash: 1,
                TargetBoneName: "target",
                TargetBoneNameHash: 2,
                Position: new Vector3(3f, 4f, 5f));
            using var session = new VfxRenderSession();
            Assert.True(session.SetAnimationSession(
                composition: null,
                idleEffects: new[] { idle },
                systems: new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                resourceMap: new Dictionary<uint, uint>(),
                searchDirectory: Path.GetTempPath(),
                seed: 7,
                animationDuration: 1d));

            Matrix4x4 target = Matrix4x4.CreateTranslation(20f, 30f, 40f);
            session.UpdateBoneTransforms((name, hash) => hash == 2 ? target : null);

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.Equal(new Vector3(3f, 4f, 5f), root.WorldTransform.Translation);
            Assert.Equal(new Vector3(20f, 30f, 40f), Assert.Single(root.Emitters).SystemTarget);
        }

        [Fact]
        public void BoneAnchorCarriesOffsetThroughJointScaleBeforeSkinScale()
        {
            Matrix4x4 joint =
                Matrix4x4.CreateScale(2f) *
                Matrix4x4.CreateRotationY(MathF.PI * 0.5f) *
                Matrix4x4.CreateTranslation(1f, 2f, 3f);

            Matrix4x4 result = VfxRenderSession.PrepareBoneAnchorTransform(
                joint,
                new Vector3(1f, 0f, 0f),
                skinScale: 2f);

            Assert.Equal(2f, result.M41, precision: 5);
            Assert.Equal(4f, result.M42, precision: 5);
            Assert.Equal(2f, result.M43, precision: 5);
            Assert.Equal(1f, new Vector3(result.M11, result.M12, result.M13).Length(), precision: 5);
            Assert.Equal(1f, new Vector3(result.M21, result.M22, result.M23).Length(), precision: 5);
            Assert.Equal(1f, new Vector3(result.M31, result.M32, result.M33).Length(), precision: 5);
        }

        [Fact]
        public void SessionSeekRestoresNearestLtkCheckpointAndMatchesReplayFromZero()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(20f),
                EmitterLifetime = 2f,
                ParticleLifetime = VfxCurveF.Const(2f),
                NumFrames = 8,
                RandomStartFrame = true,
                BirthVelocity = VfxCurve3.Const(new Vector3(2f, 3f, 4f))
            };
            var definition = new VfxSystemDefinition(1, "checkpoint", "checkpoint", new[] { emitter });

            VfxSystemModel Model() => new()
            {
                Name = "checkpoint",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [1] = definition },
                ResourceMap = new Dictionary<uint, uint>(),
                SearchDirectory = Path.GetTempPath(),
                PlaybackSeed = 17,
                TotalDuration = 2d
            };

            using var straight = new VfxRenderSession();
            straight.SetSystem(Model());
            straight.Seek(0.62d);
            VfxPlaybackRuntime straightRoot = Assert.Single(straight.Graphs).Root;
            int expectedLive = straight.LiveParticleCount;
            float expectedTime = straightRoot.CurrentTime;
            float[] expectedInstances = (float[])straightRoot.Emitters[0].Instances.Clone();

            using var throughCheckpoint = new VfxRenderSession();
            throughCheckpoint.SetSystem(Model());
            throughCheckpoint.Seek(1.0d);
            Assert.True(throughCheckpoint.CheckpointCount >= 4);
            Assert.True(throughCheckpoint.CheckpointBytes > 0);

            throughCheckpoint.Seek(0.62d);

            Assert.Equal(0.5d, throughCheckpoint.LastSeekRestoreTime, precision: 6);
            Assert.Equal(expectedLive, throughCheckpoint.LiveParticleCount);
            VfxPlaybackRuntime restoredRoot = Assert.Single(throughCheckpoint.Graphs).Root;
            Assert.Equal(expectedTime, restoredRoot.CurrentTime);
            Assert.Equal(expectedInstances, restoredRoot.Emitters[0].Instances);
        }

        [Fact]
        public void AnimationClockSynchronizationKeepsExactSceneTimeAcrossBackwardSeek()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default);
            var definition = new VfxSystemDefinition(1, "animation-clock", "animation-clock", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "animation-clock",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [1] = definition },
                ResourceMap = new Dictionary<uint, uint>(),
                SearchDirectory = Path.GetTempPath(),
                TotalDuration = 2d
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.SynchronizeTo(0.62d);
            Assert.Equal(0.62d, model.CurrentTime, precision: 10);

            session.SynchronizeTo(0.31d);
            Assert.Equal(0.31d, model.CurrentTime, precision: 10);
        }

        [Fact]
        public void EmitterVisibilityOnlyAffectsViewportRendering()
        {
            VfxEmitterDefinition first = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with { Name = "first" };
            VfxEmitterDefinition second = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with { Name = "second" };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "visibility", "visibility", new[] { first, second }), Vector3.Zero);

            runtime.Update(0.02f);

            Assert.True(runtime.SetEmitterVisibility(0, false));
            Assert.False(runtime.Emitters[0].IsVisible);
            Assert.Equal(2, runtime.LiveParticleCount);
            Assert.Equal(1, runtime.Emitters[0].InstanceCount);
            Assert.Equal(1, runtime.Emitters[1].InstanceCount);
            Assert.False(runtime.SetEmitterVisibility(99, false));
        }

        [Fact]
        public void RenderOrderUsesLtkPassAndSourceOrderAndIgnoresImportance()
        {
            var first = CreateEmitter(Vector3.One, new VfxEmitterRenderState(2, 0, 0, false, false, false, false)) with
            {
                Name = "source-first",
                Importance = 9
            };
            var second = CreateEmitter(Vector3.One, new VfxEmitterRenderState(2, 0, 0, false, false, false, false)) with
            {
                Name = "source-second",
                Importance = 0
            };
            var laterPass = CreateEmitter(Vector3.One, new VfxEmitterRenderState(3, 0, 0, false, false, false, false)) with
            {
                Name = "later-pass",
                Importance = 0
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(
                new VfxSystemDefinition(1, "render-order", "render-order", new[] { laterPass, first, second }),
                Vector3.Zero);

            runtime.ApplyRenderOrder();

            Assert.Equal("source-first", runtime.Emitters[0].Def.Name);
            Assert.Equal("source-second", runtime.Emitters[1].Def.Name);
            Assert.Equal("later-pass", runtime.Emitters[2].Def.Name);
        }

        [Fact]
        public void GlobalRenderQueueUsesStableDefinitionRankAcrossLiveChildSources()
        {
            var graphKey = new object();
            VfxEmitterDefinition laterDefinition = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                Name = "later"
            };
            VfxEmitterDefinition earlierDefinition = laterDefinition with
            {
                Name = "earlier"
            };

            var laterRuntime = new VfxPlaybackRuntime(7);
            laterRuntime.SetSystem(new VfxSystemDefinition(1, "later", "later", new[] { laterDefinition }), Vector3.Zero);
            var earlierRuntime = new VfxPlaybackRuntime(8);
            earlierRuntime.SetSystem(new VfxSystemDefinition(2, "earlier", "earlier", new[] { earlierDefinition }), Vector3.Zero);

            VfxPlaybackRuntime.EmitterState later = Assert.Single(laterRuntime.Emitters);
            later.RenderGraphKey = graphKey;
            later.RenderPath = "0.1";
            later.RenderRank = 5;

            VfxPlaybackRuntime.EmitterState earlier = Assert.Single(earlierRuntime.Emitters);
            earlier.RenderGraphKey = graphKey;
            earlier.RenderPath = "0.0";
            earlier.RenderRank = 2;

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(
                new[] { laterRuntime.Emitters, earlierRuntime.Emitters },
                Matrix4x4.Identity);

            Assert.Equal("earlier", queue[0].Emitter.Def.Name);
            Assert.Equal("later", queue[1].Emitter.Def.Name);
        }

        [Fact]
        public void GlobalRenderQueuePreservesFirstLiveSourceForSharedEmitterUniforms()
        {
            var graphKey = new object();
            VfxEmitterDefinition definition = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 4f
            };
            var firstRuntime = new VfxPlaybackRuntime(7);
            firstRuntime.SetSystem(new VfxSystemDefinition(1, "shared", "shared", new[] { definition }), Vector3.Zero);
            var secondRuntime = new VfxPlaybackRuntime(8);
            secondRuntime.SetSystem(new VfxSystemDefinition(1, "shared", "shared", new[] { definition }), Vector3.Zero);

            VfxPlaybackRuntime.EmitterState first = Assert.Single(firstRuntime.Emitters);
            VfxPlaybackRuntime.EmitterState second = Assert.Single(secondRuntime.Emitters);
            foreach (VfxPlaybackRuntime.EmitterState source in new[] { first, second })
            {
                source.RenderGraphKey = graphKey;
                source.RenderPath = "0.0";
                source.RenderRank = 2;
            }

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(
                new[] { firstRuntime.Emitters, secondRuntime.Emitters },
                Matrix4x4.Identity);

            Assert.Same(first, queue[0].Emitter);
            Assert.Same(second, queue[1].Emitter);
            Assert.Equal(0.5f, VfxOpenGlRenderer.ResolveEmitterPhase(definition, 2f), precision: 5);
            Assert.Equal(0f, VfxOpenGlRenderer.ResolveEmitterPhase(definition with { EmitterLifetime = null }, 2f));
        }

        [Fact]
        public void GlobalRenderQueueDrawsGroundLayerBeforeDefaultLayer()
        {
            VfxEmitterDefinition defaultLayer = CreateEmitter(
                Vector3.One,
                new VfxEmitterRenderState(-100, 0, 0, false, false, false, false)) with
            {
                Name = "default",
                BlendMode = 3
            };
            VfxEmitterDefinition groundLayer = CreateEmitter(
                Vector3.One,
                new VfxEmitterRenderState(999, 0, 0, false, false, false, false)) with
            {
                Name = "ground",
                IsGroundLayer = true,
                BlendMode = 8
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(
                new VfxSystemDefinition(1, "ground-order", "ground-order", new[] { defaultLayer, groundLayer }),
                Vector3.Zero);

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(new[] { runtime.Emitters }, Matrix4x4.Identity);

            Assert.Equal("ground", queue[0].Emitter.Def.Name);
            Assert.Equal("default", queue[1].Emitter.Def.Name);
        }

        [Fact]
        public void GlobalRenderQueueMatchesLtkPassBlendMiscAndIgnoresRenderPhase()
        {
            VfxEmitterDefinition alpha = CreateEmitter(Vector3.One,
                new VfxEmitterRenderState(0, 0, 0, false, false, false, false, RenderPhase: 0)) with
            {
                Name = "alpha",
                BlendMode = 1,
                MiscRenderFlags = 0
            };
            VfxEmitterDefinition addMiscHigh = CreateEmitter(Vector3.One,
                new VfxEmitterRenderState(0, 0, 0, false, false, false, false, RenderPhase: 1)) with
            {
                Name = "add-misc-high",
                BlendMode = 0,
                MiscRenderFlags = 9,
                Importance = 0
            };
            VfxEmitterDefinition addMiscLow = CreateEmitter(Vector3.One,
                new VfxEmitterRenderState(0, 0, 0, false, false, false, false, RenderPhase: 99)) with
            {
                Name = "add-misc-low",
                BlendMode = 0,
                MiscRenderFlags = 1,
                Importance = 99
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(
                new VfxSystemDefinition(1, "draw-kind", "draw-kind", new[] { alpha, addMiscHigh, addMiscLow }),
                Vector3.Zero);

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(new[] { runtime.Emitters }, Matrix4x4.Identity);

            Assert.Equal("add-misc-low", queue[0].Emitter.Def.Name);
            Assert.Equal("add-misc-high", queue[1].Emitter.Def.Name);
            Assert.Equal("alpha", queue[2].Emitter.Def.Name);
        }

        [Fact]
        public void GlobalRenderQueueDoesNotUseEmitterPositionAsAnInternalDrawKindKey()
        {
            var authoredState = new VfxEmitterRenderState(
                0, 0, 0, false, false, false, false,
                SortEmittersByPosition: true);
            VfxEmitterDefinition near = CreateEmitter(Vector3.One, authoredState) with
            {
                Name = "near",
                EmitterPosition = VfxCurve3.Const(new Vector3(0f, 0f, -2f))
            };
            VfxEmitterDefinition far = CreateEmitter(Vector3.One, authoredState) with
            {
                Name = "far",
                EmitterPosition = VfxCurve3.Const(new Vector3(0f, 0f, -10f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "depth", "depth", new[] { near, far }), Vector3.Zero);

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(new[] { runtime.Emitters }, Matrix4x4.Identity);

            Assert.Equal("near", queue[0].Emitter.Def.Name);
            Assert.Equal("far", queue[1].Emitter.Def.Name);
        }

        [Fact]
        public void InspectorTimelineIgnoresAuthoredDisabledEmitters()
        {
            VfxEmitterDefinition playable = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = VfxCurveF.Const(0.5f),
                IsSingleParticle = false
            };
            VfxEmitterDefinition disabled = playable with
            {
                Name = "disabled",
                Disabled = true,
                EmitterLifetime = 20f,
                ParticleLifetime = VfxCurveF.Const(1f)
            };
            var system = new VfxSystemDefinition(
                1,
                "timeline",
                "timeline",
                new[] { playable, disabled });

            Assert.True(VfxInspectorControl.HasPlayableEmitters(system));
            Assert.Equal(1.5, VfxDurationCalculator.SystemSpan(system), precision: 3);
        }

        [Fact]
        public void InspectorRejectsSystemsWhoseEmittersAreAllAuthoredDisabled()
        {
            VfxEmitterDefinition disabled = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                Disabled = true
            };
            var system = new VfxSystemDefinition(
                1,
                "disabled",
                "disabled",
                new[] { disabled });

            Assert.False(VfxInspectorControl.HasPlayableEmitters(system));
        }

        [Fact]
        public void DelayedEmitterTrackStartsAtItsEmissionMarker()
        {
            var metrics = VfxInspectorControl.CalculateEmitterTrackMetrics(
                delay: 5,
                duration: 3,
                totalDuration: 13,
                availableWidth: 780);

            Assert.Equal(300, metrics.BarLeft, precision: 6);
            Assert.Equal(180, metrics.BarWidth, precision: 6);
            Assert.Equal(metrics.BarLeft, metrics.MarkerLeft + 4, precision: 6);
        }

        [Fact]
        public void AssetIndexPrefersAuthoredPathOverSameNamedFallback()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxIndex", Guid.NewGuid().ToString("N"));
            string skin0 = Path.Combine(root, "assets", "characters", "hero", "skins", "skin0", "particles");
            string skin1 = Path.Combine(root, "assets", "characters", "hero", "skins", "skin1", "particles");
            Directory.CreateDirectory(skin0);
            Directory.CreateDirectory(skin1);
            string expected = Path.Combine(skin1, "shared.tex");
            File.WriteAllBytes(Path.Combine(skin0, "shared.tex"), new byte[] { 0 });
            File.WriteAllBytes(expected, new byte[] { 1 });

            try
            {
                var index = VfxResourceIndex.Build(root);
                string resolved = index.Resolve(
                    "assets/characters/hero/skins/skin1/particles/shared.tex",
                    new[] { ".tex" });

                Assert.Equal(Path.GetFullPath(expected), resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AssetIndexResolvesExtractorTruncatedBinNameInTheAuthoredDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxTruncatedBin", Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "data", "characters", "hero");
            Directory.CreateDirectory(directory);
            string extractedStem = new string('a', 236);
            string extracted = Path.Combine(directory, extractedStem + ".bin");
            File.WriteAllBytes(extracted, new byte[] { 1 });

            try
            {
                var index = VfxResourceIndex.Build(root);
                string resolved = index.Resolve(
                    $"DATA/Characters/Hero/{extractedStem}_skins_skin28.bin",
                    new[] { ".bin" });

                Assert.Equal(Path.GetFullPath(extracted), resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AssetIndexIncludesVfxSkeletonAndAnimationResources()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxRigIndex", Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "assets", "characters", "hero", "particles");
            Directory.CreateDirectory(directory);
            string skeleton = Path.Combine(directory, "effect.skl");
            string animation = Path.Combine(directory, "effect.anm");
            File.WriteAllBytes(skeleton, new byte[] { 1 });
            File.WriteAllBytes(animation, new byte[] { 2 });

            try
            {
                var index = VfxResourceIndex.Build(root);

                Assert.Equal(
                    Path.GetFullPath(skeleton),
                    index.Resolve("ASSETS/Characters/Hero/Particles/effect.skl", new[] { ".skl" }));
                Assert.Equal(
                    Path.GetFullPath(animation),
                    index.Resolve("ASSETS/Characters/Hero/Particles/effect.anm", new[] { ".anm" }));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void EmitterSpaceForceFieldRidesTheEmitterOffset()
        {
            var noise = new VfxNoiseField(
                VfxCurveF.Zero,
                VfxCurveF.Const(5f),
                VfxCurve3.Const(Vector3.Zero),
                VfxCurveF.Const(10f),
                Vector3.One);
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                new[] { noise });
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                EmitterPosition = VfxCurve3.Const(new Vector3(100f, 0f, 0f)),
                IsEmitterSpace = true,
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "emitter-space", "emitter-space", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.True(particle.Vel.LengthSquared() > 0f);
        }

        [Fact]
        public void EmitterSpaceForceFieldIgnoresTranslationOverrideLikeLtk()
        {
            var noise = new VfxNoiseField(
                VfxCurveF.Zero,
                VfxCurveF.Const(5f),
                VfxCurve3.Const(Vector3.Zero),
                VfxCurveF.Const(10f),
                Vector3.One);
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                new[] { noise });
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsEmitterSpace = true,
                TranslationOverride = new Vector3(100f, 0f, 0f),
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "field-origin", "field-origin", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.Equal(Vector3.Zero, particle.Vel);
        }

        [Fact]
        public void LocalSpaceForceFieldIgnoresAuthoredSystemTransformLikeLtk()
        {
            var fields = new VfxFieldCollectionDefinition(
                new[] { new VfxAccelerationField(VfxCurve3.Const(Vector3.UnitX), LocalSpace: true) },
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                Array.Empty<VfxNoiseField>());
            Matrix4x4 authored = Matrix4x4.CreateRotationY(MathF.PI * 0.5f);
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsLocalOrientation = true,
                ParticleLifetime = VfxCurveF.Const(10f),
                Fields = fields
            };
            VfxSystemDefinition system = new(1, "field-axis", "field-axis", new[] { emitter }, Transform: authored);
            var graph = new VfxPlaybackGraphRuntime(
                system,
                Matrix4x4.Identity,
                7,
                new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });

            graph.Update(0.02f);
            graph.Update(0.1f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(graph.Root.Emitters).Particles);
            Assert.True(particle.Vel.X > 0f);
            Assert.InRange(MathF.Abs(particle.Vel.Z), 0f, 1e-5f);
        }

        [Fact]
        public void BirthOrbitalVelocityTurnsDrawnPositionAboutSystemOriginInRadiansLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TranslationOverride = new Vector3(2f, 0f, 0f),
                ParticleLifetime = VfxCurveF.Const(10f),
                BirthOrbitalVelocity = VfxCurve3.Const(new Vector3(0f, MathF.PI * 0.5f, 0f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "birth-orbit", "birth-orbit", new[] { emitter }), Vector3.Zero);
            runtime.Update(0.01f);
            runtime.Update(1f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            VfxPlaybackRuntime.Particle particle = Assert.Single(state.Particles);
            Assert.Equal(new Vector3(2f, 0f, 0f), particle.Pos);
            Assert.InRange(MathF.Abs(state.Instances[0]), 0f, 1e-5f);
            Assert.Equal(-2f, state.Instances[2], precision: 5);
        }

        [Fact]
        public void TinyNonZeroOrbitalFieldAxisStillActsLikeLtk()
        {
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                new[] { new VfxOrbitalField(VfxCurve3.Const(new Vector3(0f, 1e-8f, 0f)), LocalSpace: false) },
                Array.Empty<VfxNoiseField>());
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                TranslationOverride = new Vector3(10f, 0f, 0f),
                BirthVelocity = VfxCurve3.Const(Vector3.UnitX),
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "tiny-orbit", "tiny-orbit", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.InRange(MathF.Abs(particle.Vel.X), 0f, 1e-5f);
            Assert.True(MathF.Abs(particle.Vel.Z) > 0.99f);
        }

        [Fact]
        public void NoiseFieldFirstImpulseMatchesLtkHashDirection()
        {
            var noise = new VfxNoiseField(
                VfxCurveF.Zero,
                VfxCurveF.Const(1f),
                VfxCurve3.Const(Vector3.Zero),
                VfxCurveF.Const(1000f),
                Vector3.One);
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                new[] { noise });
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                ParticleLifetime = VfxCurveF.Const(10f),
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "noise-golden", "noise-golden", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            Vector3 velocity = Assert.Single(Assert.Single(runtime.Emitters).Particles).Vel;
            Assert.Equal(0.5753422f, velocity.X, precision: 6);
            Assert.Equal(0.004179446f, velocity.Y, precision: 6);
            Assert.Equal(0.81790215f, velocity.Z, precision: 6);
        }

        [Fact]
        public void NoiseFieldImpulseIsPreparedOnceAndReachesEveryParticleIncludingNewborns()
        {
            var noise = new VfxNoiseField(
                VfxCurveF.Const(10f),
                VfxCurveF.Const(5f),
                VfxCurve3.Const(Vector3.Zero),
                VfxCurveF.Const(1000f),
                Vector3.One);
            var fields = new VfxFieldCollectionDefinition(
                Array.Empty<VfxAccelerationField>(),
                Array.Empty<VfxAttractionField>(),
                Array.Empty<VfxDragField>(),
                Array.Empty<VfxOrbitalField>(),
                new[] { noise });
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(100f),
                EmitterLifetime = 0.03f,
                ParticleLifetime = VfxCurveF.Const(1f),
                Fields = fields
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "noise", "noise", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(2, state.Particles.Count);
            Vector3 firstBefore = state.Particles[0].Vel;
            Vector3 secondBefore = state.Particles[1].Vel;
            Assert.True(firstBefore.LengthSquared() > 0f);
            Assert.True(secondBefore.LengthSquared() > 0f);

            runtime.Update(0.10f);

            Assert.Equal(2, state.Particles.Count);
            Assert.NotEqual(firstBefore, state.Particles[0].Vel);
            Assert.NotEqual(secondBefore, state.Particles[1].Vel);
        }

        [Fact]
        public void ModernSphereIgnoresLegacyEmitOffsetAndTurnsVelocityWithItsRadialFrame()
        {
            var shape = new VfxSpawnShape(
                VfxSpawnShapeKind.Sphere,
                VfxCurve3.Const(new Vector3(1000f, 0f, 0f)),
                new[] { Vector3.UnitZ },
                new[] { VfxCurveF.Const(90f) },
                Radius: 50f,
                Flags: 0);
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                SpawnShape = shape,
                BirthVelocity = VfxCurve3.Const(Vector3.UnitX)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "sphere", "sphere", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Assert.InRange(particle.Pos.Length(), 49.999f, 50.001f);
            Vector3 radial = Vector3.Normalize(particle.Pos);
            Vector3 velocity = Vector3.Normalize(particle.Vel);
            Assert.True(Vector3.Dot(radial, velocity) > 0.9999f);
        }

        [Fact]
        public void LegacySpawnRotationKeepsOffsetAndVelocityInTheSameFrameWithoutMovingNewborn()
        {
            var shape = new VfxSpawnShape(
                VfxSpawnShapeKind.Legacy,
                VfxCurve3.Const(new Vector3(3f, 0f, 0f)),
                new[] { Vector3.UnitY },
                new[] { VfxCurveF.Const(90f) });
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                SpawnShape = shape,
                BirthVelocity = VfxCurve3.Const(new Vector3(5f, 0f, 0f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "radial", "radial", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.Particle particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
            Matrix4x4 rotation = Matrix4x4.CreateRotationY(MathF.PI / 2f);
            Vector3 expectedVelocity = Vector3.TransformNormal(new Vector3(5f, 0f, 0f), rotation);
            Vector3 expectedPosition = Vector3.Transform(new Vector3(3f, 0f, 0f), rotation);
            Assert.Equal(expectedVelocity.X, particle.Vel.X, precision: 5);
            Assert.Equal(expectedVelocity.Y, particle.Vel.Y, precision: 5);
            Assert.Equal(expectedVelocity.Z, particle.Vel.Z, precision: 5);
            Assert.Equal(expectedPosition.X, particle.Pos.X, precision: 5);
            Assert.Equal(expectedPosition.Y, particle.Pos.Y, precision: 5);
            Assert.Equal(expectedPosition.Z, particle.Pos.Z, precision: 5);
        }

        [Fact]
        public void DirectionOrientedArbitraryQuadKeepsItsAuthoredPlane()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.ArbitraryQuad,
                IsArbitraryQuad = true,
                IsDirectionOriented = true,
                BirthVelocity = VfxCurve3.Const(Vector3.UnitX),
                BirthRotation = VfxCurve3.Const(new Vector3(90f, 0f, 0f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "oriented", "oriented", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(MathF.PI / 2f, state.Instances[15], precision: 5);
            Assert.Equal(0f, state.Instances[16], precision: 5);
            Assert.Equal(0f, state.Instances[17], precision: 5);
        }

        [Fact]
        public void DirectionOrientedQuadStretchesItsLocalYAxis()
        {
            VfxEmitterDefinition emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsDirectionOriented = true,
                BirthVelocity = VfxCurve3.Const(new Vector3(10f, 0f, 0f)),
                DirectionVelocityScale = 2f,
                DirectionVelocityMinScale = 1f
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "quad", "quad", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);
            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(2f, state.Instances[3], precision: 4);
            Assert.Equal(60f, state.Instances[4], precision: 4);
            Assert.Equal(4f, state.Instances[18], precision: 4);
        }

        [Fact]
        public void DirectionStretchTreatsAnyNonZeroTravelAsOrientedLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                IsDirectionOriented = true,
                BirthVelocity = VfxCurve3.Const(new Vector3(0.00001f, 0f, 0f)),
                DirectionVelocityScale = 2f,
                DirectionVelocityMinScale = 8f
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "slow", "slow", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);
            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(24f, state.Instances[4], precision: 4);
        }

        [Fact]
        public void DirectionOrientedMeshStretchesItsLocalZAxis()
        {
            VfxEmitterDefinition emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = true,
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                BirthVelocity = VfxCurve3.Const(new Vector3(10f, 0f, 0f)),
                IsDirectionOriented = true,
                DirectionVelocityScale = 2f,
                DirectionVelocityMinScale = 1f
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "mesh", "mesh", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);
            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(2f, state.Instances[3], precision: 4);
            Assert.Equal(3f, state.Instances[4], precision: 4);
            Assert.Equal(80f, state.Instances[18], precision: 4);
        }

        [Fact]
        public void LegacySimpleDirectionOrientedEmitterDoesNotVelocityStretch()
        {
            VfxEmitterDefinition emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                BirthVelocity = VfxCurve3.Const(new Vector3(10f, 0f, 0f)),
                IsDirectionOriented = true,
                DirectionVelocityScale = 2f,
                DirectionVelocityMinScale = 1f,
                AuthoredFeatures = new VfxEmitterAuthoredFeatures(HasLegacySimple: true)
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "legacy", "legacy", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.02f);
            runtime.Update(0.02f);

            VfxPlaybackRuntime.EmitterState state = Assert.Single(runtime.Emitters);
            Assert.Equal(2f, state.Instances[3], precision: 4);
            Assert.Equal(3f, state.Instances[4], precision: 4);
            Assert.Equal(4f, state.Instances[18], precision: 4);
        }

        [Fact]
        public void PrimitiveEnumKeepsTheShaderInterfaceContract()
        {
            Assert.Equal(5, (int)VfxPrimitiveKind.CameraTrail);
            Assert.Equal(6, (int)VfxPrimitiveKind.ArbitraryTrail);
            Assert.Equal(7, (int)VfxPrimitiveKind.Ray);
            Assert.Equal(8, (int)VfxPrimitiveKind.Beam);
            Assert.Equal(9, (int)VfxPrimitiveKind.PlanarProjection);
        }

        [Fact]
        public void BeamDistanceColorUsesRawSystemDistanceBeforeLocalOffsets()
        {
            var distanceColor = new VfxCurve4(
                Vector4.One,
                new[] { 0f, 2f, 20f },
                new[]
                {
                    new Vector4(0f, 0f, 0f, 0f),
                    new Vector4(0.25f, 0.25f, 0.25f, 0.25f),
                    Vector4.One
                });
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.Beam,
                Beam = new VfxBeamDefinition(
                    0,
                    0,
                    0,
                    VfxCurve3.Const(Vector3.One),
                    distanceColor,
                    true,
                    new Vector3(10f, 0f, 0f),
                    Vector3.Zero)
            };
            var state = BeamState(emitter, Vector3.Zero, new Vector3(0f, 0f, 2f), Vector3.Zero);

            var geometry = new VfxBeamGeometry();
            Assert.Equal(6, geometry.Build(state, new Vector3(0f, 5f, 5f)));

            // LTK samples mAnimatedColorWithDistance at |systemTarget-systemOrigin| = 2,
            // not at the longer segment after source/target local offsets are applied.
            Assert.Equal(0.25f, geometry.Vertices[7], precision: 5);
            Assert.Equal(0.25f, geometry.Vertices[8], precision: 5);
            Assert.Equal(0.25f, geometry.Vertices[9], precision: 5);
            Assert.Equal(0.25f, geometry.Vertices[10], precision: 5);
        }

        [Fact]
        public void ArbitraryBeamKeepsDegenerateWidthWhenParticleLocalCancelsItsSide()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.Beam,
                Beam = new VfxBeamDefinition(
                    1,
                    0,
                    0,
                    VfxCurve3.Const(Vector3.One),
                    VfxCurve4.Const(Vector4.One),
                    false,
                    Vector3.Zero,
                    Vector3.Zero)
            };
            // For a +X beam the arbitrary side begins at +Z. A local position of -Z
            // cancels it exactly; Riot leaves the resulting width vector at zero.
            var state = BeamState(emitter, Vector3.Zero, new Vector3(2f, 0f, 0f), new Vector3(0f, 0f, -1f));

            var geometry = new VfxBeamGeometry();
            Assert.Equal(6, geometry.Build(state, new Vector3(0f, 3f, 3f)));
            int stride = VfxBeamGeometry.VertexStride;
            Assert.Equal(geometry.Vertices[2], geometry.Vertices[stride + 2]);
            Assert.Equal(geometry.Vertices[3], geometry.Vertices[stride + 3]);
            Assert.Equal(geometry.Vertices[4], geometry.Vertices[stride + 4]);
        }

        private static VfxPlaybackRuntime.EmitterState BeamState(
            VfxEmitterDefinition emitter,
            Vector3 origin,
            Vector3 target,
            Vector3 particlePosition)
        {
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = emitter,
                SystemOrigin = origin,
                SystemTarget = target,
                Instances = new float[VfxPlaybackRuntime.InstanceStride],
                InstanceCount = 1,
                PlacementRight = Vector3.UnitX,
                PlacementUp = Vector3.UnitY,
                PlacementForward = Vector3.UnitZ
            };
            state.Instances[0] = particlePosition.X;
            state.Instances[1] = particlePosition.Y;
            state.Instances[2] = particlePosition.Z;
            state.Instances[3] = 2f;
            state.Instances[5] = state.Instances[6] = state.Instances[7] = state.Instances[8] = 1f;
            state.Instances[21] = state.Instances[22] = 1f;
            state.Instances[31] = state.Instances[32] = 1f;
            state.Particles.Add(new VfxPlaybackRuntime.Particle { TrailTiling = Vector3.One });
            return state;
        }
        [Fact]
        public void TrailCutoffLimitsAccumulatedLengthAndPreservesJointAttributes()
        {
            var state = TrailState(cutoff: 2.5f);
            var geometry = new VfxTrailGeometry();
            Assert.Equal(12, geometry.Build(state, Vector3.UnitZ));
            int stride = VfxTrailGeometry.VertexStride;
            // The shared corner is byte-identical across adjacent segments, including colour and UV.
            for (int component = 0; component < stride; component++)
                Assert.Equal(geometry.Vertices[component], geometry.Vertices[8 * stride + component]);
            Assert.NotEqual(geometry.Vertices[7], geometry.Vertices[2 * stride + 7]);
        }

        [Fact]
        public void WakeUvStaysAttachedToBirthDistanceWhenOlderParticlesDisappear()
        {
            var state = TrailState(cutoff: 0f);
            var geometry = new VfxTrailGeometry();
            Assert.Equal(18, geometry.Build(state, Vector3.UnitZ));
            float newestU = geometry.Vertices[2 * VfxTrailGeometry.VertexStride];
            state.Particles.RemoveAt(0);
            Array.Copy(state.Instances, VfxPlaybackRuntime.InstanceStride, state.Instances, 0, 3 * VfxPlaybackRuntime.InstanceStride);
            state.InstanceCount--;
            Assert.Equal(12, geometry.Build(state, Vector3.UnitZ));
            Assert.Equal(newestU, geometry.Vertices[2 * VfxTrailGeometry.VertexStride]);
        }

        [Fact]
        public void RibbonEmitterScrollUsesSourceRenderTimeInsteadOfEmitterAge()
        {
            VfxEmitterDefinition definition = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                Trail = new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 1, 30, 0),
                EmitterUvScrollRate = new Vector2(0.25f, 0f),
                TextureMultEmitterUvScrollRate = new Vector2(0.125f, 0f)
            };
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition,
                Instances = new float[VfxPlaybackRuntime.InstanceStride],
                InstanceCount = 1,
                RenderTime = 3f,
                Age = 1f
            };
            state.Instances[21] = state.Instances[22] = 1f;
            state.Instances[31] = state.Instances[32] = 1f;
            var vertex = new float[VfxTrailGeometry.VertexStride];

            VfxRibbonVertexSemantics.Pack(state, 0, vertex, 0, 0f, 0f, transpose: false);

            Assert.Equal(0.75f, vertex[0], precision: 5);
            Assert.Equal(0.375f, vertex[17], precision: 5);
        }

        [Fact]
        public void ArbitraryTrailUsesParticleSideInsteadOfFacingCamera()
        {
            var original = TrailState(0f);
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = original.Def with { PrimitiveKind = VfxPrimitiveKind.ArbitraryTrail },
                Instances = original.Instances, InstanceCount = original.InstanceCount,
                PlacementRight = Vector3.UnitX, PlacementUp = Vector3.UnitY, PlacementForward = Vector3.UnitZ
            };
            state.Particles.AddRange(original.Particles);
            var geometry = new VfxTrailGeometry();
            Assert.Equal(18, geometry.Build(state, Vector3.UnitZ));
            float[] first = (float[])geometry.Vertices.Clone();
            geometry.Build(state, Vector3.UnitY);
            Assert.Equal(first, geometry.Vertices);
        }

        private static VfxPlaybackRuntime.EmitterState TrailState(float cutoff)
        {
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
                {
                    IsMeshPrimitive = false, PrimitiveKind = VfxPrimitiveKind.CameraTrail,
                    Trail = new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 1, 30, cutoff)
                },
                Instances = new float[4 * VfxPlaybackRuntime.InstanceStride], InstanceCount = 4,
                PlacementRight = Vector3.UnitX, PlacementUp = Vector3.UnitY, PlacementForward = Vector3.UnitZ
            };
            for (int i = 0; i < 4; i++)
            {
                int at = i * VfxPlaybackRuntime.InstanceStride;
                state.Instances[at] = i;
                state.Instances[at + 3] = i + 1;
                state.Instances[at + 5] = i * 0.25f;
                state.Instances[at + 21] = state.Instances[at + 22] = 1f;
                state.Particles.Add(new VfxPlaybackRuntime.Particle { TrailTiling = Vector3.One, TrailBirthDistance = i });
            }
            return state;
        }

        private static VfxPlaybackGraphRuntime CreateGraph(
            VfxSystemDefinition parent,
            VfxSystemDefinition child,
            int seed = 7)
        {
            return new VfxPlaybackGraphRuntime(
                parent,
                Matrix4x4.Identity,
                seed,
                new Dictionary<uint, VfxSystemDefinition> { [parent.PathHash] = parent, [child.PathHash] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });
        }

        private static VfxEmitterDefinition CreateEmitter(Vector3 birthScale, VfxEmitterRenderState renderState)
            => new(
                Name: "mesh",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(birthScale),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "mesh.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: true,
                RenderState: renderState);
    }
}

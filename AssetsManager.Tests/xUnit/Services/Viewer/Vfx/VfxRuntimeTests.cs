using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxRuntimeTests
    {
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
        public void UniformBillboardBirthScalePreservesAuthoredAxes()
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
            Assert.Equal(230f, state.Instances[4]);
        }

        [Fact]
        public void ArbitraryQuadScalePreservesAuthoredAxes()
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
            Assert.Equal(800f, state.Instances[4]);
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
        public void MeshInterleavingPreservesPositionUvAndVertexColor()
        {
            float[] interleaved = VfxMeshResourceCache.BuildInterleaved(
                new[] { 1f, 2f, 3f },
                new[] { 0.25f, 0.75f },
                new[] { 0.1f, 0.2f, 0.3f, 0.4f });

            Assert.Equal(VfxMeshResourceCache.VertexStride, interleaved.Length);
            Assert.Equal(
                new[] { 1f, 2f, 3f, 0.25f, 0.75f, 0.1f, 0.2f, 0.3f, 0.4f },
                interleaved);
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
            Assert.Equal(6, state.InstanceCount);
            int last = (state.InstanceCount - 1) * VfxPlaybackRuntime.InstanceStride;
            Assert.InRange(state.Instances[last + 8], 0.499f, 0.501f);
        }

        [Fact]
        public void CameraTrailUsesBirthScaleForWidthAndSegmentDistanceForLength()
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
            Assert.Equal(1, state.InstanceCount);
            Assert.Equal(2f, state.Instances[3], 3);
            Assert.Equal(1f, state.Instances[4], 3);
            Assert.Equal(-0.5f, state.Instances[21], 3);
            Assert.Equal(-0.25f, state.Instances[19], 3);
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

            Assert.Equal(0, Assert.Single(simulator.Emitters).InstanceCount);
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
            Assert.Equal(0, state.InstanceCount);
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

            Assert.Equal(0, Assert.Single(simulator.Emitters).InstanceCount);
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
            Assert.Equal(0.25f, state.Instances[11], 3);
            Assert.Equal(1.6f, state.Instances[19], 3);
            Assert.Equal(0.2f, state.Instances[20], 3);
            Assert.Equal(MathF.PI / 6f, state.Instances[23], 3);
            Assert.Equal(0.25f, state.Instances[29], 3);
            Assert.Equal(0.5f, state.Instances[31], 3);
            Assert.Equal(0.75f, state.Instances[32], 3);
            Assert.Equal(25f * MathF.PI / 180f, state.Instances[33], 3);
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
            Assert.Equal(0.25f, state.Instances[19], 3);
        }

        [Fact]
        public void MultiplierAtlasFrameIsDeterministicAndPackedPerParticle()
        {
            var emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                TextureMultPath = "mult.tex",
                TextureMultTexDiv = new Vector2(4f, 2f),
                TextureMultRandomStartFrame = true
            };
            var first = new VfxPlaybackRuntime(37);
            var second = new VfxPlaybackRuntime(37);
            var system = new VfxSystemDefinition(1, "mult-atlas", "mult-atlas", new[] { emitter });
            first.SetSystem(system, Vector3.Zero);
            second.SetSystem(system, Vector3.Zero);

            first.Update(0.02f);
            second.Update(0.02f);

            float firstFrame = Assert.Single(first.Emitters).Instances[34];
            float secondFrame = Assert.Single(second.Emitters).Instances[34];
            Assert.Equal(firstFrame, secondFrame);
            Assert.InRange(firstFrame, 0f, 7f);
            Assert.Equal(36, VfxPlaybackRuntime.InstanceStride);
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
        public void RuntimeConsumesTheCompleteDeltaUsingStableSubsteps()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "timing", "timing", new[] { emitter }), Vector3.Zero);

            runtime.Update(0.25f);

            Assert.Equal(0.25f, runtime.Emitters[0].EmitterAge, precision: 4);
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
            Assert.True(double.IsInfinity(VfxDurationCalculator.Calculate(
                new VfxSystemDefinition(4, "loop", "loop", new[] { externalLifetime with { IsLoop = true } }))));
        }

        [Fact]
        public void PreviewDurationMatchesDeterministicParticleCompletion()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                EmitterLifetime = 1f,
                ParticleLifetime = new VfxCurveF(
                    1f,
                    null,
                    null,
                    new[] { new VfxProbTable(new[] { 0f, 1f }, new[] { 0.25f, 0.25f }) })
            };
            var system = new VfxSystemDefinition(1, "preview", "preview", new[] { emitter });

            double duration = VfxDurationCalculator.CalculatePreview(system, seed: 17);

            Assert.Equal(0.25d, duration, precision: 5);
        }

        [Fact]
        public void PreviewDurationIncludesInitialContinuousEmission()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                IsSingleParticle = false,
                Rate = VfxCurveF.Const(2f),
                EmitterLifetime = 0.5f,
                ParticleLifetime = VfxCurveF.Const(5f)
            };
            var system = new VfxSystemDefinition(1, "finite", "finite", new[] { emitter });

            double duration = VfxDurationCalculator.CalculatePreview(system, seed: 17);

            Assert.Equal(5d, duration, precision: 5);
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
        public void GroundLayersDoNotFadeThemselvesAgainstSceneDepth()
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
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(ground, hasSceneDepth: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(terrain, hasSceneDepth: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(projection, hasSceneDepth: true));
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(regular, hasSceneDepth: false));

            VfxEmitterDefinition groundRotation = regular with
            {
                BirthRotation = VfxCurve3.Const(new Vector3(-90f, -90f, 0f))
            };
            Assert.True(VfxOpenGlRenderer.IsGroundLikeBirthRotation(groundRotation.BirthRotation));
            Assert.False(VfxOpenGlRenderer.ShouldUseSoftParticles(groundRotation, hasSceneDepth: true));
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
            Assert.Equal(20f * MathF.PI / 180f, state.Instances[15], precision: 5);
            Assert.Equal(32.5f * MathF.PI / 180f, state.Instances[16], precision: 5);
            Assert.Equal(45f * MathF.PI / 180f, state.Instances[17], precision: 5);
        }

        [Fact]
        public void GroundLikeBirthRotationDetectionRecognizesAuthoredTilt()
        {
            VfxEmitterDefinition groundMesh = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = true,
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                IsGroundLayer = true,
                BirthRotation = VfxCurve3.Const(new Vector3(90f, 20f, 30f))
            };

            Assert.True(VfxOpenGlRenderer.IsGroundLikeBirthRotation(groundMesh.BirthRotation));
        }

        [Fact]
        public void NegativeNinetyBirthRotationIsRecognizedAsGroundLike()
        {
            VfxEmitterDefinition mesh = CreateEmitter(
                Vector3.One,
                VfxEmitterRenderState.Default) with
            {
                IsMeshPrimitive = true,
                PrimitiveKind = VfxPrimitiveKind.Mesh,
                BirthRotation = VfxCurve3.Const(new Vector3(-90f, 0f, 0f))
            };

            Assert.True(VfxOpenGlRenderer.IsGroundLikeBirthRotation(mesh.BirthRotation));
        }

        [Fact]
        public void BirthScaleAndRotationRangesUseTheAuthoredSecondEndpoint()
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
            float range = state.Particles[0].RangeRandom;
            Assert.Equal(2f + (4f - 2f) * range, state.Instances[3], precision: 5);
            Assert.Equal((10f + (20f - 10f) * range) * MathF.PI / 180f, state.Instances[15], precision: 5);
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

        [Fact]
        public void PreviewLoopDoesNotRestartGraphUnlessExplicitlyEnabled()
        {
            Assert.False(VfxInspectorWindow.ShouldRestartPreview(
                enabled: false,
                currentTime: 0.30,
                boundary: 0.30));
            Assert.False(VfxInspectorWindow.ShouldRestartPreview(
                enabled: true,
                currentTime: 0.29,
                boundary: 0.30));
            Assert.True(VfxInspectorWindow.ShouldRestartPreview(
                enabled: true,
                currentTime: 0.30,
                boundary: 0.30));
        }

        [Fact]
        public void TimelineUsesTheRealPlaybackDurationInsteadOfAnArtificialMinimum()
        {
            Assert.Equal(0.30, VfxInspectorWindow.ResolveTimelineDuration(0.30), 6);
            Assert.Equal(10.0, VfxInspectorWindow.ResolveTimelineDuration(double.PositiveInfinity), 6);
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
                IsKillEvent: true,
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
            session.Update(0.5f);
            Assert.Equal(0, session.LiveParticleCount);

            session.Update(0.6f);
            Assert.True(session.LiveParticleCount > 0);
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
        public void RenderOrderUsesPassThenImportanceThenSourceOrder()
        {
            var first = CreateEmitter(Vector3.One, new VfxEmitterRenderState(2, 0, 0, false, false, false, false)) with
            {
                Name = "importance-low",
                Importance = 1
            };
            var second = CreateEmitter(Vector3.One, new VfxEmitterRenderState(2, 0, 0, false, false, false, false)) with
            {
                Name = "importance-high",
                Importance = 5
            };
            var third = CreateEmitter(Vector3.One, new VfxEmitterRenderState(3, 0, 0, false, false, false, false)) with
            {
                Name = "later-pass",
                Importance = 0
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(
                new VfxSystemDefinition(1, "render-order", "render-order", new[] { third, second, first }),
                Vector3.Zero);

            runtime.ApplyRenderOrder();

            Assert.Equal("importance-low", runtime.Emitters[0].Def.Name);
            Assert.Equal("importance-high", runtime.Emitters[1].Def.Name);
            Assert.Equal("later-pass", runtime.Emitters[2].Def.Name);
        }

        [Fact]
        public void GlobalRenderQueueUsesAuthoredPhaseBeforePass()
        {
            VfxEmitterDefinition latePhase = CreateEmitter(Vector3.One,
                new VfxEmitterRenderState(0, 0, 0, false, false, false, false, RenderPhase: 8)) with { Name = "late-phase" };
            VfxEmitterDefinition earlyPhase = CreateEmitter(Vector3.One,
                new VfxEmitterRenderState(99, 0, 0, false, false, false, false, RenderPhase: 6)) with { Name = "early-phase" };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "phases", "phases", new[] { latePhase, earlyPhase }), Vector3.Zero);

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(new[] { runtime.Emitters }, Matrix4x4.Identity);

            Assert.Equal("early-phase", queue[0].Emitter.Def.Name);
            Assert.Equal("late-phase", queue[1].Emitter.Def.Name);
        }

        [Fact]
        public void GlobalRenderQueueSortsOptedInEmittersBackToFront()
        {
            var sortedState = new VfxEmitterRenderState(
                0, 0, 0, false, false, false, false,
                SortEmittersByPosition: true);
            VfxEmitterDefinition near = CreateEmitter(Vector3.One, sortedState) with
            {
                Name = "near",
                EmitterPosition = VfxCurve3.Const(new Vector3(0f, 0f, -2f))
            };
            VfxEmitterDefinition far = CreateEmitter(Vector3.One, sortedState) with
            {
                Name = "far",
                EmitterPosition = VfxCurve3.Const(new Vector3(0f, 0f, -10f))
            };
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "depth", "depth", new[] { near, far }), Vector3.Zero);

            IReadOnlyList<VfxRenderQueueEntry> queue = VfxRenderQueue.Build(new[] { runtime.Emitters }, Matrix4x4.Identity);

            Assert.Equal("far", queue[0].Emitter.Def.Name);
            Assert.Equal("near", queue[1].Emitter.Def.Name);
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

            Assert.True(VfxInspectorWindow.HasPlayableEmitters(system));
            Assert.Equal(1.5, VfxInspectorWindow.CalculatePlaybackDuration(system), precision: 3);
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

            Assert.False(VfxInspectorWindow.HasPlayableEmitters(system));
        }

        [Fact]
        public void DelayedEmitterTrackStartsAtItsEmissionMarker()
        {
            var metrics = VfxInspectorWindow.CalculateEmitterTrackMetrics(
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
        public void LegacySpawnRotationKeepsOffsetAndMotionInTheSameFrame()
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
            Vector3 expectedPosition = Vector3.Transform(new Vector3(3f, 0f, 0f), rotation) + expectedVelocity * 0.02f;
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
        public void PrimitiveEnumKeepsTheShaderInterfaceContract()
        {
            Assert.Equal(5, (int)VfxPrimitiveKind.CameraTrail);
            Assert.Equal(6, (int)VfxPrimitiveKind.ArbitraryTrail);
            Assert.Equal(7, (int)VfxPrimitiveKind.Ray);
            Assert.Equal(8, (int)VfxPrimitiveKind.Beam);
            Assert.Equal(9, (int)VfxPrimitiveKind.PlanarProjection);
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
                BlendMode: 2,
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

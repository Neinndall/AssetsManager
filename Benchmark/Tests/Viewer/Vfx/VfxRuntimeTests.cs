using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
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
        public void UniformScaleCurveUsesItsAuthoredScalarForEveryMeshAxis()
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

            Matrix4x4 transform = GlVfxRenderer.CreateSceneWorldTransform(model);
            Vector3 origin = Vector3.Transform(Vector3.Zero, transform);
            Vector3 scaledAxis = Vector3.TransformNormal(Vector3.UnitX, transform);

            Assert.Equal(new Vector3(10f, 20f, 30f), origin);
            Assert.Equal(2f, scaledAxis.Length(), 3);
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
        public void UniformMeshBirthScaleUsesTheAuthoredScalarComponent()
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
        public void UniformBillboardBirthScaleUsesOneAuthoredDimension()
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
            Assert.Equal(5, state.InstanceCount);
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
                    new[] { Vector3.Zero, new Vector3(10f, 0f, 0f) })
            };
            var simulator = new VfxPlaybackRuntime(7);
            simulator.SetSystem(new VfxSystemDefinition(1, "trail", "trail", new[] { emitter }), Vector3.Zero);

            simulator.Update(0.1f);
            simulator.Update(0.1f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(1, state.InstanceCount);
            Assert.Equal(2f, state.Instances[3], 3);
            Assert.Equal(1f, state.Instances[4], 3);
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
            Assert.Equal(35, VfxPlaybackRuntime.InstanceStride);
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
        public void SamiraSpotlightRaysUseAuthoredForwardAxisTowardSceneCenter()
        {
            Vector3 leftAxis = VfxOpenGlRenderer.GetAuthoredPrimitiveLongitudinalAxis(
                VfxPrimitiveKind.Ray,
                new Vector3(50f, 90f, 0f) * (MathF.PI / 180f));
            Vector3 rightAxis = VfxOpenGlRenderer.GetAuthoredPrimitiveLongitudinalAxis(
                VfxPrimitiveKind.Ray,
                new Vector3(130f, 90f, 0f) * (MathF.PI / 180f));
            Vector3 leftToCenter = Vector3.Normalize(new Vector3(520f, -800f, 0f));
            Vector3 rightToCenter = Vector3.Normalize(new Vector3(-520f, -800f, 0f));

            Assert.True(Vector3.Dot(leftAxis, leftToCenter) > 0.98f);
            Assert.True(Vector3.Dot(rightAxis, rightToCenter) > 0.98f);
            Assert.True(leftAxis.Y < 0f);
            Assert.True(rightAxis.Y < 0f);
        }

        [Fact]
        public void RayWidthFacesCameraWithoutChangingAuthoredDirection()
        {
            Vector3 direction = Vector3.Normalize(new Vector3(0.64f, -0.77f, 0f));
            Vector3 cameraForward = Vector3.Normalize(new Vector3(0f, -0.61f, -0.79f));
            Vector3 cameraUp = Vector3.Normalize(new Vector3(0f, 0.79f, -0.61f));

            Vector3 side = VfxOpenGlRenderer.GetCameraFacingPrimitiveSide(
                direction,
                cameraForward,
                cameraUp);

            Assert.Equal(1f, side.Length(), precision: 5);
            Assert.Equal(0f, Vector3.Dot(side, direction), precision: 5);
            Assert.Equal(0f, Vector3.Dot(side, cameraForward), precision: 5);
        }

        [Fact]
        public void GroundPlaneRotatesAuthoredDownwardFacingBasisIntoViewportDirection()
        {
            Vector3 right = Vector3.UnitZ;
            Vector3 authoredForward = -Vector3.UnitX;

            (Vector3 correctedRight, Vector3 correctedForward) =
                VfxOpenGlRenderer.GetGroundPlaneAxes(right, authoredForward);

            Assert.Equal(Vector3.UnitX, correctedRight);
            Assert.Equal(-Vector3.UnitZ, correctedForward);
            Assert.True(Vector3.Dot(Vector3.Cross(correctedRight, correctedForward), Vector3.UnitY) > 0f);
        }

        [Fact]
        public void DownwardSpotlightRayReachesTheGroundContact()
        {
            Vector3 origin = new(-520f, 800f, 0f);
            Vector3 direction = VfxOpenGlRenderer.GetAuthoredPrimitiveLongitudinalAxis(
                VfxPrimitiveKind.Ray,
                new Vector3(50f, 90f, 0f) * (MathF.PI / 180f));

            float length = VfxOpenGlRenderer.GetGroundContactRayLength(origin, direction, 900f);
            Vector3 endpoint = origin + direction * length;

            Assert.True(length > 900f);
            Assert.True(endpoint.Y < 0f);
            Assert.True(endpoint.Y > -2f);
        }

        [Fact]
        public void RaysChooseTheGroundImpactAlignedWithTheirAuthoredCrossing()
        {
            VfxEmitterDefinition leftRay = CreateEmitter(new Vector3(600f, 900f, 0f), VfxEmitterRenderState.Default) with
            {
                Name = "left-ray",
                IsMeshPrimitive = false,
                PrimitiveKind = VfxPrimitiveKind.Ray,
                EmitterPosition = VfxCurve3.Const(new Vector3(-520f, 800f, 0f)),
                BirthRotation = VfxCurve3.Const(new Vector3(50f, 90f, 0f))
            };
            VfxEmitterDefinition leftImpact = CreateEmitter(Vector3.One, VfxEmitterRenderState.Default) with
            {
                Name = "left-impact",
                EmitterPosition = VfxCurve3.Const(new Vector3(-125f, 2f, 0f))
            };
            VfxEmitterDefinition rightRay = leftRay with
            {
                Name = "right-ray",
                EmitterPosition = VfxCurve3.Const(new Vector3(520f, 800f, 0f)),
                BirthRotation = VfxCurve3.Const(new Vector3(130f, 90f, 0f))
            };
            VfxEmitterDefinition rightImpact = leftImpact with
            {
                Name = "right-impact",
                EmitterPosition = VfxCurve3.Const(new Vector3(125f, 2f, 0f))
            };
            var emitters = new List<VfxEmitterDefinition> { leftRay, leftImpact, rightRay, rightImpact };

            VfxGraphParser.LinkRayImpacts(emitters);

            Assert.Equal(new Vector3(645f, -798f, 0f), emitters[0].RayTargetOffset);
            Assert.Equal(new Vector3(-645f, -798f, 0f), emitters[2].RayTargetOffset);
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
        public void FinitePlaybackWaitsForTimelineBoundaryAfterParticlesExpire()
        {
            Assert.False(GlVfxRenderer.ShouldRestartPlayback(
                hasFiniteDuration: true,
                currentTime: 0.83,
                totalDuration: 1.25,
                graphIsComplete: true));
            Assert.True(GlVfxRenderer.ShouldRestartPlayback(
                hasFiniteDuration: true,
                currentTime: 1.25,
                totalDuration: 1.25,
                graphIsComplete: true));
            Assert.True(GlVfxRenderer.ShouldRestartPlayback(
                hasFiniteDuration: false,
                currentTime: 0.83,
                totalDuration: 0,
                graphIsComplete: true));
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

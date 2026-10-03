using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxRigMotionTests
    {

        [Fact]
        public void MissileRig_TravelsAlongFlightPathAndStopsOnArrival()
        {
            // Range = 1200, Speed = 1600, FlightTime = 0.75s, FlightHeight = 100
            var atStart = VfxRigMotion.Evaluate(VfxRigPreset.Missile, 0.0, 5.0);
            Assert.Equal(-600f, atStart.Origin.X, tolerance: 0.1f);
            Assert.Equal(100f, atStart.Origin.Y, tolerance: 0.1f);
            Assert.Equal(0f, atStart.Origin.Z, tolerance: 0.1f);
            Assert.False(atStart.IsStopped);

            var atMid = VfxRigMotion.Evaluate(VfxRigPreset.Missile, 0.375, 5.0);
            Assert.Equal(0f, atMid.Origin.X, tolerance: 0.1f);
            Assert.Equal(100f, atMid.Origin.Y, tolerance: 0.1f);
            Assert.False(atMid.IsStopped);

            var atEnd = VfxRigMotion.Evaluate(VfxRigPreset.Missile, 0.75, 5.0);
            Assert.Equal(600f, atEnd.Origin.X, tolerance: 0.1f);
            Assert.Equal(100f, atEnd.Origin.Y, tolerance: 0.1f);
            Assert.True(atEnd.IsStopped);

            // LTK's missile object convention carries local +Y along flight and local +Z up and local +X left.
            Vector3 localY = Vector3.TransformNormal(Vector3.UnitY, atStart.Transform);
            Assert.Equal(1f, localY.X, tolerance: 1e-4f);
            Assert.Equal(0f, localY.Y, tolerance: 1e-4f);
            Assert.Equal(0f, localY.Z, tolerance: 1e-4f);

            Vector3 localZ = Vector3.TransformNormal(Vector3.UnitZ, atStart.Transform);
            Assert.Equal(0f, localZ.X, tolerance: 1e-4f);
            Assert.Equal(1f, localZ.Y, tolerance: 1e-4f);
            Assert.Equal(0f, localZ.Z, tolerance: 1e-4f);
        }

        [Theory]
        [InlineData(1f, 0f)]
        [InlineData(0f, 1f)]
        [InlineData(3f, 4f)]
        [InlineData(0f, 0f)]
        public void SpellPathKeepsGroundOffsetsBelowAndLocalTravelForward(float x, float z)
        {
            Vector3 from = new(10f, 100f, 20f);
            Vector3 to = from + new Vector3(x, 5f, z);
            Matrix4x4 placement = VfxRigMotion.PathTransform(from, to, 0d, 0d, 1d);
            Vector3 ground = Vector3.Transform(new Vector3(0f, 0f, -100f), placement);
            Assert.Equal(new Vector3(10f, 0f, 20f), ground);
            Vector3 expected = x == 0f && z == 0f
                ? Vector3.UnitZ : Vector3.Normalize(new Vector3(x, 0f, z));
            Assert.True(Vector3.Distance(expected, Vector3.TransformNormal(Vector3.UnitY, placement)) < 1e-5f);
            Assert.True(Vector3.Distance(Vector3.Cross(expected, Vector3.UnitY),
                Vector3.TransformNormal(Vector3.UnitX, placement)) < 1e-5f);
        }

        [Fact]
        public void MissileSessionSpawnsBelowFlightAndCarriesUpwardBirthVelocity()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                TexturePath = null,
                Rate = VfxCurveF.Const(20f),
                EmitterPosition = VfxCurve3.Const(new Vector3(0f, 0f, -100f)),
                BirthVelocity = VfxCurve3.Const(new Vector3(0f, 50f, 100f))
            };
            var definition = new VfxSystemDefinition(9, "missile_ground", "missile_ground", new[] { emitter });
            using var session = new VfxRenderSession();
            session.SetSystem(new VfxSystemModel
            {
                Name = definition.Name,
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [9] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            });
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Missile);
            session.Play();
            session.Update(0.1f);
            VfxPlaybackRuntime.EmitterState state = Assert.Single(Assert.Single(session.Graphs).Root.Emitters);
            Assert.Equal(0f, state.BasePos.Y, precision: 4);
            Assert.NotEmpty(state.Particles);
            Assert.All(state.Particles, particle =>
            {
                Assert.Equal(50f, particle.Vel.X, precision: 4);
                Assert.Equal(100f, particle.Vel.Y, precision: 4);
                Assert.Equal(0f, particle.Vel.Z, precision: 4);
            });
        }

        [Fact]
        public void TrailRig_OrbitsAtAuthoredRadiusAndYawFacesTangent()
        {
            var at0 = VfxRigMotion.Evaluate(VfxRigPreset.Trail, 0.0, 5.0);
            // cos(0) * 300 = 300, sin(0) = 0
            Assert.Equal(300f, at0.Origin.X, tolerance: 0.1f);
            Assert.Equal(100f, at0.Origin.Y, tolerance: 0.1f);
            Assert.Equal(0f, at0.Origin.Z, tolerance: 0.1f);

            var atQuarter = VfxRigMotion.Evaluate(VfxRigPreset.Trail, 0.75, 5.0);
            // turn = PI/2 -> cos = 0, sin = 1 -> z = 300
            Assert.Equal(0f, atQuarter.Origin.X, tolerance: 0.5f);
            Assert.Equal(100f, atQuarter.Origin.Y, tolerance: 0.1f);
            Assert.Equal(300f, atQuarter.Origin.Z, tolerance: 0.5f);
        }

        [Fact]
        public void TrailRig_KeepsOnceLifecyclePhaseAcrossOrbitWrap()
        {
            var afterOneOrbit = VfxRigMotion.Evaluate(
                VfxRigPreset.Trail,
                VfxRigMotion.OrbitPeriod + 0.25,
                5.0,
                new Vector3(
                    MathF.Cos((VfxRigMotion.OrbitPeriod - 0.01f) / VfxRigMotion.OrbitPeriod * MathF.PI * 2f) * VfxRigMotion.OrbitRadius,
                    VfxRigMotion.FlightHeight,
                    MathF.Sin((VfxRigMotion.OrbitPeriod - 0.01f) / VfxRigMotion.OrbitPeriod * MathF.PI * 2f) * VfxRigMotion.OrbitRadius));

            Assert.Equal(VfxRigMotion.OrbitPeriod + 0.25f, afterOneOrbit.Phase, precision: 4);
            Assert.NotEqual(Vector3.Zero, afterOneOrbit.Moved);
        }

        [Fact]
        public void BurstAndStillRigs_StandOnGround()
        {
            var burst = VfxRigMotion.Evaluate(VfxRigPreset.Burst, 1.2, 5.0);
            Assert.Equal(0f, burst.Origin.X);
            Assert.Equal(0f, burst.Origin.Y);
            Assert.Equal(0f, burst.Origin.Z);
            Assert.False(burst.IsStopped);

            var still = VfxRigMotion.Evaluate(VfxRigPreset.Still, 0.0, 5.0);
            Assert.Equal(0f, still.Origin.X);
            Assert.Equal(0f, still.Origin.Y);
            Assert.Equal(0f, still.Origin.Z);
        }

        [Fact]
        public void MissileRunLengthUsesAuthoredLingerInsteadOfFixedTail()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                EmitterLifetime = 0.5f,
                ParticleLifetime = VfxCurveF.Const(1f),
                EmitterLinger = 2f,
                ParticleLinger = 1.5f
            };
            var system = new VfxSystemDefinition(1, "missile", "missile", new[] { emitter });

            // Flight is 0.75s. At landing the emitter still owes 1.25s of wait,
            // followed by 1.5s of particle linger: 0.75 + 1.25 + 1.5 = 3.5s.
            Assert.Equal(3.5d, VfxRigMotion.RunLength(VfxRigPreset.Missile, system), precision: 4);
        }

        [Fact]
        public void RigRunLengthsFollowLtkPresetLifecycle()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                EmitterLifetime = 0.5f,
                ParticleLifetime = VfxCurveF.Const(0.25f)
            };
            var system = new VfxSystemDefinition(1, "rig", "rig", new[] { emitter });

            Assert.Equal(1d, VfxRigMotion.RunLength(VfxRigPreset.Still, system), precision: 4);
            Assert.Equal(1d, VfxRigMotion.RunLength(VfxRigPreset.Burst, system), precision: 4);
            Assert.Equal(3d, VfxRigMotion.RunLength(VfxRigPreset.Trail, system), precision: 4);
        }

        [Fact]
        public void RigSettingsDefaultsMatchPresetLifecycle()
        {
            VfxRigSettings still = VfxRigSettings.ForPreset(VfxRigPreset.Still);
            VfxRigSettings burst = VfxRigSettings.ForPreset(VfxRigPreset.Burst);
            VfxRigSettings missile = VfxRigSettings.ForPreset(VfxRigPreset.Missile);
            VfxRigSettings trail = VfxRigSettings.ForPreset(VfxRigPreset.Trail);

            Assert.Equal(VfxRigMotionKind.Still, still.MotionKind);
            Assert.False(still.IsLooping);
            Assert.True(burst.IsLooping);
            Assert.Equal(VfxRigMotionKind.Path, missile.MotionKind);
            Assert.True(missile.IsLooping);
            Assert.Equal(VfxRigMotion.FlightRange, missile.FlightRange);
            Assert.Equal(VfxRigMotion.FlightSpeed, missile.FlightSpeed);
            Assert.Equal(VfxRigMotionKind.Orbit, trail.MotionKind);
            Assert.False(trail.IsLooping);
            Assert.Equal(VfxRigMotion.OrbitRadius, trail.OrbitRadius);
            Assert.Equal(VfxRigMotion.OrbitPeriod, trail.OrbitPeriod);
            Assert.Equal(VfxRigMotion.FlightHeight, trail.Height);
        }

        [Theory]
        [InlineData(VfxRigPreset.Still, VfxRigPreset.Missile, 100f)]
        [InlineData(VfxRigPreset.Burst, VfxRigPreset.Trail, 100f)]
        [InlineData(VfxRigPreset.Missile, VfxRigPreset.Still, 0f)]
        [InlineData(VfxRigPreset.Trail, VfxRigPreset.Burst, 0f)]
        public void PresetSwitchUsesNewDefaultHeight(VfxRigPreset from, VfxRigPreset to, float height)
        {
            VfxRigSettings next = VfxRigSettings.ForPreset(from).WithPreset(to);
            Assert.Equal(height, next.Height);
            Assert.Equal(to, next.Preset);
            Assert.Null(next.StopAt);
            Assert.Equal(height, VfxRigMotion.Evaluate(next, 0d, 5d).Origin.Y);
        }

        [Theory]
        [InlineData(VfxRigPreset.Still, VfxRigPreset.Trail, 40f)]
        [InlineData(VfxRigPreset.Missile, VfxRigPreset.Burst, 0f)]
        public void PresetSwitchKeepsTunedHeightAndStop(VfxRigPreset from, VfxRigPreset to, float height)
        {
            VfxRigSettings current = VfxRigSettings.ForPreset(from) with { Height = height, StopAt = 0f };
            VfxRigSettings next = current.WithPreset(to);
            Assert.Equal(height, next.Height);
            Assert.Equal(0f, next.StopAt);
            Assert.Equal(VfxRigSettings.ForPreset(to).IsLooping, next.IsLooping);
        }

        [Fact]
        public void PresetSwitchCarriesTuningThroughLiveSession()
        {
            using var session = new VfxRenderSession();
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Still) with { Height = 40f, StopAt = 3f };
            session.RigPreset = VfxRigPreset.Missile;
            Assert.Equal(40f, session.RigSettings.Height);
            Assert.Equal(3f, session.RigSettings.StopAt);
            Assert.Equal(VfxRigMotionKind.Path, session.RigSettings.MotionKind);
        }

        [Fact]
        public void TunedPathUsesHeightDistanceSpeedAndSoftStop()
        {
            VfxRigSettings settings = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
            {
                Height = 250f,
                FlightRange = 2000f,
                FlightSpeed = 1000f,
                IsLooping = false,
                StopAt = 0.5f
            };

            VfxRigStep step = VfxRigMotion.Evaluate(settings, 0.5d, 4d);

            Assert.Equal(-500f, step.Origin.X, tolerance: 0.1f);
            Assert.Equal(250f, step.Origin.Y, tolerance: 0.1f);
            Assert.Equal(1000f, step.Target.X, tolerance: 0.1f);
            Assert.Equal(250f, step.Target.Y, tolerance: 0.1f);
            Assert.True(step.IsStopped);
        }

        [Fact]
        public void TunedOrbitCanUseLoopLifecycle()
        {
            VfxRigSettings settings = VfxRigSettings.ForPreset(VfxRigPreset.Trail) with
            {
                OrbitPeriod = 2f,
                IsLooping = true
            };

            VfxRigStep step = VfxRigMotion.Evaluate(settings, 3.25d, 3d);

            Assert.Equal(0.25f, step.Phase, precision: 4);
            Assert.False(step.IsStopped);
        }

        [Fact]
        public void StandaloneSoftStopStopsNewEmissionAtTheConfiguredTime()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                Rate = VfxCurveF.Const(20f),
                ParticleLifetime = VfxCurveF.Const(5f),
                EmitterLinger = 10f,
                ParticleLinger = 10f
            };
            var definition = new VfxSystemDefinition(0x50F7u, "soft_stop", "soft_stop", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "soft_stop",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Still) with { StopAt = 0.25f };
            session.Play();
            for (int frame = 0; frame < 3; frame++) session.Update(0.1f);

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.True(root.IsStopped);
            int particlesAtStop = Assert.Single(root.Emitters).Particles.Count;
            Assert.True(particlesAtStop > 0);

            session.Update(0.3f);

            Assert.Equal(particlesAtStop, Assert.Single(root.Emitters).Particles.Count);
        }

        [Fact]
        public void ChangingStandaloneRigSettingsRepositionsTheLoadedGraphImmediately()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                Rate = VfxCurveF.Const(20f),
                ParticleLifetime = VfxCurveF.Const(10f)
            };
            var definition = new VfxSystemDefinition(0xA11CEu, "tuned", "tuned", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "tuned",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.Equal(0f, root.WorldTransform.Translation.Y, precision: 4);
            session.Play();
            session.Update(0.2f);
            double time = session.ActiveSystem.CurrentTime;
            float rootTime = root.CurrentTime;
            int particles = Assert.Single(root.Emitters).Particles.Count;
            Assert.True(particles > 0);

            session.RigSettings = session.RigSettings with { Height = 275f };

            Assert.Equal(time, session.ActiveSystem.CurrentTime, precision: 6);
            Assert.Equal(rootTime, root.CurrentTime, precision: 6);
            Assert.Equal(particles, Assert.Single(root.Emitters).Particles.Count);
            Assert.Equal(275f, root.WorldTransform.Translation.Y, precision: 4);
            Assert.Equal(275f, Assert.Single(root.Emitters).SystemTarget.Y, precision: 4);
        }

        [Fact]
        public void ChangingStandaloneRigMotionRestartsTheRunLikeSteer()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                Rate = VfxCurveF.Const(20f),
                ParticleLifetime = VfxCurveF.Const(10f)
            };
            var definition = new VfxSystemDefinition(0x5A11u, "steer", "steer", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "steer",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.Play();
            session.Update(0.2f);

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.True(session.ActiveSystem.CurrentTime > 0d);
            Assert.True(root.CurrentTime > 0f);
            Assert.True(Assert.Single(root.Emitters).Particles.Count > 0);

            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { IsLooping = false };

            Assert.Equal(0d, session.ActiveSystem.CurrentTime, precision: 6);
            Assert.Equal(0f, root.CurrentTime, precision: 6);
            Assert.Empty(Assert.Single(root.Emitters).Particles);
            Assert.NotEqual(Vector3.Zero, root.WorldTransform.Translation);
        }

        [Fact]
        public void StandaloneLoopingRigKeepsPlayingAcrossItsRunBoundary()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                EmitterLifetime = 0.2f,
                ParticleLifetime = VfxCurveF.Const(0.1f)
            };
            var definition = new VfxSystemDefinition(7, "loop", "loop", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "loop",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [7] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Burst);
            session.Play();

            for (int frame = 0; frame < 11; frame++) session.Update(0.1f);

            Assert.True(session.ActiveSystem.CurrentTime > session.RigDuration);
            Assert.InRange(session.PlaybackTime, 0d, session.RigDuration);
            double before = session.ActiveSystem.CurrentTime;

            session.Update(0.1f);

            Assert.True(session.ActiveSystem.CurrentTime > before);
        }

        [Theory]
        [InlineData(0.0625f, 1d)]
        [InlineData(0.0625f, 2d)]
        [InlineData(0.07f, 2d)]
        public void MissileLoopDetectsCyclesEvenWhenTheEndPhaseDoesNotDecrease(float frameTime, double speed)
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                TexturePath = null,
                IsSingleParticle = true,
                EmitterLifetime = 0.01f,
                ParticleLifetime = VfxCurveF.Const(0.01f)
            };
            var definition = new VfxSystemDefinition(7, "short-flight", "short-flight", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = definition.Name,
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [7] = definition },
                ResourceMap = new Dictionary<uint, uint>(),
                Speed = 1d
            };
            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
            {
                FlightRange = 0.0625f,
                FlightSpeed = 1f
            };
            Assert.Equal(0.0625d, session.RigDuration, precision: 6);
            session.Play();
            session.Update(0.03125f);
            model.Speed = speed;
            session.Update(frameTime);

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.Equal(frameTime * (float)speed, root.CurrentTime, precision: 6);
        }

        [Fact]
        public void LoopWrapReplaysTheWholeVariableFrameLikeLtk()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                EmitterLifetime = 0.2f,
                ParticleLifetime = VfxCurveF.Const(0.1f)
            };
            var definition = new VfxSystemDefinition(0x70u, "loop_variable", "loop_variable", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "loop_variable",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Burst);
            Assert.True(session.RigDuration > 0.1d);

            // Reach 50 ms before the boundary through the deterministic fixed-rate seek path.
            session.Seek(session.RigDuration - 0.05d);
            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.True(root.CurrentTime > 0.5f);

            session.Play();
            session.Update(0.1f);

            // LTK detects the wrap from the end phase, replays at phase zero and then gives the
            // new run this frame's complete 100 ms step. Splitting at the boundary would leave 50 ms.
            Assert.Equal(0.1f, root.CurrentTime, precision: 4);
            Assert.InRange(session.PlaybackTime, 0.049d, 0.051d);
        }

        [Fact]
        public void LoopLifecycleReplaysAfterSoftStop()
        {
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One) with
            {
                Rate = VfxCurveF.Const(20f),
                EmitterLifetime = 0.2f,
                ParticleLifetime = VfxCurveF.Const(0.1f),
                EmitterLinger = 10f,
                ParticleLinger = 10f
            };
            var definition = new VfxSystemDefinition(8, "loop_soft_stop", "loop_soft_stop", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "loop_soft_stop",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [8] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            using var session = new VfxRenderSession();
            session.SetSystem(model);
            session.RigSettings = VfxRigSettings.ForPreset(VfxRigPreset.Burst) with { StopAt = 0.15f };
            session.Play();

            for (int frame = 0; frame < 4; frame++) session.Update(0.05f);
            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.True(root.IsStopped);

            for (int frame = 0; frame < 17; frame++) session.Update(0.05f);

            Assert.False(root.IsStopped);
            Assert.InRange(session.PlaybackTime, 0.04d, 0.06d);
        }

        [Fact]
        public void RootSystemTransformWrapsRigOriginAndTargetLikeLtk()
        {
            var session = new VfxRenderSession();
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One);
            var definition = new VfxSystemDefinition(
                0x12345678,
                "scaled",
                "scaled",
                new[] { emitter },
                Transform: Matrix4x4.CreateScale(2f));
            var model = new VfxSystemModel
            {
                Name = "scaled",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };

            session.SetSystem(model);

            VfxPlaybackRuntime root = Assert.Single(session.Graphs).Root;
            Assert.Equal(Vector3.Zero, root.WorldTransform.Translation);
            Assert.Equal(new Vector3(1200f, 0f, 0f), Assert.Single(root.Emitters).SystemTarget);
        }

        [Fact]
        public void SystemNamesDoNotOverrideThePreviewRig()
        {
            var session = new VfxRenderSession();
            var emitter = CreateEmitter(Vector3.One);

            var def = new VfxSystemDefinition(0x12345678, "Lulu_Base_Q_mis", "Lulu_Base_Q_mis", new[] { emitter });
            var model = new VfxSystemModel
            {
                Name = "Lulu_Base_Q_mis",
                Definition = def,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition>(),
                ResourceMap = new Dictionary<uint, uint>()
            };

            session.SetSystem(model);

            Assert.Equal(VfxRigPreset.Still, session.RigPreset);
            Assert.True(session.Graphs.Count > 0);

            session.Play();
            session.Update(0.1f);

            // After moving forward in time, the active system has advanced and the graph has a valid transform
            Assert.True(session.ActiveSystem.CurrentTime > 0);
        }

        [Theory]
        [InlineData("texture")]
        [InlineData("material")]
        [InlineData("mesh")]
        public void InitialPendingResourcesHoldClockWithoutLosingFirstBurst(string resource)
        {
            var clock = new ResourceWaitClock();
            using var session = new VfxRenderSession(timeProvider: clock);
            session.SetSystem(ResourceWaitModel());
            VfxPlaybackRuntime.EmitterState emitter = Assert.Single(Assert.Single(session.Graphs).Root.Emitters);
            SetPendingResource(emitter, resource);
            session.Play();
            session.Update(0.1f);
            Assert.Equal(0d, session.PlaybackTime);
            clock.Advance(TimeSpan.FromSeconds(3.9));
            session.Update(0.1f);
            Assert.Equal(0d, session.PlaybackTime);
            Assert.Empty(emitter.Particles);

            emitter.PendingTexture = null;
            emitter.PendingMesh = null;
            emitter.PendingProgramTextures.Clear();
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.099, 0.101);
            Assert.NotEmpty(emitter.Particles);

            // Later resources must not stall an already running effect.
            SetPendingResource(emitter, resource);
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.199, 0.201);
        }

        [Fact]
        public void InitialResourceTimeoutDoesNotCatchUpAndNewSystemGetsItsOwnWait()
        {
            var clock = new ResourceWaitClock();
            using var session = new VfxRenderSession(timeProvider: clock);
            session.SetSystem(ResourceWaitModel());
            SetPendingResource(Assert.Single(Assert.Single(session.Graphs).Root.Emitters), "texture");
            session.Play();
            session.Update(0.1f);
            clock.Advance(VfxRenderSession.InitialResourceWaitLimit);
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.099, 0.101);
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.199, 0.201);

            session.SetSystem(ResourceWaitModel());
            SetPendingResource(Assert.Single(Assert.Single(session.Graphs).Root.Emitters), "texture");
            session.Play();
            session.Update(0.1f);
            Assert.Equal(0d, session.PlaybackTime);
        }

        [Fact]
        public void ExplicitSeekBypassesInitialResourceWaitAndPauseStillStopsClock()
        {
            using var session = new VfxRenderSession();
            session.SetSystem(ResourceWaitModel());
            SetPendingResource(Assert.Single(Assert.Single(session.Graphs).Root.Emitters), "texture");
            session.Play();
            session.Update(0.1f);
            Assert.Equal(0d, session.PlaybackTime);
            session.Seek(0.5);
            Assert.Equal(0.5, session.PlaybackTime, precision: 6);
            session.Pause();
            session.Update(0.1f);
            Assert.Equal(0.5, session.PlaybackTime, precision: 6);
            session.Play();
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.599, 0.601);
        }

        [Fact]
        public void MissingOrHiddenResourcesDoNotDelayPlayback()
        {
            using var session = new VfxRenderSession();
            session.SetSystem(ResourceWaitModel());
            VfxPlaybackRuntime.EmitterState emitter = Assert.Single(Assert.Single(session.Graphs).Root.Emitters);
            emitter.PendingTexture = new object();
            session.Play();
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.099, 0.101);

            session.SetSystem(ResourceWaitModel());
            emitter = Assert.Single(Assert.Single(session.Graphs).Root.Emitters);
            SetPendingResource(emitter, "texture");
            emitter.IsVisible = false;
            session.Play();
            session.Update(0.1f);
            Assert.InRange(session.PlaybackTime, 0.099, 0.101);
        }

        private static VfxSystemModel ResourceWaitModel()
        {
            var emitter = CreateEmitter(Vector3.One) with
            {
                TexturePath = null,
                Rate = VfxCurveF.Const(20f),
                ParticleLifetime = VfxCurveF.Const(10f)
            };
            var definition = new VfxSystemDefinition(0xA11CEu, "resources", "resources", new[] { emitter });
            return new VfxSystemModel
            {
                Name = "resources",
                Definition = definition,
                SystemCatalog = new Dictionary<uint, VfxSystemDefinition> { [definition.PathHash] = definition },
                ResourceMap = new Dictionary<uint, uint>()
            };
        }

        private static void SetPendingResource(VfxPlaybackRuntime.EmitterState emitter, string kind)
        {
            if (kind == "mesh")
            {
                emitter.PendingMesh = new VfxMeshData(
                    new float[9], new float[9], new float[6], null, new uint[] { 0, 1, 2 });
                return;
            }
            BitmapSource bitmap = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
            bitmap.Freeze();
            if (kind == "material") emitter.PendingProgramTextures["authored.dds"] = bitmap;
            else emitter.PendingTexture = bitmap;
        }

        private sealed class ResourceWaitClock : TimeProvider
        {
            private long _timestamp;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => _timestamp;
            internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        }

        private static VfxEmitterDefinition CreateEmitter(Vector3 birthScale)
            => new(
                Name: "emitter",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
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
                TexturePath: "test.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false,
                RenderState: VfxEmitterRenderState.Default);
    }
}

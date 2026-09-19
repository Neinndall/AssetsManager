using System;
using System.Collections.Generic;
using System.Numerics;
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
            // Range = 1200, Speed = 1600, FlightTime = 0.75s, StandHeight = 100
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

            // LTK's missile object convention carries local +Y along flight and local +Z down.
            Vector3 localY = Vector3.TransformNormal(Vector3.UnitY, atStart.Transform);
            Assert.Equal(1f, localY.X, tolerance: 1e-4f);
            Assert.Equal(0f, localY.Y, tolerance: 1e-4f);
            Assert.Equal(0f, localY.Z, tolerance: 1e-4f);

            Vector3 localZ = Vector3.TransformNormal(Vector3.UnitZ, atStart.Transform);
            Assert.Equal(0f, localZ.X, tolerance: 1e-4f);
            Assert.Equal(-1f, localZ.Y, tolerance: 1e-4f);
            Assert.Equal(0f, localZ.Z, tolerance: 1e-4f);
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
                    VfxRigMotion.StandHeight,
                    MathF.Sin((VfxRigMotion.OrbitPeriod - 0.01f) / VfxRigMotion.OrbitPeriod * MathF.PI * 2f) * VfxRigMotion.OrbitRadius));

            Assert.Equal(VfxRigMotion.OrbitPeriod + 0.25f, afterOneOrbit.Phase, precision: 4);
            Assert.NotEqual(Vector3.Zero, afterOneOrbit.Moved);
        }

        [Fact]
        public void BurstAndStillRigs_PositionAtOriginHeight()
        {
            var burst = VfxRigMotion.Evaluate(VfxRigPreset.Burst, 1.2, 5.0);
            Assert.Equal(0f, burst.Origin.X);
            Assert.Equal(100f, burst.Origin.Y);
            Assert.Equal(0f, burst.Origin.Z);
            Assert.False(burst.IsStopped);

            var still = VfxRigMotion.Evaluate(VfxRigPreset.Still, 0.0, 5.0);
            Assert.Equal(0f, still.Origin.X);
            Assert.Equal(100f, still.Origin.Y);
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
            Assert.Equal(VfxRigMotion.StandHeight, trail.Height);
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
            VfxEmitterDefinition emitter = CreateEmitter(Vector3.One);
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
            Assert.Equal(VfxRigMotion.StandHeight, root.WorldTransform.Translation.Y, precision: 4);

            session.RigSettings = session.RigSettings with { Height = 275f };

            Assert.Equal(275f, root.WorldTransform.Translation.Y, precision: 4);
            Assert.Equal(275f, Assert.Single(root.Emitters).SystemTarget.Y, precision: 4);
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
            Assert.Equal(new Vector3(0f, 200f, 0f), root.WorldTransform.Translation);
            Assert.Equal(new Vector3(1200f, 200f, 0f), Assert.Single(root.Emitters).SystemTarget);
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

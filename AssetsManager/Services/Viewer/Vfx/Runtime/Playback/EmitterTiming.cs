using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private ParticleLifecycleInfo LifecycleInfo(EmitterState state, in Particle particle, bool died)
        {
            float emitterT = EmitterTime(state);
            Vector3 position = DrawnPosition(particle, state);
            Vector3 orbitalAngles = particle.BirthOrbitalVelocity * particle.Age;
            Matrix4x4 orbitalTurn = OrbitalTurn(orbitalAngles);

            // Billboard travel alignment belongs to the draw, not to a child's inherited bearing.
            Matrix4x4 basis = StandingBasis(particle, state, orbitalTurn, 0f);
            Matrix4x4 frame = state.Def.ParticleIsLocalOrientation
                ? state.SystemOrientation
                : VectorMathUtils.OrientationOnly(particle.BirthFrame);

            float particleTime = died && float.IsFinite(particle.Life) ? particle.Life : particle.Age;
            return new ParticleLifecycleInfo(
                position,
                basis,
                frame,
                particle.Serial,
                state.SourceOrder,
                particleTime,
                particle.Life,
                emitterT,
                died)
            {
                DrawnScale = state.Def.IsMeshPrimitive ? ResolveMeshScale(state, particle) : Vector3.One
            };
        }

        internal static float EmitterTime(EmitterState state)
            => EmitterPhase(state.Def, state.Age);

        internal static float EmitterPhase(VfxEmitterDefinition definition, float age)
        {
            float span = MathF.Min(EmissionEnd(definition) ?? float.PositiveInfinity,
                MathF.Min(definition.EmissionPeriod?.Length ?? float.PositiveInfinity,
                    definition.EmissionPeriod?.Active ?? float.PositiveInfinity));
            return float.IsPositiveInfinity(span) || MathF.Abs(span) <= 1e-6f
                ? 0f : (age - definition.TimeBeforeFirstEmission) / span;
        }

        internal static float CurveMaximum(VfxCurveF curve)
            => curve.Values is { Length: > 0 } values ? System.Linq.Enumerable.Max(values) : curve.Constant;

        internal static float? EmissionEnd(VfxEmitterDefinition definition)
        {
            float? end = definition.EmitterLifetime;
            if (definition.IsSimpleEmitter || !definition.IsSingleParticle || definition.OverridesMaterials) return end;
            float particle = CurveMaximum(definition.ParticleLifetime);
            if (particle == -1f) return end;
            return end is null || end > particle + 10f ? particle : end;
        }

        internal static bool IsSimpleListEmitter(VfxEmitterDefinition definition)
            => definition?.IsSimpleEmitter == true;

        internal static float LingerSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition) ? 0f : CurveMaximum(definition.ParticleLifetime);
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.ParticleLinger));
        }

        internal static float StopWaitSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition)
                ? 0f
                : EmissionEnd(definition) ?? float.PositiveInfinity;
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.EmitterLinger));
        }

        private Matrix4x4 EmitterPlacement(VfxEmitterDefinition definition)
        {
            Matrix4x4 world = definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform;
            if (_definition?.HudLayer == true) world.Translation += _definition.Transform?.Translation ?? Vector3.Zero;
            return EmitterTransform(definition, world);
        }

        private static Matrix4x4 EmitterTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            Matrix4x4 frame = EmitterFieldTransform(definition, world, rotation);
            Vector3 translation = Vector3.TransformNormal(definition.TranslationOverride.GetValueOrDefault(), world);
            frame.Translation = world.Translation + translation;
            return frame;
        }

        private static Matrix4x4 EmitterFieldTransform(VfxEmitterDefinition definition, Matrix4x4 world, Vector3 rotation)
            => Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
               Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
               Matrix4x4.CreateRotationY(rotation.Y) * world;

        private Vector3 EmitterOdometerPosition(VfxEmitterDefinition definition, float emitterT)
        {
            return Vector3.Transform(definition.EmitterPosition.Sample(emitterT), EmitterPlacement(definition));
        }

        private void AdvanceTrailOdometer(EmitterState state, float emitterT)
        {
            Vector3 spawnedAt = EmitterOdometerPosition(state.Def, emitterT);
            if (state.TrailSpawnedAt is Vector3 previous)
                state.TrailDistance += Vector3.Distance(previous, spawnedAt);
            state.TrailSpawnedAt = spawnedAt;
        }
    }
}

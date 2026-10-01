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
            float particleT = ParticleAge01(particle.Age, particle.Life);
            float emitterT = EmitterTime(state);
            Vector3 position = particle.Pos;
            Vector3 orbitalAngles = particle.BirthOrbitalVelocity * particle.Age;
            Matrix4x4 orbitalTurn = OrbitalTurn(orbitalAngles);
            if (orbitalTurn != Matrix4x4.Identity)
            {
                Vector3 origin = state.SystemOrigin;
                position = origin + Vector3.Transform(position - origin, orbitalTurn);
            }
            if (state.Def.Acceleration is { } worldAcceleration && float.IsFinite(particle.Life))
            {
                float reached = particleT * particle.Life * particle.Life;
                position += worldAcceleration.Sample(emitterT) * reached;
            }

            float legacyRoll = state.Def.LegacyRotation?.Sample(particleT) * (MathF.PI / 180f) ?? 0f;
            Matrix4x4 basis = ParticleBasis(particle, state, particle.Travel, orbitalTurn, legacyRoll);
            Matrix4x4 frame = state.Def.ParticleIsLocalOrientation
                ? new Matrix4x4(
                    state.PlacementRight.X, state.PlacementRight.Y, state.PlacementRight.Z, 0f,
                    state.PlacementUp.X, state.PlacementUp.Y, state.PlacementUp.Z, 0f,
                    state.PlacementForward.X, state.PlacementForward.Y, state.PlacementForward.Z, 0f,
                    0f, 0f, 0f, 1f)
                : VectorMathUtils.OrientationOnly(particle.BirthFrame);
            if (orbitalTurn != Matrix4x4.Identity)
                frame = VectorMathUtils.OrientationOnly(frame * orbitalTurn);

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
                died);
        }

        internal static float EmitterTime(EmitterState state)
        {
            VfxEmitterDefinition definition = state.Def;
            return definition.EmitterLifetime is > 0f
                ? Math.Clamp(state.Age / definition.EmitterLifetime.Value, 0f, 1f)
                : 0f;
        }

        internal static bool IsSimpleListEmitter(VfxEmitterDefinition definition)
            => definition?.IsSimpleEmitter == true;

        internal static float LingerSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition) ? 0f : MathF.Max(0f, definition.ParticleLifetime.Constant);
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.ParticleLinger));
        }

        internal static float StopWaitSeconds(VfxEmitterDefinition definition)
        {
            float lifetime = IsSimpleListEmitter(definition)
                ? 0f
                : definition.EmitterLifetime ?? float.PositiveInfinity;
            return MathF.Min(lifetime + 10f, MathF.Max(0f, definition.EmitterLinger));
        }

        private static float LingerProgress(EmitterState state)
        {
            if (state.FinishedAt < 0f) return 0f;
            float seconds = LingerSeconds(state.Def);
            return seconds > 0f ? Math.Clamp((state.Age - state.FinishedAt) / seconds, 0f, 1f) : 1f;
        }

        private Matrix4x4 EmitterPlacement(VfxEmitterDefinition definition)
            => EmitterTransform(definition, definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform);

        private Vector3 EmitterFieldPosition(VfxEmitterDefinition definition, float emitterT)
        {
            Matrix4x4 world = definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform;
            return Vector3.Transform(definition.EmitterPosition.Sample(emitterT), EmitterFieldTransform(definition, world));
        }

        private Matrix4x4 FieldLocalOrientation()
        {
            if (!Matrix4x4.Invert(_orientationRootTransform, out Matrix4x4 inverseRoot))
                return Matrix4x4.Identity;
            return VectorMathUtils.OrientationOnly(_worldTransform * inverseRoot);
        }

        private static Matrix4x4 EmitterTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            return Matrix4x4.CreateTranslation(definition.TranslationOverride.GetValueOrDefault()) *
                EmitterFieldTransform(definition, world, rotation);
        }

        private static Matrix4x4 EmitterFieldTransform(VfxEmitterDefinition definition, Matrix4x4 world)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            return EmitterFieldTransform(definition, world, rotation);
        }

        private static Matrix4x4 EmitterFieldTransform(VfxEmitterDefinition definition, Matrix4x4 world, Vector3 rotation)
            => Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
               Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
               Matrix4x4.CreateRotationY(rotation.Y) * world;

        private Vector3 EmitterOdometerPosition(VfxEmitterDefinition definition, float emitterT)
        {
            Vector3 rotation = definition.RotationOverride.GetValueOrDefault() * (MathF.PI / 180f);
            Matrix4x4 world = definition.IsLocalOrientation ? _worldTransform : _orientationRootTransform;
            Matrix4x4 spawnFrame = Matrix4x4.CreateScale(definition.ScaleOverride ?? Vector3.One) *
                Matrix4x4.CreateRotationZ(rotation.Z) * Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y) * world;
            return Vector3.Transform(definition.EmitterPosition.Sample(emitterT), spawnFrame);
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

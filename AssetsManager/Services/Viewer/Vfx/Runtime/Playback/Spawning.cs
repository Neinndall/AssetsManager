using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private void Spawn(EmitterState s, float emitterT)
        {
            var d = s.Def;
            if (d.ParticlesShareRandomValue && !s.SharedRandomRolled)
            {
                s.SharedRandom = _rng.NextUnitFloat();
                s.SharedRandomRolled = true;
            }
            float roll = _rng.NextUnitFloat();
            // The ordinary chance is drawn even while inspection pins a replacement value. This
            // keeps every later RNG draw on the same stream as the unpinned run.
            float drawnChance = d.ParticlesShareRandomValue ? s.SharedRandom : _rng.NextUnitFloat();
            float sharedRoll = _pinnedBirthChance ?? drawnChance;

            float life = d.ParticleLifetime.SampleBirth(emitterT, _rng, sharedRoll);
            // A negative particleLifetime (usually -1) never expires: the particle stays until its system is
            // removed (Akshan's E hook mesh, Bloom's pre-warmed waterfall glow, whose emitter stops after 1 s).
            // Retiring it at its first step hid every such particle.
            if (life < 0f)
                life = float.PositiveInfinity;
            var birthScale = d.BirthScale.SampleBirthOver(emitterT, _rng, Vector3.One, sharedRoll);
            if (d.LegacyBirthScale is { } legacyBirthScale)
            {
                float scalar = legacyBirthScale.SampleBirth(emitterT, _rng, sharedRoll);
                Vector2 bias = d.LegacyScaleBias ?? Vector2.One;
                birthScale = new Vector3(scalar * bias.X, scalar * bias.Y, scalar);
            }
            // isUniformScale promotes the first authored component to every axis for all
            // particle kinds, not only mesh primitives.
            if (d.IsUniformScale)
                birthScale = new Vector3(birthScale.X);
            var vel = d.BirthVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthOrbitalVelocity = d.BirthOrbitalVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthDrag = d.BirthDrag?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var birthRotation = d.BirthRotation?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var rotVel = d.BirthRotationalVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            var rotationalAcceleration = d.BirthRotationalAcceleration?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero;
            Vector2 birthUvOffset = d.BirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 birthUvScrollRate = d.BirthUvScrollRateCurve?.SampleBirth(emitterT, _rng, sharedRoll) ?? d.UvScrollRate;
            float birthUvRotateRate = d.BirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;
            Vector2 textureMultBirthUvOffset = d.TextureMultBirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 textureMultBirthUvScrollRate = d.TextureMultBirthUvScrollRate?.SampleBirth(emitterT, _rng, sharedRoll)
                ?? d.TextureMultUvScrollRate;
            float textureMultBirthUvRotateRate = d.TextureMultBirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;

            Matrix4x4 spawnRotation = Matrix4x4.Identity;
            var localOffset = d.SpawnShape is { } shape
                ? shape.SampleOffset(_rng, emitterT, sharedRoll, out spawnRotation)
                : Vector3.Zero;

            bool onEmissionSurface = false;
            VfxSurfaceBirth surfaceBirth = default;
            if (d.EmissionSurface is not null &&
                _emissionSurfaces.TryGetValue(d, out IVfxEmissionSurfaceSampler surfaceSampler))
            {
                onEmissionSurface = surfaceSampler.TrySample(s.Age, _rng, out surfaceBirth);
                if (onEmissionSurface)
                    localOffset += surfaceBirth.Position;
            }

            if (onEmissionSurface && d.EmissionSurface.UseNormal)
                vel = surfaceBirth.Normal * vel.Length();

            Matrix4x4 placement = EmitterPlacement(d);
            var worldOffset = Vector3.TransformNormal(localOffset, placement);
            vel = Vector3.TransformNormal(vel, spawnRotation);
            vel = Vector3.TransformNormal(vel, placement);
            Vector3 finalBirthSize = birthScale * ExtractScale(placement);
            Vector3 analyticTerminal = Vector3.Zero;
            Vector3 analyticOffset = Vector3.Zero;
            if (_dragMotion == VfxDragMotion.Analytic)
            {
                analyticTerminal = new Vector3(
                    birthDrag.X > 0f ? vel.X / birthDrag.X : 0f,
                    birthDrag.Y > 0f ? vel.Y / birthDrag.Y : 0f,
                    birthDrag.Z > 0f ? vel.Z / birthDrag.Z : 0f);
                analyticOffset = analyticTerminal;
                vel = Vector3.Zero;
            }

            s.Particles.Add(new Particle
            {
                Pos = s.BasePos + worldOffset,
                Vel = vel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                AnalyticTerminal = analyticTerminal,
                AnalyticOffset = analyticOffset,
                BirthFrame = placement,
                SpawnRotation = Quaternion.CreateFromRotationMatrix(spawnRotation),
                Age = 0f,
                Life = life,
                Serial = _particleSerial++,
                TrailTiling = d.Trail?.BirthTilingSize.SampleBirth(emitterT, _rng, sharedRoll)
                    ?? d.Beam?.BirthTilingSize.SampleBirth(emitterT, _rng, sharedRoll)
                    ?? Vector3.Zero,
                TrailBirthDistance = s.TrailDistance,
                BirthSize = finalBirthSize,
                BirthColor = VfxColorSemantics.ResolveBirth(d.BirthColor, emitterT, _rng, sharedRoll),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                RotationalVelocity = rotVel * (MathF.PI / 180f),
                RotationalAcceleration = rotationalAcceleration * (MathF.PI / 180f),
                Rot = birthRotation.X * (MathF.PI / 180f),
                RotVel = rotVel.X * (MathF.PI / 180f),
                RangeRandom = roll,
                StartFrame = d.RandomStartFrame && d.NumFrames > 1 ? roll * d.NumFrames : 0f,
                FrameRate = (d.FrameRate ?? 0f) *
                    (d.BirthFrameRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 1f),
                BirthUvOffset = birthUvOffset,
                BirthUvScrollRate = birthUvScrollRate,
                BirthUvRotateRate = birthUvRotateRate,
                TextureMultBirthUvOffset = textureMultBirthUvOffset,
                TextureMultBirthUvScrollRate = textureMultBirthUvScrollRate,
                TextureMultBirthUvRotateRate = textureMultBirthUvRotateRate,
                LingerFrom = -1f
            });
            ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, s.Particles[^1], died: false));
        }
    }
}

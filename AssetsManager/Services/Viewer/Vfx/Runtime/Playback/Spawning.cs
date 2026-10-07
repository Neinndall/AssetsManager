using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private void Spawn(EmitterState s, float emitterT, Vector3 anchor, float age)
        {
            var d = s.Def;
            if (d.ParticlesShareRandomValue && !s.SharedRandomRolled)
            {
                s.SharedRandom = _rng.NextUnitFloat();
                s.SharedRandomRolled = true;
            }
            // The ordinary chance is drawn even while inspection pins a replacement value. This
            // keeps every later RNG draw on the same stream as the unpinned run.
            float drawnChance = d.ParticlesShareRandomValue ? s.SharedRandom : _rng.NextUnitFloat();
            float sharedRoll = _pinnedBirthChance ?? drawnChance;

            float roll = sharedRoll;
            float life = BirthScalar(d.ParticleLifetime, emitterT);
            // A negative particleLifetime (usually -1) never expires: the particle stays until its system is
            // removed (Akshan's E hook mesh, Bloom's pre-warmed waterfall glow, whose emitter stops after 1 s).
            // Retiring it at its first step hid every such particle.
            if (life < 0f)
                life = float.PositiveInfinity;
            var vel = BirthVector(d.BirthVelocity, emitterT);
            var birthAcceleration = BirthVector(d.BirthAcceleration, emitterT);
            var birthRotation = d.IsSimpleEmitter
                ? d.BirthRotation?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero : BirthVector(d.BirthRotation, emitterT);
            var rotVel = d.IsSimpleEmitter
                ? d.BirthRotationalVelocity?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector3.Zero : BirthVector(d.BirthRotationalVelocity, emitterT);
            var birthScale = d.IsSimpleEmitter ? Vector3.One : BirthVector(d.BirthScale, emitterT, Vector3.One);
            if (d.IsSimpleEmitter && d.LegacyBirthScale is { } legacyBirthScale)
            {
                float scalar = legacyBirthScale.SampleBirth(emitterT, _rng, sharedRoll);
                Vector2 bias = d.LegacyScaleBias ?? Vector2.One;
                birthScale = new Vector3(scalar * bias.X, scalar * bias.Y, scalar);
            }
            if (UsesUniformScale(d))
                birthScale = new Vector3(birthScale.X);
            var rotationalAcceleration = BirthVector(d.BirthRotationalAcceleration, emitterT);
            var birthDrag = BirthVector(d.BirthDrag, emitterT);
            var birthOrbitalVelocity = BirthVector(d.BirthOrbitalVelocity, emitterT);
            var birthColor = BirthColor(d.BirthColor, emitterT);
            Vector2 birthUvOffset = d.BirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 birthUvScrollRate = d.BirthUvScrollRateCurve?.SampleBirth(emitterT, _rng, sharedRoll) ?? d.UvScrollRate;
            float birthUvRotateRate = d.BirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;
            Vector2 textureMultBirthUvOffset = d.TextureMultBirthUvOffset?.SampleBirth(emitterT, _rng, sharedRoll) ?? Vector2.Zero;
            Vector2 textureMultBirthUvScrollRate = d.TextureMultBirthUvScrollRate?.SampleBirth(emitterT, _rng, sharedRoll)
                ?? d.TextureMultUvScrollRate;
            float textureMultBirthUvRotateRate = d.TextureMultBirthUvRotateRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 0f;

            Matrix4x4 spawnRotation = Matrix4x4.Identity;
            var localOffset = d.SpawnShape is { } shape
                ? SampleSpawnShape(shape, emitterT, out spawnRotation)
                : Vector3.Zero;

            Vector3 rawOffset = Vector3.TransformNormal(localOffset, Matrix4x4.Transpose(spawnRotation));
            byte symmetry = d.OffsetLifeScalingSymmetryMode;
            if ((symmetry & 1) != 0) rawOffset.X = MathF.Abs(rawOffset.X);
            if ((symmetry & 2) != 0) rawOffset.Y = MathF.Abs(rawOffset.Y);
            if ((symmetry & 4) != 0) rawOffset.Z = MathF.Abs(rawOffset.Z);
            life = MathF.Max(0f, life + Vector3.Dot(rawOffset, d.OffsetLifetimeScaling));

            if (d.EmissionMesh is { } emissionMesh && _emissionSurfaces.TryGetValue(d, out IVfxEmissionSurfaceSampler loaded)
                && loaded is VfxEmitterEmissionSampler combined && combined.Mesh is { } meshSampler
                && meshSampler.TrySample(s.Age, _rng, out VfxSurfaceBirth meshBirth))
            {
                localOffset += meshBirth.Position;
                if (emissionMesh.UseNormal)
                {
                    vel = meshBirth.Normal * vel.Length();
                    birthAcceleration = meshBirth.Normal * birthAcceleration.Length();
                }
            }

            bool onEmissionSurface = false;
            VfxSurfaceBirth surfaceBirth = default;
            if (d.EmissionSurface is not null &&
                _emissionSurfaces.TryGetValue(d, out IVfxEmissionSurfaceSampler surfaceSampler))
            {
                IVfxEmissionSurfaceSampler sampler = surfaceSampler is VfxEmitterEmissionSampler pair ? pair.Surface : surfaceSampler;
                onEmissionSurface = sampler?.TrySample(s.Age, _rng, out surfaceBirth) == true;
                if (onEmissionSurface)
                    localOffset += surfaceBirth.Position;
            }

            if (onEmissionSurface && d.EmissionSurface.UseNormal)
                vel = surfaceBirth.Normal * vel.Length();

            Matrix4x4 placement = EmitterPlacement(d);
            vel = Vector3.TransformNormal(vel, spawnRotation);
            birthAcceleration = Vector3.TransformNormal(birthAcceleration, spawnRotation);
            Matrix4x4 birthFrame = placement;
            birthFrame.Translation = Vector3.Zero;
            Vector3 finalBirthSize = birthScale * ExtractScale(s.DefinitionTransform * birthFrame);
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

            var particle = new Particle
            {
                LocalPosition = (d.IsEmitterSpace ? Vector3.Zero : d.EmitterPosition.Sample(emitterT)) + localOffset,
                BirthAnchor = anchor,
                BirthAcceleration = birthAcceleration,
                Vel = vel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                AnalyticTerminal = analyticTerminal,
                AnalyticOffset = analyticOffset,
                BirthFrame = birthFrame,
                Age = age,
                Life = life,
                Serial = _particleSerial++,
                TrailTiling = BirthVector(d.Trail?.BirthTilingSize ?? d.Beam?.BirthTilingSize, emitterT),
                TrailBirthDistance = s.TrailDistance,
                BirthSize = finalBirthSize,
                BirthColor = birthColor,
                InitialRotation = birthRotation * (MathF.PI / 180f),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                RotationalVelocity = rotVel * (MathF.PI / 180f),
                RotationalAcceleration = rotationalAcceleration * (MathF.PI / 180f),
                RangeRandom = roll,
                StartFrame = d.RandomStartFrame ? roll * Math.Max(d.NumFrames, 0) : 0f,
                FrameRate = (d.FrameRate ?? 0f) *
                    (d.IsSimpleEmitter ? 1f : d.BirthFrameRate?.SampleBirth(emitterT, _rng, sharedRoll) ?? 1f),
                BirthUvOffset = birthUvOffset,
                BirthUvScrollRate = birthUvScrollRate,
                BirthUvRotateRate = birthUvRotateRate,
                TextureMultBirthUvOffset = textureMultBirthUvOffset,
                TextureMultBirthUvScrollRate = textureMultBirthUvScrollRate,
                TextureMultBirthUvRotateRate = textureMultBirthUvRotateRate,
                LingerFrom = -1f
            };
            RebuildRotation(ref particle, s);
            particle.Placed = MatrixTranslation(particle, s);
            particle.Pos = (d.ParticleIsLocalOrientation ? particle.Placed : Vector3.TransformNormal(particle.Placed, birthFrame)) + anchor;
            s.Particles.Add(particle);
            ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, s.Particles[^1], died: false));
        }
    }
}

using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        internal readonly record struct PreparedNoiseField(
            Vector3 Center,
            float Radius,
            float Delta,
            Vector3 Axes,
            int First,
            int Kicks,
            int Slot);

        private static PreparedNoiseField[] PrepareNoiseFields(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            float now,
            Vector3 fieldOrigin)
        {
            int noiseCount = fields?.Noise?.Count ?? 0;
            if (noiseCount == 0) return Array.Empty<PreparedNoiseField>();

            if (state.NoiseLast.Length != noiseCount)
            {
                state.NoiseLast = new float[noiseCount];
                Array.Fill(state.NoiseLast, float.NaN);
                state.NoiseFired = new int[noiseCount];
            }

            if (state.PreparedNoise.Length != noiseCount)
                state.PreparedNoise = new PreparedNoiseField[noiseCount];
            PreparedNoiseField[] prepared = state.PreparedNoise;
            for (int slot = 0; slot < noiseCount; slot++)
            {
                VfxNoiseField field = fields.Noise[slot];
                float frequency = field.Frequency.Sample(emitterT);
                int first = state.NoiseFired[slot];
                int owed;
                if (float.IsNaN(state.NoiseLast[slot]))
                {
                    owed = 1;
                }
                else
                {
                    double currentTicks = Math.Truncate((double)frequency * now);
                    double previousTicks = Math.Truncate((double)frequency * state.NoiseLast[slot]);
                    owed = (int)Math.Clamp(currentTicks - previousTicks, 0d, int.MaxValue);
                }

                if (owed > 0) state.NoiseLast[slot] = now;
                int kicks = Math.Min(owed, 256);
                state.NoiseFired[slot] += kicks;
                prepared[slot] = new PreparedNoiseField(
                    fieldOrigin + field.Position.Sample(emitterT),
                    field.Radius.Sample(emitterT),
                    field.VelocityDelta.Sample(emitterT),
                    field.AxisFraction,
                    first,
                    kicks,
                    slot);
            }

            return prepared;
        }

        private void ApplyFieldsToNewborns(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            PreparedNoiseField[] preparedNoise,
            Vector3 fieldOrigin,
            int firstNewborn)
        {
            if (fields is null || firstNewborn >= state.Particles.Count) return;

            for (int index = firstNewborn; index < state.Particles.Count; index++)
            {
                Particle particle = state.Particles[index];
                Vector3 drift = LifeVector(state.Def.VelocityOverLife, ParticleAge01(particle.Age, particle.Life), particle.Serial, SimulationTime);
                Vector3 moving = particle.Vel + drift;
                Vector3 kept = particle.Vel;

                // Riot runs the field pass on a newborn with dt=0. Only the unscaled noise
                // impulses and orbital turn can change that birth step, and the delta persists.
                ApplyFields(fields, state, emitterT, preparedNoise, fieldOrigin, particle.LocalPosition, particle.Serial, 0f, ref moving, ref kept);
                particle.Vel = kept;
                state.Particles[index] = particle;
            }
        }

        private void ApplyFields(
            VfxFieldCollectionDefinition fields,
            EmitterState state,
            float emitterT,
            PreparedNoiseField[] preparedNoise,
            Vector3 fieldOrigin,
            Vector3 particlePosition,
            uint serial,
            float dt,
            ref Vector3 moving,
            ref Vector3 kept)
        {
            if (fields is null) return;

            Vector3 before = moving;
            Matrix4x4 localOrientation = state.SystemOrientation;

            // Riot samples every field at emitter life, not particle life, and applies
            // fields after the particle's own drag in this exact order. Acceleration fields
            // are pre-summed before the one dt multiplication performed by the engine.
            Vector3 accelerationFields = Vector3.Zero;
            foreach (VfxAccelerationField field in fields.Acceleration)
            {
                Vector3 value = field.Acceleration.Sample(emitterT);
                if (field.LocalSpace && state.Def.IsLocalOrientation)
                    value = Vector3.TransformNormal(value, localOrientation);
                accelerationFields += value;
            }
            moving += accelerationFields * dt;

            foreach (VfxAttractionField field in fields.Attraction)
            {
                Vector3 center = fieldOrigin + field.Position.Sample(emitterT);
                Vector3 delta = center - particlePosition;
                float radius = field.Radius.Sample(emitterT);
                float reach = delta.LengthSquared();
                if (reach > radius * radius) continue;
                float strength = field.Acceleration.Sample(emitterT);
                float scale = strength * dt / MathF.Sqrt(MathF.Max(reach, 1f));
                moving += delta * scale;
            }

            foreach (PreparedNoiseField noise in preparedNoise)
            {
                if (noise.Kicks == 0 || Vector3.DistanceSquared(particlePosition, noise.Center) > noise.Radius * noise.Radius)
                    continue;

                for (int kick = 0; kick < noise.Kicks; kick++)
                {
                    Vector3 direction = NoiseDirection(serial, noise.Slot, noise.First + kick);
                    moving += direction * noise.Axes * noise.Delta;
                }
            }

            foreach (VfxDragField field in fields.Drag)
            {
                Vector3 center = fieldOrigin + field.Position.Sample(emitterT);
                float radius = field.Radius.Sample(emitterT);
                if (Vector3.DistanceSquared(particlePosition, center) > radius * radius) continue;
                float strength = field.Strength.Sample(emitterT);
                moving *= MathF.Max(1f - strength * dt, 0f);
            }

            foreach (VfxOrbitalField field in fields.Orbital)
            {
                Vector3 axis = field.Direction.Sample(emitterT);
                if (field.LocalSpace && state.Def.IsLocalOrientation)
                    axis = Vector3.TransformNormal(axis, localOrientation);
                if (!TryNormalizeExact(axis, out double axisX, out double axisY, out double axisZ)) continue;
                ApplyOrbitalField(ref moving, particlePosition, fieldOrigin, axisX, axisY, axisZ);
            }

            // Field deltas persist in the particle velocity just as LTK's pushInto adds
            // MOVING-PUSHED into KEPT.
            kept += moving - before;
        }

        private static void ApplyOrbitalField(
            ref Vector3 velocity,
            Vector3 particle,
            Vector3 center,
            double axisX,
            double axisY,
            double axisZ)
        {
            double along = velocity.X * axisX + velocity.Y * axisY + velocity.Z * axisZ;
            double px = velocity.X - along * axisX;
            double py = velocity.Y - along * axisY;
            double pz = velocity.Z - along * axisZ;
            double speed = Math.Sqrt(px * px + py * py + pz * pz);
            if (speed <= 0.001d) return;

            double dx = (double)particle.X - center.X;
            double dy = (double)particle.Y - center.Y;
            double dz = (double)particle.Z - center.Z;
            double reach = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (reach == 0d) return;
            dx /= reach;
            dy /= reach;
            dz /= reach;
            if (Math.Abs(1d - (axisX * dx + axisY * dy + axisZ * dz)) <= 1e-4d) return;

            double tx = dy * axisZ - dz * axisY;
            double ty = dz * axisX - dx * axisZ;
            double tz = dx * axisY - dy * axisX;
            double tangentLength = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tangentLength == 0d) return;
            double scale = speed / tangentLength;
            if (tx * px + ty * py + tz * pz < 0d) scale = -scale;
            velocity = new Vector3(
                (float)(along * axisX + tx * scale),
                (float)(along * axisY + ty * scale),
                (float)(along * axisZ + tz * scale));
        }

        private static bool TryNormalizeExact(
            Vector3 value,
            out double x,
            out double y,
            out double z)
        {
            double squared = (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
            if (squared == 0d)
            {
                x = 0d;
                y = 0d;
                z = 0d;
                return false;
            }

            double inverse = 1d / Math.Sqrt(squared);
            x = value.X * inverse;
            y = value.Y * inverse;
            z = value.Z * inverse;
            return true;
        }

        private Vector3 NoiseDirection(uint serial, int slot, int impulse)
        {
            // The lineage seed separates otherwise identical sibling systems without advancing birth RNG.
            uint first = Mix32(Mix32(unchecked((uint)_seed)) ^ Mix32(serial + 1u) ^
                Mix32(unchecked((uint)((slot + 1) * (int)0x9e3779b1 + impulse))));
            uint second = Mix32(first ^ 0x68e31da4u);
            uint third = Mix32(second ^ 0x1b56c4e9u);
            const double span = 4294967296.0;
            float x = (float)(first / span * 2.0 - 1.0);
            float y = (float)(second / span * 2.0 - 1.0);
            float z = (float)(third / span * 2.0 - 1.0);
            double squared = (double)x * x + (double)y * y + (double)z * z;
            if (squared < 1e-12d) return new Vector3(x, y, z);

            double inverse = 1d / Math.Sqrt(squared);
            return new Vector3(
                (float)(x * inverse),
                (float)(y * inverse),
                (float)(z * inverse));
        }

        private static uint Mix32(uint value)
        {
            unchecked
            {
                uint held = value;
                held = (held ^ (held >> 16)) * 0x7feb352du;
                held = (held ^ (held >> 15)) * 0x846ca68bu;
                return held ^ (held >> 16);
            }
        }
    }
}

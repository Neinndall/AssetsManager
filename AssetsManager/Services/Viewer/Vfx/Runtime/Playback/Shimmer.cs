using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Runtime;

public sealed partial class VfxPlaybackRuntime
{
    private void RebuildShimmerParticles(EmitterState state, int available)
    {
        var definition = state.Def;
        var timing = definition.Shimmer;
        state.Particles.Clear();
        if (definition.Disabled || state.Absent || available <= 0 || state.Age < timing.StartDelay) return;

        // Shimmer evaluates a bounded set of births from the clock, independent of the legacy pool RNG.
        var births = state.ShimmerBirths ??= new List<double>(33);
        births.Clear();
        double now = state.Age;
        double span = timing.Duration < 0 ? double.PositiveInfinity : timing.Duration;
        double life = timing.Lifetime <= 0 ? double.PositiveInfinity : timing.Lifetime;
        bool looping = timing.Looping && double.IsFinite(span) && span + timing.LoopDelay > 0;
        state.BurstDone = !looping && now >= timing.StartDelay + Math.Max(
            timing.Burst > 0 ? timing.Spread * (timing.Burst - 1d) / timing.Burst : 0,
            timing.Rate > 0 ? span : 0);
        double cycleLength = looping ? span + timing.LoopDelay : double.PositiveInfinity;
        double current = looping ? Math.Floor((now - timing.StartDelay) / cycleLength) : 0;
        double first = looping ? Math.Max(0, current - 32) : 0;
        int capacity = Math.Min(32, available);
        void Add(double birth)
        {
            double age = now - birth;
            if (age < 0 || age >= life || births.Count == capacity && birth < births[0]) return;
            int index = births.BinarySearch(birth);
            births.Insert(index < 0 ? ~index : index, birth);
            if (births.Count > capacity) births.RemoveAt(0);
        }
        int cycles = (int)Math.Min(33, current - first + 1);
        for (int offset = 0; offset < cycles; offset++)
        {
            double start = timing.StartDelay + (looping ? (first + offset) * cycleLength : 0);
            int lastBurst = timing.Burst - 1;
            if (timing.Spread > 0 && timing.Burst > 1)
                lastBurst = (int)Math.Min(lastBurst, Math.Floor((now - start) * timing.Burst / timing.Spread));
            for (int back = Math.Min(capacity - 1, lastBurst); back >= 0; back--)
                Add(start + (timing.Burst > 1 ? (double)timing.Spread * (lastBurst - back) / timing.Burst : 0));
            if (timing.Rate <= 0 || !float.IsFinite(timing.Rate)) continue;
            double spawning = Math.Min(now - start, span);
            double count = Math.Floor(spawning * timing.Rate);
            for (int index = 0; index <= capacity; index++)
            {
                double ordinal = Math.Max(0, count - capacity) + index;
                if (ordinal > count) break;
                Add(start + ordinal / timing.Rate);
            }
        }

        var savedRandom = _rng;
        uint savedSerial = _particleSerial;
        try
        {
            _rng = VfxLtkRandom.FromState(unchecked((uint)(_seed + state.SourceOrder)));
            _particleSerial = 0;
            state.Spawned = 0;
            Matrix4x4 placement = EmitterPlacement(definition);
            foreach (double birth in births)
            {
                float age = (float)(now - birth);
                Spawn(state, 0f, placement.Translation, age);
                int index = state.Particles.Count - 1;
                var particle = state.Particles[index];
                particle.LocalPosition += (definition.BirthVelocity?.Constant ?? Vector3.Zero) * age +
                    (definition.BirthAcceleration?.Constant ?? Vector3.Zero) * (0.5f * age * age);
                particle.Placed = MatrixTranslation(particle, state);
                particle.Pos = Vector3.TransformNormal(particle.Placed, particle.BirthFrame) + particle.BirthAnchor;
                state.Particles[index] = particle;
            }
        }
        finally
        {
            _rng = savedRandom;
            _particleSerial = savedSerial;
        }
        state.InvalidateInstances();
    }
}

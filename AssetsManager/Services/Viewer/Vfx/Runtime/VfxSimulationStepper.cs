using System;
using System.Collections.Generic;
using System.Collections;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime;

internal sealed class VfxSimulationStepper
{
    internal double Bank { get; set; }

    internal Steps Advance(float frameTime, uint flags, VfxEffectsQuality quality)
    {
        if ((flags & 8) != 0)
        {
            return new(frameTime > 0f ? 1 : 0, frameTime);
        }
        if (frameTime > 0f) Bank += frameTime;
        int rate = VfxQuality.StepRate(quality);
        double count = Math.Floor(Bank * rate);
        if (count == 0 && (flags & 0x40) != 0 && Bank > 0)
        {
            float shortStep = (float)Bank;
            Bank = 0;
            return new(1, shortStep);
        }
        double dt = 1d / rate;
        while (count > 20) { count *= .5; dt *= 2; }
        int steps = (int)count;
        Bank -= steps * dt;
        return new(steps, (float)dt);
    }

    internal readonly struct Steps(int count, float dt) : IReadOnlyList<float>
    {
        public int Count => count;
        public float this[int index] => index >= 0 && index < count ? dt : throw new ArgumentOutOfRangeException(nameof(index));
        public Enumerator GetEnumerator() => new(count, dt);
        IEnumerator<float> IEnumerable<float>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        internal struct Enumerator(int count, float dt) : IEnumerator<float>
        {
            private int _at = -1;
            public float Current => dt;
            object IEnumerator.Current => Current;
            public bool MoveNext() => ++_at < count;
            public void Reset() => _at = -1;
            public void Dispose() { }
        }
    }
}

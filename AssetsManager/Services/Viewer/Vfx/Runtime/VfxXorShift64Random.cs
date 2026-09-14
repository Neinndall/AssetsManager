using System;
using System.Runtime.CompilerServices;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>
    /// Deterministic 64-bit XorShift pseudo-random number generator.
    /// Matches the deterministic RNG behavior of Riot Games' VFX simulation engine
    /// (equivalent to Rand_UnitFloat), enabling 100% reproducible particle simulations
    /// and precise timeline scrubbing/rewinding without drift.
    /// </summary>
    public class VfxXorShift64Random : Random
    {
        private const ulong DefaultNonZero = 0x9e3779b97f4a7c15UL;
        private ulong _state;

        public VfxXorShift64Random(ulong seed = 1234UL)
        {
            _state = seed == 0 ? DefaultNonZero : seed;
        }

        public VfxXorShift64Random(int seed) : this(seed == 0 ? DefaultNonZero : (ulong)(uint)seed)
        {
        }

        public ulong State
        {
            get => _state;
            set => _state = value == 0 ? DefaultNonZero : value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong NextUInt64()
        {
            ulong x = _state;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _state = x;
            return x;
        }

        /// <summary>
        /// Generates the next random float in [0.0f, 1.0f).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextUnitFloat()
        {
            return (float)((NextUInt64() >> 40) * (1.0 / (1U << 24)));
        }

        /// <summary>
        /// Generates the next random float in [lo, hi).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Range(float lo, float hi)
        {
            return lo + (hi - lo) * NextUnitFloat();
        }

        /// <summary>
        /// Generates a signed unit float in [-1.0f, 1.0f).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextSignedUnitFloat()
        {
            return NextUnitFloat() * 2f - 1f;
        }

        public override double NextDouble()
        {
            return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
        }

        public override int Next()
        {
            return (int)(NextUInt64() & 0x7FFFFFFFUL);
        }

        public override int Next(int maxValue)
        {
            if (maxValue <= 0) return 0;
            return (int)(NextDouble() * maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue) return minValue;
            return minValue + (int)(NextDouble() * (maxValue - minValue));
        }

        public override void NextBytes(byte[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            int i = 0;
            while (i < buffer.Length)
            {
                ulong value = NextUInt64();
                for (int b = 0; b < 8 && i < buffer.Length; b++)
                {
                    buffer[i++] = (byte)(value & 0xFF);
                    value >>= 8;
                }
            }
        }

        public override void NextBytes(Span<byte> buffer)
        {
            int i = 0;
            while (i < buffer.Length)
            {
                ulong value = NextUInt64();
                for (int b = 0; b < 8 && i < buffer.Length; b++)
                {
                    buffer[i++] = (byte)(value & 0xFF);
                    value >>= 8;
                }
            }
        }

        public VfxXorShift64Random Clone()
        {
            var clone = new VfxXorShift64Random(DefaultNonZero)
            {
                _state = _state
            };
            return clone;
        }
    }
}

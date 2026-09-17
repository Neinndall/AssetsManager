using System;
using System.Runtime.CompilerServices;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>
    /// Deterministic RNG used by LTK's VFX viewport. LTK intentionally uses Marsaglia's
    /// 32-bit xorshift stream for stable playback/scrubbing rather than claiming the exact
    /// private in-game Rand_UnitFloat stream.
    /// </summary>
    public sealed class VfxLtkRandom : Random
    {
        private const uint DefaultNonZero = 0x9e3779b9u;
        private const float Unit = 1f / 0x1000000;
        private uint _state;

        public VfxLtkRandom(uint seed = 1234u)
        {
            _state = seed == 0 ? DefaultNonZero : seed;
        }

        public VfxLtkRandom(ulong seed) : this(unchecked((uint)seed))
        {
        }

        public VfxLtkRandom(int seed) : this(unchecked((uint)seed))
        {
        }

        public uint State
        {
            get => _state;
            set => _state = value == 0 ? DefaultNonZero : value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint NextUInt32()
        {
            uint state = _state;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            _state = state;
            return state;
        }

        /// <summary>The exact [0,1) conversion used by LTK's Rng.unitFloat().</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextUnitFloat()
            => (NextUInt32() >> 8) * Unit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Range(float lo, float hi)
            => lo + (hi - lo) * NextUnitFloat();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextSignedUnitFloat()
            => NextUnitFloat() * 2f - 1f;

        // VFX code samples System.Random through NextDouble in a few curve helpers. Make
        // that consume the same single LTK draw instead of introducing a second RNG stream.
        public override double NextDouble() => NextUnitFloat();

        public override int Next()
            => (int)(NextUInt32() & 0x7fffffffu);

        public override int Next(int maxValue)
        {
            if (maxValue <= 0) return 0;
            return (int)(NextUnitFloat() * maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue) return minValue;
            return minValue + (int)(NextUnitFloat() * (maxValue - minValue));
        }

        public override void NextBytes(byte[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            NextBytes(buffer.AsSpan());
        }

        public override void NextBytes(Span<byte> buffer)
        {
            int index = 0;
            while (index < buffer.Length)
            {
                uint value = NextUInt32();
                for (int byteIndex = 0; byteIndex < 4 && index < buffer.Length; byteIndex++)
                {
                    buffer[index++] = (byte)(value & 0xffu);
                    value >>= 8;
                }
            }
        }

        public VfxLtkRandom Clone()
        {
            var clone = new VfxLtkRandom(DefaultNonZero)
            {
                _state = _state
            };
            return clone;
        }
    }
}

using System;

namespace AssetsManager.Services.Hashes
{
    /// <summary>
    /// FNV-1a 32-bit helpers for incremental hashing. A running hash can be
    /// extended chunk by chunk, and because the multiplier is odd it has a
    /// multiplicative inverse mod 2^32, so a trailing chunk can be stripped
    /// from a hash once and the remaining state reused across candidates.
    /// </summary>
    internal static class Fnv1aIncremental
    {
        internal const uint Offset = 0x811c9dc5;
        internal const uint Prime = 0x01000193;
        // Prime is odd, so it is a unit mod 2^32; this is its inverse.
        private const uint PrimeInverse = 0x359c449b;

        internal static uint Append(uint hash, ReadOnlySpan<byte> bytes)
        {
            foreach (byte raw in bytes)
            {
                byte b = raw is >= (byte)'A' and <= (byte)'Z' ? (byte)(raw + 32) : raw;
                hash = unchecked((hash ^ b) * Prime);
            }
            return hash;
        }

        internal static uint AppendWord(uint hash, string word)
        {
            ReadOnlySpan<byte> bytes = System.Text.Encoding.UTF8.GetBytes(word);
            return Append(hash, bytes);
        }

        /// <summary>Given h == fnv1a(Append(x, data)), returns x.</summary>
        internal static uint Rewind(uint hash, ReadOnlySpan<byte> bytes)
        {
            for (int index = bytes.Length - 1; index >= 0; index--)
            {
                byte b = bytes[index] is >= (byte)'A' and <= (byte)'Z' ? (byte)(bytes[index] + 32) : bytes[index];
                hash = unchecked(hash * PrimeInverse ^ b);
            }
            return hash;
        }

        internal static uint Hash(string value)
        {
            ReadOnlySpan<byte> bytes = System.Text.Encoding.UTF8.GetBytes(value);
            return Append(Offset, bytes);
        }
    }
}

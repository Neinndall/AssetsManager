using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using AssetsManager.Tests.Diagnostics.Hashes;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameTailRecoveryTests
    {
        [Fact]
        public void RecoversEveryAlignedTailWordAcrossStripeAndRemainderLengths()
        {
            var random = new Random(1729);
            for (int length = 8; length < 256; length++)
            {
                byte[] original = new byte[length];
                random.NextBytes(original);
                ulong hash = XxHash64.HashToUInt64(original);
                for (int offset = length / 32 * 32; offset + 8 <= length; offset += 8)
                {
                    byte[] template = (byte[])original.Clone();
                    template.AsSpan(offset, 8).Clear();
                    ulong value = GameTailRecoveryDiagnostic.Recover(template, hash, offset);
                    Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(offset)), value);
                    BinaryPrimitives.WriteUInt64LittleEndian(template.AsSpan(offset), value);
                    Assert.Equal(hash, XxHash64.HashToUInt64(template));
                }
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        [InlineData(40)]
        public void RejectsOffsetsOutsideAlignedEightByteTailWords(int offset)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GameTailRecoveryDiagnostic.Recover(new byte[40], 0, offset));
        }
    }
}

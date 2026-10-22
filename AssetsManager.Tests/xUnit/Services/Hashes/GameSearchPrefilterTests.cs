using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes.Guessers.Game;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameSearchPrefilterTests
{
    [Fact]
    public void SharedBinProbePreservesLegacySelectionIncludingOverlappingAndTruncatedFields()
    {
        const uint field = 0x01010101;
        var unknown = new HashSet<ulong> { 0xabcdef1234567890 };
        var random = new Random(724);
        for (int length = 0; length < 160; length++)
        for (int sample = 0; sample < 16; sample++)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            if (length >= 4)
            {
                int offset = random.Next(length - 3);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), field);
                if (length - offset >= 13 && sample % 2 == 0)
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 5), unknown.First());
            }
            if (sample == 15) Array.Fill(data, (byte)1);
            Assert.Equal(LegacyProbe(data, field, unknown),
                GameBinLinkProbe.ContainsPendingLink(data, field, unknown, CancellationToken.None));
        }
    }

    [Fact]
    public void BinProbeRetainsHeuristicTypeHandlingAndCancellation()
    {
        byte[] data = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 42);
        data[4] = 255; // The parsed BIN validates the type; the prefilter must not narrow coverage.
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(5), 123);
        var unknown = new HashSet<ulong> { 123 };
        Assert.True(GameBinLinkProbe.ContainsPendingLink(data, 42, unknown, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() =>
            GameBinLinkProbe.ContainsPendingLink(data, 42, unknown, new CancellationToken(true)));
    }

    [Fact]
    public void IndexedContainersPreserveLegacySelectionAcrossOwnersAliasesAndNestedLayouts()
    {
        var containers = new HashSet<(string Character, string Container)>
        {
            ("owner", "base"), ("owner", "skin01"), ("owner", "t1"),
            ("other", "skin0"), ("third", "skin1"), ("OWNER", "skin2")
        };
        var index = new GameAnimationContainerIndex(containers);
        string[] components = { "owner", "other", "third", "OWNER", "unknown", "base", "skin0", "skin00", "skin1", "skin01", "skin2", "t1", "skins", "themes" };
        var random = new Random(725);
        for (int sample = 0; sample < 20000; sample++)
        {
            var folders = Enumerable.Range(0, random.Next(1, 10))
                .Select(_ => components[random.Next(components.Length)]).ToArray();
            if (folders.Length > 1) folders[1] = sample % 4 == 0 ? "other" : "characters";
            bool expected = folders.Length >= 5 && folders[1] == "characters" &&
                containers.Any(container => container.Character == folders[2] &&
                    (folders.Skip(3).Contains(container.Container) ||
                     container.Container == "base" && folders.Skip(3).Contains("skin0")));
            Assert.Equal(expected, index.Contains(folders));
        }
    }

    private static bool LegacyProbe(ReadOnlySpan<byte> data, uint field, HashSet<ulong> unknown)
    {
        byte[] fieldBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(fieldBytes, field);
        while (data.Length >= 13)
        {
            int offset = data.IndexOf(fieldBytes);
            if (offset < 0 || data.Length - offset < 13) break;
            if (unknown.Contains(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 5, 8)))) return true;
            data = data[(offset + 1)..];
        }
        return false;
    }
}

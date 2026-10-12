using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class BinTraversalTests
{
    private static uint Hash(string value) => Fnv1a.HashLower(value);
    private static Dictionary<InternalHashKind, HashSet<ulong>> Targets() =>
        Enum.GetValues<InternalHashKind>().ToDictionary(kind => kind, _ => new HashSet<ulong>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializedNestedValuesKeepHashDomainsAndVisitMapKeys(bool unordered)
    {
        const string key = "NestedKey", name = "NestedName";
        const ulong wide = 0xfedcba9876543210, wad = 0xabcdef1234567890;
        BinTreeProperty[] elements = { new BinTreeOptional(0, new BinTreeString(0, name)) };
        BinTreeContainer strings = unordered
            ? new BinTreeUnorderedContainer(20, BinPropertyType.Optional, elements)
            : new BinTreeContainer(20, BinPropertyType.Optional, elements);
        var map = new BinTreeMap(10, BinPropertyType.String, BinPropertyType.Embedded,
            new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, key),
                new BinTreeEmbedded(0, 0xff9d3409, new BinTreeProperty[]
                {
                    strings, new BinTreeHash(21, Hash(key)), new BinTreeHash(22, Hash(name)),
                    new BinTreeObjectLink(23, 300), new BinTreeHash64(0x8d39bde6, wide), new BinTreeWadChunkLink(25, wad)
                })) });
        var tree = new BinTree(new[] { new BinTreeObject(100, 101, new BinTreeProperty[] { map }) }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        stream.Position = 0;
        var parsed = new BinTree(stream);
        var observed = Targets();
        BinRstHashGuessingService.ReadBinInventory(parsed, observed);
        Assert.True(observed[InternalHashKind.BinEntries].SetEquals(new ulong[] { 100, 300 }));
        Assert.True(observed[InternalHashKind.BinTypes].SetEquals(new ulong[] { 101, 0xff9d3409 }));
        Assert.True(observed[InternalHashKind.BinFields].SetEquals(new ulong[] { 10, 20, 21, 22, 23, 0x8d39bde6, 25 }));
        Assert.True(observed[InternalHashKind.BinHashes].SetEquals(new ulong[] { Hash(key), Hash(name) }));
        Assert.Equal(wide, Assert.Single(observed[InternalHashKind.BinXxh3]));
        Assert.DoesNotContain(wad, observed.Values.SelectMany(values => values));
        var matcher = new InternalHashEvidenceMatcher(new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinHashes] = observed[InternalHashKind.BinHashes].ToHashSet()
        });
        BinContentEvidenceSource.MatchBinContentEvidence(parsed, matcher, "nested.bin",
            selectedSubMethods: new HashSet<string> { "bin-context-strings" });
        Assert.Equal(0, matcher.Remaining);
        Assert.All(matcher.Matches, match => Assert.True(match.CanPromote));
    }

    [Fact]
    public void PatchOverridesSupplyHashesAndStringsWithoutRootEntries()
    {
        const string name = "OverrideName";
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("PTCH"u8); writer.Write(1U); writer.Write(0U);
        writer.Write("PROP"u8); writer.Write(3U); writer.Write(0U); writer.Write(0U);
        writer.Write(2U);
        WriteHeader(BinPropertyType.Hash, "hash", 4);
        writer.Write(Hash(name));
        WriteHeader(BinPropertyType.String, "name", 2 + name.Length);
        writer.Write((ushort)name.Length); writer.Write(Encoding.ASCII.GetBytes(name));
        stream.Position = 0;
        var tree = new BinTree(stream);
        var observed = Targets();
        BinRstHashGuessingService.ReadBinInventory(tree, observed);
        Assert.Equal(42UL, Assert.Single(observed[InternalHashKind.BinEntries]));
        Assert.Equal((ulong)Hash(name), Assert.Single(observed[InternalHashKind.BinHashes]));
        var matcher = new InternalHashEvidenceMatcher(observed);
        BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "patch.bin",
            selectedSubMethods: new HashSet<string> { "bin-context-strings" });
        Assert.Equal(name, Assert.Single(matcher.Matches).Value);

        void WriteHeader(BinPropertyType type, string path, int size)
        {
            writer.Write(42U); writer.Write((uint)(1 + 2 + path.Length + size));
            writer.Write((byte)type); writer.Write((ushort)path.Length); writer.Write(Encoding.ASCII.GetBytes(path));
        }
    }

    [Fact]
    public void StringTraversalStopsWhenCancellationArrivesDuringNestedMatching()
    {
        using var cancellation = new CancellationTokenSource();
        const string first = "FirstNested", second = "SecondNested";
        var tree = new BinTree(new[] { new BinTreeObject(1, 2, new BinTreeProperty[]
        {
            new BinTreeEmbedded(3, 4, new BinTreeProperty[]
            {
                new BinTreeHash(5, Hash(first)), new BinTreeHash(6, Hash(second)),
                new BinTreeString(7, first), new BinTreeString(8, second)
            })
        }) }, Array.Empty<string>());
        var matcher = new InternalHashEvidenceMatcher(new Dictionary<InternalHashKind, HashSet<ulong>>
        {
            [InternalHashKind.BinHashes] = new() { Hash(first), Hash(second) }
        }, _ => cancellation.Cancel());
        Assert.Throws<OperationCanceledException>(() => BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher,
            "nested.bin", selectedSubMethods: new HashSet<string> { "bin-context-strings" }, cancellationToken: cancellation.Token));
        Assert.Equal(first, Assert.Single(matcher.Matches).Value);
        Assert.True(matcher.IsRemaining(InternalHashKind.BinHashes, Hash(second)));
    }
}

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

public sealed class BinHashDocumentReaderTests
{
    [Fact]
    public void RecoveryKeepsEntriesOnBothSidesAndTheirLocalEvidence()
    {
        using var stream = new MemoryStream(DamagedBin());
        Assert.Throws<InvalidPropertyTypeException>(() => new BinTree(stream));
        stream.Position = 0;
        var messages = new List<string>();
        var tree = BinHashDocumentReader.Read(stream, messages.Add);
        Assert.Equal(new uint[] { 1, 3 }, tree.Objects.Keys);
        Assert.Equal(new[] { "data/shared.bin" }, tree.Dependencies);
        Assert.Contains(messages, message => message.Contains("Skipped entry 1"));
        Assert.Contains(messages, message => message.Contains("Recovered 2/3"));
        var targets = Enum.GetValues<InternalHashKind>().ToDictionary(kind => kind, _ => new HashSet<ulong>());
        BinRstHashGuessingService.ReadBinInventory(tree, targets);
        Assert.DoesNotContain(2UL, targets[InternalHashKind.BinEntries]);
        var matcher = new InternalHashEvidenceMatcher(targets);
        BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin",
            selectedSubMethods: new HashSet<string> { "bin-context-strings" });
        Assert.Contains(matcher.Matches, match => match.Value == "FirstName" && match.CanPromote);
        Assert.Contains(matcher.Matches, match => match.Value == "LastName" && match.CanPromote);
    }

    [Fact]
    public void InvalidBoundaryStopsRecoveryWithoutSearchingForAnotherEntry()
    {
        byte[] broken = Entry(2, "BadName");
        broken[14] = 0xff;
        BitConverter.GetBytes(uint.MaxValue).CopyTo(broken, 0);
        using var stream = new MemoryStream(Document(new[] { Entry(1, "FirstName"), broken, Entry(3, "LastName") }));
        var messages = new List<string>();
        var tree = BinHashDocumentReader.Read(stream, messages.Add);
        Assert.Equal(1u, Assert.Single(tree.Objects).Key);
        Assert.Contains(messages, message => message.Contains("invalid or truncated boundary"));
    }

    [Fact]
    public void RecoveryKeepsValidPatchOverridesAfterAnInvalidEntryAndOverride()
    {
        byte[] bad = Entry(2, "BadName"); bad[14] = 0xff;
        byte[] badOverride = Override(7, (BinPropertyType)0xff, Array.Empty<byte>());
        using var value = new MemoryStream();
        using (var writer = new BinaryWriter(value, Encoding.UTF8, true)) writer.Write(123u);
        byte[] valid = Override(8, BinPropertyType.Hash, value.ToArray());
        using var stream = new MemoryStream(Document(new[] { Entry(1, "FirstName"), bad }, new[] { valid, badOverride, valid }));
        var messages = new List<string>();
        var tree = BinHashDocumentReader.Read(stream, messages.Add);
        Assert.True(tree.IsOverride);
        Assert.Single(tree.Objects);
        Assert.Equal(2, tree.DataOverrides.Count);
        Assert.All(tree.DataOverrides, item => Assert.Equal(123u, Assert.IsType<BinTreeHash>(item.Property).Value));
        Assert.Contains(messages, message => message.Contains("Skipped patch override 1"));
    }

    [Fact]
    public void CancellationDuringRecoveryIsNotSwallowed()
    {
        using var stream = new MemoryStream(DamagedBin());
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => BinHashDocumentReader.Read(stream,
            _ => cancellation.Cancel(), cancellation.Token));
    }

    [Fact]
    public void LegacyEntriesKeepTheExistingTypeInterpretationDuringRecovery()
    {
        var legacy = new BinTreeObject(5, 100, new BinTreeProperty[]
        {
            new BinTreeStruct(11, 200, new BinTreeProperty[] { new BinTreeString(12, "LegacyName") })
        });
        using var encoded = new MemoryStream();
        new BinTree(new[] { legacy }, Array.Empty<string>()).Write(encoded);
        byte[] entry = encoded.ToArray()[20..];
        entry[14] = 19; // Legacy Struct tag before WadChunkLink was introduced.
        byte[] broken = Entry(2, "BadName"); broken[14] = 0xff;
        using var stream = new MemoryStream(Document(new[] { broken, entry }));
        var recovered = BinHashDocumentReader.Read(stream, _ => { });
        Assert.Equal(legacy, Assert.Single(recovered.Objects).Value);
        using var intact = new MemoryStream(Document(new[] { entry }));
        var messages = new List<string>();
        Assert.Equal(legacy, Assert.Single(BinHashDocumentReader.Read(intact, messages.Add).Objects).Value);
        Assert.Empty(messages);
    }

    [Fact]
    public void UnreadableHeaderIsRejected()
    {
        using var stream = new MemoryStream("PROP"u8.ToArray());
        Assert.Throws<EndOfStreamException>(() => BinHashDocumentReader.Read(stream, null));
    }

    [Fact]
    public void ValidFilesUseTheExistingParserWithoutRecoveryMessages()
    {
        byte[] bytes = Document(new[] { Entry(1, "FirstName"), Entry(3, "LastName") });
        using var expectedStream = new MemoryStream(bytes);
        using var stream = new MemoryStream(bytes);
        var expected = new BinTree(expectedStream);
        var messages = new List<string>();
        var actual = BinHashDocumentReader.Read(stream, messages.Add);
        Assert.Equal(expected.Objects.Values, actual.Objects.Values);
        Assert.Empty(messages);
    }

    internal static byte[] DamagedBin()
    {
        byte[] broken = Entry(2, "BadName"); broken[14] = 0xff;
        return Document(new[] { Entry(1, "FirstName"), broken, Entry(3, "LastName") });
    }

    private static byte[] Entry(uint hash, string name)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(0u); writer.Write(hash); writer.Write((ushort)2);
            writer.Write(11u); writer.Write((byte)BinPropertyType.String);
            writer.Write((ushort)name.Length); writer.Write(Encoding.UTF8.GetBytes(name));
            writer.Write(12u); writer.Write((byte)BinPropertyType.Hash); writer.Write(Fnv1a.HashLower(name));
            stream.Position = 0; writer.Write((uint)stream.Length - 4);
        }
        return stream.ToArray();
    }

    private static byte[] Override(uint hash, BinPropertyType type, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(hash); writer.Write((uint)(4 + payload.Length));
        writer.Write((byte)type); writer.Write((ushort)1); writer.Write((byte)'x'); writer.Write(payload);
        return stream.ToArray();
    }

    private static byte[] Document(byte[][] entries, byte[][] overrides = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (overrides != null) { writer.Write("PTCH"u8); writer.Write(1u); writer.Write(0u); }
        writer.Write("PROP"u8); writer.Write(3u); writer.Write(1u);
        byte[] dependency = Encoding.ASCII.GetBytes("data/shared.bin");
        writer.Write((ushort)dependency.Length); writer.Write(dependency);
        writer.Write((uint)entries.Length);
        foreach (byte[] entry in entries) writer.Write(100u);
        foreach (byte[] entry in entries) writer.Write(entry);
        if (overrides != null)
        {
            writer.Write((uint)overrides.Length);
            foreach (byte[] value in overrides) writer.Write(value);
        }
        return stream.ToArray();
    }
}

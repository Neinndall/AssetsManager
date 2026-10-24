using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameSpellSlotAnimationTests
{
    [Theory]
    [InlineData("spell4_self", "spell1_self")]
    [InlineData("spell4_self", "spell2_self")]
    [InlineData("spell4_self", "spell3_self")]
    [InlineData("spell4_self", "spell4_self")]
    [InlineData("spell04_self", "spell03_self")]
    public void GrepResolvesHashedClipLabelsFromOtherSpellSlots(string seed, string action)
    {
        string target = $"assets/characters/target/skins/skin67/animations/{action}.anm";
        var engine = Engine(target);
        var guesser = Guesser(seed);
        guesser.GrepWad(engine, Payload(action, target), "data/characters/target/animations/skin67.bin",
            "Target.wad.client", 123, CancellationToken.None);
        var match = Assert.Single(engine.Matches.Values);
        Assert.Equal(target, match.Path);
        Assert.Equal(123UL, match.SourceChunkHash);
    }

    [Fact]
    public void ARecognizedLabelStillRequiresTheExactFileHash()
    {
        const string unrelated = "assets/characters/target/skins/skin67/animations/spell99_self.anm";
        var engine = Engine(unrelated);
        Guesser("spell4_self").GrepWad(engine, Payload("spell3_self", unrelated),
            "data/characters/target/animations/skin67.bin", "target", 1, CancellationToken.None);
        Assert.Empty(engine.Matches);
        Assert.Equal(1, engine.RemainingUnknownCount);
    }

    private static GameHashGuesser Guesser(string seed) => new(new HashFile(HashGuessDomain.Game,
        new[] { $"assets/characters/teacher/skins/base/animations/teacher_{seed}.anm" }));
    private static HashGuessEngine Engine(string path) => new(HashGuessDomain.Game, new[] { XxHash64Ext.Hash(path) }.ToHashSet());

    private static ArraySegment<byte> Payload(string action, string target)
    {
        var resource = new BinTreeStruct(Fnv1a.HashLower("mAnimationResourceData"), Fnv1a.HashLower("AnimationResourceData"),
            new BinTreeProperty[] { new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), XxHash64Ext.Hash(target)) });
        var clip = new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new BinTreeProperty[] { resource });
        var map = new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct,
            new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Fnv1a.HashLower(action)), clip) });
        var tree = new BinTree(new[] { new BinTreeObject(123, Fnv1a.HashLower("animationGraphData"), new BinTreeProperty[] { map }) }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        return new(stream.ToArray());
    }
}

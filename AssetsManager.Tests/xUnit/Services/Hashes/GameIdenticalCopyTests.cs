using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameIdenticalCopyTests
{
    [Theory]
    [InlineData("Companions", false)]
    [InlineData("Map22", false)]
    [InlineData("example", false)]
    [InlineData("Map22", true)]
    public void IdenticalCopiesUseActualContainerFoldersAndAllDistinctCopyNames(string container, bool skinDefinitionOnly)
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("Game");
        byte[] shared = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        string[] copies = Enumerable.Range(0, 80).Select(i => $"assets/characters/source/skins/skin0/copy{i}.tex").ToArray();
        string sourceWad = Path.Combine(root, "source.wad.client");
        WadBuilder.Bake(copies.Select(path => new WadBakeEntry(path, () => new MemoryStream(shared), WadChunkCompression.None)), sourceWad, new WadBakeSettings());
        string source;
        using (var wad = new WadFile(sourceWad))
            source = copies.Single(path => XxHash64Ext.Hash(path) == wad.Chunks.Keys.Last());
        string seed = skinDefinitionOnly ? "data/characters/example/skins/skin7.bin" : "assets/characters/example/skins/skin7/seed.tex";
        string expected = $"assets/characters/example/skins/skin7/{Path.GetFileName(source)}";
        WadBuilder.Bake(new[]
        {
            new WadBakeEntry(seed, () => new MemoryStream(new byte[40]), WadChunkCompression.None),
            new WadBakeEntry(expected, () => new MemoryStream(shared), WadChunkCompression.None)
        }, Path.Combine(root, container + ".wad.client"), new WadBakeSettings());
        var catalog = copies.Append(seed).ToArray();
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, catalog));
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(expected), 42 });
        guesser.GuessIdenticalCopies(engine, bridge.RootPath, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(engine.Matches).Value.Path);
        Assert.Contains(42UL, engine.UnknownHashes);
        Assert.Equal(HashGuessStrategy.IdenticalContentCopy, Assert.Single(engine.Matches).Value.Strategy);
    }

    [Fact]
    public void CharacterFoldersFromCatalogStillWorkWithoutNamedLocalTextures()
    {
        using var bridge = new AssetsManagerTestBridge();
        string root = bridge.CreateDirectory("Game");
        byte[] shared = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        const string source = "assets/characters/source/skins/base/borrowed.tex";
        const string expected = "assets/characters/example/skins/skin7/borrowed.tex";
        WadBuilder.Bake(new[] { new WadBakeEntry(source, () => new MemoryStream(shared), WadChunkCompression.None) },
            Path.Combine(root, "source.wad.client"), new WadBakeSettings());
        WadBuilder.Bake(new[] { new WadBakeEntry(expected, () => new MemoryStream(shared), WadChunkCompression.None) },
            Path.Combine(root, "example.wad.client"), new WadBakeSettings());
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { source, "assets/characters/example/skins/skin7/not-installed.tex" }));
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(expected), 42 });
        guesser.GuessIdenticalCopies(engine, bridge.RootPath, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(engine.Matches).Value.Path);
        Assert.Contains(42UL, engine.UnknownHashes);
    }
}

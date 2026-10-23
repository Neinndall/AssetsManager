using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameMapGeometrySiblingTests
{
    [Fact]
    public void FindsNavigationRegionsWithoutAnExistingNavigationRegionSeed()
    {
        const string source = "data/maps/mapgeometry/map22/arena.mapgeo";
        const string expected = "data/maps/mapgeometry/map22/arena.navregions";
        var game = Guesser(source);
        var engine = Engine(expected);
        Assert.Equal(1, game.GuessMapGeometrySiblings(engine, CancellationToken.None));
        Assert.Equal(expected, Assert.Single(engine.Matches.Values).Path);
        Assert.Empty(engine.UnknownHashes);
    }

    [Fact]
    public void OnlyUsesMapGeometryFilesAndConfirmsExactHashes()
    {
        var game = Guesser("data/maps/mapgeometry/map11/base.mapgeo", "data/maps/mapgeometry/map11/base.bin",
            "assets/characters/test/test.mapgeo", "data/maps/mapgeometry/map11/other.mapgeo.backup");
        var engine = Engine("data/maps/mapgeometry/map11/base.navregion", "assets/characters/test/test.navregions");
        Assert.Equal(1, game.GuessMapGeometrySiblings(engine, CancellationToken.None));
        Assert.Empty(engine.Matches);
        Assert.Equal(2, engine.RemainingUnknownCount);
    }

    [Fact]
    public void HonorsCandidateBudgetAndCancellation()
    {
        var game = Guesser("data/maps/mapgeometry/map11/a.mapgeo", "data/maps/mapgeometry/map11/b.mapgeo");
        const string target = "data/maps/mapgeometry/map11/b.navregions";
        var engine = Engine(target);
        Assert.Equal(0, game.GuessMapGeometrySiblings(engine, CancellationToken.None, 0));
        Assert.Equal(1, game.GuessMapGeometrySiblings(engine, CancellationToken.None, 1));
        Assert.Empty(engine.Matches);
        game.GuessMapGeometrySiblings(engine, CancellationToken.None);
        Assert.Single(engine.Matches);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => game.GuessMapGeometrySiblings(Engine(target), cancellation.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BasicOnlyRunsMapSiblingRuleWhenExtensionSubmethodIsSelected(bool selected)
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        const string geometry = "data/maps/mapgeometry/map12/base.mapgeo";
        const string target = "data/maps/mapgeometry/map12/base.navregions";
        await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
            $"{XxHash64Ext.Hash(geometry):x16} {geometry}\n");
        var store = new HashGuessingStore(bridge.Directories);
        var pending = new HashSet<ulong> { XxHash64Ext.Hash(target) };
        await store.SaveUnknownHashesAsync(HashGuessDomain.Game, pending, pending, "test", CancellationToken.None);
        using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
        var service = new HashGuessingService(resolver, store,
            new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories)), bridge.LogService, bridge.Directories);
        var result = await service.RunGameBasicGuessingAsync(bridge.RootPath, null, CancellationToken.None,
            selectedSubMethods: selected ? new HashSet<string> { "game-basic-extensions" } : new HashSet<string>());
        Assert.Equal(selected ? new[] { target } : Array.Empty<string>(), result.Matches.Select(match => match.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".mapgeo")]
    [InlineData(".materials.bin")]
    public void EmbeddedGeometryReferenceResolvesNavigationSdfInfo(string extension)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            byte[] path = Encoding.ASCII.GetBytes("Maps/MapGeometry/Map22/Base" + extension);
            writer.Write((ushort)path.Length);
            writer.Write(path);
        }
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 0x00f6663d8b8ceb61 });
        new GameHashGuesser().GrepWad(engine, new ArraySegment<byte>(stream.ToArray()),
            "data/maps/example.inibin", "Map22.wad.client", 456, CancellationToken.None);

        var match = Assert.Single(engine.Matches.Values);
        Assert.Equal("data/maps/mapgeometry/map22/base.navsdfinfo", match.Path);
        Assert.Equal("Map22.wad.client", match.SourceWadPath);
        Assert.Equal(456UL, match.SourceChunkHash);
    }

    private static GameHashGuesser Guesser(params string[] paths) => new(new HashFile(HashGuessDomain.Game, paths));
    private static HashGuessEngine Engine(params string[] paths) => new(HashGuessDomain.Game, paths.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
}

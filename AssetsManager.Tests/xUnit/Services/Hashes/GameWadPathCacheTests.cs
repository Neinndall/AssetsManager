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
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameWadPathCacheTests
{
    [Fact]
    public void CombinedCustomRunMatchesTheSameMethodsExecutedSeparately()
    {
        using var bridge = new AssetsManagerTestBridge();
        const string folder = "assets/characters/example/";
        string[] seeds = { folder + "animations/joke_loop.anm", folder + "animations/idle_sad.anm",
            folder + "particles/joke_loop.tex", folder + "particles/idle_sad.tex" };
        string[] targets = { folder + "animations/joke_sad_loop.anm", folder + "particles/joke_sad_loop.tex" };
        bridge.BakeWad(bridge.RootPath, "test.wad.client", seeds.Concat(targets).Select(path => (path, "body")).ToArray());
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, seeds));
        HashGuessEngine Engine() => new(HashGuessDomain.Game, targets.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet());
        var combined = Engine();
        var separate = Engine();
        var methods = new[] { "game-custom-animations", "game-custom-textures" };
        int together = guesser.RunCustomAttacks(combined, null, CancellationToken.None, methods.ToHashSet(), bridge.RootPath);
        int individually = 0;
        foreach (string method in methods)
            individually += guesser.RunCustomAttacks(separate, null, CancellationToken.None, new HashSet<string> { method }, bridge.RootPath);
        Assert.Equal(individually, together);
        Assert.Equal(separate.CheckedCandidates, combined.CheckedCandidates);
        Assert.Equal(separate.Matches.Values.Select(m => (m.Hash, m.Path, m.SourceWadPath)),
            combined.Matches.Values.Select(m => (m.Hash, m.Path, m.SourceWadPath)));
        Assert.Equal(2, combined.Matches.Count);
        Assert.Equal(new[] { 42UL }, combined.UnknownHashes);
    }

    [Fact]
    public void ReusesHeaderOrderWithoutKeepingTheFileOpenAndHonorsCancellationOnHits()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = bridge.BakeWad(bridge.RootPath, "test.wad.client", ("data/b.bin", "b"), ("data/a.bin", "a"));
        var cache = new GameWadPathCache();
        var hashes = cache.Read(path, CancellationToken.None);
        Assert.Equal(GameWadPathCache.ReadUncached(path), hashes);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Same(hashes, cache.Read(path, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => cache.Read(path, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadsWhenLengthOrTimestampChanges(bool sameLength)
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = bridge.BakeWad(bridge.RootPath, "test.wad.client", ("data/a.bin", "a"));
        var cache = new GameWadPathCache();
        var original = cache.Read(path, CancellationToken.None);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        bridge.BakeWad(bridge.RootPath, "test.wad.client", ("data/b.bin", sameLength ? "b" : "a longer body"));
        File.SetLastWriteTimeUtc(path, sameLength ? stamp.AddSeconds(2) : stamp);
        var updated = cache.Read(path, CancellationToken.None);
        Assert.NotSame(original, updated);
        Assert.Equal(new[] { XxHash64Ext.Hash("data/b.bin") }, updated);
    }

    [Fact]
    public void FailedReadsAreRetriedAndMemoryBudgetFallsBackToUncachedReads()
    {
        using var bridge = new AssetsManagerTestBridge();
        string path = Path.Combine(bridge.RootPath, "test.wad.client");
        File.WriteAllBytes(path, new byte[] { 1, 2 });
        var cache = new GameWadPathCache();
        Assert.ThrowsAny<Exception>(() => cache.Read(path, CancellationToken.None));
        bridge.BakeWad(bridge.RootPath, "test.wad.client", ("data/a.bin", "a"));
        var first = cache.Read(path, CancellationToken.None);
        Assert.Single(first);
        var uncached = new GameWadPathCache(byteBudget: 0);
        var next = uncached.Read(path, CancellationToken.None);
        Assert.Equal(first, next);
        Assert.NotSame(next, uncached.Read(path, CancellationToken.None));
        Assert.NotSame(first, new GameWadPathCache().Read(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(7L)]
    [InlineData(long.MaxValue)]
    public void CachedPassesPreserveCandidatesMatchesProgressAndNewSessionEvidence(long budget)
    {
        using var bridge = new AssetsManagerTestBridge();
        const string folder = "assets/characters/example/animations/";
        const string tex = "assets/characters/example/particles/";
        const string late = "assets/characters/late/particles/";
        string animTarget = folder + "joke_sad_loop.anm", texTarget = tex + "joke_sad_loop.tex";
        var known = new HashFile(HashGuessDomain.Game, new[] { folder + "joke_loop.anm", folder + "idle_sad.anm", tex + "joke_loop.tex",
            late + "joke_loop.tex", late + "idle_sad.tex" });
        var guesser = new GameHashGuesser(known);

        var legacy = Run(false);
        var shared = Run(true);
        Assert.Equal(legacy.Checks, shared.Checks);
        Assert.Equal(legacy.Matches, shared.Matches);
        Assert.Equal(legacy.Pending, shared.Pending);
        Assert.Equal(legacy.Progress, shared.Progress);
        if (budget == long.MaxValue)
        {
            Assert.Contains(texTarget, shared.Matches);
            Assert.Contains(late + "joke_sad_loop.tex", shared.Matches);
        }

        (long Checks, string[] Matches, ulong[] Pending, long[] Progress) Run(bool cached)
        {
            string root = bridge.CreateDirectory(cached ? "cached" : "legacy");
            bridge.BakeWad(root, "a.wad.client", (folder + "joke_loop.anm", "a"), (folder + "idle_sad.anm", "b"), (animTarget, "c"));
            bridge.BakeWad(root, "b.wad.client", (tex + "joke_loop.tex", "a"), (tex + "idle_sad.tex", "b"), (texTarget, "c"));
            var cache = cached ? new GameWadPathCache() : null;
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(animTarget), 42 });
            var progress = new List<long>();
            guesser.GuessLocalNamePatterns(engine, root, true, CancellationToken.None, budget, progress.Add, cache);
            engine.EnsureUnknown(XxHash64Ext.Hash(tex + "idle_sad.tex"));
            engine.EnsureUnknown(XxHash64Ext.Hash(texTarget));
            engine.Check(tex + "idle_sad.tex", HashGuessStrategy.WordlistVariant, "earlier method");
            // A fresh directory scan must include WADs added after the first pass.
            bridge.BakeWad(root, "new.wad.client", (late + "joke_loop.tex", "a"), (late + "idle_sad.tex", "b"), (late + "joke_sad_loop.tex", "c"));
            engine.EnsureUnknown(XxHash64Ext.Hash(late + "joke_sad_loop.tex"));
            guesser.GuessLocalNamePatterns(engine, root, false, CancellationToken.None, budget, progress.Add, cache);
            return (engine.CheckedCandidates, engine.Matches.Values.Select(m => m.Path).Order(StringComparer.Ordinal).ToArray(),
                engine.UnknownHashes.Order().ToArray(), progress.ToArray());
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class GameTextureFamilyTests
    {
        private const string Seed = "assets/characters/example/skins/skin80/example_skin80_body_f1_tx_cm.tex";
        private const string Target = "assets/characters/example/skins/skin80/example_skin80_body_f1_scrollmask_tx_cm.tex";

        private static string[] Corpus => new[]
        {
            Seed,
            "assets/characters/teacher/skins/skin1/teacher_scrollmask_tx_cm.tex",
            "assets/characters/teacher/skins/skin2/teacher_scrollmask_tx_cm.tex"
        };

        private static BinTree Tree(ulong target, uint classHash = 0xff9d3409) => new(
            new[]
            {
                new BinTreeObject(1, classHash, new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(2, XxHash64Ext.Hash(Seed)),
                    new BinTreeOptional(3, new BinTreeWadChunkLink(0, target))
                })
            }, Array.Empty<string>());

        [Fact]
        public void LearnsCompoundSuffixAndPreservesVerifiedProvenance()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
            index.Guess(engine, Tree(XxHash64Ext.Hash(Target)), "unknown.bin", "example.wad.client", 123, CancellationToken.None);
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(Target, match.Path);
            Assert.Equal("example.wad.client", match.SourceWadPath);
            Assert.Equal(123UL, match.SourceChunkHash);
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void ScansFamilyOncePerEngineButAllowsIndependentRuns()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var tree = Tree(42);
            var first = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            index.Guess(first, tree, "unknown.bin", "example", 1, CancellationToken.None);
            long candidates = first.CheckedCandidates;
            Assert.True(candidates > 0);
            index.Guess(first, tree, "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Equal(candidates, first.CheckedCandidates);
            var next = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            index.Guess(next, Tree(XxHash64Ext.Hash(Target)), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Single(next.Matches);
        }

        [Fact]
        public void UnrelatedClassDoesNotTriggerTextureAttack()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            index.Guess(engine, Tree(XxHash64Ext.Hash(Target), 99), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Equal(0, engine.CheckedCandidates);
        }

        [Fact]
        public void CancellationDoesNotPoisonFamilyRetry()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 }, _ => cancellation.Cancel());
            var tree = Tree(XxHash64Ext.Hash(Target));
            Assert.Throws<OperationCanceledException>(() =>
                index.Guess(engine, tree, "unknown.bin", "example", 1, cancellation.Token));
            long candidates = engine.CheckedCandidates;
            index.Guess(engine, Tree(42), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.True(engine.CheckedCandidates > candidates);
        }

        [Fact]
        public void TextureBuildListResolvesTargetAcrossFamilies()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Corpus));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            long candidates = guesser.SubstituteTextureBuildListWords(engine, CancellationToken.None);
            Assert.True(candidates > 0);
            Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void GameCustomAttacksResolvesTextureBuildListSubMethod()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Corpus));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            guesser.RunCustomAttacks(
                engine,
                null,
                CancellationToken.None,
                new HashSet<string> { "game-custom-textures" });
            Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        }
    }
}

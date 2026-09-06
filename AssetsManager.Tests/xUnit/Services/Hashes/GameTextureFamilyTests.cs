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

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        public void TextureBuildListChecksEntireBudgetAndReportsActualAttempts(long budget)
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            long reported = -1;

            long attempts = index.RunBuildList(engine, CancellationToken.None, budget, count => reported = count);

            Assert.Equal(budget, engine.CheckedCandidates);
            Assert.Equal(engine.CheckedCandidates, attempts);
            Assert.Equal(attempts, reported);
        }

        [Theory]
        [InlineData("assets/characters/petexample/themes/summer/petexample_summer")]
        [InlineData("assets/maps/kitpieces/tft/set18/textures/example")]
        public void TextureBuildListIncludesThemesAndMapKitpieces(string prefix)
        {
            string target = prefix + "_wall_a_tx.tex";
            string[] paths =
            {
                prefix + "_floor_a_tx.tex",
                "assets/maps/kitpieces/tft/set1/textures/teacher_wall_a_tx.tex",
                "assets/maps/kitpieces/tft/set2/textures/teacher_wall_a_tx.tex"
            };
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, paths));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            guesser.SubstituteTextureBuildListWords(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
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

        [Theory]
        [InlineData("assets/maps/particles/tft/example/rocket", ".tex")]
        [InlineData("assets/characters/example/skins/skin1/particles/rocket", ".dds")]
        public void TextureBuildListChecksSmallParticleRoleFamilies(string stem, string extension)
        {
            string target = stem + "_m2" + extension;
            var index = new GameTextureFamilyIndex(new[] { stem + "_tx" + extension }, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            long attempts = index.RunBuildList(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            Assert.InRange(attempts, 1, 5);
        }

        [Fact]
        public void ParticleRolePassPreservesBudgetAccountingAndCancellation()
        {
            var index = new GameTextureFamilyIndex(new[] { "assets/maps/particles/example_tx.tex" }, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            long reported = -1;
            Assert.Equal(2, index.RunBuildList(engine, CancellationToken.None, 2, count => reported = count));
            Assert.Equal(2, reported);
            Assert.Equal(2, engine.CheckedCandidates);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => index.RunBuildList(engine, cancellation.Token));
            Assert.Equal(2, engine.CheckedCandidates);
        }

        [Theory]
        [InlineData("dds")]
        [InlineData("tex")]
        public void CharacterWordSubstitutionRetainsExclusiveParticleCoverage(string extension)
        {
            string target = $"assets/characters/example/skins/skin1/particles/smoke_blue.{extension}";
            string[] paths =
            {
                $"assets/characters/example/skins/skin1/particles/smoke_red.{extension}",
                $"assets/characters/teacher/skins/skin1/particles/fire_blue.{extension}"
            };
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, paths));
            var words = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            var families = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            if (extension == "dds") game.SubstituteCharacterDdsBasenameWords(words, CancellationToken.None);
            else game.SubstituteCharacterTexBasenameWords(words, CancellationToken.None);
            game.SubstituteTextureBuildListWords(families, CancellationToken.None);
            Assert.Equal(target, Assert.Single(words.Matches).Value.Path);
            Assert.Empty(families.Matches);
        }
    }
}

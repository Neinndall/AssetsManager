using System.Collections.Generic;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameBinCoverageTests
    {
        [Fact]
        public void CombinedBinAndSwordlistRetainsExclusiveSwordlistWords()
        {
            const string target = "data/example/beta.bin";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                new[] { "data/example/alpha.bin", "assets/teacher/beta.bin.json" }));
            foreach (var selected in new[]
            {
                new HashSet<string> { "game-custom-swordlist" },
                new HashSet<string> { "game-custom-bin", "game-custom-swordlist" }
            })
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
                game.RunCustomAttacks(engine, null, CancellationToken.None, selected);
                Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            }
        }

        [Fact]
        public void CombinedBinAndSwordlistSkipsOnlyFullyCoveredVocabulary()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                new[] { "loadouts/alpha.bin", "data/example/beta.bin" }));
            var bin = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            var combined = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            game.RunCustomAttacks(bin, null, CancellationToken.None, new HashSet<string> { "game-custom-bin" });
            game.RunCustomAttacks(combined, null, CancellationToken.None, new HashSet<string> { "game-custom-bin", "game-custom-swordlist" });
            Assert.True(bin.CheckedCandidates > 0);
            Assert.Equal(bin.CheckedCandidates, combined.CheckedCandidates);
        }

        [Theory]
        [InlineData("loadouts/companions/")]
        [InlineData("characters/example/")]
        [InlineData("")]
        public void BinSearchIncludesAllVirtualRoots(string directory)
        {
            string target = directory + "beta.bin";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                new[] { directory + "alpha.bin", "data/teacher/beta.bin" }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            game.SubstituteBinBasenameWords(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void BinVocabularyIncludesWordsFromLoadoutPaths()
        {
            const string target = "data/example/beta.bin";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                new[] { "data/example/alpha.bin", "loadouts/companions/beta.bin" }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            game.SubstituteBinBasenameWords(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }
    }
}

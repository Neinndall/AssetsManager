using System.Collections.Generic;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class NormalizedCandidatePartsTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(300)]
        public void PartsPreserveHashPathCountersAndProvenance(int padding)
        {
            string prefix = "assets/characters/éxample/" + new string('a', padding);
            const string word = "attack_crit";
            const string suffix = "_to_run_-90.anm";
            string expected = prefix + word + suffix;
            ulong hash = XxHash64Ext.Hash(expected);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { hash });

            Assert.False(engine.CheckNormalizedParts(prefix, "idle", suffix,
                HashGuessStrategy.WordlistVariant, "parts", 42));
            Assert.True(engine.CheckNormalizedParts(prefix, word, suffix,
                HashGuessStrategy.WordlistVariant, "parts", 42));

            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(expected, match.Path);
            Assert.Equal("parts", match.SourceWadPath);
            Assert.Equal(42UL, match.SourceChunkHash);
            Assert.Equal(HashGuessStrategy.WordlistVariant, match.Strategy);
            Assert.Equal(2L, engine.CheckedCandidates);
            Assert.Equal(1L, engine.DiscardedCandidates);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Fact]
        public void EmptyPartsRemainDiscarded()
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            Assert.False(engine.CheckNormalizedParts("", "", "", HashGuessStrategy.WordlistVariant, "parts"));
            Assert.Equal(1L, engine.CheckedCandidates);
            Assert.Equal(1L, engine.DiscardedCandidates);
            Assert.Empty(engine.Matches);
        }
    }
}

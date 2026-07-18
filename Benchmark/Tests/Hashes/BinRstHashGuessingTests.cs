using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Hashes
{
    public sealed class BinRstHashGuessingTests
    {
        [Fact]
        public void CandidateMatcherPublishesEachMatchExactlyOnce()
        {
            const string candidate = "data/test/example.bin";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new BinRstHashGuessingService.CandidateMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.TextContent, "test");

            IReadOnlyList<InternalHashGuessMatch> firstBatch = matcher.TakePendingMatches();
            Assert.Equal(2, firstBatch.Count);
            Assert.Equal(
                new[] { InternalHashKind.BinEntries, InternalHashKind.BinHashes },
                firstBatch.Select(match => match.Kind).OrderBy(kind => kind));
            Assert.Empty(matcher.TakePendingMatches());

            matcher.Check(candidate, InternalHashGuessStrategy.TextContent, "test");

            Assert.Empty(matcher.TakePendingMatches());
            Assert.Equal(2, matcher.Matches.Count);
        }

        private static Dictionary<InternalHashKind, HashSet<ulong>> CreateTargets() => new()
        {
            [InternalHashKind.BinEntries] = new(),
            [InternalHashKind.BinFields] = new(),
            [InternalHashKind.BinTypes] = new(),
            [InternalHashKind.BinHashes] = new(),
            [InternalHashKind.RstXxh3] = new(),
            [InternalHashKind.RstXxh64] = new()
        };
    }
}

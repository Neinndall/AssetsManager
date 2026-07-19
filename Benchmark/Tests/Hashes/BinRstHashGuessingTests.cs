using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Hashes
{
    public sealed class BinRstHashGuessingTests
    {
        [Fact]
        public void LocalEvidenceMatcherPublishesEachLiteralMatchExactlyOnce()
        {
            const string candidate = "data/test/example.bin";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            targets[InternalHashKind.BinHashes].Add(hash);
            var localObserved = CreateTargets();
            localObserved[InternalHashKind.BinEntries].Add(hash);
            localObserved[InternalHashKind.BinHashes].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "test", localTargets: localObserved);

            IReadOnlyList<InternalHashGuessMatch> firstBatch = matcher.TakePendingMatches();
            Assert.Equal(2, firstBatch.Count);
            Assert.Equal(
                new[] { InternalHashKind.BinEntries, InternalHashKind.BinHashes },
                firstBatch.Select(match => match.Kind).OrderBy(kind => kind));
            Assert.Empty(matcher.TakePendingMatches());

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "test", localTargets: localObserved);

            Assert.Empty(matcher.TakePendingMatches());
            Assert.Equal(2, matcher.Matches.Count);
        }

        [Fact]
        public void PathCandidateCannotResolveBinField()
        {
            const string candidate = "data/characters/illaoi/skins/skin38/birthscale0/spec_intensity";
            uint hash = Fnv1a.HashLower(candidate);
            Assert.Equal(0x15f32511u, hash);
            var targets = CreateTargets();
            targets[InternalHashKind.BinFields].Add(hash);
            var localObserved = CreateTargets();
            localObserved[InternalHashKind.BinFields].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "test", localTargets: localObserved);

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinFields]);
        }

        [Fact]
        public void BinMatchWithoutSameFileDomainEvidenceIsRejected()
        {
            const string candidate = "data/characters/illaoi/skins/skin38/birthscale0/spec_intensity";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "other.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinHashes]);

            var localTargets = CreateTargets();
            localTargets[InternalHashKind.BinHashes].Add(hash);
            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "illaoi.bin", localTargets: localTargets);

            Assert.True(matcher.Matches.Single().IsVerified);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void ContextualEntryAttributeResolvesObjectPath()
        {
            const string candidate = "Characters/Cassiopeia/Skins/Skin28/Particles/Cassiopeia_Skin28_W_buf_acidtrail_01";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);
            var tree = CreateEntryTree(hash, "VfxSystemDefinitionData", "particlePath", candidate);

            BinRstHashGuessingService.MatchBinContextualEvidence(tree, matcher, "cassiopeia.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
            Assert.Equal(candidate.ToLowerInvariant(), match.Value);
            Assert.True(match.IsVerified);
        }

        [Fact]
        public void UnrelatedEntryStringIsRejected()
        {
            const string candidate = "Characters/Cassiopeia/Skins/Skin28/Particles/Cassiopeia_Skin28_W_buf_acidtrail_01";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);
            var tree = CreateEntryTree(hash, "UnrelatedClass", "unrelatedPath", candidate);

            BinRstHashGuessingService.MatchBinContextualEvidence(tree, matcher, "cassiopeia.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public async Task StoreDiscardsUnverifiedInputAndPromotesVerifiedMatches()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            var candidate = new InternalHashGuessMatch
            {
                Hash = 0x15f32511,
                LookupHash = 0x15f32511,
                HashBits = 32,
                Value = "data/characters/illaoi/skins/skin38/birthscale0/spec_intensity",
                Kind = InternalHashKind.BinHashes,
                Strategy = InternalHashGuessStrategy.BinContent,
                IsVerified = false
            };

            await store.SaveMatchesAsync(new[] { candidate }, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "internal.research.json")));
            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
            Assert.Equal("[]", await File.ReadAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "internal.research.json")));

            await store.SaveMatchesAsync(new[] { new InternalHashGuessMatch
            {
                Hash = candidate.Hash,
                LookupHash = candidate.LookupHash,
                HashBits = candidate.HashBits,
                Value = candidate.Value,
                Kind = candidate.Kind,
                Strategy = candidate.Strategy,
                IsVerified = true
            } }, CancellationToken.None);

            Assert.Contains("15f32511", await File.ReadAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
        }

        [Fact]
        public void InventoryClassifiesObjectLinksAsBinEntries()
        {
            const uint entryPath = 0x11111111;
            const uint classHash = 0x22222222;
            const uint linkField = 0x33333333;
            const uint linkedEntry = 0x44444444;
            const uint hashField = 0x55555555;
            const uint hashValue = 0x66666666;
            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryPath, classHash, new BinTreeProperty[]
                {
                    new BinTreeObjectLink(linkField, linkedEntry),
                    new BinTreeHash(hashField, hashValue)
                })
            }, Array.Empty<string>());
            var observed = CreateTargets();

            BinRstHashGuessingService.ReadBinInventory(tree, observed);

            Assert.Contains(entryPath, observed[InternalHashKind.BinEntries]);
            Assert.Contains(linkedEntry, observed[InternalHashKind.BinEntries]);
            Assert.Contains(classHash, observed[InternalHashKind.BinTypes]);
            Assert.Contains(linkField, observed[InternalHashKind.BinFields]);
            Assert.Contains(hashField, observed[InternalHashKind.BinFields]);
            Assert.Contains(hashValue, observed[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void DomainSpecificResolverDoesNotLeakCollisions()
        {
            const string hash = "15f32511";
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"), $"{hash} hash_value{System.Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{hash} entry_value{System.Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"), $"{hash} field_value{System.Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.bintypes.txt"), $"{hash} type_value{System.Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            resolver.LoadBinHashes();

            const uint value = 0x15f32511;
            Assert.Equal("hash_value", resolver.ResolveBinHash(value));
            Assert.Equal("entry_value", resolver.ResolveBinEntry(value));
            Assert.Equal("field_value", resolver.ResolveBinField(value));
            Assert.Equal("type_value", resolver.ResolveBinType(value));
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

        private static BinTree CreateEntryTree(uint hash, string className, string field, string value) =>
            new(new[]
            {
                new BinTreeObject(hash, Fnv1a.HashLower(className), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower(field), value)
                })
            }, System.Array.Empty<string>());
    }
}

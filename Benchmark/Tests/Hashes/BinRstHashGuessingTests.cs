using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        public void EvidenceFromDifferentBinDomainIsRejected()
        {
            const string candidate = "data/test/example";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var localObserved = CreateTargets();
            localObserved[InternalHashKind.BinEntries].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "test.bin", localTargets: localObserved);

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void LiteralStringCannotCertifyItselfAsBinHash()
        {
            const string candidate = "data/characters/illaoi/skins/skin38/birthscale0/spec_intensity";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);
            var tree = CreateEntryTree(0x11111111, "UnrelatedClass", "unrelatedPath", candidate);

            BinRstHashGuessingService.MatchBinContentEvidence(tree, matcher, "illaoi.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinHashes]);
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
        public async Task StoreKeepsCandidatesInResearchAndPromotesVerifiedMatchesToOverrides()
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
                IsVerified = false,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = InternalHashConfidence.Candidate,
                Evidence = InternalHashEvidence.MetaSchemaWordset
            };

            await store.SaveMatchesAsync(new[] { candidate }, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "internal.research.json")));
            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
            Assert.Contains("spec_intensity", await File.ReadAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "internal.research.json")));

            await store.SaveMatchesAsync(new[] { new InternalHashGuessMatch
            {
                Hash = candidate.Hash,
                LookupHash = candidate.LookupHash,
                HashBits = candidate.HashBits,
                Value = candidate.Value,
                Kind = candidate.Kind,
                Strategy = candidate.Strategy,
                IsVerified = true,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = InternalHashConfidence.Verified,
                Evidence = InternalHashEvidence.RuntimeContext
            } }, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
            Assert.Contains("15f32511", await File.ReadAllTextAsync(store.GetOverridePath(InternalHashKind.BinHashes)));
        }

        [Fact]
        public void SchemaCandidateDoesNotResolveTarget()
        {
            const string candidate = "VfxGeComponentDef";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new BinRstHashGuessingService.LocalEvidenceMatcher(targets);

            Assert.True(matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                candidate,
                InternalHashGuessStrategy.CrossDictionary,
                "test schema"));

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.False(match.CanPromote);
            Assert.Equal(InternalHashConfidence.Candidate, match.Confidence);
            Assert.Contains(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void MetaSchemaParserKeepsOnlyActiveUnnamedClassesAndProperties()
        {
            const string json = """
                {
                  "latest": 42,
                  "classes": {
                    "0x11111111": {
                      "revisions": [{ "from": 1 }],
                      "properties": {
                        "0x22222222": { "revisions": [{ "from": 1 }] },
                        "0x33333333": { "name": "knownField", "revisions": [{ "from": 1 }] },
                        "0x44444444": { "revisions": [{ "from": 1, "to": 2 }] }
                      }
                    },
                    "0x55555555": {
                      "name": "KnownClass",
                      "revisions": [{ "from": 1 }],
                      "properties": {}
                    },
                    "0x66666666": {
                      "revisions": [{ "from": 1, "to": 2 }],
                      "properties": {}
                    }
                  }
                }
                """;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            MetaSchemaHashSnapshot snapshot = MetaSchemaHashSource.Parse(stream);

            Assert.Equal("42", snapshot.Version);
            Assert.Equal(new ulong[] { 0x11111111 }, snapshot.UnknownTypes);
            Assert.Equal(new ulong[] { 0x22222222 }, snapshot.UnknownFields);
            Assert.Contains("KnownClass", snapshot.KnownTypeNames);
            Assert.Contains("knownField", snapshot.KnownFieldNames);
        }

        [Fact]
        public async Task LegacyResearchIsQuarantinedInsteadOfPromoted()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            string researchPath = Path.Combine(bridge.Directories.HashLabPath, "internal.research.json");
            var legacy = new InternalHashGuessMatch
            {
                Hash = 0x12345678,
                LookupHash = 0x12345678,
                HashBits = 32,
                Value = "legacy_false_positive",
                Kind = InternalHashKind.BinHashes,
                IsVerified = true
            };
            await File.WriteAllTextAsync(researchPath, JsonSerializer.Serialize(new[] { legacy }));

            await store.SaveMatchesAsync(Array.Empty<InternalHashGuessMatch>(), CancellationToken.None);

            Assert.Equal("[]", await File.ReadAllTextAsync(researchPath));
            Assert.Contains("legacy_false_positive", await File.ReadAllTextAsync(
                Path.Combine(bridge.Directories.HashLabPath, "internal.legacy-quarantine.json")));
            Assert.False(File.Exists(store.GetOverridePath(InternalHashKind.BinHashes)));
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

        [Fact]
        public async Task VerifiedOverrideResolvesOnlyInItsBinDomain()
        {
            const uint value = 0x15f32511;
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            await store.SaveMatchesAsync(new[]
            {
                new InternalHashGuessMatch
                {
                    Hash = value,
                    LookupHash = value,
                    HashBits = 32,
                    Value = "verifiedField",
                    Kind = InternalHashKind.BinFields,
                    Strategy = InternalHashGuessStrategy.BinContent,
                    IsVerified = true,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Verified,
                    Evidence = InternalHashEvidence.RuntimeContext
                }
            }, CancellationToken.None);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            resolver.LoadBinHashes();

            Assert.Equal("verifiedField", resolver.ResolveBinField(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinHash(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinEntry(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinType(value));
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

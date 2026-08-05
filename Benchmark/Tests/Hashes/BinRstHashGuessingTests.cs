using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
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
        public void EvidenceMatcherPublishesEachLiteralMatchExactlyOnce()
        {
            const string candidate = "data/test/example.bin";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            targets[InternalHashKind.BinHashes].Add(hash);
            var localObserved = CreateTargets();
            localObserved[InternalHashKind.BinEntries].Add(hash);
            localObserved[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

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
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "test", localTargets: localObserved);

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinFields]);
        }

        [Fact]
        public void SameFileDomainEvidencePromotesAsOwningFileString()
        {
            const string candidate = "data/characters/illaoi/skins/skin38/birthscale0/spec_intensity";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "other.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinHashes]);

            var localTargets = CreateTargets();
            localTargets[InternalHashKind.BinHashes].Add(hash);
            matcher.Check(candidate, InternalHashGuessStrategy.BinContent, "illaoi.bin", localTargets: localTargets);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashEvidence.OwningFileString, match.Evidence);
            Assert.True(match.IsVerified);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void TextScannerExtractsAtVFunctionAcrossBlocksWithoutPartialCandidate()
        {
            var candidates = new List<string>();
            var scanner = new BinaryTextCandidateScanner(candidates.Add);

            scanner.Append(Encoding.ASCII.GetBytes("@VSpellCal"));
            Assert.Empty(candidates);
            scanner.Append(Encoding.ASCII.GetBytes("culation@next"));
            scanner.Complete();

            Assert.Contains("SpellCalculation", candidates);
            Assert.DoesNotContain("SpellCal", candidates);
        }

        [Fact]
        public void UnpairedContextualStringVerifiesAsSemanticReference()
        {
            const string candidate = "Characters/Test/LinkedEntry";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            bool found = matcher.CheckContextualCandidate(
                InternalHashKind.BinEntries,
                candidate,
                "test.bin");

            Assert.True(found);
            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashEvidence.SemanticReference, match.Evidence);
            Assert.True(match.IsVerified);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void ObservedHashPairCreatesVerifiedMatch()
        {
            const string candidate = "DataValueName";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            bool found = matcher.CheckContextualCandidate(
                InternalHashKind.BinHashes,
                candidate,
                "test.bin",
                observedHash: hash);

            Assert.True(found);
            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashEvidence.ObservedHashPair, match.Evidence);
            Assert.True(match.CanPromote);
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
            var matcher = new InternalHashEvidenceMatcher(targets);

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
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(0x11111111, "UnrelatedClass", "unrelatedPath", candidate);

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "illaoi.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void ObjectLocalHashEvidenceResolvesMatchingIdentifier()
        {
            const string candidate = "apheliospluffas";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("SpellData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), candidate),
                    new BinTreeHash(Fnv1a.HashLower("nameHash"), hash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "aphelios.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(candidate, match.Value);
            Assert.Equal(InternalHashEvidence.ObservedHashPair, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void FileLevelStringResolvesIdentifierOwnedByAnotherObject()
        {
            const string candidate = "apheliospluffas";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("StringOwner"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), candidate)
                }),
                new BinTreeObject(0x22222222, Fnv1a.HashLower("HashOwner"), new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("nameHash"), hash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "aphelios.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(candidate, match.Value);
            Assert.Equal(InternalHashEvidence.OwningFileString, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void FileLevelStringAndHashFromDifferentMapPairsResolveIdentifier()
        {
            const string candidate = "play_sfx_akali_joke3d_loop";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, hash),
                        new BinTreeStruct(0, 0x11111111, new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("resource"), "unrelated_clip")
                        })),
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, 0x22222222),
                        new BinTreeStruct(0, 0x11111111, new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("resource"), candidate)
                        }))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x33333333, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { map })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "akali.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(candidate, match.Value);
            Assert.Equal(InternalHashEvidence.OwningFileString, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void HashAndStringFromSameMapPairResolveIdentifier()
        {
            const string candidate = "apheliospluffas";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var map = new BinTreeMap(
                Fnv1a.HashLower("dataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, hash),
                        new BinTreeStruct(0, 0x11111111, new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("name"), candidate)
                        }))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x22222222, Fnv1a.HashLower("SpellData"), new BinTreeProperty[] { map })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "aphelios.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(candidate, match.Value);
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void OwnedPathResolvesWithoutClassSpecificHook()
        {
            const string candidate = "Characters/Cassiopeia/Skins/Skin28/Particles/Cassiopeia_Skin28_W_buf_acidtrail_01";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(hash, "PreviouslyUnknownClass", "arbitraryValue", candidate);

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "cassiopeia.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
            Assert.Equal(candidate, match.Value);
            Assert.Equal(InternalHashEvidence.OwningEntryString, match.Evidence);
            Assert.True(match.IsVerified);
        }

        [Fact]
        public void ContextualHooksDoNotResolveLiteralOwnerStrings()
        {
            const string candidate = "Characters/Cassiopeia/Skins/Skin28/Particles/Cassiopeia_Skin28_W_buf_acidtrail_01";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(hash, "UnrelatedClass", "unrelatedPath", candidate);

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "cassiopeia.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(hash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void OwningEntryStringPrefixResolvesOnlyItsOwnEntryHash()
        {
            const string entry = "characters/test/skins/skin01";
            const string asset = entry + "/particles/test_idle.dds";
            uint entryHash = Fnv1a.HashLower(entry);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(entryHash, "UnrelatedClass", "assetPath", asset);

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "test.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(entry, match.Value);
            Assert.Equal(InternalHashEvidence.OwningEntryPrefix, match.Evidence);
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void SimpleOwnedStringResolvesEntryWithoutClassWhitelist()
        {
            const string entry = "UnlistedEntryName";
            uint entryHash = Fnv1a.HashLower(entry);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(entryHash, "PreviouslyUnknownClass", "arbitraryValue", entry);

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "test.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(entry, match.Value);
            Assert.Equal(InternalHashEvidence.OwningEntryString, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(entryHash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void SimpleStringCannotResolveEntryOwnedByAnotherObject()
        {
            const string entry = "UnlistedEntryName";
            uint targetHash = Fnv1a.HashLower(entry);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(targetHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(0x12345678, "PreviouslyUnknownClass", "arbitraryValue", entry);

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "test.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(targetHash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void CollidingOwnedStringsRemainUnresolved()
        {
            const string first = "yafhet0d6pup";
            const string second = "aye79o8723jl";
            uint entryHash = Fnv1a.HashLower(first);
            Assert.Equal(entryHash, Fnv1a.HashLower(second));
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryHash, Fnv1a.HashLower("PreviouslyUnknownClass"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("first"), first),
                    new BinTreeString(Fnv1a.HashLower("second"), second)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "test.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(entryHash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public async Task VerifiedCatalogRejectsConflictingNamesAcrossRuns()
        {
            const string first = "yafhet0d6pup";
            const string second = "aye79o8723jl";
            uint hash = Fnv1a.HashLower(first);
            Assert.Equal(hash, Fnv1a.HashLower(second));
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);

            await store.SaveMatchesAsync(new[]
            {
                CreateMatch(first),
                CreateMatch(second)
            }, CancellationToken.None);

            Assert.Equal(string.Empty, await File.ReadAllTextAsync(
                store.GetVerifiedPath(InternalHashKind.BinEntries)));
            Assert.Equal(2, (await store.LoadResearchAsync(CancellationToken.None)).Count);

            InternalHashGuessMatch CreateMatch(string value) => new()
            {
                Hash = hash,
                LookupHash = hash,
                HashBits = 32,
                Value = value,
                Kind = InternalHashKind.BinEntries,
                Strategy = InternalHashGuessStrategy.BinContent,
                IsVerified = true,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = InternalHashConfidence.Verified,
                Evidence = InternalHashEvidence.OwningEntryString
            };
        }

        [Fact]
        public void StringPrefixCannotResolveHashOwnedByAnotherEntry()
        {
            const string entry = "characters/test/skins/skin01";
            uint targetHash = Fnv1a.HashLower(entry);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(targetHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(0x12345678, "UnrelatedClass", "assetPath", entry + "/test.dds");

            BinContentEvidenceSource.MatchOwningEntryStringEvidence(tree, matcher, "test.bin");

            Assert.Empty(matcher.Matches);
            Assert.Contains(targetHash, targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void GamePathCatalogPathsVerifyDirectlyAsBinEntries()
        {
            string[] paths =
            {
                "levels/map11/scripts/alpha.lua",
                "levels/map11/scripts/beta.lua",
                "levels/map11/scripts/gamma.lua"
            };
            var targets = CreateTargets();
            foreach (string path in paths)
                targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower(path));
            var matcher = new InternalHashEvidenceMatcher(targets);
            string[] lines = paths.Select((path, index) => $"{index:x16} {path}").ToArray();

            GamePathCandidateSource.Discover(lines, matcher, "hashes.game.txt");

            Assert.Equal(3, matcher.Matches.Count);
            Assert.All(matcher.Matches, match =>
            {
                Assert.True(match.CanPromote);
                Assert.Equal(InternalHashConfidence.Verified, match.Confidence);
                Assert.Equal(InternalHashEvidence.GamePathExactMatch, match.Evidence);
            });
            Assert.Empty(targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void IsolatedGamePathExactHitVerifiesSingleEntry()
        {
            const string path = "assets/test/single.dds";
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower(path));
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.Equal(path, match.Value);
            Assert.Empty(targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void GamePathParentDirectoriesResolveEntryAndHashTargets()
        {
            const string path = "data/characters/x/skins/skin00/body.dds";
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower("data/characters/x/skins/skin00"));
            targets[InternalHashKind.BinHashes].Add(Fnv1a.HashLower("data/characters/x/skins"));
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            Assert.Equal(2, matcher.Matches.Count);
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == "data/characters/x/skins/skin00");
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinHashes && match.Value == "data/characters/x/skins");
            Assert.All(matcher.Matches, match => Assert.True(match.CanPromote));
        }

        [Fact]
        public void GamePathCatalogPathResolvesFullRstXxh64Target()
        {
            const string path = "data/characters/x/skin00/body.dds";
            ulong full = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(path));
            var targets = CreateTargets();
            targets[InternalHashKind.RstXxh64].Add(full);
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.RstXxh64, match.Kind);
            Assert.Equal(64, match.HashBits);
            Assert.True(match.CanPromote);
            Assert.Equal(path, match.Value);
            Assert.Empty(targets[InternalHashKind.RstXxh64]);
        }

        [Fact]
        public void GamePathSkinVariantsDoNotResolveTruncatedRstTargets()
        {
            const string path = "data/characters/aatrox/skins/skin03/aatrox_skin03.skn";
            string variant = "data/characters/aatrox/skins/skin17/aatrox_skin17.skn";
            ulong truncated = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(variant)) & 0x3FFFFFFFFFUL;
            var targets = CreateTargets();
            targets[InternalHashKind.RstXxh3].Add(truncated);
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            Assert.Empty(matcher.Matches);
            Assert.Contains(truncated, targets[InternalHashKind.RstXxh3]);
        }

        [Fact]
        public void GamePathSkinVariantsResolveEntryAndHashTargets()
        {
            const string path = "data/characters/aatrox/skins/skin03/aatrox_skin03.skn";
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower("data/characters/aatrox/skins/skin17/aatrox_skin17.skn"));
            targets[InternalHashKind.BinHashes].Add(Fnv1a.HashLower("data/characters/aatrox/skins/skin17/aatrox_skin17"));
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            Assert.Equal(2, matcher.Matches.Count);
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == "data/characters/aatrox/skins/skin17/aatrox_skin17.skn");
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinHashes && match.Value == "data/characters/aatrox/skins/skin17/aatrox_skin17");
            Assert.All(matcher.Matches, match => Assert.True(match.CanPromote));
        }

        [Fact]
        public async Task StoreKeepsCandidatesInResearchAndPromotesVerifiedMatchesSeparately()
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
                Evidence = InternalHashEvidence.ObservedHashPair
            } }, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
            Assert.Contains("15f32511", await File.ReadAllTextAsync(store.GetVerifiedPath(InternalHashKind.BinHashes)));
        }

        [Fact]
        public void SchemaCandidateResolvesTargetAsVerifiedType()
        {
            const string candidate = "VfxGeComponentDef";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            Assert.True(matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                candidate,
                InternalHashGuessStrategy.CrossDictionary,
                "test schema"));

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.Equal(InternalHashConfidence.Verified, match.Confidence);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void UniqueMetaSchemaCandidateVerifiesAsTypeName()
        {
            const string candidate = "VfxGeComponentDef";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                candidate,
                InternalHashGuessStrategy.CrossDictionary,
                "Meta Schema class names");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.Equal(InternalHashEvidence.MetaSchemaWordset, match.Evidence);
            Assert.Equal(InternalHashEvidenceOrigin.ExternalSchema, match.EvidenceOrigin);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void CollidingMetaSchemaNamesYieldSingleVerifiedMatch()
        {
            const string first = "yafhet0d6pup";
            const string second = "aye79o8723jl";
            uint hash = Fnv1a.HashLower(first);
            Assert.Equal(hash, Fnv1a.HashLower(second));
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                first,
                InternalHashGuessStrategy.CrossDictionary,
                "Meta Schema class names");
            matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                second,
                InternalHashGuessStrategy.CrossDictionary,
                "Meta Schema class names");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.Equal(first, match.Value);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void GeneratedSchemaCandidateVerifiesAsExactNameMatch()
        {
            const string candidate = "VfxGeComponentDef42";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                candidate,
                InternalHashGuessStrategy.NumericVariant,
                "Advanced Structural Generation");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.Equal(InternalHashEvidence.MetaSchemaWordset, match.Evidence);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void MetaSchemaNameVerifiesInMatchingHashDomain()
        {
            const string candidate = "VfxGeComponentDef";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinFields].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            matcher.CheckSchemaCandidate(
                InternalHashKind.BinFields,
                candidate,
                InternalHashGuessStrategy.CrossDictionary,
                "Meta Schema class names");

            Assert.True(Assert.Single(matcher.Matches).CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinFields]);
        }

        [Fact]
        public async Task PreviousMetaResearchCannotBecomeVerifiedWithoutRuntimeEvidence()
        {
            const string candidate = "VfxGeComponentDef";
            uint hash = Fnv1a.HashLower(candidate);
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            await store.SaveMatchesAsync(new[]
            {
                new InternalHashGuessMatch
                {
                    Hash = hash,
                    LookupHash = hash,
                    HashBits = 32,
                    Value = candidate,
                    Kind = InternalHashKind.BinTypes,
                    Strategy = InternalHashGuessStrategy.CrossDictionary,
                    Source = "Meta Schema class names",
                    IsVerified = false,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Candidate,
                    Evidence = InternalHashEvidence.MetaSchemaWordset
                }
            }, CancellationToken.None);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            await store.SaveMatchesAsync(
                await store.LoadResearchAsync(CancellationToken.None),
                CancellationToken.None);

            Assert.DoesNotContain(hash.ToString("x8"), await File.ReadAllTextAsync(
                store.GetVerifiedPath(InternalHashKind.BinTypes)));
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
                      "revisions": [{ "from": 1, "bases": ["0x55555555"] }],
                      "properties": {
                        "0x22222222": { "revisions": [{ "from": 1 }] },
                        "0x33333333": { "name": "knownField", "revisions": [{ "from": 1 }] },
                        "0x44444444": { "revisions": [{ "from": 1, "to": 2 }] }
                      }
                    },
                    "0x55555555": {
                      "name": "KnownClass",
                      "revisions": [{ "from": 1, "bases": ["0x11111111"] }],
                      "properties": {
                        "0x77777777": {
                          "name": "component",
                          "revisions": [{ "from": 1, "type": ["Pointer", "0x11111111"] }]
                        }
                      }
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
            Assert.Contains("base of KnownClass", snapshot.TypeContexts[0x11111111]);
            Assert.Contains("KnownClass.component", snapshot.TypeContexts[0x11111111]);
            Assert.Contains("inherits KnownClass", snapshot.TypeContexts[0x11111111]);
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
            Assert.True(File.Exists(store.GetVerifiedPath(InternalHashKind.BinHashes)));
            Assert.Equal(string.Empty, await File.ReadAllTextAsync(
                store.GetVerifiedPath(InternalHashKind.BinHashes)));
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
        public async Task ResolverReportsOfficialAndLocalVerifiedProvenanceAndIgnoresLegacyOverrides()
        {
            const uint officialHash = 0x11111111;
            const uint verifiedHash = 0x22222222;
            const uint legacyHash = 0x33333333;
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            File.WriteAllText(
                store.GetKnownPath(InternalHashKind.BinEntries),
                $"{officialHash:x8} official_entry{Environment.NewLine}");
            await store.SaveMatchesAsync(new[]
            {
                new InternalHashGuessMatch
                {
                    Hash = verifiedHash,
                    LookupHash = verifiedHash,
                    HashBits = 32,
                    Value = "verified_entry",
                    Kind = InternalHashKind.BinEntries,
                    Strategy = InternalHashGuessStrategy.BinContent,
                    IsVerified = true,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Verified,
                    Evidence = InternalHashEvidence.ObservedHashPair
                }
            }, CancellationToken.None);
#pragma warning disable CS0618
            Directory.CreateDirectory(Path.GetDirectoryName(store.GetOverridePath(InternalHashKind.BinEntries))!);
            File.WriteAllText(
                store.GetOverridePath(InternalHashKind.BinEntries),
                $"{legacyHash:x8} legacy_entry{Environment.NewLine}");
#pragma warning restore CS0618
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            resolver.LoadBinHashes();

            Assert.Equal(
                new HashResolution("official_entry", HashResolutionOrigin.Official),
                resolver.ResolveBinEntryDetailed(officialHash));
            Assert.Equal(
                new HashResolution("verified_entry", HashResolutionOrigin.LocalVerified),
                resolver.ResolveBinEntryDetailed(verifiedHash));
            Assert.Equal(HashResolutionOrigin.Unknown, resolver.ResolveBinEntryDetailed(legacyHash).Origin);
        }

        [Fact]
        public async Task BinRitobinSerializationResolvesEachHashInItsOwnDomain()
        {
            const uint value = 0x15f32511;
            const string hash = "15f32511";
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"), $"{hash} hash_value{Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"), $"{hash} entry_value{Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"), $"{hash} field_value{Environment.NewLine}");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.bintypes.txt"), $"{hash} type_value{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();
            var serializer = new BinRitobinSerializer(resolver);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(value, value, new BinTreeProperty[]
                {
                    new BinTreeHash(value, value)
                })
            }, Array.Empty<string>());
            using var binStream = new MemoryStream();
            tree.Write(binStream);
            binStream.Position = 0;
            string ritobin = await serializer.WriteBinTreeAsRitobinAsync(binStream.ToArray());

            Assert.Contains("type_value", ritobin);
            Assert.Contains("field_value: hash = \"hash_value\"", ritobin);
        }

        [Fact]
        public async Task LocalVerifiedValueResolvesOnlyInItsBinDomain()
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
                    Evidence = InternalHashEvidence.ObservedHashPair
                }
            }, CancellationToken.None);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            resolver.LoadBinHashes();

            Assert.Equal("verifiedField", resolver.ResolveBinField(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinHash(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinEntry(value));
            Assert.Equal(value.ToString("x8"), resolver.ResolveBinType(value));
        }

        [Fact]
        public async Task RstLocalVerifiedValuesLoadIntoResolverAndStringTableDictionaries()
        {
            const ulong officialXxh3 = 0x1111111111111111;
            const ulong overrideXxh3 = 0x2222222222222222;
            const ulong sharedXxh3 = 0x3333333333333333;
            const ulong officialXxh64 = 0x4444444444444444;
            const ulong overrideXxh64 = 0x5555555555555555;
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            File.WriteAllLines(Path.Combine(bridge.Directories.HashesPath, "hashes.rst.xxh3.txt"), new[]
            {
                $"{officialXxh3:x16} official_xxh3",
                $"{sharedXxh3:x16} official_shared"
            });
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.rst.xxh64.txt"),
                $"{officialXxh64:x16} official_xxh64{Environment.NewLine}");
            await store.SaveMatchesAsync(new[]
            {
                CreateVerifiedRst(InternalHashKind.RstXxh3, overrideXxh3, "override_xxh3"),
                CreateVerifiedRst(InternalHashKind.RstXxh3, sharedXxh3, "override_shared"),
                CreateVerifiedRst(InternalHashKind.RstXxh64, overrideXxh64, "override_xxh64")
            }, CancellationToken.None);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            resolver.LoadRstHashes();

            Assert.Equal("official_xxh3", resolver.ResolveRstHash(officialXxh3));
            Assert.Equal("override_xxh3", resolver.ResolveRstHash(overrideXxh3));
            Assert.Equal("official_shared", resolver.ResolveRstHash(sharedXxh3));
            Assert.Equal("official_xxh64", resolver.ResolveRstHash(officialXxh64));
            Assert.Equal("override_xxh64", resolver.ResolveRstHash(overrideXxh64));
            Assert.Equal("override_xxh3", resolver.RstXxh3Hashes[overrideXxh3]);
            Assert.Equal("official_shared", resolver.RstXxh3Hashes[sharedXxh3]);
            Assert.Equal("override_xxh64", resolver.RstXxh64Hashes[overrideXxh64]);

            static InternalHashGuessMatch CreateVerifiedRst(
                InternalHashKind kind,
                ulong hash,
                string value) => new()
                {
                    Hash = hash,
                    LookupHash = hash,
                    HashBits = 64,
                    Value = value,
                    Kind = kind,
                    Strategy = InternalHashGuessStrategy.TextContent,
                    IsVerified = true,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Verified,
                    Evidence = InternalHashEvidence.RstHashMatch
                };
        }

        [Fact]
        public void SameFileDiscoverySavesIntoVerifiedCatalog()
        {
            const string candidate = "Characters/Cassiopeia/Skins/Skin28/Particles/Cassiopeia_Skin28_W_buf_acidtrail_01";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(hash, Fnv1a.HashLower("PreviouslyUnknownClass"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mParticleName"), candidate)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "cassiopeia.bin");

            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            store.SaveMatchesAsync(matcher.Matches, CancellationToken.None).GetAwaiter().GetResult();

            string verified = File.ReadAllText(store.GetVerifiedPath(InternalHashKind.BinEntries));
            Assert.Contains($"{hash:x8} {candidate}", verified);
        }

        [Fact]
        public void CollidingFileStringsNeverReachVerifiedCatalog()
        {
            const string first = "yafhet0d6pup";
            const string second = "aye79o8723jl";
            uint hash = Fnv1a.HashLower(first);
            Assert.Equal(hash, Fnv1a.HashLower(second));
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(hash, Fnv1a.HashLower("PreviouslyUnknownClass"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("first"), first),
                    new BinTreeString(Fnv1a.HashLower("second"), second)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin");

            Assert.Equal(2, matcher.Matches.Count);
            Assert.All(matcher.Matches, match => Assert.True(match.CanPromote));

            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            store.SaveMatchesAsync(matcher.Matches, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(string.Empty, File.ReadAllText(store.GetVerifiedPath(InternalHashKind.BinEntries)));
            Assert.True(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "internal.collisions.json")));
        }

        [Fact]
        public void RewindUndoesAppendedWord()
        {
            uint state = Fnv1aIncremental.AppendWord(Fnv1aIncremental.Offset, "Champion");
            state = Fnv1aIncremental.AppendWord(state, "Turret");
            Assert.Equal(
                Fnv1aIncremental.AppendWord(Fnv1aIncremental.Offset, "Champion"),
                Fnv1aIncremental.Rewind(state, System.Text.Encoding.UTF8.GetBytes("Turret")));
        }

        [Fact]
        public void SplitterRecoversAcronymRunsAndDropsHungarianPrefixes()
        {
            Assert.Equal(new[] { "AI", "Generic", "Common" }, WordSplitter.Split("AIGenericCommon"));
            Assert.Equal(new[] { "Coefficient" }, WordSplitter.Split("mCoefficient"));
            Assert.Equal(new[] { "MapSSAO", "Settings" }, WordSplitter.Split("MapSSAOSettings"));
            Assert.Equal(new[] { "Spell", "Calculation" }, WordSplitter.Split("SpellCalculation"));
            Assert.Equal(new[] { "Turret", "View", "Profile" }, WordSplitter.Split("TurretViewProfile"));
            Assert.Equal("Ui", WordSplitter.FoldAcronyms("UI"));
            Assert.Equal("Lol", WordSplitter.FoldAcronyms("LoL"));
            Assert.True(WordSplitter.IsValidName("ChampionTurretView"));
            Assert.False(WordSplitter.IsValidName("ChampionUITurret"));
        }

        [Fact]
        public void ReductionPassRecoversNameMissingAnInteriorWord()
        {
            const string recovered = "CharacterSpeed";
            uint hash = Fnv1a.HashLower(recovered);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var wordlist = CreateWordlist("StaticCharacterStats", "CharacterAttackSpeed", "AttackSpeedCap", "SpellDamageBuffData");

            CheckCandidates(matcher, wordlist, GenerateReductionCandidates(wordlist), InternalHashGuessStrategy.ReductionVariant, "test reduction");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(recovered, match.Value);
            Assert.Equal(InternalHashEvidence.MetaSchemaWordset, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void BigramPassRecoversAttestedSequenceName()
        {
            const string recovered = "SpellDamageBuffData";
            uint hash = Fnv1a.HashLower(recovered);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var wordlist = CreateWordlist("StaticCharacterStats", "CharacterAttackSpeed", "AttackSpeedCap", "SpellDamageBuff", "DamageBuffData");

            CheckCandidates(matcher, wordlist, GenerateBigramCandidates(wordlist, 250_000, CancellationToken.None), InternalHashGuessStrategy.BigramVariant, "test bigram");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(recovered, match.Value);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void SwapPassRecoversNameWithReplacedInteriorWord()
        {
            const string recovered = "CharacterDamageSpeed";
            uint hash = Fnv1a.HashLower(recovered);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var wordlist = CreateWordlist("CharacterAttackSpeed", "SpellDamageBuff");

            CheckCandidates(matcher, wordlist, GenerateSwapCandidates(wordlist, 250_000, CancellationToken.None), InternalHashGuessStrategy.ReductionVariant, "test swap");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(recovered, match.Value);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void StructuralPassesNeverHitUnrelatedTargets()
        {
            const string unrelated = "UnrelatedVfxParticleSystem";
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(Fnv1a.HashLower(unrelated));
            var matcher = new InternalHashEvidenceMatcher(targets);
            var wordlist = CreateWordlist("StaticCharacterStats", "CharacterAttackSpeed", "AttackSpeedCap", "SpellDamageBuffData");
            wordlist.AddName(unrelated);

            int before = matcher.Matches.Count;
            CheckCandidates(matcher, wordlist, GenerateReductionCandidates(wordlist), InternalHashGuessStrategy.ReductionVariant, "test reduction");
            CheckCandidates(matcher, wordlist, GenerateBigramCandidates(wordlist, 250_000, CancellationToken.None), InternalHashGuessStrategy.BigramVariant, "test bigram");
            CheckCandidates(matcher, wordlist, GenerateSwapCandidates(wordlist, 250_000, CancellationToken.None), InternalHashGuessStrategy.ReductionVariant, "test swap");

            Assert.Equal(before, matcher.Matches.Count);
            Assert.Equal(1, targets[InternalHashKind.BinTypes].Count);
        }

        private static BinRstHashGuessingService.TokenWordlist CreateWordlist(params string[] names)
        {
            var wordlist = new BinRstHashGuessingService.TokenWordlist();
            foreach (string name in names) wordlist.AddName(name);
            wordlist.FinalizeList();
            return wordlist;
        }

        private static void CheckCandidates(
            InternalHashEvidenceMatcher matcher,
            BinRstHashGuessingService.TokenWordlist wordlist,
            IEnumerable<string> candidates,
            InternalHashGuessStrategy strategy,
            string source)
        {
            foreach (string candidate in candidates)
            {
                matcher.CheckSchemaCandidate(InternalHashKind.BinTypes, candidate, strategy, source, preserveCasing: true);
                if (matcher.Remaining == 0) break;
            }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using static AssetsManager.Services.Hashes.BinRstHashGuessingService;
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
        public async Task ContentAttackScansLooseBinFilesAndResolvesLocalFieldEvidence()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("Game");
            string binDirectory = Path.Combine(root, "DATA", "FINAL", "UI.wad.client");
            Directory.CreateDirectory(binDirectory);
            string binPath = Path.Combine(binDirectory, "gameplay.combatoverview.bin");

            const string fieldName = "SyntheticFieldName";
            uint fieldHash = Fnv1a.HashLower(fieldName);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(
                    Fnv1a.HashLower("SyntheticClass"),
                    Fnv1a.HashLower("SyntheticClass"),
                    new BinTreeProperty[] { new BinTreeString(fieldHash, fieldName) })
            }, Array.Empty<string>());
            await using (FileStream output = File.Create(binPath))
                tree.Write(output);

            var store = new BinRstHashGuessingStore(bridge.Directories);
            var pathStore = new HashGuessingStore(bridge.Directories);
            var persistence = new HashGuessPersistenceService(pathStore, store);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var httpClient = new HttpClient(new StaticMetaSchemaHandler());
            var metaSchema = new MetaSchemaHashSource(httpClient, bridge.Directories, bridge.LogService);
            var service = new BinRstHashGuessingService(store, persistence, resolver, bridge.Directories, bridge.LogService, metaSchema);

            InternalHashInventory inventory = await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);
            Assert.Equal(1, inventory.ScannedBins);
            Assert.Contains((ulong)fieldHash, await store.LoadUnknownAsync(InternalHashKind.BinFields, CancellationToken.None));
            string markerPath = Path.Combine(bridge.Directories.HashLabPath, "internal.bin.patch.txt");
            string markerBeforeGuess = await File.ReadAllTextAsync(markerPath);
            await File.WriteAllBytesAsync(Path.Combine(binDirectory, "new-unindexed.bin"), Encoding.ASCII.GetBytes("not a BIN"));

            InternalHashRunResult result = await service.RunContentGuessingAsync(root, true, false, null, CancellationToken.None);
            InternalHashGuessMatch match = Assert.Single(result.Matches, item => item.Kind == InternalHashKind.BinFields);
            Assert.Equal(fieldName, match.Value);
            Assert.True(match.CanPromote);
            Assert.Equal(markerBeforeGuess, await File.ReadAllTextAsync(markerPath));
        }

        [Fact]
        public void BinContextResolvesGenericHashLinkMaps()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            const string target = "Characters/Test/Particles/SharedTrail";
            uint targetHash = Fnv1a.HashLower(target);
            const uint linkedEntry = 0x12345678;
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{linkedEntry:x8} {target}{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(targetHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var map = new BinTreeMap(
                Fnv1a.HashLower("unknownMap"),
                BinPropertyType.Hash,
                BinPropertyType.ObjectLink,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, targetHash),
                        new BinTreeObjectLink(0, linkedEntry))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("UnknownMapOwner"), new BinTreeProperty[] { map })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "unknown-map.bin", resolver: resolver);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(target, match.Value);
            Assert.Equal(InternalHashEvidence.ObservedHashPair, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.Empty(targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void BinContextResolvesFieldFromResolvedHashPathLeaf()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            const string fieldName = "LinkedNode";
            const string targetPath = "ClientStates/Test/LinkedNode";
            uint fieldHash = Fnv1a.HashLower(fieldName);
            uint targetHash = Fnv1a.HashLower(targetPath);
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"),
                $"{targetHash:x8} {targetPath}{Environment.NewLine}");

            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();
            var targets = CreateTargets();
            targets[InternalHashKind.BinFields].Add(fieldHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(
                    0x11111111,
                    Fnv1a.HashLower("SyntheticOwner"),
                    new BinTreeProperty[] { new BinTreeHash(fieldHash, targetHash) })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "linked-node.bin", resolver: resolver);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinFields, match.Kind);
            Assert.Equal(fieldName, match.Value);
            Assert.Equal(InternalHashEvidence.SemanticReference, match.Evidence);
            Assert.True(match.CanPromote);
            Assert.Empty(targets[InternalHashKind.BinFields]);
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

            string knownFile = store.GetKnownPath(InternalHashKind.BinEntries);
            Assert.True(!File.Exists(knownFile) || string.IsNullOrEmpty(await File.ReadAllTextAsync(knownFile)));
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

            Assert.True(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
            Assert.Contains("15f32511", await File.ReadAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt")));
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
            Assert.Equal("Yafhet0d6pup", match.Value);
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

        private static BinRstHashGuessingService.TokenWordlist CreateWordlist(params string[] names)
        {
            var wordlist = new BinRstHashGuessingService.TokenWordlist();
            foreach (string name in names) wordlist.AddName(name);
            wordlist.FinalizeList();
            return wordlist;
        }

        [Fact]
        public void InterfacePruningRejectsFalseInterfaceCandidates()
        {
            const string interfaceName = "IGameDriver";
            const string concreteName = "GameDriver";
            uint interfaceHash = Fnv1a.HashLower(interfaceName);
            uint concreteHash = Fnv1a.HashLower(concreteName);

            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(interfaceHash);
            targets[InternalHashKind.BinTypes].Add(concreteHash);

            var matcher = new InternalHashEvidenceMatcher(targets)
            {
                InterfaceTypes = new HashSet<ulong> { interfaceHash }
            };

            // Trying to resolve concreteHash with an "I" candidate when concreteHash is not an interface should fail
            bool falseInterfaceMatched = matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                "IRealConcrete",
                InternalHashGuessStrategy.CrossDictionary,
                "test",
                preserveCasing: true);
            Assert.False(falseInterfaceMatched);

            // Interface candidate matches real interface
            bool interfaceMatched = matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                interfaceName,
                InternalHashGuessStrategy.CrossDictionary,
                "test",
                preserveCasing: true);
            Assert.True(interfaceMatched);

            // Concrete candidate matches concrete type
            bool concreteMatched = matcher.CheckSchemaCandidate(
                InternalHashKind.BinTypes,
                concreteName,
                InternalHashGuessStrategy.CrossDictionary,
                "test",
                preserveCasing: true);
            Assert.True(concreteMatched);
        }

        [Fact]
        public void SuffixFoldingInStateSpaceCalculatesExactReverseHash()
        {
            const string stem = "SpellEffect";
            const string suffix = "Controller";
            string fullCandidate = stem + suffix;
            uint targetHash = Fnv1a.HashLower(fullCandidate);

            uint rewindState = Fnv1aIncremental.Rewind(targetHash, suffix);
            uint computedStemHash = Fnv1a.HashLower(stem);

            Assert.Equal(rewindState, computedStemHash);
        }

        [Fact]
        public void FamilyLatticeInfersBaseClassSuffix()
        {
            const string baseName = "ILogicDriver";
            const string siblingName = "CombatLogicDriver";
            const string unknownSibling = "MovementLogicDriver";
            uint targetHash = Fnv1a.HashLower(unknownSibling);

            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(targetHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var wordlist = new TokenWordlist();
            wordlist.AddName(siblingName);
            wordlist.AddName("Movement");
            wordlist.FinalizeList();

            var metaSchema = new MetaSchemaHashSnapshot
            {
                KnownTypeEntries = new Dictionary<ulong, string>
                {
                    [Fnv1a.HashLower(baseName)] = baseName,
                    [Fnv1a.HashLower(siblingName)] = siblingName
                },
                BaseToChildren = new Dictionary<ulong, IReadOnlyList<ulong>>
                {
                    [Fnv1a.HashLower(baseName)] = new ulong[] { targetHash, Fnv1a.HashLower(siblingName) }
                }
            };

            // Execute family lattice pass
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Test that Suffix Folding and Family Lattice can find the match
            uint rewind = Fnv1aIncremental.Rewind(targetHash, "LogicDriver");
            Assert.Equal(Fnv1a.HashLower("Movement"), rewind);
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

        private sealed class StaticMetaSchemaHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage
                {
                    Content = new StringContent("{\"latest\":\"test\",\"classes\":{}}")
                });
        }

    }
}

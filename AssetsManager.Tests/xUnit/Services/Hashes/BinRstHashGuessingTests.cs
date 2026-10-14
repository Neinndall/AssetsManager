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
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using static AssetsManager.Services.Hashes.BinRstHashGuessingService;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class BinRstHashGuessingTests
    {
        [Theory]
        [InlineData(WadChunkCompression.None)]
        [InlineData(WadChunkCompression.Zstd)]
        public async Task InventoryFindsKnownUnknownAndMisnamedBinsAfterSignatureReads(WadChunkCompression compression)
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("Game");
            string[] paths = { "data/known.bin", "assets/misnamed.tex", "data/unknown.bin" };
            uint[] entryHashes = { 0x11111111, 0x22222222, 0x33333333 };
            var entries = new List<WadBakeEntry>();
            for (int index = 0; index < paths.Length; index++)
            {
                using var output = new MemoryStream();
                CreateEntryTree(entryHashes[index], "SyntheticClass", "SyntheticField", "SyntheticValue").Write(output);
                byte[] data = output.ToArray();
                entries.Add(new WadBakeEntry(paths[index], () => new MemoryStream(data), compression));
            }
            entries.Insert(1, new WadBakeEntry("assets/not-a-bin.tex", () => new MemoryStream(new byte[65536]), compression));
            entries.Insert(2, new WadBakeEntry("assets/short.dat", () => new MemoryStream("PR"u8.ToArray()), compression));
            WadBuilder.Bake(entries, Path.Combine(root, "test.wad.client"), new WadBakeSettings());
            await File.WriteAllTextAsync(
                Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
                string.Join("\n", paths.Take(2).Select(path => $"{XxHash64Ext.Hash(path):x16} {path}")) + "\n");

            var store = new BinRstHashGuessingStore(bridge.Directories);
            var persistence = new HashGuessPersistenceService(new HashGuessingStore(bridge.Directories), store);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var httpClient = new HttpClient(new StaticMetaSchemaHandler());
            var metaSchema = new MetaSchemaHashSource(httpClient, bridge.Directories, bridge.LogService);
            var service = new BinRstHashGuessingService(store, persistence, resolver, bridge.Directories, bridge.LogService, metaSchema);

            InternalHashInventory inventory = await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);

            Assert.Equal(3, inventory.ScannedBins);
            Assert.Equal(0, inventory.ScannedStringTables);
            Assert.True(entryHashes.Select(hash => (ulong)hash).ToHashSet().SetEquals(
                await store.LoadUnknownAsync(InternalHashKind.BinEntries, CancellationToken.None)));
            Assert.Contains((ulong)Fnv1a.HashLower("SyntheticField"),
                await store.LoadUnknownAsync(InternalHashKind.BinFields, CancellationToken.None));
        }

        [Fact]
        public async Task HashResolverSkipsEmptyCatalogWarmupAndLoadsLaterCatalogs()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            Assert.False(resolver.HasLocalHashCatalogs);
            await resolver.LoadAllHashesAsync();

            const ulong hash = 0x1234567890abcdef;
            const string path = "assets/hash-resolver-late-load.bin";
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
                $"{hash:x16} {path}{Environment.NewLine}");

            Assert.True(resolver.HasLocalHashCatalogs);
            await resolver.LoadAllHashesAsync();

            Assert.Equal(path, resolver.ResolveHash(hash));
        }

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
        public void ItemDataVfxResourceMapResolvesKeyFromLinkedBasename()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            const string linkedPath = "Characters/Test/Particles/ItemTrail";
            const string expected = "ItemTrail";
            uint expectedHash = Fnv1a.HashLower(expected);
            const uint linkedEntry = 0x23456789;
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{linkedEntry:x8} {linkedPath}{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(expectedHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var resourceMap = new BinTreeMap(
                Fnv1a.HashLower("resourceMap"),
                BinPropertyType.Hash,
                BinPropertyType.ObjectLink,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, expectedHash),
                        new BinTreeObjectLink(0, linkedEntry))
                });
            var vfxResolver = new BinTreeStruct(
                Fnv1a.HashLower("mVFXResourceResolver"),
                Fnv1a.HashLower("ResourceResolver"),
                new BinTreeProperty[] { resourceMap });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("ItemData"), new BinTreeProperty[] { vfxResolver })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "item.bin", resolver: resolver);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(expected, match.Value);
        }

        [Fact]
        public void ResourceMapRemovesSkin89PrefixFromLinkedBasename()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            const string linkedPath = "Characters/Test/Particles/Skin89_ItemTrail";
            const string expected = "ItemTrail";
            uint expectedHash = Fnv1a.HashLower(expected);
            const uint linkedEntry = 0x3456789a;
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{linkedEntry:x8} {linkedPath}{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(expectedHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var resourceMap = new BinTreeMap(
                Fnv1a.HashLower("resourceMap"),
                BinPropertyType.Hash,
                BinPropertyType.ObjectLink,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, expectedHash),
                        new BinTreeObjectLink(0, linkedEntry))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(
                    0x11111111,
                    Fnv1a.HashLower("ResourceResolver"),
                    new BinTreeProperty[] { resourceMap })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "resolver.bin", resolver: resolver);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Equal(expected, match.Value);
        }

        [Fact]
        public void ContextReusesEntryPathsDiscoveredEarlierInTheSameRun()
        {
            const string linkedPath = "Characters/Test/Particles/SessionTrail";
            const string expectedKey = "SessionTrail";
            uint linkedHash = Fnv1a.HashLower(linkedPath);
            uint expectedKeyHash = Fnv1a.HashLower(expectedKey);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(linkedHash);
            targets[InternalHashKind.BinHashes].Add(expectedKeyHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var sourceTree = new BinTree(new[]
            {
                new BinTreeObject(linkedHash, Fnv1a.HashLower("UiComponent"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), linkedPath)
                })
            }, Array.Empty<string>());
            BinContentEvidenceSource.MatchBinContextualEvidence(sourceTree, matcher, "source.bin");

            var resourceMap = new BinTreeMap(
                Fnv1a.HashLower("resourceMap"),
                BinPropertyType.Hash,
                BinPropertyType.ObjectLink,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, expectedKeyHash),
                        new BinTreeObjectLink(0, linkedHash))
                });
            var resolverTree = new BinTree(new[]
            {
                new BinTreeObject(
                    0x11111111,
                    Fnv1a.HashLower("ResourceResolver"),
                    new BinTreeProperty[] { resourceMap })
            }, Array.Empty<string>());
            BinContentEvidenceSource.MatchBinContextualEvidence(resolverTree, matcher, "resolver.bin");

            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == linkedPath);
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinHashes && match.Value == expectedKey);
        }

        [Fact]
        public void ItemGroupReusesHashValuesDiscoveredEarlierInTheSameRun()
        {
            const string groupId = "SyntheticGroup";
            uint groupIdHash = Fnv1a.HashLower(groupId);
            const string groupPath = "Items/ItemGroup/SyntheticGroup";
            uint groupPathHash = Fnv1a.HashLower(groupPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(groupIdHash);
            targets[InternalHashKind.BinEntries].Add(groupPathHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            Assert.True(matcher.CheckContextualCandidate(
                InternalHashKind.BinHashes,
                groupId,
                "seed.bin",
                observedHash: groupIdHash));

            var tree = new BinTree(new[]
            {
                new BinTreeObject(groupPathHash, Fnv1a.HashLower("ItemGroup"), new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("mItemGroupID"), groupIdHash)
                })
            }, Array.Empty<string>());
            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "item-group.bin");

            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == groupPath);
        }

        [Fact]
        public void ContextCollectsItemListsAgainstItemDataSeenInTheSameRun()
        {
            const uint itemId = 4242;
            const string itemPath = "Items/4242";
            uint itemHash = Fnv1a.HashLower(itemPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(itemHash);
            targets[InternalHashKind.BinHashes].Add(itemHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var itemTree = new BinTree(new[]
            {
                new BinTreeObject(itemHash, Fnv1a.HashLower("ItemData"), new BinTreeProperty[]
                {
                    new BinTreeU32(Fnv1a.HashLower("itemID"), itemId)
                })
            }, Array.Empty<string>());
            BinContentEvidenceSource.MatchBinContextualEvidence(itemTree, matcher, "items.bin");

            var itemList = new BinTreeContainer(
                Fnv1a.HashLower("mItems"),
                BinPropertyType.Hash,
                new BinTreeProperty[] { new BinTreeHash(0, itemHash) });
            var listTree = new BinTree(new[]
            {
                new BinTreeObject(0x22222222, Fnv1a.HashLower("GameModeItemList"), new BinTreeProperty[] { itemList })
            }, Array.Empty<string>());
            BinContentEvidenceSource.MatchBinContextualEvidence(listTree, matcher, "item-list.bin");

            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == itemPath);
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinHashes && match.Value == itemPath);
        }

        [Fact]
        public void CdragonTftShopPatternResolvesSetScopedEntryPath()
        {
            const string name = "SyntheticShopItem";
            const string expected = "Maps/Shipping/Map22/Sets/TFTSet7/Shop/SyntheticShopItem";
            uint expectedHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(expectedHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(expectedHash, Fnv1a.HashLower("TftShopData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mName"), name)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-shop.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
            Assert.Equal(expected, match.Value);
        }

        [Fact]
        public void CdragonAugmentPatternResolvesAugmentAndRootSpellPaths()
        {
            const string augmentName = "SyntheticAugment";
            string augmentPath = $"Maps/ModeSpecificData/Augments/{augmentName}";
            string rootSpellPath = $"{augmentPath}/Augment_{augmentName}";
            uint augmentHash = Fnv1a.HashLower(augmentPath);
            uint rootSpellHash = Fnv1a.HashLower(rootSpellPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(augmentHash);
            targets[InternalHashKind.BinEntries].Add(rootSpellHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(augmentHash, Fnv1a.HashLower("AugmentData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("AugmentNameId"), augmentName),
                    new BinTreeObjectLink(Fnv1a.HashLower("RootSpell"), rootSpellHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-augment.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            Assert.Equal(2, matcher.Matches.Count);
            Assert.Contains(matcher.Matches, match => match.Value == augmentPath);
            Assert.Contains(matcher.Matches, match => match.Value == rootSpellPath);
        }

        [Fact]
        public void CdragonQuestPatternResolvesModeQuestEntryPath()
        {
            const string questName = "SyntheticQuest";
            const string expected = "Maps/ModeSpecificData/ModesQuests/SyntheticQuest";
            uint expectedHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(expectedHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(expectedHash, 0x8d31b69b, new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("QuestName"), questName)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-quest.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Value);
        }

        [Fact]
        public void CdragonNamedAttributePatternResolvesEntryPath()
        {
            const string expected = "UI/Scenes/SyntheticScene";
            uint expectedHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(expectedHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(expectedHash, Fnv1a.HashLower("UISceneData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), expected)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-ui.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Value);
        }

        [Fact]
        public void CdragonRelationPatternsResolveStringGroupLinksAndGdsMapObjects()
        {
            const string groupPath = "Maps/Shipping/Map22/MapGroups/SyntheticGroup";
            const string objectPath = "Maps/Shipping/Map22/Objects/SyntheticObject";
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower(groupPath));
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower(objectPath));
            var matcher = new InternalHashEvidenceMatcher(targets);
            var items = new BinTreeMap(
                Fnv1a.HashLower("items"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, 0x12345678),
                        new BinTreeStruct(
                            0,
                            Fnv1a.HashLower("GdsMapObject"),
                            new BinTreeProperty[]
                            {
                                new BinTreeString(0xad304db5, objectPath)
                            }))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("TftMapSkin"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("GroupLink"), groupPath)
                }),
                new BinTreeObject(0x22222222, Fnv1a.HashLower("MapPlaceableContainer"), new BinTreeProperty[]
                {
                    items
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-relations.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            Assert.Equal(2, matcher.Matches.Count);
            Assert.Contains(matcher.Matches, match => match.Value == groupPath);
            Assert.Contains(matcher.Matches, match => match.Value == objectPath);
        }

        [Fact]
        public void LegacyRelationObjectLinksRemainResolvable()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            const string groupPath = "Maps/Shipping/Map22/MapGroups/LegacyGroup";
            uint groupHash = Fnv1a.HashLower(groupPath);
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{groupHash:x8} {groupPath}{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(groupHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x33333333, Fnv1a.HashLower("TftMapSkin"), new BinTreeProperty[]
                {
                    new BinTreeObjectLink(Fnv1a.HashLower("GroupLink"), groupHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "legacy-relations.bin",
                resolver: resolver,
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(groupPath, match.Value);
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
        public void ObjectPathReusesEntryPathDiscoveredByTheSameContextPass()
        {
            const string entryPath = "Characters/Test/Particles/TestVfx";
            uint entryHash = Fnv1a.HashLower(entryPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            targets[InternalHashKind.BinHashes].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryHash, Fnv1a.HashLower("VfxSystemDefinitionData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particlePath"), entryPath),
                    new BinTreeHash(Fnv1a.HashLower("objectPath"), entryHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "vfx.bin");

            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinEntries && match.Value == entryPath);
            Assert.Contains(matcher.Matches, match =>
                match.Kind == InternalHashKind.BinHashes && match.Value == entryPath);
        }

        [Theory]
        [InlineData("VfxSystemDefinitionData")]
        [InlineData("SpellObject")]
        [InlineData("SkinCharacterDataProperties")]
        [InlineData("TftSkinCharacterDataProperties")]
        [InlineData("AnimationGraphData")]
        public async Task ObjectPathFromEntryPathResolvesMatchingHashForSupportedTypes(string className)
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            const string entryPath = "Characters/Aatrox/Spells/AatroxQ";
            uint entryHash = Fnv1a.HashLower(entryPath);
            uint objectPathHash = entryHash;

            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{entryHash:x8} {entryPath}{Environment.NewLine}");
            await resolver.LoadAllHashesAsync();

            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(objectPathHash);
            targets[InternalHashKind.BinEntries].Add(objectPathHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryHash, Fnv1a.HashLower(className), new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("objectPath"), objectPathHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin", resolver: resolver);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(entryPath, match.Value);
            Assert.Equal(InternalHashKind.BinHashes, match.Kind);
            Assert.Contains(objectPathHash, targets[InternalHashKind.BinEntries]);
        }

        [Theory]
        [InlineData("SkinCharacterDataProperties")]
        [InlineData("TftSkinCharacterDataProperties")]
        public void SkinCharacterDataPropertiesResolvesLinksForLoLAndTFT(string className)
        {
            const string skinPath = "Characters/Aatrox/Skins/Skin1";
            uint skinEntryHash = Fnv1a.HashLower(skinPath);
            const string expectedResourcePath = "Characters/Aatrox/Skins/Skin1/Resources";
            uint resourceHash = Fnv1a.HashLower(expectedResourcePath);
            const string expectedAnimPath = "Characters/Aatrox/Animations/Skin1";
            uint animHash = Fnv1a.HashLower(expectedAnimPath);

            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(skinEntryHash);
            targets[InternalHashKind.BinEntries].Add(resourceHash);
            targets[InternalHashKind.BinEntries].Add(animHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var animStruct = new BinTreeStruct(Fnv1a.HashLower("skinAnimationProperties"), Fnv1a.HashLower("SkinAnimationProperties"), new BinTreeProperty[]
            {
                new BinTreeObjectLink(Fnv1a.HashLower("animationGraphData"), animHash)
            });

            var tree = new BinTree(new[]
            {
                new BinTreeObject(skinEntryHash, Fnv1a.HashLower(className), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("championSkinName"), "AatroxSkin01"),
                    new BinTreeObjectLink(Fnv1a.HashLower("mResourceResolver"), resourceHash),
                    animStruct
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, m => m.Value == skinPath);
            Assert.Contains(matcher.Matches, m => m.Value == expectedResourcePath);
            Assert.Contains(matcher.Matches, m => m.Value == expectedAnimPath);
        }

        [Fact]
        public void CharacterRecordReadsOptionalAttackNamesLikeCdragon()
        {
            const string spellPath = "Characters/Aatrox/Spells/AatroxBasicAttack";
            uint spellHash = Fnv1a.HashLower(spellPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(spellHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var basicAttack = new BinTreeEmbedded(
                Fnv1a.HashLower("basicAttack"),
                Fnv1a.HashLower("AttackSlotData"),
                new BinTreeProperty[]
                {
                    new BinTreeOptional(
                        Fnv1a.HashLower("mAttackName"),
                        new BinTreeString(0, "AatroxBasicAttack"))
                });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("CharacterRecord"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mCharacterName"), "Aatrox"),
                    basicAttack
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "character.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(spellPath, match.Value);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
        }

        [Fact]
        public void SkinCharacterDataReadsOptionalIconSquareLikeCdragon()
        {
            const string skinPath = "Characters/Ahri/Skins/Skin17";
            uint skinHash = Fnv1a.HashLower(skinPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(skinHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(skinHash, Fnv1a.HashLower("SkinCharacterDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeOptional(
                        Fnv1a.HashLower("iconSquare"),
                        new BinTreeString(0, "ASSETS/Characters/Ahri/HUD/Ahri_Square.dds"))
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "skin.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(skinPath, match.Value);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
        }

        [Fact]
        public void CharacterRecordResolvesSpellsAndGameModes()
        {
            const string cname = "Aatrox";
            const string rootPath = "Characters/Aatrox/CharacterRecords/Root";
            const string slimePath = "Characters/Aatrox/CharacterRecords/SLIME";
            const string spellPath = "Characters/Aatrox/Spells/AatroxBasicAttack";

            uint rootHash = Fnv1a.HashLower(rootPath);
            uint slimeHash = Fnv1a.HashLower(slimePath);
            uint spellHash = Fnv1a.HashLower(spellPath);

            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(rootHash);
            targets[InternalHashKind.BinEntries].Add(slimeHash);
            targets[InternalHashKind.BinEntries].Add(spellHash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            var basicAttackStruct = new BinTreeStruct(Fnv1a.HashLower("basicAttack"), Fnv1a.HashLower("AttackSlotData"), new BinTreeProperty[]
            {
                new BinTreeString(Fnv1a.HashLower("mAttackName"), "AatroxBasicAttack")
            });

            var tree = new BinTree(new[]
            {
                new BinTreeObject(rootHash, Fnv1a.HashLower("CharacterRecord"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mCharacterName"), cname),
                    basicAttackStruct
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, m => m.Value == rootPath);
            Assert.Contains(matcher.Matches, m => m.Value == slimePath);
            Assert.Contains(matcher.Matches, m => m.Value == spellPath);
        }

        [Theory]
        [InlineData("ContextualActionData", "mObjectPath")]
        [InlineData("CustomShaderDef", "objectPath")]
        [InlineData("RewardGroup", "internalName")]
        [InlineData("Sequence", "path")]
        [InlineData("MapContainer", "mapPath")]
        [InlineData("VfxSystemDefinitionData", "particlePath")]
        [InlineData("UiComponent", "name")]
        public void DirectEntryAttributesResolveTheirOwningEntry(string className, string fieldName)
        {
            const string entryPath = "Maps/Shipping/Map11/TestEntry";
            uint entryHash = Fnv1a.HashLower(entryPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = CreateEntryTree(entryHash, className, fieldName, entryPath);

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(entryPath, match.Value);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
        }

        [Fact]
        public void RawNamedTypeHashResolvesNameFieldEntry()
        {
            const string entryPath = "Maps/Shipping/Map11/RawNamedTypeEntry";
            uint entryHash = Fnv1a.HashLower(entryPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(entryHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryHash, 0x857c08ad, new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), entryPath)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin");

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(entryPath, match.Value);
            Assert.Equal(InternalHashKind.BinEntries, match.Kind);
        }

        [Fact]
        public void AugmentDataResolvesEntryAndRootSpell()
        {
            const string augmentName = "TestAugment";
            const string augmentPath = "Maps/ModeSpecificData/Augments/TestAugment";
            const string spellPath = "Maps/ModeSpecificData/Augments/TestAugment/Augment_TestAugment";
            uint augmentHash = Fnv1a.HashLower(augmentPath);
            uint spellHash = Fnv1a.HashLower(spellPath);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(augmentHash);
            targets[InternalHashKind.BinEntries].Add(spellHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(augmentHash, Fnv1a.HashLower("AugmentData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("AugmentNameId"), augmentName),
                    new BinTreeObjectLink(Fnv1a.HashLower("RootSpell"), spellHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContextualEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, match => match.Value == augmentPath);
            Assert.Contains(matcher.Matches, match => match.Value == spellPath);
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
        public void GamePathCatalogPathsDoNotVerifyAsBinEntries()
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

            Assert.Empty(matcher.Matches);
            Assert.Equal(3, targets[InternalHashKind.BinEntries].Count);
        }

        [Fact]
        public void IsolatedGamePathExactHitDoesNotVerifyBinEntry()
        {
            const string path = "assets/test/single.dds";
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(Fnv1a.HashLower(path));
            var matcher = new InternalHashEvidenceMatcher(targets);

            GamePathCandidateSource.Discover(
                new[] { $"0000000000000000 {path}" },
                matcher,
                "hashes.game.txt");

            Assert.Empty(matcher.Matches);
            Assert.Contains(Fnv1a.HashLower(path), targets[InternalHashKind.BinEntries]);
        }

        [Fact]
        public void GamePathParentDirectoriesDoNotResolveBinTargets()
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

            Assert.Empty(matcher.Matches);
            Assert.Single(targets[InternalHashKind.BinEntries]);
            Assert.Single(targets[InternalHashKind.BinHashes]);
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
        public void RstCandidatesUseLowercaseAndSupportAllPackedBitWidths()
        {
            const string candidate = "ClientStates/Gameplay/TranslationKey";
            byte[] bytes = Encoding.UTF8.GetBytes(candidate.ToLowerInvariant());
            ulong xxh3 = XxHash3.HashToUInt64(bytes);
            ulong xxh64 = XxHash64.HashToUInt64(bytes);
            var targets = CreateTargets();
            targets[InternalHashKind.RstXxh3].Add(xxh3 & ((1UL << 39) - 1));
            targets[InternalHashKind.RstXxh64].Add(xxh64 & ((1UL << 40) - 1));
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.Check(candidate, InternalHashGuessStrategy.CrossDictionary, "test");

            Assert.Equal(2, matcher.Matches.Count);
            Assert.All(matcher.Matches, match =>
            {
                Assert.Equal(candidate, match.Value);
                Assert.True(match.CanPromote);
                Assert.Contains(match.HashBits, new[] { 39, 40 });
            });
            Assert.Empty(targets[InternalHashKind.RstXxh3]);
            Assert.Empty(targets[InternalHashKind.RstXxh64]);
        }

        [Fact]
        public void RstInventoryUnpacksOffsetAndSelectsAlgorithmByPatch()
        {
            const ulong hash39 = (1UL << 38) | 0x12345UL;
            const ulong hash38 = 0x23456UL;
            var xxh3 = new HashSet<ulong>();
            var xxh64 = new HashSet<ulong>();

            using (MemoryStream stream = CreateRstStream(5, (7UL << 39) | hash39))
                BinRstHashGuessingService.ReadRstInventory(stream, xxh3, xxh64, gameVersion: 1501);

            Assert.Contains(hash39, xxh3);
            Assert.Empty(xxh64);

            xxh3.Clear();
            xxh64.Clear();
            using (MemoryStream stream = CreateRstStream(5, (7UL << 38) | hash38))
                BinRstHashGuessingService.ReadRstInventory(stream, xxh3, xxh64, gameVersion: 1502);

            Assert.Contains(hash38, xxh3);
            Assert.Empty(xxh64);

            xxh3.Clear();
            xxh64.Clear();
            using (MemoryStream stream = CreateRstStream(5, (7UL << 39) | hash39))
                BinRstHashGuessingService.ReadRstInventory(stream, xxh3, xxh64, gameVersion: 1409);

            Assert.Empty(xxh3);
            Assert.Contains(hash39, xxh64);
            Assert.DoesNotContain(7UL << 39, xxh64);
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
        public void GamePathSkinVariantsDoNotResolveBinTargets()
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

            Assert.Empty(matcher.Matches);
            Assert.Single(targets[InternalHashKind.BinEntries]);
            Assert.Single(targets[InternalHashKind.BinHashes]);
        }

        [Fact]
        public void LegacyGamePathEvidenceCannotPromoteBinCatalogValues()
        {
            var match = new InternalHashGuessMatch
            {
                Hash = 0x12345678,
                LookupHash = 0x12345678,
                HashBits = 32,
                Value = "assets/test/file.dds",
                Kind = InternalHashKind.BinEntries,
                Strategy = InternalHashGuessStrategy.GamePath,
                IsVerified = true,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = InternalHashConfidence.Verified,
                Evidence = InternalHashEvidence.GamePathExactMatch
            };

            Assert.False(match.CanPromote);
        }

        [Fact]
        public async Task RstContentCanScanLooseBinStringsWithoutEnablingBinTargets()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("Game");
            string binPath = Path.Combine(root, "translation.bin");
            const string candidate = "ClientStates/Gameplay/TranslationKey";
            ulong target = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(candidate.ToLowerInvariant())) & ((1UL << 38) - 1);

            File.WriteAllText(
                Path.Combine(bridge.Directories.HashLabPath, "unknowns.rst.xxh3.38.txt"),
                target.ToString("x16"));
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashLabPath, "internal.rst.patch.txt"),
                "fixture");

            var tree = new BinTree(new[]
            {
                new BinTreeObject(
                    0x11111111,
                    Fnv1a.HashLower("TranslationOwner"),
                    new BinTreeProperty[]
                    {
                        new BinTreeString(Fnv1a.HashLower("mKey"), candidate)
                    })
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

            InternalHashRunResult result = await service.RunContentGuessingAsync(
                root,
                includeBin: false,
                includeRst: true,
                progress: null,
                cancellationToken: CancellationToken.None,
                selectedSubMethods: new HashSet<string> { "rst-content-binstrings" });

            InternalHashGuessMatch match = Assert.Single(result.Matches);
            Assert.Equal(InternalHashKind.RstXxh3, match.Kind);
            Assert.Equal(candidate, match.Value);
            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt")));
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

        [Fact]
        public void NoisyGateKeepsSchemaHitsVerifiedAndPromotable()
        {
            const string candidate = "SyntheticNoisyType";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.BeginGate("noisy pass");
            matcher.CheckSchemaCandidate(InternalHashKind.BinTypes, candidate, InternalHashGuessStrategy.NumericVariant, "noisy pass", preserveCasing: true);
            matcher.AddGateNoise(1.0);
            Assert.Empty(matcher.TakePendingMatches());
            var result = matcher.EndGate();

            Assert.False(result.Accepted);
            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.IsVerified);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
            Assert.Single(matcher.TakePendingMatches());
        }

        [Fact]
        public void QuietGateKeepsSchemaHitsVerified()
        {
            const string candidate = "SyntheticQuietType";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinTypes].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.BeginGate("quiet pass");
            matcher.CheckSchemaCandidate(InternalHashKind.BinTypes, candidate, InternalHashGuessStrategy.CrossDictionary, "quiet pass", preserveCasing: true);
            Assert.True(matcher.EndGate().Accepted);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.True(match.CanPromote);
            Assert.DoesNotContain(hash, targets[InternalHashKind.BinTypes]);
        }

        [Fact]
        public void UntargetedHitOutsideItsBinWaitsForTheUntargetedGate()
        {
            const string candidate = "Maps/Shipping/Map22/MapGroups/Remote";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.CheckContextualCandidate(InternalHashKind.BinEntries, candidate, "remote.bin");
            Assert.Empty(matcher.TakePendingMatches());

            Assert.True(matcher.ResolveUntargetedGate().Accepted);
            InternalHashGuessMatch match = Assert.Single(matcher.TakePendingMatches());
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void UntargetedHitInsideItsBinBypassesTheGate()
        {
            const string candidate = "Maps/Shipping/Map22/MapGroups/Local";
            uint hash = Fnv1a.HashLower(candidate);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var local = CreateTargets();
            local[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.CheckContextualCandidate(InternalHashKind.BinEntries, candidate, "local.bin", localTargets: local);

            Assert.Single(matcher.TakePendingMatches());
            Assert.Equal(0, matcher.ResolveUntargetedGate().Hits);
        }

        [Theory]
        [InlineData("Character", "name", "Aatrox", "Characters/Aatrox")]
        [InlineData("CheatSet", "mName", "Arena", "Cheats/CheatSets/Arena")]
        public void CdragonCharacterAndCheatSetPatternsResolveEntryPath(string className, string field, string value, string expected)
        {
            uint hash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            BinContentEvidenceSource.MatchBinContentEvidence(
                CreateEntryTree(hash, className, field, value),
                matcher,
                "synthetic.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Value);
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void AnimationClipNameUsesCdragonCasing()
        {
            const string clipName = "Idle_Base";
            uint clipHash = Fnv1a.HashLower(clipName);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(clipHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var clip = new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new BinTreeProperty[]
            {
                new BinTreeStruct(Fnv1a.HashLower("mAnimationResourceData"), Fnv1a.HashLower("AnimationResourceData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mAnimationFilePath"), "ASSETS/Characters/Ahri/Animations/ahri_idle_base.anm")
                })
            });
            var clipMap = new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, clipHash), clip)
            });
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { clipMap })
            }, System.Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "synthetic-animations.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(clipName, match.Value);
        }

        [Fact]
        public void FilePathNamesRootEntryAndResolverWithRiotCasing()
        {
            const string root = "Characters/PetShark/Themes/RPG/Tier1";
            uint rootHash = Fnv1a.HashLower(root);
            uint resolverHash = Fnv1a.HashLower(root + "/Resources");
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(rootHash);
            targets[InternalHashKind.BinEntries].Add(resolverHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(rootHash, Fnv1a.HashLower("SkinCharacterDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("championSkinName"), "PetShark")
                }),
                new BinTreeObject(resolverHash, Fnv1a.HashLower("ResourceResolver"), Array.Empty<BinTreeProperty>())
            }, Array.Empty<string>());
            var casing = BinPathCasing.FromKnownNames(new[] { "Characters/Other/Themes/RPG/Tier1/Resources" });

            BinContentEvidenceSource.MatchBinContentEvidence(
                tree,
                matcher,
                "data/characters/petshark/themes/rpg/tier1.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-filepath" },
                casing: casing);

            Assert.Equal(2, matcher.Matches.Count);
            Assert.Contains(matcher.Matches, m => m.Value == root && m.CanPromote);
            Assert.Contains(matcher.Matches, m => m.Value == root + "/Resources" && m.CanPromote);
        }

        [Fact]
        public void FilePathNamesObjectNestedUnderItsFile()
        {
            const string expected = "passes/tft/assets/Firelight_Rio";
            uint hash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            BinContentEvidenceSource.MatchBinContentEvidence(
                CreateEntryTree(hash, "TftPassAsset", "internalName", "Firelight_Rio"),
                matcher,
                "passes/tft/assets",
                selectedSubMethods: new HashSet<string> { "bin-context-filepath" });

            Assert.Equal(expected, Assert.Single(matcher.Matches).Value);
        }

        [Theory]
        [InlineData("ScriptCheat", "mName", "TFT14_Virus_ForceBlob", "Cheats/GameModes/TFT/TFT14/TFT14_Virus_ForceBlob")]
        [InlineData("SpellObject", "mScriptName", "TFT14_AnimaSquad_Ship_Mis", "Maps/Shipping/Map22/Sets/TFTSet14/Spells/TFT14_AnimaSquad_Ship_Mis")]
        [InlineData("TftPlaybook", "name", "Poro", "Loadouts/TFTPlaybooks/Poro")]
        public void TftSetScopedPatternsResolveEntryPath(string className, string field, string value, string expected)
        {
            uint hash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            BinContentEvidenceSource.MatchBinContentEvidence(
                CreateEntryTree(hash, className, field, value),
                matcher,
                "synthetic.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            Assert.Equal(expected, Assert.Single(matcher.Matches).Value);
        }

        [Fact]
        public void LearnedTemplateResolvesUnknownSiblingFromResolvedExamples()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string[] known = { "Loadouts/Synthetic/Alpha", "Loadouts/Synthetic/Beta", "Loadouts/Synthetic/Gamma" };
            File.WriteAllLines(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                known.Select(name => $"{Fnv1a.HashLower(name):x8} {name}"));
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            const string expected = "Loadouts/Synthetic/Delta";
            uint unknownHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(unknownHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var learned = new BinLearnedTemplateSource(resolver);
            var objects = known.Select(name => name[(name.LastIndexOf('/') + 1)..])
                .Append("Delta")
                .Select(leaf => new BinTreeObject(
                    Fnv1a.HashLower($"Loadouts/Synthetic/{leaf}"),
                    Fnv1a.HashLower("SyntheticLoadout"),
                    new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("name"), leaf) }));

            learned.Observe(new BinTree(objects, Array.Empty<string>()), matcher, "synthetic.bin", null);
            Assert.Equal(1, learned.Apply(matcher));

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Value);
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void SameNameInAnotherCasingIsNotASecondVerifiedMatch()
        {
            const string name = "Characters/DA_18_Lux_Coven/Animations/Skin0";
            uint hash = Fnv1a.HashLower(name);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var local = CreateTargets();
            local[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);

            matcher.Check(name, InternalHashGuessStrategy.BinContent, "a.bin", localTargets: local);
            matcher.Check(name.ToLowerInvariant(), InternalHashGuessStrategy.BinContent, "b.bin", localTargets: local);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(name, match.Value);
            Assert.True(match.CanPromote);
        }

        [Fact]
        public void MapKeyVocabularyResolvesNumberedVariantOfKnownKey()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var known = Enumerable.Range(1, 20).Select(i => $"Swipe{i}").Append("Trail_01").ToList();
            File.WriteAllLines(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"),
                known.Select(name => $"{Fnv1a.HashLower(name):x8} {name}"));
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            const string expected = "Trail_07";
            uint unknownHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinHashes].Add(unknownHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var events = new BinTreeMap(
                Fnv1a.HashLower("mEventDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                known.Select(name => Fnv1a.HashLower(name)).Append(unknownHash)
                    .Select(hash => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, hash),
                        new BinTreeStruct(0, Fnv1a.HashLower("ParticleEventData"), Array.Empty<BinTreeProperty>()))));
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x11111111, Fnv1a.HashLower("AtomicClipData"), new BinTreeProperty[] { events })
            }, Array.Empty<string>());
            var learned = new BinLearnedTemplateSource(resolver);

            learned.Observe(tree, matcher, "synthetic-animations.bin", null);
            learned.Apply(matcher);

            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Value);
            Assert.True(match.CanPromote);
            Assert.True(learned.LastVocabularyGate?.Accepted);
        }

        [Fact]
        public void TrophyDataResolvesCupAndGemCountFromSkeleton()
        {
            const string expected = "Loadouts/SummonerTrophies/Trophies/Bandle_City/Trophy_8";
            uint hash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(hash, Fnv1a.HashLower("TrophyData"), new BinTreeProperty[]
                {
                    new BinTreeStruct(Fnv1a.HashLower("skinMeshProperties"), Fnv1a.HashLower("SkinMeshDataProperties"), new BinTreeProperty[]
                    {
                        new BinTreeString(Fnv1a.HashLower("skeleton"), "ASSETS/Loadouts/SummonerTrophies/Trophies/Bandle_City/Trophy.skl")
                    })
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "synthetic.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-structures" });

            Assert.Equal(expected, Assert.Single(matcher.Matches).Value);
        }

        [Fact]
        public void LearnedTemplateNeedsMinimumSupport()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{Fnv1a.HashLower("Loadouts/Synthetic/Alpha"):x8} Loadouts/Synthetic/Alpha{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();

            uint unknownHash = Fnv1a.HashLower("Loadouts/Synthetic/Delta");
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(unknownHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var learned = new BinLearnedTemplateSource(resolver);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(Fnv1a.HashLower("Loadouts/Synthetic/Alpha"), Fnv1a.HashLower("SyntheticLoadout"),
                    new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("name"), "Alpha") }),
                new BinTreeObject(unknownHash, Fnv1a.HashLower("SyntheticLoadout"),
                    new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("name"), "Delta") })
            }, Array.Empty<string>());

            learned.Observe(tree, matcher, "synthetic.bin", null);

            Assert.Equal(0, learned.Apply(matcher));
            Assert.Empty(matcher.Matches);
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

        [Fact]
        public void BinContentResolvesCommunityDragonPatterns()
        {
            var targets = CreateTargets();

            // 1. TftPassAsset: internalName -> Passes/TFT/Assets/{name}
            const string passAssetExpected = "Passes/TFT/Assets/Set13Pass";
            uint passAssetHash = Fnv1a.HashLower(passAssetExpected);
            targets[InternalHashKind.BinEntries].Add(passAssetHash);

            // 2. GuestOfHonor: name -> Maps/Shipping/Map30/GuestOfHonor/{name}
            const string guestExpected = "Maps/Shipping/Map30/GuestOfHonor/Ambessa";
            uint guestHash = Fnv1a.HashLower(guestExpected);
            targets[InternalHashKind.BinEntries].Add(guestHash);

            // 3. TftZoomSkin: name -> Loadouts/TFTZoomSkins/{name}, VfxResourceResolver -> {entry}/ResourceBin/Resources
            const string zoomExpected = "Loadouts/TFTZoomSkins/ZoomSkin01";
            uint zoomHash = Fnv1a.HashLower(zoomExpected);
            const string zoomResExpected = "Loadouts/TFTZoomSkins/ZoomSkin01/ResourceBin/Resources";
            uint zoomResHash = Fnv1a.HashLower(zoomResExpected);
            targets[InternalHashKind.BinEntries].Add(zoomHash);
            targets[InternalHashKind.BinHashes].Add(zoomResHash);

            // 4. TftItemData: mName -> Maps/Shipping/Map22/Sets/TFTSet13/Items/{name} & Augments/Shared/{name}
            const string tftItemExpected = "Maps/Shipping/Map22/Sets/TFTSet13/Items/TFT13_Item_BrawlerEmblem";
            uint tftItemHash = Fnv1a.HashLower(tftItemExpected);
            targets[InternalHashKind.BinEntries].Add(tftItemHash);

            // 5. TftItemList: name -> Maps/Shipping/Map22/Sets/TFTSet13/{name}
            const string tftListExpected = "Maps/Shipping/Map22/Sets/TFTSet13/Set13Augments";
            uint tftListHash = Fnv1a.HashLower(tftListExpected);
            targets[InternalHashKind.BinEntries].Add(tftListHash);

            // 6. TFTDamageSkin: mName -> Loadouts/TFTDamageSkins/{name}/{tiered_name}, VfxResourceResolver -> {entry}/ResourceBin/Resources
            const string dmgExpected = "Loadouts/TFTDamageSkins/Boom_Fire/Boom_Fire_Tier1";
            uint dmgHash = Fnv1a.HashLower(dmgExpected);
            const string dmgResExpected = "Loadouts/TFTDamageSkins/Boom_Fire/Boom_Fire_Tier1/ResourceBin/Resources";
            uint dmgResHash = Fnv1a.HashLower(dmgResExpected);
            targets[InternalHashKind.BinEntries].Add(dmgHash);
            targets[InternalHashKind.BinHashes].Add(dmgResHash);

            // 7. TftPlaybook: name (with spaces) -> Loadouts/TFTPlaybooks/{cleanName}, VfxResourceResolver -> {entry}/Resources
            const string playbookExpected = "Loadouts/TFTPlaybooks/TFTSet13Playbook";
            uint playbookHash = Fnv1a.HashLower(playbookExpected);
            const string playbookResExpected = "Loadouts/TFTPlaybooks/TFTSet13Playbook/Resources";
            uint playbookResHash = Fnv1a.HashLower(playbookResExpected);
            targets[InternalHashKind.BinEntries].Add(playbookHash);
            targets[InternalHashKind.BinHashes].Add(playbookResHash);

            // 8. AugmentNameId: AugmentNameId -> Maps/Shipping/Map30/AugmentTags/{augName}
            const string augTagExpected = "Maps/Shipping/Map30/AugmentTags/GoldenTicket";
            uint augTagHash = Fnv1a.HashLower(augTagExpected);
            targets[InternalHashKind.BinEntries].Add(augTagHash);

            // 9. ChallengeConfigData: ID (BinU64) -> LCU/Challenges/Config/{id}/Config
            const string challengeExpected = "LCU/Challenges/Config/202601/Config";
            uint challengeHash = Fnv1a.HashLower(challengeExpected);
            targets[InternalHashKind.BinEntries].Add(challengeHash);

            // 10. CompanionData: speciesLink -> Loadouts/Companions/Pengu
            const string companionExpected = "Loadouts/Companions/Pengu";
            uint companionHash = Fnv1a.HashLower(companionExpected);
            targets[InternalHashKind.BinEntries].Add(companionHash);

            var matcher = new InternalHashEvidenceMatcher(targets);

            var tree = new BinTree(new[]
            {
                new BinTreeObject(passAssetHash, Fnv1a.HashLower("TftPassAsset"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("internalName"), "Set13Pass")
                }),
                new BinTreeObject(guestHash, Fnv1a.HashLower("GuestOfHonor"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "Ambessa")
                }),
                new BinTreeObject(zoomHash, Fnv1a.HashLower("TftZoomSkin"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "ZoomSkin01"),
                    new BinTreeHash(Fnv1a.HashLower("VfxResourceResolver"), zoomResHash)
                }),
                new BinTreeObject(tftItemHash, Fnv1a.HashLower("TftItemData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mName"), "TFT13_Item_BrawlerEmblem")
                }),
                new BinTreeObject(tftListHash, Fnv1a.HashLower("TftItemList"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "Set13Augments")
                }),
                new BinTreeObject(dmgHash, Fnv1a.HashLower("TFTDamageSkin"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mName"), "Boom_Fire_Tier1"),
                    new BinTreeHash(Fnv1a.HashLower("VfxResourceResolver"), dmgResHash)
                }),
                new BinTreeObject(playbookHash, Fnv1a.HashLower("TftPlaybook"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "TFT Set13 Playbook"),
                    new BinTreeHash(Fnv1a.HashLower("VfxResourceResolver"), playbookResHash)
                }),
                new BinTreeObject(augTagHash, Fnv1a.HashLower("AugmentData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("AugmentNameId"), "GoldenTicket")
                }),
                new BinTreeObject(challengeHash, Fnv1a.HashLower("ChallengeConfigData"), new BinTreeProperty[]
                {
                    new BinTreeU64(Fnv1a.HashLower("ID"), 202601)
                }),
                new BinTreeObject(companionHash, Fnv1a.HashLower("CompanionSpeciesData"), Array.Empty<BinTreeProperty>()),
                new BinTreeObject(0x99999999, Fnv1a.HashLower("CompanionData"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("speciesLink"), companionExpected)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == passAssetExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == guestExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == zoomExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinHashes && m.Value == zoomResExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == tftItemExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == tftListExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == dmgExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinHashes && m.Value == dmgResExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == playbookExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinHashes && m.Value == playbookResExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == augTagExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == challengeExpected);
            Assert.Contains(matcher.Matches, m => m.Kind == InternalHashKind.BinEntries && m.Value == companionExpected);
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

        [Fact]
        public void ChallengeWithoutAnIdResolvesTheDefaultConfig()
        {
            const string expected = "LCU/Challenges/Config/0/Config";
            uint hash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(hash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(hash, Fnv1a.HashLower("ChallengeConfigData"), Array.Empty<BinTreeProperty>())
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, match => match.Kind == InternalHashKind.BinEntries && match.Value == expected);
        }

        [Theory]
        [InlineData("TFTDamageSkin", "mName", "Loadouts/TFTDamageSkins/Actual/Actual_Tier1", "ResourceBin/Resources")]
        [InlineData("TftZoomSkin", "name", "Loadouts/TFTZoomSkins/Actual", "ResourceBin/Resources")]
        [InlineData("TftPlaybook", "name", "Loadouts/TFTPlaybooks/Actual", "Resources")]
        public void VfxResolverUsesAnAlreadyVerifiedEntryPath(string type, string field, string entryPath, string suffix)
        {
            uint entryHash = Fnv1a.HashLower(entryPath);
            string expected = $"{entryPath}/{suffix}";
            uint resourceHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].UnionWith(new ulong[] { entryHash, resourceHash });
            targets[InternalHashKind.BinHashes].Add(resourceHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            matcher.CheckContextualCandidate(InternalHashKind.BinEntries, entryPath, "previous.bin", observedHash: entryHash);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(entryHash, Fnv1a.HashLower(type), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower(field), "Different_Name"),
                    new BinTreeHash(Fnv1a.HashLower("VfxResourceResolver"), resourceHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin");

            Assert.Contains(matcher.Matches, match => match.Kind == InternalHashKind.BinEntries && match.Value == expected);
            Assert.Contains(matcher.Matches, match => match.Kind == InternalHashKind.BinHashes && match.Value == expected);
        }

        [Theory]
        [InlineData("TFTDamageSkin", "mName", "Loadouts/TFTDamageSkins/Different/Different_Name", "ResourceBin/Resources")]
        [InlineData("TftZoomSkin", "name", "Loadouts/TFTZoomSkins/Different_Name", "ResourceBin/Resources")]
        [InlineData("TftPlaybook", "name", "Loadouts/TFTPlaybooks/Different_Name", "Resources")]
        public void VfxResolverDoesNotUseAnUnverifiedGuessedEntryPath(string type, string field, string entryPath, string suffix)
        {
            string expected = $"{entryPath}/{suffix}";
            uint resourceHash = Fnv1a.HashLower(expected);
            var targets = CreateTargets();
            targets[InternalHashKind.BinEntries].Add(0x12345678);
            targets[InternalHashKind.BinHashes].Add(resourceHash);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var tree = new BinTree(new[]
            {
                new BinTreeObject(0x12345678, Fnv1a.HashLower(type), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower(field), "Different_Name"),
                    new BinTreeHash(Fnv1a.HashLower("VfxResourceResolver"), resourceHash)
                })
            }, Array.Empty<string>());

            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "test.bin");

            Assert.DoesNotContain(matcher.Matches, match => match.Kind == InternalHashKind.BinHashes && match.Value == expected);
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

        private static MemoryStream CreateRstStream(int version, ulong packedHash)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RST"));
                writer.Write((byte)version);
                writer.Write((uint)1);
                writer.Write(packedHash);
            }
            stream.Position = 0;
            return stream;
        }

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

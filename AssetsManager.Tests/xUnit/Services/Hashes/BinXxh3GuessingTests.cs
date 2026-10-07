using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class BinXxh3GuessingTests
    {
        private const string Material = "Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef";
        private const ulong MaterialHash = 0xccdb6584d78a04f6;

        [Theory]
        [InlineData(Material, MaterialHash)]
        [InlineData("Characters/Jade_Evelynn/Skins/Skin34/Materials/Jade_Evelynn_material_inst", 0xbc0d4a1d0012f3d3UL)]
        [InlineData("Characters/Jade_Evelynn/Skins/Skin40/Materials/Jade_Evelynn_material_inst", 0x450167cc0457491dUL)]
        public void MatchesPbeMaterialVectorsWithLowercaseXxh3(string name, ulong expected)
        {
            Assert.Equal(expected, InternalHashEvidenceMatcher.ComputeBinXxh3(name));
            Assert.Equal(expected, InternalHashEvidenceMatcher.ComputeBinXxh3(name.ToUpperInvariant().Replace('/', '\\')));
            var targets = Targets();
            targets[InternalHashKind.BinXxh3].Add(expected);
            var matcher = new InternalHashEvidenceMatcher(targets);

            Assert.True(matcher.CheckBinXxh3Candidate(name, "test", observedHash: expected));
            Assert.False(matcher.CheckBinXxh3Candidate(name.ToUpperInvariant(), "test", observedHash: expected));
            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(expected, match.Hash);
            Assert.Equal(expected, match.LookupHash);
            Assert.Equal(64, match.HashBits);
            Assert.Equal(expected.ToString("x16"), match.HashText);
            Assert.Equal("BIN XXH3-64", match.DomainText);
            Assert.True(match.CanPromote);
            Assert.Single(matcher.TakePendingMatches());
            Assert.Empty(matcher.TakePendingMatches());
            Assert.Equal(0, matcher.GetRemainingCount(InternalHashKind.BinXxh3));
        }

        [Fact]
        public void WideMatchesDoNotConsumeFNVGameOrRstTargetsOrTruncatedValues()
        {
            var targets = Targets();
            uint fnv = Fnv1a.HashLower(Material);
            ulong truncated = MaterialHash & ((1UL << 38) - 1);
            ulong xxh64 = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(Material.ToLowerInvariant()));
            targets[InternalHashKind.BinXxh3].UnionWith(new[] { MaterialHash, truncated, xxh64, (ulong)fnv });
            targets[InternalHashKind.BinHashes].Add(fnv);
            targets[InternalHashKind.RstXxh3].UnionWith(new[] { MaterialHash, truncated });
            targets[InternalHashKind.RstXxh64].Add(xxh64);
            var matcher = new InternalHashEvidenceMatcher(targets);

            Assert.False(matcher.CheckContextualCandidate(InternalHashKind.BinXxh3, Material, "FNV context"));
            Assert.False(matcher.CheckResearchCandidate(InternalHashKind.BinXxh3, Material,
                InternalHashGuessStrategy.CrossDictionary, "FNV research", InternalHashEvidence.ObservedHashPair));
            Assert.False(matcher.CheckBinXxh3Candidate(Material, "wrong observed hash", observedHash: truncated));
            Assert.True(matcher.CheckBinXxh3Candidate(Material, "wide"));

            Assert.Single(matcher.Matches);
            Assert.Equal(3, matcher.GetRemainingCount(InternalHashKind.BinXxh3));
            Assert.Contains(truncated, targets[InternalHashKind.BinXxh3]);
            Assert.Contains(xxh64, targets[InternalHashKind.BinXxh3]);
            Assert.Contains((ulong)fnv, targets[InternalHashKind.BinXxh3]);
            Assert.Single(targets[InternalHashKind.BinHashes]);
            Assert.Equal(2, targets[InternalHashKind.RstXxh3].Count);
            Assert.Single(targets[InternalHashKind.RstXxh64]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task InventoryGuessPersistReloadAndPreviewUseSeparateFullWidthCatalog(bool owningEntry)
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("wide-bins");
            byte[] bytes = WriteTree(MaterialTree(owningEntry ? Fnv1a.HashLower(Material) : 123));
            await File.WriteAllBytesAsync(Path.Combine(root, "skin10.bin"), bytes);
            string entries = Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt");
            await File.WriteAllTextAsync(entries, $"{Fnv1a.HashLower(Material):x8} {Material}\n");
            string fnvCatalog = Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt");
            const string originalFnv = "12345678 ExistingFNVName\n";
            await File.WriteAllTextAsync(fnvCatalog, originalFnv);
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"), "8d39bde6 name\n");
            var store = new BinRstHashGuessingStore(bridge.Directories);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var http = new HttpClient(new SchemaHandler());
            var service = Service(bridge, store, resolver, http);
            await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);

            Assert.Contains(MaterialHash, await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None));
            Assert.Equal("ccdb6584d78a04f6", (await File.ReadAllTextAsync(
                Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh364.txt"))).Trim());
            InternalHashSummary summary = await store.LoadSummaryAsync(CancellationToken.None);
            Assert.Equal(1, summary.BinXxh3);
            Assert.Equal(summary.BinEntries + summary.BinFields + summary.BinTypes + summary.BinHashes + 1, summary.BinTotal);
            Assert.Empty(await store.LoadUnknownAsync(InternalHashKind.RstXxh3, CancellationToken.None));
            var serializer = new BinRitobinSerializer(resolver);
            Assert.Contains("name: hash = 0xccdb6584d78a04f6", await serializer.WriteBinTreeAsRitobinAsync(bytes));

            InternalHashRunResult result = await service.RunContentGuessingAsync(root, true, false, null,
                CancellationToken.None, new HashSet<string> { "bin-context-xxh3" });

            InternalHashGuessMatch match = Assert.Single(result.Matches);
            Assert.Equal(InternalHashKind.BinXxh3, match.Kind);
            Assert.Equal(Material, match.Value);
            Assert.Equal("hashes.bin.xxh364.txt", Path.GetFileName(store.GetKnownPath(match.Kind)));
            Assert.Equal(owningEntry ? InternalHashGuessStrategy.BinContent : InternalHashGuessStrategy.CrossDictionary, match.Strategy);
            Assert.Equal($"ccdb6584d78a04f6 {Material}", (await File.ReadAllTextAsync(store.GetKnownPath(match.Kind))).Trim());
            Assert.Empty(await store.LoadUnknownAsync(match.Kind, CancellationToken.None));
            Assert.Equal(originalFnv, await File.ReadAllTextAsync(fnvCatalog));
            Assert.False(File.Exists(store.GetKnownPath(InternalHashKind.RstXxh3)));
            Assert.Equal(Material, resolver.ResolveBinXxh3(MaterialHash));
            Assert.Contains($"name: hash = \"{Material}\"", await serializer.WriteBinTreeAsRitobinAsync(bytes));
            var diff = await serializer.WriteBinDiffAsRitobinAsync(bytes, WriteTree(MaterialTree(123, MaterialHash + 1)));
            Assert.Contains($"name: hash = \"{Material}\"", diff.OldRitobin);
            Assert.Contains($"name: hash = 0x{MaterialHash + 1:x16}", diff.NewRitobin);

            using var restarted = new HashResolverService(bridge.Directories, bridge.LogService);
            await restarted.LoadAllHashesAsync();
            Assert.Equal(Material, restarted.ResolveBinXxh3(MaterialHash));
            Assert.Equal((MaterialHash ^ (1UL << 60)).ToString("x16"), restarted.ResolveBinXxh3(MaterialHash ^ (1UL << 60)));
            Assert.Equal("0000000012345678", restarted.ResolveBinXxh3(0x12345678));
            Assert.Equal("ExistingFNVName", restarted.ResolveBinHash(0x12345678));
        }

        [Fact]
        public async Task PreviousCatalogNameLoadsAndIsPreservedWhenWritingTheRenamedCatalog()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string legacy = Path.Combine(bridge.Directories.HashesPath, "hashes.bin.xxh3.txt");
            string legacyContent = $"{MaterialHash:x16} {Material}\n";
            await File.WriteAllTextAsync(legacy, legacyContent);
            var store = new BinRstHashGuessingStore(bridge.Directories);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            Assert.True(resolver.HasLocalHashCatalogs);
            await resolver.LoadAllHashesAsync();
            Assert.Equal(Material, resolver.ResolveBinXxh3(MaterialHash));
            Assert.Equal(Material, (await store.LoadKnownAsync(InternalHashKind.BinXxh3, CancellationToken.None))[MaterialHash]);

            const string nextName = "Characters/Jade_Evelynn/Skins/Skin34/Materials/Jade_Evelynn_material_inst";
            const ulong nextHash = 0xbc0d4a1d0012f3d3;
            await store.SaveMatchesAsync(new[] { new InternalHashGuessMatch
            {
                Kind = InternalHashKind.BinXxh3, Hash = nextHash, LookupHash = nextHash, HashBits = 64, Value = nextName,
                IsVerified = true, Confidence = InternalHashConfidence.Verified, Evidence = InternalHashEvidence.ObservedHashPair,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema
            } }, CancellationToken.None);

            Assert.Equal("hashes.bin.xxh364.txt", Path.GetFileName(store.GetKnownPath(InternalHashKind.BinXxh3)));
            var known = await store.LoadKnownAsync(InternalHashKind.BinXxh3, CancellationToken.None);
            Assert.Equal(2, known.Count);
            Assert.Equal(Material, known[MaterialHash]);
            Assert.Equal(nextName, known[nextHash]);
            Assert.Equal(legacyContent, await File.ReadAllTextAsync(legacy));
            resolver.ReloadBinRstHashes();
            Assert.Equal(Material, resolver.ResolveBinXxh3(MaterialHash));
            Assert.Equal(nextName, resolver.ResolveBinXxh3(nextHash));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SearchUnknownsFromWadsCollectsWideNamesAndRescansChangedPatch(bool knownPaths)
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("pbe-inventory");
            string wadDirectory = Path.Combine(root, "Game", "DATA", "FINAL", "Champions");
            Directory.CreateDirectory(wadDirectory);
            string wadPath = Path.Combine(wadDirectory, "Evelynn.wad.client");
            const string knownPath = "data/characters/evelynn/skins/skin10.bin";
            const string unknownPath = "data/characters/evelynn/skins/skin11.bin";
            const string nextPatchPath = "data/characters/evelynn/skins/skin12.bin";
            const ulong unknownHash = 0xbc0d4a1d0012f3d3;
            const ulong nextPatchHash = 0x450167cc0457491d;
            byte[] knownBytes = WriteTree(MaterialTree(1));
            byte[] unknownBytes = WriteTree(MaterialTree(2, unknownHash));
            byte[] nextPatchBytes = WriteTree(MaterialTree(3, nextPatchHash));
            var entries = new List<WadBakeEntry>
            {
                new(knownPath, () => new MemoryStream(knownBytes), WadChunkCompression.Zstd),
                new(unknownPath, () => new MemoryStream(unknownBytes), WadChunkCompression.None)
            };
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());
            if (knownPaths)
                await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"),
                    $"{XxHash64Ext.Hash(knownPath):x16} {knownPath}\n{XxHash64Ext.Hash(unknownPath):x16} {unknownPath}\n");
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.bin.xxh364.txt"),
                $"{MaterialHash:x16} {Material}\n");
            var store = new BinRstHashGuessingStore(bridge.Directories);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var http = new HttpClient(new SchemaHandler());
            var service = Service(bridge, store, resolver, http);

            InternalHashInventory first = await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);
            Assert.Equal(2, first.ScannedBins);
            Assert.Equal(unknownHash, Assert.Single(await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None)));
            Assert.Empty(await store.LoadUnknownAsync(InternalHashKind.BinHashes, CancellationToken.None));
            Assert.Empty(await store.LoadUnknownAsync(InternalHashKind.RstXxh3, CancellationToken.None));

            entries.Add(new(nextPatchPath, () => new MemoryStream(nextPatchBytes), WadChunkCompression.Zstd));
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());
            InternalHashInventory second = await service.BuildInventoryAsync(root, true, false, null, CancellationToken.None);
            Assert.Equal(3, second.ScannedBins);
            HashSet<ulong> unknowns = await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None);
            Assert.Equal(2, unknowns.Count);
            Assert.Contains(unknownHash, unknowns);
            Assert.Contains(nextPatchHash, unknowns);
            Assert.DoesNotContain(MaterialHash, unknowns);
            Assert.Equal(2, (await store.LoadSummaryAsync(CancellationToken.None)).BinXxh3);
        }

        [Fact]
        public async Task OldInventoryIsRebuiltOnceAndMethodSelectionIsRespected()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("inventory-migration");
            await File.WriteAllBytesAsync(Path.Combine(root, "skin10.bin"), WriteTree(MaterialTree(1)));
            Directory.CreateDirectory(bridge.Directories.HashLabPath);
            string marker = Path.Combine(bridge.Directories.HashLabPath, "internal.bin.patch.txt");
            await File.WriteAllTextAsync(marker, "old inventory");
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{Fnv1a.HashLower(Material):x8} {Material}\n");
            var store = new BinRstHashGuessingStore(bridge.Directories);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var http = new HttpClient(new SchemaHandler());
            var service = Service(bridge, store, resolver, http);

            InternalHashRunResult excluded = await service.RunContentGuessingAsync(root, true, false, null,
                CancellationToken.None, new HashSet<string> { "bin-context-owning" });
            Assert.Empty(excluded.Matches);
            Assert.Contains(MaterialHash, await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None));
            string markerAfterMigration = await File.ReadAllTextAsync(marker);
            Assert.NotEqual("old inventory", markerAfterMigration);
            InternalHashRunResult enabled = await service.RunContentGuessingAsync(root, true, false, null,
                CancellationToken.None, new HashSet<string> { "bin-context-xxh3" });
            Assert.Single(enabled.Matches);
            Assert.Equal(markerAfterMigration, await File.ReadAllTextAsync(marker));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task PreviousUnknownFileNamesMergeWithoutLosingFullWidthValues(bool loadSummary)
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            Directory.CreateDirectory(bridge.Directories.HashLabPath);
            const ulong first = 0xbc0d4a1d0012f3d3;
            const ulong second = 0x450167cc0457491d;
            const ulong historical = 0xf123456789abcdef;
            string oldUnknown = Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh3.txt");
            string oldCurrent = Path.Combine(bridge.Directories.HashLabPath, "current.bin.xxh3.txt");
            string canonical = Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh364.txt");
            await File.WriteAllTextAsync(oldUnknown, $"{MaterialHash:x16}\n{first:x16}\n0000000000000000\n");
            await File.WriteAllTextAsync(oldCurrent, $"{second:x16}\n{first:x16}\n");
            await File.WriteAllTextAsync(canonical, $"{historical:x16}\n{first:x16}\n");
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.bin.xxh364.txt"),
                $"{MaterialHash:x16} {Material}\n");
            string rst = Path.Combine(bridge.Directories.HashLabPath, "unknowns.rst.xxh3.38.txt");
            const string rstContents = "0000000012345678\n";
            await File.WriteAllTextAsync(rst, rstContents);
            var store = new BinRstHashGuessingStore(bridge.Directories);

            if (loadSummary) await store.LoadSummaryAsync(CancellationToken.None);
            else await store.SaveMatchesAsync(Array.Empty<InternalHashGuessMatch>(), CancellationToken.None);

            Assert.False(File.Exists(oldUnknown));
            Assert.False(File.Exists(oldCurrent));
            Assert.Equal(new[] { $"{second:x16}", $"{first:x16}", $"{historical:x16}" },
                await File.ReadAllLinesAsync(canonical));
            Assert.Equal(3, (await store.LoadSummaryAsync(CancellationToken.None)).BinXxh3);
            Assert.Equal(rstContents, await File.ReadAllTextAsync(rst));
            Assert.Equal(3, (await store.LoadCurrentUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None)).Count);
        }

        [Fact]
        public async Task PreviousUnknownNameMigratesWithoutRebuildingAnExistingInventory()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            Directory.CreateDirectory(bridge.Directories.HashLabPath);
            string root = bridge.CreateDirectory("renamed-inventory");
            string marker = Path.Combine(bridge.Directories.HashLabPath, "internal.bin.patch.txt");
            await File.WriteAllTextAsync(marker, "existing inventory");
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh3.txt"),
                $"{MaterialHash:x16}\n");
            await File.WriteAllTextAsync(Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{Fnv1a.HashLower(Material):x8} {Material}\n");
            var store = new BinRstHashGuessingStore(bridge.Directories);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            using var http = new HttpClient(new SchemaHandler());
            var service = Service(bridge, store, resolver, http);

            Assert.Single((await service.RunContentGuessingAsync(root, true, false, null, CancellationToken.None,
                new HashSet<string> { "bin-context-xxh3" })).Matches);
            Assert.Equal("existing inventory", await File.ReadAllTextAsync(marker));
            Assert.True(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh364.txt")));
            Assert.False(File.Exists(Path.Combine(bridge.Directories.HashLabPath, "unknowns.bin.xxh3.txt")));
            Assert.Empty(await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None));
        }

        [Fact]
        public void NestedMaterialValuesAndLocalStringsAreInventoriedAndMatched()
        {
            var tree = new BinTree(new[]
            {
                new BinTreeObject(1, 123, new BinTreeProperty[]
                {
                    new BinTreeOptional(2, new BinTreeEmbedded(0, 0xff9d3409, new BinTreeProperty[]
                    {
                        new BinTreeHash64(0x8d39bde6, MaterialHash),
                        new BinTreeString(3, Material)
                    }))
                })
            }, Array.Empty<string>());
            var observed = Targets();
            BinRstHashGuessingService.ReadBinInventory(tree, observed);
            Assert.Contains(MaterialHash, observed[InternalHashKind.BinXxh3]);
            Assert.Empty(observed[InternalHashKind.BinHashes]);
            var matcher = new InternalHashEvidenceMatcher(observed);
            BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, "nested.bin",
                selectedSubMethods: new HashSet<string> { "bin-context-xxh3" });
            InternalHashGuessMatch match = Assert.Single(matcher.Matches);
            Assert.Equal(MaterialHash, match.Hash);
            Assert.Equal("nested.bin", match.SourceBin);
        }

        [Fact]
        public async Task StoreRejectsUnverifiedOrTruncatedWideMatchesAndKeepsFullUnknowns()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            var store = new BinRstHashGuessingStore(bridge.Directories);
            var inventory = Targets();
            inventory[InternalHashKind.BinXxh3].UnionWith(new[] { MaterialHash, (ulong)unchecked((uint)MaterialHash) });
            await store.SaveInventoryAsync(inventory, "test", "bin", CancellationToken.None);
            var invalid = new[]
            {
                WideMatch(MaterialHash, false, 64),
                WideMatch(unchecked((uint)MaterialHash), true, 32),
                WideMatch(MaterialHash ^ (1UL << 60), true, 64)
            };
            await store.SaveMatchesAsync(invalid, CancellationToken.None);

            Assert.False(File.Exists(store.GetKnownPath(InternalHashKind.BinXxh3)));
            Assert.Equal(2, (await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None)).Count);
            await store.SaveMatchesAsync(new[] { WideMatch(MaterialHash, true, 64) }, CancellationToken.None);
            Assert.Equal((ulong)unchecked((uint)MaterialHash), Assert.Single(await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None)));
            await store.SaveInventoryAsync(inventory, "next", "bin", CancellationToken.None);
            Assert.Equal((ulong)unchecked((uint)MaterialHash), Assert.Single(await store.LoadUnknownAsync(InternalHashKind.BinXxh3, CancellationToken.None)));
        }

        private static InternalHashGuessMatch WideMatch(ulong hash, bool verified, int bits) => new()
        {
            Kind = InternalHashKind.BinXxh3, Hash = hash, LookupHash = hash, HashBits = bits, Value = Material,
            IsVerified = verified, Confidence = verified ? InternalHashConfidence.Verified : InternalHashConfidence.Candidate,
            Evidence = InternalHashEvidence.ObservedHashPair, VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema
        };

        private static Dictionary<InternalHashKind, HashSet<ulong>> Targets() =>
            Enum.GetValues<InternalHashKind>().ToDictionary(kind => kind, _ => new HashSet<ulong>());

        private static BinTree MaterialTree(uint entry, ulong hash = MaterialHash) => new(new[]
        {
            new BinTreeObject(entry, 0xff9d3409, new BinTreeProperty[]
            {
                new BinTreeHash64(0x8d39bde6, hash),
                new BinTreeUnorderedContainer(0x0a6f0eb5, BinPropertyType.Embedded, Array.Empty<BinTreeProperty>())
            })
        }, Array.Empty<string>());

        private static byte[] WriteTree(BinTree tree)
        {
            using var stream = new MemoryStream();
            tree.Write(stream);
            return stream.ToArray();
        }

        private static BinRstHashGuessingService Service(AssetsManagerTestBridge bridge,
            BinRstHashGuessingStore store, HashResolverService resolver, HttpClient http) =>
            new(store, new HashGuessPersistenceService(new HashGuessingStore(bridge.Directories), store), resolver,
                bridge.Directories, bridge.LogService, new MetaSchemaHashSource(http, bridge.Directories, bridge.LogService));

        private sealed class SchemaHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"latest\":\"test\",\"classes\":{}}")
                });
        }
    }
}

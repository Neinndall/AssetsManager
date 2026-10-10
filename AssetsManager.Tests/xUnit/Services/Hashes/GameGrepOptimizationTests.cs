using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameGrepOptimizationTests
    {
        [Fact]
        public async Task GrepServiceAppliesCachedExtensionsToRepeatedUnknownChunkPaths()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string root = bridge.CreateDirectory("Game");
            const string chunkPath = "data/shared/unknown.preload", target = "data/shared/particles/rare_effect.troybin";
            foreach (var fixture in new[] { ("a.wad.client", "PreLoadBuildingBlocks = {}"),
                ("b.wad.client", "PreLoadBuildingBlocks = {Name=\"rare_effect.troy\"}") })
                bridge.BakeWad(root, fixture.Item1, (chunkPath, fixture.Item2));
            var store = new HashGuessingStore(bridge.Directories);
            var targets = new HashSet<ulong> { XxHash64Ext.Hash(target) };
            await store.SaveUnknownHashesAsync(HashGuessDomain.Game, targets, targets, "", CancellationToken.None);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            var persistence = new HashGuessPersistenceService(store, new BinRstHashGuessingStore(bridge.Directories));
            var service = new HashGuessingService(resolver, store, persistence, bridge.LogService, bridge.Directories);

            var result = await service.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, root, null, CancellationToken.None);

            Assert.Equal(target, Assert.Single(result.Matches).Path);
            Assert.Equal(2, result.ScannedChunks);
            Assert.Empty(await store.LoadUnknownHashesAsync(HashGuessDomain.Game, CancellationToken.None));
        }

        [Theory]
        [InlineData("0123456789abcdef", false)]
        [InlineData("0123456789abcdef", true)]
        [InlineData("example.bin", false)]
        [InlineData("example.dat", false)]
        [InlineData("example.tex", false)]
        public void PropertyBinSignaturesSelectStructuredGrepRegardlessOfFilename(string source, bool patch)
        {
            const string target = "assets/ui/cards/moon_guardian.tex";
            var tree = new BinTree(new[] { new BinTreeObject(1, 100, new BinTreeProperty[]
            {
                new BinTreeString(20, "moon_guardian"), new BinTreeString(30, "assets/ui/cards/forest_spirit.tex"),
                new BinTreeWadChunkLink(40, XxHash64Ext.Hash(target))
            }) }, Array.Empty<string>());
            using var serialized = new MemoryStream();
            if (patch) { using var writer = new BinaryWriter(serialized, System.Text.Encoding.ASCII, true); writer.Write("PTCH"u8); writer.Write(1U); writer.Write(0U); }
            tree.Write(serialized);
            if (patch) { using var writer = new BinaryWriter(serialized, System.Text.Encoding.ASCII, true); writer.Write(0U); }
            AssertGrepReference(serialized.ToArray(), source, target);
        }

        [Theory]
        [InlineData("characters/example/scripts/action.lua", "assets/characters/example/scripts/action.luabin64")]
        [InlineData("characters/example/images/icon.png", "assets/characters/example/images/icon.dds")]
        [InlineData("maps/mapgeometry/example/example", "maps/mapgeometry/example/example")]
        [InlineData("maps/mapgeometry/example/example.mapgeo", "data/maps/mapgeometry/example/example.mapgeo")]
        [InlineData("maps/mapgeometry/example/example.materials.bin", "data/maps/mapgeometry/example/example.materials.bin")]
        [InlineData("assets/shaders/test.ps", "assets/shaders/test.ps")]
        [InlineData("shaders/test.ps", "assets/shaders/generated/shaders/test.ps-dx11_2")]
        public void BinReferencesPreservePathsAndExpandTheirActualExtensions(string reference, string expected)
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(reference);
            byte[] bytes = new byte[text.Length + 2];
            bytes[0] = (byte)text.Length;
            bytes[1] = (byte)(text.Length >> 8);
            text.CopyTo(bytes, 2);
            AssertGrepReference(bytes, "data/test.bin", expected);
        }

        [Theory]
        [InlineData("#include\t\"../shared/common.hlsl\"", "assets/shaders/shared/common.hlsl")]
        [InlineData("  # include  \"../shared/common.hlsl\"", "assets/shaders/shared/common.hlsl")]
        [InlineData("#include \"assets/shaders/shared/common.hlsl\"", "assets/shaders/shared/common.hlsl")]
        [InlineData("#include <../shared/common.hlsl>", "assets/shaders/shared/common.hlsl")]
        public void ShaderIncludesResolveWhitespaceAndVirtualPaths(string text, string expected)
            => AssertGrepReference(System.Text.Encoding.ASCII.GetBytes(text), "assets/shaders/effects/main.hlsl", expected);

        [Theory]
        [InlineData("../shared/icon.tex\r\n", "assets/ui/shared/icon.tex")]
        [InlineData("assets/ui/shared/icon.tex\n", "assets/ui/shared/icon.tex")]
        public void TextAtlasReferencesResolveVirtualPaths(string text, string expected)
            => AssertGrepReference(System.Text.Encoding.ASCII.GetBytes(text), "assets/ui/cards/main.atlas", expected);

        [Theory]
        [InlineData("Name = 'logic/test.preload'", "data/shared/logic/test.preload")]
        [InlineData("Name=\"../logic/test\"", "data/logic/test.preload")]
        [InlineData("Name=\"data/shared/particles/test.troy\"", "data/shared/particles/test.troybin")]
        public void PreloadReferencesPreserveExtensionsAndResolvePaths(string text, string expected)
            => AssertGrepReference(System.Text.Encoding.ASCII.GetBytes(text), "data/shared/test.preload", expected);

        private static void AssertGrepReference(byte[] bytes, string source, string expected)
        {
            var buffer = new byte[bytes.Length + 23];
            bytes.CopyTo(buffer, 11);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(expected), 42 });
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            guesser.GrepWad(engine, new ArraySegment<byte>(buffer, 11, bytes.Length), source, "example.wad", 7);
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(expected, match.Path);
            Assert.Equal(7UL, match.SourceChunkHash);
            Assert.Equal(new[] { 42UL }, engine.UnknownHashes);
        }

        [Fact]
        public void SiblingPathsReachPatchOverrideStructures()
        {
            const string target = "assets/ui/cards/moon_guardian.tex";
            var scope = new BinTreeStruct(10, 100, new BinTreeProperty[]
            {
                new BinTreeString(20, "moon_guardian"), new BinTreeString(30, "assets/ui/cards/forest_spirit.tex"),
                new BinTreeWadChunkLink(40, XxHash64Ext.Hash(target))
            });
            var wrapper = new BinTree(new[] { new BinTreeObject(1, 100, new BinTreeProperty[] { scope }) }, Array.Empty<string>());
            using var serialized = new MemoryStream();
            wrapper.Write(serialized);
            // PROP header, class table, object header and property header precede the struct payload.
            byte[] payload = serialized.ToArray()[35..];
            using var patch = new MemoryStream();
            using var writer = new BinaryWriter(patch, System.Text.Encoding.ASCII, true);
            writer.Write("PTCH"u8); writer.Write(1U); writer.Write(0U);
            writer.Write("PROP"u8); writer.Write(3U); writer.Write(0U); writer.Write(0U);
            writer.Write(1U); writer.Write(1U); writer.Write((uint)(1 + 2 + "scope".Length + payload.Length));
            writer.Write((byte)BinPropertyType.Struct); writer.Write((ushort)"scope".Length);
            writer.Write("scope"u8); writer.Write(payload);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            guesser.GrepWad(engine, patch.ToArray(), "example.bin", "example.wad", 1);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(17)]
        public void MalformedPropertiesPreserveDottedEntriesAndEmbeddedPaths(int bufferOffset)
        {
            const string dotted = "loadouts/tftdamageskins.12345678.bin", embedded = "assets/ui/cards/example.tex";
            var tree = new BinTree(new[] { new BinTreeObject(0x12345678, 100,
                new BinTreeProperty[] { new BinTreeString(10, embedded) }) }, Array.Empty<string>());
            using var serialized = new MemoryStream();
            tree.Write(serialized);
            byte[] bytes = serialized.ToArray();
            bytes[34] = 0xff;
            var buffer = new byte[bufferOffset + bytes.Length + 9];
            bytes.CopyTo(buffer, bufferOffset);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong> { XxHash64Ext.Hash(dotted), XxHash64Ext.Hash(embedded), 42 });
            guesser.GrepWad(engine, new ArraySegment<byte>(buffer, bufferOffset, bytes.Length), "broken.bin", "Global.wad.client", 7);
            Assert.Equal(2, engine.Matches.Count);
            Assert.All(engine.Matches.Values, match => Assert.Equal(7UL, match.SourceChunkHash));
        }

        [Fact]
        public void AnimationClipNameAppliesToEveryNestedResource()
        {
            const string action = "rare_sequence_zebra";
            string[] targets = { "assets/characters/example/skins/skin5/animations/first_resource.anm",
                $"data/characters/example/skins/skin5/animations/{action}.anm" };
            var resources = targets.Select((path, index) => (BinTreeProperty)new BinTreeStruct((uint)index + 1,
                Fnv1a.HashLower("AnimationResourceData"), new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), XxHash64Ext.Hash(path))
                })).ToArray();
            var map = new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Fnv1a.HashLower(action)),
                    new BinTreeStruct(0, 100, resources)) });
            var tree = new BinTree(new[] { new BinTreeObject(1, 100, new BinTreeProperty[] { map }) }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { targets[0] }), null,
                hash => hash == Fnv1a.HashLower(action) ? action : null);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(targets[1]) });
            guesser.GrepWad(engine, stream.ToArray(), "data/characters/example/animations/skin5.bin", "example.wad", 1);
            Assert.Equal(targets[1], Assert.Single(engine.Matches).Value.Path);
            Assert.All(engine.Matches.Values, match => Assert.Equal(HashGuessStrategy.AnimationBinLink, match.Strategy));
        }

        [Fact]
        public void ResolvedSkinObjectDoesNotSkipFollowingSkinObjects()
        {
            const string first = "assets/characters/example/skins/skin1/example_skin1_tx_cm.tex";
            const string second = "assets/characters/other/skins/skin2/custombody_tx_cm.tex";
            BinTreeObject Skin(uint id, string mesh, string texture) => new(id, Fnv1a.HashLower("SkinCharacterDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeStruct(Fnv1a.HashLower("skinMeshProperties"), Fnv1a.HashLower("SkinMeshDataProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(0xd6a00df6, mesh), new BinTreeWadChunkLink(30, XxHash64Ext.Hash(texture))
                        })
                });
            var material = new BinTreeObject(3, Fnv1a.HashLower("StaticMaterialDef"), new BinTreeProperty[]
            {
                new BinTreeString(0x8d39bde6, "characters/example/skins/skin1/materials/body"),
                new BinTreeWadChunkLink(30, XxHash64Ext.Hash(first))
            });
            var tree = new BinTree(new[] { Skin(1, "assets/characters/example/skins/skin1/body.skn", first),
                Skin(2, "assets/characters/other/skins/skin2/custombody.skn", second), material }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new[] { XxHash64Ext.Hash(first), XxHash64Ext.Hash(second) }.ToHashSet());
            guesser.GrepWad(engine, stream.ToArray(), "combined.bin", "example.wad", 1);
            Assert.Equal(2, engine.Matches.Count);
        }

        [Fact]
        public void SiblingPathsReachStructInsideContainerMapAndOptional()
        {
            const string target = "assets/ui/cards/moon_guardian.tex";
            var scope = new BinTreeStruct(0, 100, new BinTreeProperty[]
            {
                new BinTreeString(10, "Moon_Guardian"), new BinTreeString(20, "assets/ui/cards/forest_spirit.tex"),
                new BinTreeWadChunkLink(30, XxHash64Ext.Hash(target))
            });
            var map = new BinTreeMap(40, BinPropertyType.Hash, BinPropertyType.Optional,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1), new BinTreeOptional(0, scope)) });
            var container = new BinTreeContainer(50, BinPropertyType.Map, new BinTreeProperty[] { map });
            var tree = new BinTree(new[] { new BinTreeObject(1, 100, new BinTreeProperty[] { container }) }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            guesser.GrepWad(engine, stream.ToArray(), "example.bin", "example.wad", 1);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Theory]
        [InlineData("example", "skin1", false)]
        [InlineData("jade_example", "skin01", false)]
        [InlineData("petexample", "summer", true)]
        [InlineData("éxample", "skin100", false)]
        public void OptimizedAnimationMatchingPreservesLegacyPathsAndOrder(string character, string skin, bool theme)
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            foreach (string name in new[] { "attack01a", "spawn_in", "variant_out", "idle_cycle", new string('a', 520) })
            {
                string[] skins = skin == "skin1" ? new[] { "skin1", "skin01" }
                    : skin == "skin01" ? new[] { "skin01", "skin1" } : new[] { skin };
                string[] modifiers = { "", "test_" };
                string[] suffixes = { "", "_end" };
                var expected = new List<string>();
                foreach (string sk in skins)
                foreach (string pre in modifiers)
                foreach (string suf in suffixes)
                {
                    string stem = pre + name + suf;
                    var variants = new List<string> { stem };
                    foreach (var (from, to) in new[] { ("variant", "varient"), ("spawn", "spwan"), ("_in", "in"), ("_out", "out"), ("_cycle", "cycle") })
                        if (stem.Contains(from)) variants.Add(stem.Replace(from, to));
                    foreach (string variant in variants)
                    foreach (string root in new[] { "assets", "data" })
                    {
                        string directory = $"{root}/characters/{character}/{(theme ? "themes" : "skins")}/{sk}/animations/";
                        var prefixes = new List<string> { "", character + "_", character + "_" + sk + "_", sk + "_" };
                        if (!theme && character.StartsWith("jade_")) prefixes.AddRange(new[] { character[5..] + "_", character[5..] + "_" + sk + "_" });
                        expected.AddRange(prefixes.Select(prefix => directory + prefix + variant + ".anm"));
                    }
                }
                var hashes = expected.Select(path => XxHash64Ext.Hash(path)).ToHashSet();
                hashes.Add(42);
                Assert.Equal(expected.Distinct(), guesser.MatchAnimationVariants(name + ".anm", character, skin, hashes, modifiers, suffixes, theme));
                Assert.Equal(new[] { 42UL }, hashes);
                var legacy = expected.ToHashSet(StringComparer.Ordinal);
                Assert.Equal(expected.Distinct(), GameHashGuesser.EnumerateAnimationNameVariants(character, skin, name, modifiers, suffixes, theme).Distinct().Where(legacy.Contains));
            }
        }

        [Theory]
        [InlineData("skin3", "skin03", "skin3_win_01")]
        [InlineData("skin03", "skin3", "skin03_win_01")]
        [InlineData("skin3", "skin03", "example_skin3_win_01")]
        public void AnimationFolderAndFilenamePaddingAreIndependent(string skin, string directorySkin, string filename)
        {
            string path = $"assets/characters/example/skins/{directorySkin}/animations/{filename}.anm";
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var remaining = new HashSet<ulong> { XxHash64Ext.Hash(path), 42 };
            Assert.Equal(new[] { path }, guesser.MatchAnimationVariants("win_01", "example", skin, remaining));
            Assert.Equal(new[] { 42UL }, remaining);
            Assert.Contains(path, GameHashGuesser.EnumerateAnimationNameVariants("example", skin, "win_01"));
        }

        [Fact]
        public void RegaliaCachePreservesIndependentEnginesAndChunkProvenance()
        {
            const string seed = "assets/loadouts/summoneremotes/event/example_glow.tex";
            const string target = "assets/loadouts/summoneremotes/event/example_selector.tex";
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { seed }));
            ulong hash = XxHash64Ext.Hash(target);
            var tree = new BinTree(new[] { new BinTreeObject(1, 2, new BinTreeProperty[] { new BinTreeWadChunkLink(3, hash) }) }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            foreach (ulong chunk in new[] { 100UL, 200UL })
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { hash, 42 });
                guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "data/loadouts/example.bin", "Global.wad.client", chunk);
                var match = Assert.Single(engine.Matches).Value;
                Assert.Equal(target, match.Path);
                Assert.Equal(chunk, match.SourceChunkHash);
                Assert.Equal("Global.wad.client", match.SourceWadPath);
                Assert.Contains(42UL, engine.UnknownHashes);
            }
        }

        [Fact]
        public void RegaliaGrepRetainsOnlyTargetsAndMatchesBasicSuiteOrder()
        {
            string[] seeds =
            {
                "assets/loadouts/summoneremotes/event/example_glow.tex",
                "assets/loadouts/regalia/banners/gold_banner_256x256.tex",
                "data/characters/petexample/themes/summer/model.bin",
                "assets/ux/tft/troves_bannercontent/demo/example.tex",
                "assets/loadouts/tftdamageskins/example_tier1.tex"
            };
            string[] targets =
            {
                "assets/loadouts/summoneremotes/event/example_selector.tex",
                "assets/loadouts/regalia/banners/ranked_solo_5s_gold_banner_256x256.tex",
                "loadouts/companions/example_summer_tier1.cutscene.bin",
                "assets/ux/tft/troves_bannercontent/demo/example.dds",
                "loadouts/tftdamageskins/example_tier3.resourcebin.bin"
            };
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, seeds));
            var hashes = targets.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet();
            var basicOrder = new List<string>();
            var basic = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(hashes), match => basicOrder.Add(match.Path));
            guesser.GuessRegaliaAssets(basic, CancellationToken.None);
            Assert.Equal(targets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), basicOrder);

            var grepOrder = new List<string>();
            var grep = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(hashes), match => grepOrder.Add(match.Path));
            GrepLinks(guesser, grep, hashes, 100);
            Assert.Equal(basicOrder, grepOrder);
            Assert.Equal(new[] { 42UL }, grep.UnknownHashes);

            // The index holds five matches, despite thousands of generated combinations.
            object cache = typeof(GameHashGuesser).GetField("_regaliaMatches", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(guesser);
            object[] args = { grep, null };
            Assert.True((bool)cache.GetType().GetMethod("TryGetValue").Invoke(cache, args));
            var paths = (Dictionary<ulong, string>)args[1].GetType().GetProperty("Paths").GetValue(args[1]);
            Assert.Equal(targets.Length, paths.Count);
        }

        [Fact]
        public void RegaliaGrepHandlesTargetsIntroducedAfterItsInitialSnapshot()
        {
            string[] targets =
            {
                "assets/loadouts/summoneremotes/event/first_selector.tex",
                "assets/loadouts/summoneremotes/event/second_selector.tex"
            };
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, targets.Select(path => path.Replace("_selector", "_glow"))));
            ulong first = XxHash64Ext.Hash(targets[0]);
            ulong second = XxHash64Ext.Hash(targets[1]);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { first, 42 });
            GrepLinks(guesser, engine, new[] { first }, 100);
            engine.EnsureUnknown(second);
            GrepLinks(guesser, engine, new[] { second }, 200);
            Assert.Equal(2, engine.Matches.Count);
            Assert.Equal(targets[1], engine.Matches[second].Path);
            Assert.Equal(200UL, engine.Matches[second].SourceChunkHash);
        }

        private static void GrepLinks(GameHashGuesser guesser, HashGuessEngine engine, IEnumerable<ulong> hashes, ulong chunk)
        {
            var tree = new BinTree(new[]
            {
                new BinTreeObject(1, 2, hashes.Select((hash, index) => (BinTreeProperty)new BinTreeWadChunkLink((uint)index + 3, hash)).ToArray())
            }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "data/loadouts/example.bin", "Global.wad.client", chunk);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Hashes
{
    public sealed class WadHashGuesserTests
    {
        [Fact]
        public async Task PersistedInventoryCanBeReusedWithoutScanningWads()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-hash-{Guid.NewGuid():N}");
            try
            {
                var store = new HashGuessingStore(new DirectoriesCreator(root));
                var pending = new HashSet<ulong> { 1, 2 };
                var current = new HashSet<ulong> { 2 };
                await store.SaveUnknownHashesAsync(HashGuessDomain.Lcu, pending, current, "LCU:patch", CancellationToken.None);

                HashUnknownInventory inventory = await store.LoadUnknownInventoryAsync(HashGuessDomain.Lcu, CancellationToken.None);

                Assert.Equal(pending, inventory.All);
                Assert.Equal(current, inventory.Current);
                Assert.Equal("LCU:patch", inventory.PatchFingerprint);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task HashMergeWritesCdtbCompatibleLfWithoutBom()
        {
            string path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path,
                    "0000000000000001 assets/a.bin\r\n0000000000000003 assets/c.bin\r\n");
                var incoming = new Dictionary<ulong, string>
                {
                    [2] = "assets/b.bin"
                };

                await HashGuessingStore.MergeHashFileAsync(path, incoming, CancellationToken.None);

                byte[] bytes = await File.ReadAllBytesAsync(path);
                string content = Encoding.UTF8.GetString(bytes);
                Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
                Assert.DoesNotContain('\r', content);
                Assert.Equal(
                    "0000000000000001 assets/a.bin\n0000000000000002 assets/b.bin\n0000000000000003 assets/c.bin\n",
                    content);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void GuessersOwnTheirDomainAndWadPattern()
        {
            var game = new GameHashGuesser();
            var lcu = new LcuHashGuesser(Array.Empty<string>(), null);

            Assert.Equal(HashGuessDomain.Game, game.Domain);
            Assert.Equal("*.wad.client", game.WadPattern);
            Assert.Equal(HashGuessDomain.Lcu, lcu.Domain);
            Assert.Equal("*.wad", lcu.WadPattern);
        }

        [Fact]
        public void CorpusCollectionsAreReusedWithinTheSameRevision()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.bin",
                "assets/characters/lux/lux.bin"
            }));

            Assert.Same(game.KnownPaths, game.KnownPaths);
            Assert.Same(game.DirectoryList(), game.DirectoryList());
            Assert.Same(game.BuildWordlist(), game.BuildWordlist());
            Assert.Same(game.BuildSwordlist(), game.BuildSwordlist());
        }

        [Fact]
        public void SwordlistUsesBasenamesFromPathsContainingBin()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.bin",
                "assets/characters/lux/lux.bin.meta",
                "assets/characters/zed/zed.dds"
            }));

            IReadOnlyList<string> swordlist = game.BuildSwordlist();

            Assert.Contains("ahri", swordlist);
            Assert.Contains("lux", swordlist);
            Assert.DoesNotContain("zed", swordlist);
        }

        [Fact]
        public void GeneralWordlistMatchesPythonTokenAndNumericFiltering()
        {
            IReadOnlyList<string> words = HashGuessEngine.BuildWordlist(new[]
            {
                "assets/123/scene.json",
                "assets/maps/1234",
                "assets/foo_42-bar.data"
            });

            Assert.Contains("assets", words);
            Assert.Contains("foo", words);
            Assert.Contains("42", words);
            Assert.Contains("bar", words);
            Assert.DoesNotContain("123", words);
            Assert.DoesNotContain("1234", words);
            Assert.DoesNotContain("json", words);
            Assert.DoesNotContain("data", words);
        }

        [Fact]
        public void GrepDoesNotReadPooledArrayPaddingOutsideTheOwnedSegment()
        {
            const string hiddenPath = "assets/characters/ahri/hidden.bin";
            byte[] prefix = Encoding.ASCII.GetBytes("no paths here");
            byte[] padding = Encoding.ASCII.GetBytes(hiddenPath);
            byte[] pooledArray = prefix.Concat(padding).ToArray();
            var engine = CreateEngine(HashGuessDomain.Game, hiddenPath);
            var game = new GameHashGuesser();

            game.GrepWad(engine, new ArraySegment<byte>(pooledArray, 0, prefix.Length), "test.txt", "test.wad.client", 1);

            Assert.Equal(1, engine.RemainingUnknownCount);
        }

        [Fact]
        public void EngineTelemetryCountsCheckedAndDiscardedCandidates()
        {
            const string expected = "assets/test.bin";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            engine.Check("assets/missing.bin", HashGuessStrategy.EmbeddedPathGrep);
            engine.Check(expected, HashGuessStrategy.EmbeddedPathGrep);
            HashGuessProgress progress = engine.CreateProgress("test");

            Assert.Equal(2, progress.CheckedCandidates);
            Assert.Equal(1, progress.DiscardedCandidates);
            Assert.Equal(1, progress.FoundMatches);
            Assert.True(progress.CandidatesPerSecond > 0);
            Assert.True(progress.ManagedMemoryBytes > 0);
        }

        [Fact]
        public void ComposedPathHashingMatchesRegularUtf8HashingWithoutLengthLimits()
        {
            string[] relativePaths = { "imágenes/icono-é.js", new string('a', 1_100) + ".json" };
            foreach (string relativePath in relativePaths)
            {
                const string directory = "plugins/rcp-fe-test/global/default";
                string expected = directory + "/" + relativePath;
                var engine = CreateEngine(HashGuessDomain.Lcu, expected);

                Assert.True(engine.CheckCombined(
                    directory,
                    relativePath,
                    HashGuessStrategy.LcuRelativeBasename,
                    "test.wad",
                    1));
                AssertResolved(engine, expected);
            }
        }

        [Fact]
        public void WadGrepDoesNotRunNumberedShaderAttack()
        {
            const string numberedShader = "assets/shaders/generated/shaders/test.ps_2_0.dx11_100";
            var game = new GameHashGuesser();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
            {
                XxHash64Ext.Hash(numberedShader)
            });

            game.GrepFile(engine, data: Encoding.ASCII.GetBytes("SHADERS/test"));

            Assert.Empty(engine.Matches);
            Assert.Equal(1, engine.RemainingUnknownCount);
        }

        [Fact]
        public void LcuGrepCombinesRelativeBasenamesWithKnownDirectories()
        {
            const string expected = "plugins/rcp-fe-test/global/default/images/icon.png";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(
                new[] { "plugins/rcp-fe-test/global/default/existing.json" },
                null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("const icon = \"images/icon.png\";"),
                "plugins/rcp-fe-test/global/default/init.js",
                "test.wad",
                1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuLootTranslationGrepAddsHextechImagePaths()
        {
            const string expected = "plugins/rcp-be-lol-game-data/global/default/v1/hextech-images/item.png";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("{\"item\":{}}"),
                "plugins/rcp-fe-lol-loot/global/default/trans.json",
                "loot.wad",
                2);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuPluginDescriptionGrepAddsCommonPluginPaths()
        {
            const string expected = "plugins/rcp-fe-test/global/default/init.js";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("{\"pluginDependencies\":[],\"name\":\"rcp-fe-test\"}"),
                "plugins/rcp-fe-test/global/default/description.json",
                "test.wad",
                3);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuChampionSummaryGrepAddsChampionAndSplashMetadata()
        {
            const string champion = "plugins/rcp-be-lol-game-data/global/default/v1/champions/123.json";
            const string splash = "plugins/rcp-be-lol-game-data/global/default/v1/champion-splashes/123/metadata.json";
            var engine = new HashGuessEngine(
                HashGuessDomain.Lcu,
                new[] { champion, splash }.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("[{\"id\":123}]"),
                "plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json",
                "game.wad",
                4);

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Equal(
                new[] { champion, splash }.OrderBy(path => path),
                engine.Matches.Values.Select(match => match.Path).OrderBy(path => path));
        }

        [Fact]
        public void LcuRecommendedItemsGrepAddsGameDataPaths()
        {
            const string expected = "plugins/rcp-be-lol-game-data/global/default/data/items/1001.json";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("{\"recommendedItemDefaults\":[\"/data/items/1001.json\"]}"),
                "plugins/rcp-be-lol-game-data/global/default/v1/items.json",
                "game.wad",
                5);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuGrepCoversFrontendDataAndGameDataAssetReferences()
        {
            const string frontend = "plugins/rcp-fe-test/global/default/init.js";
            const string data = "plugins/rcp-be-lol-game-data/global/default/data/items/1001.json";
            const string asset = "plugins/rcp-be-lol-game-data/global/default/data/characters/ahri/ahri.json";
            var engine = new HashGuessEngine(
                HashGuessDomain.Lcu,
                new[] { frontend, data, asset }.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("fe/test/init.js /DATA/items/1001.json lol-game-data/assets/data/characters/ahri/ahri.json"),
                "init.js",
                "game.wad",
                6);

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Equal(
                new[] { asset, data, frontend },
                engine.Matches.Values.Select(match => match.Path).OrderBy(path => path));
        }

        [Fact]
        public void LcuGrepCoversTemplateFileNameAndSourceMapBasenames()
        {
            const string directory = "plugins/rcp-fe-test/global/default";
            const string template = directory + "/abc/template.html";
            const string fileName = directory + "/icon.png";
            const string sourceMap = directory + "/map.js";
            var engine = new HashGuessEngine(
                HashGuessDomain.Lcu,
                new[] { template, fileName, sourceMap }.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            var guesser = new LcuHashGuesser(new[] { directory + "/existing.json" }, null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("<template id=\"app-template-abc\"></template> \"icon.png\" sourceMappingURL=map.js.map"),
                directory + "/init.js",
                "test.wad",
                7);

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Equal(
                new[] { fileName, sourceMap, template }.OrderBy(path => path),
                engine.Matches.Values.Select(match => match.Path).OrderBy(path => path));
        }

        [Fact]
        public void LcuSplashGrepUsesCdtbNameAndFileCartesianProduct()
        {
            const string expected = "plugins/rcp-fe-lol-splash/global/default/splash-assets/one/common.webm";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);
            const string json = "{\"musicVolume\":1,\"files\":{\"intro\":\"music-splash-one.webm\",\"shared\":\"common.webm\"}}";

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes(json),
                "plugins/rcp-fe-lol-splash/global/default/config.json",
                "test.wad",
                2);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameBinGrepExpandsCharacterAndLuaPaths()
        {
            const string source = "Characters/Ahri/Spells/Test.lua";
            const string expected = "assets/characters/ahri/spells/test.lua";
            byte[] path = Encoding.ASCII.GetBytes(source);
            byte[] data = new byte[path.Length + 2];
            data[0] = (byte)(path.Length & 0xff);
            data[1] = (byte)(path.Length >> 8);
            path.CopyTo(data, 2);
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            var guesser = new GameHashGuesser();

            guesser.GrepWad(engine, data, "data/test.bin", "test.wad.client", 3);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameBinGrepResolvesDottedBinsFromObjectPaths()
        {
            const string expected = "loadouts/tftdamageskins.79a3aef6.bin";
            var tree = new BinTree(
                new[]
                {
                    new BinTreeObject(
                        0x11111111,
                        Fnv1a.HashLower("TftDamageSkinCeremony"),
                        new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("effectKey"), "test") }),
                    new BinTreeObject(0x79a3aef6, Fnv1a.HashLower("VfxSystemDefinitionData"), Array.Empty<BinTreeProperty>())
                },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "loadouts/tftdamageskins.00000000.bin"
            }));
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(stream.ToArray()),
                "ac3005bb6bb022eb.bin",
                "Global.wad.client",
                0xac3005bb6bb022eb);

            AssertResolved(engine, expected);
            HashGuessMatch match = Assert.Single(engine.Matches).Value;
            Assert.Equal(HashGuessStrategy.BinEntry, match.Strategy);
            Assert.Equal("Global.wad.client", match.SourceWadPath);
            Assert.Equal(0xac3005bb6bb022ebUL, match.SourceChunkHash);
        }

        [Fact]
        public void GameAnimationBinLinksResolveContextualAnmPath()
        {
            const string expected = "assets/characters/seraphine/skins/skin69/animations/joke_start.anm";
            ulong targetHash = XxHash64Ext.Hash(expected);
            var animationResource = new BinTreeStruct(
                Fnv1a.HashLower("mAnimationResourceData"),
                Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), targetHash)
                });
            var clip = new BinTreeStruct(
                0,
                Fnv1a.HashLower("AtomicClipData"),
                new BinTreeProperty[] { animationResource });
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, Fnv1a.HashLower("JokeStart")),
                        clip)
                });
            var tree = new BinTree(
                new[]
                {
                    new BinTreeObject(0x12345678, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { map })
                },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);

            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/seraphine/skins/skin10/animations/joke_start.anm"
            }));
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(stream.ToArray()),
                "data/characters/seraphine/animations/skin69.bin",
                "Seraphine.wad.client",
                0x1234UL);

            AssertResolved(engine, expected);
            HashGuessMatch match = Assert.Single(engine.Matches).Value;
            Assert.Equal(HashGuessStrategy.AnimationBinLink, match.Strategy);
            Assert.Equal("Seraphine.wad.client", match.SourceWadPath);
            Assert.Equal(0x1234UL, match.SourceChunkHash);
        }

        [Fact]
        public void GameAnimationBinLinksUseClipNameHashesAndNumericVariants()
        {
            const string happy = "assets/characters/seraphine/skins/skin69/animations/joke_happy.anm";
            const string sad = "assets/characters/seraphine/skins/skin69/animations/joke_sad.anm";
            const string passive = "assets/characters/seraphine/skins/skin69/animations/passive_attack_-180.anm";
            var entries = new[]
            {
                CreateClip("joke_happy", happy),
                CreateClip("joke_sad", sad),
                CreateClip("passive_attack_left", passive)
            };
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                entries);
            var tree = new BinTree(
                new[]
                {
                    new BinTreeObject(0x12345678, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { map })
                },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);

            var resolvedNames = new Dictionary<uint, string>
            {
                [Fnv1a.HashLower("joke_happy")] = "joke_happy",
                [Fnv1a.HashLower("joke_sad")] = "joke_sad"
            };
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/seraphine/skins/skin10/animations/joke_start.anm",
                "assets/characters/seraphine/skins/skin10/animations/passive_attack_-90.anm",
                "assets/shared/happy/file.bin",
                "assets/shared/sad/file.bin"
            }), null, hash => resolvedNames.GetValueOrDefault(hash));
            var engine = new HashGuessEngine(
                HashGuessDomain.Game,
                new[] { happy, sad, passive }.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            game.GrepWad(
                engine,
                new ArraySegment<byte>(stream.ToArray()),
                "data/characters/seraphine/animations/skin69.bin",
                "Seraphine.wad.client",
                0x5678UL);

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Equal(new[] { happy, sad, passive }, engine.Matches.Values.Select(match => match.Path).OrderBy(path => path));
            Assert.All(engine.Matches.Values, match => Assert.Equal(HashGuessStrategy.AnimationBinLink, match.Strategy));

            static KeyValuePair<BinTreeProperty, BinTreeProperty> CreateClip(string name, string path)
            {
                var resource = new BinTreeStruct(
                    Fnv1a.HashLower("mAnimationResourceData"),
                    Fnv1a.HashLower("AnimationResourceData"),
                    new BinTreeProperty[]
                    {
                        new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), XxHash64Ext.Hash(path))
                    });
                var clip = new BinTreeStruct(
                    0,
                    Fnv1a.HashLower("AtomicClipData"),
                    new BinTreeProperty[] { resource });
                return new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeHash(0, Fnv1a.HashLower(name)),
                    clip);
            }
        }

        [Fact]
        public void GameAnimationBinLinksFindNestedDataPathFromResolvedClipHash()
        {
            const string expected = "data/characters/seraphine/skins/skin69/animations/seraphine_skin69_spell1.anm";
            var resource = new BinTreeStruct(
                0,
                Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), XxHash64Ext.Hash(expected))
                });
            var clip = new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new[] { resource });
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, Fnv1a.HashLower("spell1")),
                        clip)
                });
            var wrapper = new BinTreeStruct(0, Fnv1a.HashLower("Wrapper"), new BinTreeProperty[] { map });
            var tree = new BinTree(
                new[] { new BinTreeObject(1, Fnv1a.HashLower("Container"), new BinTreeProperty[] { wrapper }) },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);

            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, hash =>
                hash == Fnv1a.HashLower("spell1") ? "spell1" : null);
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(stream.ToArray()),
                "data/characters/seraphine/animations/skin69.bin",
                "Seraphine.wad.client",
                1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameAnimationBinLinksKeepLegacyStringPaths()
        {
            const string expected = "assets/characters/seraphine/skins/skin69/animations/spell1.anm";
            var resource = new BinTreeStruct(
                0,
                Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("mAnimationFilePath"), expected) });
            var clip = new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new[] { resource });
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Fnv1a.HashLower("spell1")), clip)
                });
            var tree = new BinTree(
                new[] { new BinTreeObject(1, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { map }) },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);

            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(stream.ToArray()),
                "data/characters/seraphine/animations/skin69.bin",
                "Seraphine.wad.client",
                1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameAnimationBinLinksReuseContextualPrefixes()
        {
            const string expected = "assets/characters/seraphine/skins/skin69/animations/p_spell1_to_idle.anm";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/seraphine/skins/skin10/animations/spell1_to_idle.anm",
                "assets/characters/seraphine/skins/skin10/animations/p_spell1_to_run.anm",
                "assets/characters/seraphine/skins/skin10/animations/spell1_to_run.anm"
            }));
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(CreateAnimationBin("p_spell1_to_idle", XxHash64Ext.Hash(expected))),
                "data/characters/seraphine/animations/skin69.bin",
                "Seraphine.wad.client",
                1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void NormalizationPreservesLongRiotCandidates()
        {
            string candidate = "assets/" + new string('a', 600) + ".bin";

            Assert.Equal(candidate, PathUtils.NormalizePath(candidate));
        }

        [Fact]
        public void CommonWadTextDecoderMatchesUtf8SigAndRejectsInvalidUtf8()
        {
            byte[] text = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("café")).ToArray();

            Assert.True(HashGuesser.TryDecodeWadText(text, out string decoded));
            Assert.Equal("café", decoded);
            Assert.False(HashGuesser.TryDecodeWadText(new byte[] { 0xff, 0xfe }, out _));
        }

        [Fact]
        public void LcuGrepSkipsTheWholeChunkWhenUtf8IsInvalid()
        {
            const string hiddenPath = "plugins/rcp-fe-test/global/default/hidden.js";
            byte[] path = Encoding.ASCII.GetBytes(hiddenPath);
            byte[] data = new byte[path.Length + 1];
            data[0] = 0xFF;
            path.CopyTo(data, 1);
            var engine = CreateEngine(HashGuessDomain.Lcu, hiddenPath);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);

            guesser.GrepWad(engine, data, "init.js", "test.wad", 1);

            Assert.Equal(1, engine.RemainingUnknownCount);
            Assert.Empty(engine.Matches);
        }

        [Fact]
        public void LcuMalformedJsonContinuesGeneralGrepWithoutAConfiguredLogger()
        {
            const string expected = "plugins/rcp-fe-test/global/default/hidden.js";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);
            byte[] data = Encoding.UTF8.GetBytes("{\"broken\":,\"path\":\"plugins/rcp-fe-test/global/default/hidden.js\"}");

            guesser.GrepWad(engine, data, "broken.json", "test.wad", 1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameFallbackMatchesCdtbNonOverlappingRanges()
        {
            const string nestedPath = "data/nested.bin";
            var engine = CreateEngine(HashGuessDomain.Game, nestedPath);
            var guesser = new GameHashGuesser();

            guesser.GrepFile(engine, data: Encoding.ASCII.GetBytes("ASSETS/root/DATA/nested.bin"));

            Assert.Equal(1, engine.RemainingUnknownCount);
            Assert.Empty(engine.Matches);
        }

        [Fact]
        public void GameImageAutoAtlasResolvesEmbeddedSpritesAndIconPaths()
        {
            const string expected1 = "assets/items/icons2d/autoatlas/largeicons/1001_class_t1_bootsofspeed.png";
            const string expected2 = "assets/items/icons2d/autoatlas/largeicons/1040_obsidianedge.png";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/items/icons2d/1001_class_t1_bootsofspeed.png",
                "assets/items/icons2d/1040_obsidianedge.dds"
            }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
            {
                XxHash64Ext.Hash(expected1),
                XxHash64Ext.Hash(expected2)
            });

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(new byte[] { 0x49, 0x4D, 0x41, 0x41 }); // IMAA
            writer.Write((uint)2); // version 2
            writer.Write((ulong)0x1111); // tex0
            writer.Write((ulong)0x2222); // tex1
            writer.Write((uint)2); // count = 2
            writer.Write(XxHash64Ext.Hash(expected1));
            writer.Write(0.1f); writer.Write(0.1f); writer.Write(0.2f); writer.Write(0.2f);
            writer.Write((uint)0);
            writer.Write(XxHash64Ext.Hash(expected2));
            writer.Write(0.3f); writer.Write(0.3f); writer.Write(0.4f); writer.Write(0.4f);
            writer.Write((uint)0);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(ms.ToArray()),
                "assets/items/icons2d/autoatlas/largeicons/atlas_info.bin",
                "Global.wad.client",
                0xabcdef1234567890);

            Assert.Equal(2, engine.Matches.Count);
            Assert.Equal(expected1, engine.Matches[XxHash64Ext.Hash(expected1)].Path);
            Assert.Equal(expected2, engine.Matches[XxHash64Ext.Hash(expected2)].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Fact]
        public void GameImageAutoAtlasResolvesSmallIconsAndDynamicVariants()
        {
            const string expected1 = "assets/items/icons2d/autoatlas/smallicons/2003_healthpotion_64px.milkshake_env.dds";
            const string expected2 = "assets/items/icons2d/autoatlas/smallicons/strawberry/9308_riven_weapon.tex";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/items/icons2d/2003_healthpotion_64px.milkshake_env.dds",
                "assets/items/icons2d/autoatlas/smallicons/strawberry/9308_riven_weapon.tex"
            }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
            {
                XxHash64Ext.Hash(expected1),
                XxHash64Ext.Hash(expected2)
            });

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(new byte[] { 0x49, 0x4D, 0x41, 0x41 }); // IMAA
            writer.Write((uint)2); // version 2
            writer.Write((ulong)0x1111); // tex0
            writer.Write((ulong)0x2222); // tex1
            writer.Write((uint)2); // count = 2
            writer.Write(XxHash64Ext.Hash(expected1));
            writer.Write(0.1f); writer.Write(0.1f); writer.Write(0.2f); writer.Write(0.2f);
            writer.Write((uint)0);
            writer.Write(XxHash64Ext.Hash(expected2));
            writer.Write(0.3f); writer.Write(0.3f); writer.Write(0.4f); writer.Write(0.4f);
            writer.Write((uint)0);

            game.GrepWad(
                engine,
                new ArraySegment<byte>(ms.ToArray()),
                "assets/items/icons2d/autoatlas/smallicons/atlas_info.bin",
                "Global.wad.client",
                0xabcdef1234567890);

            Assert.Equal(2, engine.Matches.Count);
            Assert.Equal(expected1, engine.Matches[XxHash64Ext.Hash(expected1)].Path);
            Assert.Equal(expected2, engine.Matches[XxHash64Ext.Hash(expected2)].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }


        [Fact]
        public void TestFromWadsExtractsSmallIconsSprites()
        {
            string globalWad = @"C:\Riot Games\League of Legends (PBE)\Game\DATA\FINAL\Global.wad.client";
            if (!File.Exists(globalWad))
                globalWad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Global.wad.client";
            if (!File.Exists(globalWad)) return;

            var guesser = new GameHashGuesser();
            var inventory = guesser.FromWads(new[] { globalWad }, CancellationToken.None);

            // Sprite hash 0x00676bcb6e800d01 from PBE smallicons atlas_info.bin
            ulong spriteHash1 = 0x00676bcb6e800d01;
            Assert.True(inventory.Hashes.Contains(spriteHash1), "FromWads failed to include sprite 0x00676bcb6e800d01 from PBE smallicons");
            Assert.True(inventory.Hashes.Count > 400, $"Expected > 400 hashes in inventory, got {inventory.Hashes.Count}");
        }

        [Fact]
        public void GameFromWadsExtractsImageAutoAtlasSprites()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "Global.wad.client");
            ulong spriteHash1 = 0x1234567890abcdef;
            ulong spriteHash2 = 0xfedcba0987654321;

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(new byte[] { 0x49, 0x4D, 0x41, 0x41 }); // IMAA
                writer.Write((uint)2); // version
                writer.Write((ulong)0x1111); // tex0
                writer.Write((ulong)0x2222); // tex1
                writer.Write((uint)2); // count
                writer.Write(spriteHash1);
                writer.Write(0.1f); writer.Write(0.1f); writer.Write(0.2f); writer.Write(0.2f);
                writer.Write((uint)0);
                writer.Write(spriteHash2);
                writer.Write(0.3f); writer.Write(0.3f); writer.Write(0.4f); writer.Write(0.4f);
                writer.Write((uint)0);
            }
            byte[] imaaData = ms.ToArray();

            var entries = new[]
            {
                new WadBakeEntry(
                    "assets/items/icons2d/autoatlas/largeicons/atlas_info.bin",
                    () => new MemoryStream(imaaData),
                    WadChunkCompression.None)
            };
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());

            try
            {
                var guesser = new GameHashGuesser();
                var inventory = guesser.FromWads(new[] { wadPath }, CancellationToken.None);

                Assert.Contains(spriteHash1, inventory.Hashes);
                Assert.Contains(spriteHash2, inventory.Hashes);
                Assert.Contains(XxHash64Ext.Hash("assets/items/icons2d/autoatlas/largeicons/atlas_info.bin"), inventory.Hashes);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void LcuFromWadsExtractsChunksDirectlyWithoutAtlasOverhead()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "rcp-fe-lol-test.wad");

            const string path1 = "plugins/rcp-fe-lol-test/global/default/app.js";
            const string path2 = "plugins/rcp-fe-lol-test/global/default/manifest.json";
            var entries = new[]
            {
                new WadBakeEntry(path1, () => new MemoryStream(Encoding.UTF8.GetBytes("console.log('hi');")), WadChunkCompression.None),
                new WadBakeEntry(path2, () => new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"test\"}")), WadChunkCompression.None)
            };
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());

            try
            {
                var guesser = new LcuHashGuesser(Array.Empty<string>(), null);
                var inventory = guesser.FromWads(new[] { wadPath }, CancellationToken.None);

                Assert.Equal(2, inventory.Hashes.Count);
                Assert.Contains(XxHash64Ext.Hash(path1), inventory.Hashes);
                Assert.Contains(XxHash64Ext.Hash(path2), inventory.Hashes);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void LcuMirrorDirectoriesDiscoversMirroredPaths()
        {
            const string knownPath = "plugins/rcp-fe-lol-static-assets/global/default/images/aram-wardrobe/open-lock.png";
            const string expectedTarget = "plugins/rcp-fe-lol-static-assets/global/default/aram-wardrobe/open-lock.png";
            ulong targetHash = XxHash64Ext.Hash(expectedTarget);

            var guesser = new LcuHashGuesser(new[] { knownPath }, null);
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, new HashSet<ulong> { targetHash });

            int checkedCount = guesser.MirrorDirectories(engine, CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.True(engine.Matches.ContainsKey(targetHash));
            Assert.Equal(expectedTarget, engine.Matches[targetHash].Path);
        }

        [Fact]
        public void LcuGuessPatternsDiscoversSanctumAndTrackerPaths()
        {
            const string sanctumTarget = "plugins/rcp-fe-lol-static-assets/global/default/sanctum/card-frame-tier1-back.svg";
            const string trackerTarget = "plugins/rcp-fe-lol-static-assets/global/default/reward-tracker/current-left.svg";
            ulong sanctumHash = XxHash64Ext.Hash(sanctumTarget);
            ulong trackerHash = XxHash64Ext.Hash(trackerTarget);

            var guesser = new LcuHashGuesser(Array.Empty<string>(), null);
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, new HashSet<ulong> { sanctumHash, trackerHash });

            guesser.GuessPatterns(engine, CancellationToken.None);

            Assert.True(engine.Matches.ContainsKey(sanctumHash));
            Assert.Equal(sanctumTarget, engine.Matches[sanctumHash].Path);
            Assert.True(engine.Matches.ContainsKey(trackerHash));
            Assert.Equal(trackerTarget, engine.Matches[trackerHash].Path);
        }

        [Fact]
        public void LcuBuildMediaSwordlistExtractsTokensFromWebmAndOggPaths()
        {
            var paths = new[]
            {
                "plugins/rcp-fe-lol-static-assets/global/default/videos/ranked/tier-promotion-to-gold.webm",
                "plugins/rcp-fe-lol-static-assets/global/default/sounds/honor/sfx-honor-vote-outro.ogg",
                "plugins/rcp-fe-lol-static-assets/global/default/images/icon.png"
            };

            var guesser = new LcuHashGuesser(paths, null);
            var words = guesser.BuildMediaSwordlist();

            Assert.Contains("tier", words);
            Assert.Contains("promotion", words);
            Assert.Contains("gold", words);
            Assert.Contains("honor", words);
            Assert.Contains("outro", words);
            Assert.DoesNotContain("icon", words);
        }

        [Fact]
        public void LcuUniversalPluginModifierAttackDiscoversModifierVariants()
        {
            const string basePath = "plugins/rcp-fe-lol-store/global/default/images/button.png";
            const string expectedTarget = "plugins/rcp-fe-lol-store/global/default/images/button-hover.png";
            ulong targetHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(expectedTarget);

            var guesser = new LcuHashGuesser(new[] { basePath }, null);
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, new HashSet<ulong> { targetHash });

            int checkedCount = guesser.UniversalPluginModifierAttack(engine, CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.True(engine.Matches.ContainsKey(targetHash));
            Assert.Equal(expectedTarget, engine.Matches[targetHash].Path);
        }

        [Fact]
        public void LcuRunScopedPluginAttacksDiscoversIntraPluginAssets()
        {
            var known = new[]
            {
                "plugins/rcp-fe-lol-store/global/default/storefront/addon/public/img/silvershields.svg",
                "plugins/rcp-fe-lol-store/global/default/storefront/addon/public/img/sprite-source/lcu-sale.png",
                "plugins/rcp-fe-lol-loot/global/default/assets/loot_item_icons/chest_10.png"
            };

            const string expectedTarget = "plugins/rcp-fe-lol-store/global/default/storefront/addon/public/img/sprite-source/silvershields.svg";
            ulong targetHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(expectedTarget);

            var guesser = new LcuHashGuesser(known, null);
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, new HashSet<ulong> { targetHash });

            int checkedCount = guesser.RunScopedPluginAttacks(engine, null, CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.True(engine.Matches.ContainsKey(targetHash));
            Assert.Equal(expectedTarget, engine.Matches[targetHash].Path);
        }

        [Fact]
        public void GameFallbackRequiresContentAfterThePrefixLikeCdtbRegex()
        {
            const string barePrefix = "assets/";
            var engine = CreateEngine(HashGuessDomain.Game, barePrefix);
            var guesser = new GameHashGuesser();

            guesser.GrepFile(engine, data: Encoding.ASCII.GetBytes("ASSETS/\0"));

            Assert.Equal(1, engine.RemainingUnknownCount);
            Assert.Empty(engine.Matches);
        }

        [Fact]
        public void GameBinGrepRejectsNonAsciiPathsLikeCdtb()
        {
            const string replacementPath = "assets/test?.bin";
            byte[] path = Encoding.ASCII.GetBytes("ASSETS/test?.bin");
            path[11] = 0xFF;
            byte[] data = new byte[path.Length + 2];
            data[0] = (byte)path.Length;
            path.CopyTo(data, 2);
            var engine = CreateEngine(HashGuessDomain.Game, replacementPath);
            var guesser = new GameHashGuesser();

            guesser.GrepWad(engine, data, "data/test.bin", "test.wad.client", 1);

            Assert.Equal(1, engine.RemainingUnknownCount);
            Assert.Empty(engine.Matches);
        }

        [Fact]
        public void GamePreloadUsesCdtbExclusiveBranchingForTroyReferences()
        {
            const string rawTroy = "particles/test.troy";
            const string expected = "data/shared/particles/particles/test.troybin";
            byte[] data = Encoding.ASCII.GetBytes("Name=\"particles/test.troy\"");
            var expectedEngine = CreateEngine(HashGuessDomain.Game, expected);
            var rawEngine = CreateEngine(HashGuessDomain.Game, rawTroy);
            var guesser = new GameHashGuesser();

            guesser.GrepWad(expectedEngine, data, "data/shared/test.preload", "test.wad.client", 1);
            guesser.GrepWad(rawEngine, data, "data/shared/test.preload", "test.wad.client", 1);

            AssertResolved(expectedEngine, expected);
            Assert.Equal(1, rawEngine.RemainingUnknownCount);
            Assert.Empty(rawEngine.Matches);
        }

        [Fact]
        public void TestShaderStep500IsMatched()
        {
            const string expected = "assets/shaders/generated/shaders/skinnedmesh/hkg_outline.ps-dx11_500";
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/shaders/generated/shaders/skinnedmesh/hkg_outline.ps" }));

            guesser.GuessShaderVariants(engine, CancellationToken.None);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GamePreloadAddsOnlyTheContextualPreloadForOrdinaryNames()
        {
            const string rawName = "logic/test";
            const string expected = "data/shared/logic/test.preload";
            byte[] data = Encoding.ASCII.GetBytes("Name=\"logic/test\"");
            var expectedEngine = CreateEngine(HashGuessDomain.Game, expected);
            var rawEngine = CreateEngine(HashGuessDomain.Game, rawName);
            var guesser = new GameHashGuesser();

            guesser.GrepWad(expectedEngine, data, "data/shared/test.preload", "test.wad.client", 1);
            guesser.GrepWad(rawEngine, data, "data/shared/test.preload", "test.wad.client", 1);

            AssertResolved(expectedEngine, expected);
            Assert.Equal(1, rawEngine.RemainingUnknownCount);
            Assert.Empty(rawEngine.Matches);
        }

        [Fact]
        public void HashFileCachesAndAutomaticallyReloadsChangedKnownPaths()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "0000000000000001 Assets/Test.bin\n");
                var hashFile = new HashFile(HashGuessDomain.Game, path);

                Assert.Equal("assets/test.bin", Assert.Single(hashFile.Load()).Value);
                File.WriteAllText(path, "0000000000000002 Assets/Changed.bin\n");
                Assert.Equal("assets/changed.bin", Assert.Single(hashFile.Load()).Value);
                Assert.Equal("assets/changed.bin", Assert.Single(hashFile.Load(force: true)).Value);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void SpecializedGuessersOwnCharacterAndCrossDomainStrategies()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/hud/ahri_square.dds"
            }));
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-be-lol-game-data/global/default/assets/characters/ahri/hud/ahri_square.png"
            }), null);

            const string expectedCharacter = "data/characters/ahri/skins/base/ahri.skn";
            var characterEngine = CreateEngine(HashGuessDomain.Game, expectedCharacter);
            int checkedCharacters = game.GuessCharacterFiles(characterEngine, CancellationToken.None);
            AssertResolved(characterEngine, expectedCharacter);
            Assert.True(checkedCharacters > 0);

            const string expectedCharacterTexture = "assets/characters/ahri/hud/ahri_square.dds";
            var textureEngine = CreateEngine(HashGuessDomain.Game, expectedCharacterTexture);
            game.GuessCharacterFiles(textureEngine, CancellationToken.None);
            AssertResolved(textureEngine, expectedCharacterTexture);
            const string expectedCrossDomain = "plugins/rcp-be-lol-game-data/global/default/assets/characters/ahri/hud/ahri_square.png";
            var crossDomainEngine = CreateEngine(HashGuessDomain.Lcu, expectedCrossDomain);
            int checkedCrossDomain = lcu.GuessFromGameHashes(crossDomainEngine, game, CancellationToken.None);
            AssertResolved(crossDomainEngine, expectedCrossDomain);
            Assert.True(checkedCrossDomain > 0);
            var gameCrossDomainEngine = CreateEngine(HashGuessDomain.Game, expectedCharacterTexture);
            int checkedGameCrossDomain = game.GuessFromLcuHashes(gameCrossDomainEngine, lcu, CancellationToken.None);
            AssertResolved(gameCrossDomainEngine, expectedCharacterTexture);
            Assert.True(checkedGameCrossDomain > 0);
        }

        [Fact]
        public void CrossDomainBasicCandidatesContainEveryCdtbRule()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/game/source.dds",
                "data/game/config.json",
                "assets/game/already.png"
            }));
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-be-lol-game-data/global/default/assets/client/icon.png",
                "plugins/rcp-be-lol-game-data/global/default/data/client/config.json",
                "plugins/rcp-be-lol-game-data/global/default/assets/client/already.dds"
            }), null);

            const string sourcePng = "plugins/rcp-be-lol-game-data/global/default/assets/game/source.png";
            const string sourceJpg = "plugins/rcp-be-lol-game-data/global/default/assets/game/source.jpg";
            const string config = "plugins/rcp-be-lol-game-data/global/default/data/game/config.json";
            const string webp = "plugins/rcp-be-lol-game-data/global/default/assets/game/source.webp";
            var lcuEngine = new HashGuessEngine(
                HashGuessDomain.Lcu,
                new[] { sourcePng, sourceJpg, config, webp }.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            int lcuCheckedCandidates = lcu.GuessFromGameHashes(lcuEngine, game, CancellationToken.None);
            Assert.Equal(1, lcuEngine.RemainingUnknownCount);
            Assert.Contains(lcuEngine.Matches.Values, match => match.Path == sourcePng);
            Assert.Contains(lcuEngine.Matches.Values, match => match.Path == sourceJpg);
            Assert.Contains(lcuEngine.Matches.Values, match => match.Path == config);
            Assert.DoesNotContain(lcuEngine.Matches.Values, match => match.Path == webp);
            Assert.Equal(3, lcuCheckedCandidates);

            string[] expectedGameCandidates = { "assets/client/icon.dds", "data/client/config.json" };
            var gameEngine = new HashGuessEngine(
                HashGuessDomain.Game,
                expectedGameCandidates
                    .Append("assets/client/already.dds")
                    .Select(path => XxHash64Ext.Hash(path))
                    .ToHashSet());
            int gameCheckedCandidates = game.GuessFromLcuHashes(gameEngine, lcu, CancellationToken.None);
            Assert.Equal(1, gameEngine.RemainingUnknownCount);
            Assert.Equal(2, gameCheckedCandidates);
            Assert.All(expectedGameCandidates, expected =>
                Assert.Contains(gameEngine.Matches.Values, match => match.Path == expected));
            Assert.DoesNotContain(gameEngine.Matches.Values, match => match.Path == "assets/client/already.dds");
        }

        [Theory]
        [InlineData("PROP", true, "bin")]
        [InlineData("PreLoad", true, "preload")]
        [InlineData("  {\"value\":1}", true, "json")]
        [InlineData("  {\"value\":1}", false, "")]
        public void UnknownChunkExtensionsAreInferredWithoutAnotherWadRead(string content, bool detectJson, string expected)
        {
            byte[] data = Encoding.UTF8.GetBytes(content);

            string extension = HashGuessingService.InferChunkExtension(data, detectJson);

            Assert.Equal(expected, extension);
        }

        [Fact]
        public void GameCrossDomainPhaseDoesNotRepeatOtherBasicAttacks()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/game/source.dds" }));
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-be-lol-game-data/global/default/assets/client/icon.png"
            }), null);
            var engine = CreateEngine(HashGuessDomain.Game, "assets/unrelated.bin");

            int checkedCandidates = game.GuessFromLcuHashes(engine, lcu, CancellationToken.None);

            Assert.Equal(1, checkedCandidates);
        }

        [Fact]
        public void CommonNumberAndExtensionStrategiesRemainAvailableToBothDomains()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon1.dds",
                "assets/test/icon2.png"
            }));

            Assert.Contains(game.SubstituteNumbers(3), candidate => candidate.Path == "assets/test/icon2.dds");
            var extensionEngine = CreateEngine(HashGuessDomain.Game, "assets/test/icon1.png");
            int checkedExtensions = game.SubstituteExtensions(extensionEngine, CancellationToken.None);
            AssertResolved(extensionEngine, "assets/test/icon1.png");
            Assert.True(checkedExtensions > 0);
        }

        [Fact]
        public void CommonDirectoryAndBasenameSubstitutionMatchesCdtbBehavior()
        {
            const string expected = "assets/ui/icon.png";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/ui/existing.json",
                "other/icon.png"
            }));
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            Assert.Contains("assets", game.DirectoryList());
            Assert.Contains("assets/ui", game.DirectoryList());
            int lastProgress = 0;
            int checkedCandidates = game.SubstituteBasenames(
                engine,
                CancellationToken.None,
                progress: count => lastProgress = count);

            AssertResolved(engine, expected);
            Assert.Equal(checkedCandidates, lastProgress);
        }

        [Theory]
        [InlineData(1, 2, "assets/ui/old_icon.png", "assets/ui/red_blue_icon.png")]
        [InlineData(2, 1, "assets/ui/old-value_icon.png", "assets/ui/new_icon.png")]
        public void GenericBasenameWordSubstitutionSupportsArbitraryOldAndNewCounts(
            int oldWordCount,
            int newWordCount,
            string knownPath,
            string expected)
        {
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { knownPath }));

            game.SubstituteBasenameWordsCore(
                engine,
                new[] { knownPath },
                new[] { "red", "blue", "new" },
                oldWordCount,
                newWordCount,
                CancellationToken.None);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void BannerGuessUsesAttestedCompoundVocabulary()
        {
            const string expected = "assets/esports/sponsoredbanners/secret/lpl_li-ning.tex";
            const string outsideBanner = "assets/characters/outsider/outsider_special.tex";
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/esports/sponsoredbanners/secret/lpl_2024.tex",
                "assets/esports/sponsoredbanners/secret/lpl_li_ning.tex",
                "assets/esports/sponsoredbanners/secret/lec_blue.tex",
                outsideBanner
            }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
            {
                XxHash64Ext.Hash(expected),
                XxHash64Ext.Hash(outsideBanner)
            });

            int checkedCandidates = game.GuessEsportsBanners(engine, null, CancellationToken.None);

            Assert.True(checkedCandidates > 0);
            Assert.Equal(expected, Assert.Single(engine.Matches).Value.Path);
            Assert.Contains(XxHash64Ext.Hash(outsideBanner), engine.UnknownHashes);
            Assert.Equal(HashGuessStrategy.BannerVariant, Assert.Single(engine.Matches).Value.Strategy);
        }

        [Fact]
        public void CommonCheckHelpersAndWordAdditionPreserveCdtbCoverage()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/known.bin" }));
            const string direct = "assets/direct.bin";
            const string many = "assets/many.bin";
            const string text = "assets/text.bin";
            const string xdbg = "assets/xdbg.bin";
            const string inserted = "assets/ui/new-icon.png";

            var directEngine = CreateEngine(HashGuessDomain.Game, direct);
            Assert.True(game.Check(directEngine, direct, HashGuessStrategy.WordlistVariant));
            AssertResolved(directEngine, direct);

            Assert.True(game.IsKnown(new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>()), "assets/known.bin", HashGuessStrategy.WordlistVariant));

            var manyEngine = CreateEngine(HashGuessDomain.Game, many);
            game.CheckIter(manyEngine, new[] { "assets/missing.bin", many }, HashGuessStrategy.WordlistVariant);
            AssertResolved(manyEngine, many);

            var textEngine = CreateEngine(HashGuessDomain.Game, text);
            game.CheckTextList(textEngine, $"assets/missing.bin\n{text}", HashGuessStrategy.WordlistVariant);
            AssertResolved(textEngine, text);

            string xdbgFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(xdbgFile, $"ignored\nhash: \"{xdbg}\"\n");
                var xdbgEngine = CreateEngine(HashGuessDomain.Game, xdbg);
                game.CheckXdbgHashes(xdbgEngine, xdbgFile);
                AssertResolved(xdbgEngine, xdbg);
            }
            finally
            {
                File.Delete(xdbgFile);
            }

            var insertionEngine = CreateEngine(HashGuessDomain.Game, inserted);
            game.AddBasenameWordCore(
                insertionEngine,
                new[] { "assets/ui/icon.png" },
                new[] { "new" },
                CancellationToken.None);
            AssertResolved(insertionEngine, inserted);
        }

        [Fact]
        public void CheckIterNormalizesWadPathsBeforeHashing()
        {
            const string exactPath = "assets/UI/MixedCase.bin";
            const string canonicalPath = "assets/ui/mixedcase.bin";
            var engine = CreateEngine(HashGuessDomain.Game, canonicalPath);
            var game = new GameHashGuesser();

            game.CheckIter(engine, new[] { exactPath }, HashGuessStrategy.WordlistVariant);

            AssertResolved(engine, canonicalPath);
        }

        [Fact]
        public void CheckIterNormalizesBackslashesAndPreservesDataSoonIdentity()
        {
            const string inputPath = @"DATA_SOON\Characters\Annie\Annie.bin";
            const string canonicalPath = "data_soon/characters/annie/annie.bin";
            var engine = CreateEngine(HashGuessDomain.Game, canonicalPath);
            var game = new GameHashGuesser();

            game.CheckIter(engine, new[] { inputPath }, HashGuessStrategy.WordlistVariant);

            AssertResolved(engine, canonicalPath);
        }

        [Fact]
        public void NumberSubstitutionSupportsExactCdtbPaddingModes()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon12.dds"
            }));

            var fixedWidth = game.GenerateNumberCandidates(3, digits: 2, includeCommonPadding: false).ToList();
            var unpadded = game.GenerateNumberCandidates(3, includeCommonPadding: false).ToList();

            Assert.Contains(fixedWidth, candidate => candidate.Path == "assets/test/icon02.dds");
            Assert.DoesNotContain(fixedWidth, candidate => candidate.Path == "assets/test/icon002.dds");
            Assert.Contains(unpadded, candidate => candidate.Path == "assets/test/icon2.dds");
            Assert.DoesNotContain(unpadded, candidate => candidate.Path == "assets/test/icon02.dds");
        }

        [Fact]
        public void GameSubstituteNumbersChecksFileNameVariantsThroughCommonCore()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon12.dds"
            }));
            var engine = CreateEngine(HashGuessDomain.Game, "assets/test/icon2.dds");

            int checkedCandidates = game.SubstituteNumbers(engine, CancellationToken.None);

            AssertResolved(engine, "assets/test/icon2.dds");
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameBasicNumbersIncludeUnpaddedAndD2CdtbPasses()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon12.dds"
            }));

            var candidates = game.SubstituteBasicNumbers(3).Select(candidate => candidate.Path).ToHashSet();

            Assert.Contains("assets/test/icon2.dds", candidates);
            Assert.Contains("assets/test/icon02.dds", candidates);
            Assert.DoesNotContain("assets/test/icon002.dds", candidates);
        }

        [Fact]
        public void GameBasicNumbersDoNotRepeatEquivalentTwoDigitCandidates()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon12.dds"
            }));

            var candidates = game.SubstituteBasicNumbers(100).Select(candidate => candidate.Path).ToList();

            Assert.Equal(110, candidates.Count);
            Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void BinEntryBasenamesGenerateFilteredGameExtensionCandidates()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/source.dds",
                "assets/shaders/source.glsl_100"
            }));

            var candidates = game.GuessFromBinEntryBasenames(new[] { "Characters/Ahri/Spells/Orb" })
                .Select(candidate => candidate.Path)
                .ToList();

            Assert.Contains("orb.dds", candidates);
            Assert.DoesNotContain("orb.glsl_100", candidates);
        }

        [Fact]
        public void LcuSpecificPluginAndLanguageStrategiesStayInLcuGuesser()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-one/global/en_us/assets/icon.png",
                "plugins/rcp-fe-two/global/default/assets/other.png"
            }), null);

            const string expectedPlugin = "plugins/rcp-fe-two/global/en_us/assets/icon.png";
            var pluginEngine = CreateEngine(HashGuessDomain.Lcu, expectedPlugin);
            int checkedPlugins = lcu.SubstitutePlugin(pluginEngine, CancellationToken.None);
            AssertResolved(pluginEngine, expectedPlugin);
            Assert.True(checkedPlugins > 0);
            const string expectedPbe = "plugins/rcp-fe-one/pbe/default/assets/icon.png";
            var pbeEngine = CreateEngine(HashGuessDomain.Lcu, expectedPbe);
            int checkedPbe = lcu.SubstituteRegionLang(pbeEngine, CancellationToken.None);
            AssertResolved(pbeEngine, expectedPbe);
            Assert.True(checkedPbe > 0);

            const string expectedGlobal = "plugins/rcp-fe-one/global/default/assets/icon.png";
            var globalEngine = CreateEngine(HashGuessDomain.Lcu, expectedGlobal);
            int checkedGlobal = lcu.SubstituteRegionLang(globalEngine, CancellationToken.None);
            AssertResolved(globalEngine, expectedGlobal);
            Assert.True(checkedGlobal > 0);
            const string expectedPattern = "plugins/rcp-fe-lol-perks/global/default/images/construct/8000/environment.jpg";
            var patternEngine = CreateEngine(HashGuessDomain.Lcu, expectedPattern);
            int checkedPatterns = lcu.GuessPatterns(patternEngine, CancellationToken.None);
            AssertResolved(patternEngine, expectedPattern);
            Assert.True(checkedPatterns > 0);
        }

        [Fact]
        public void LcuExtensionSubstitutionUsesEveryKnownPathLikeCdtb()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "root/source.one",
                "plugins/rcp-fe-test/global/default/asset.two"
            }), null);

            const string expected = "root/source.two";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);

            int checkedCandidates = lcu.SubstituteExtensions(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameBasenamePrefixesAreDeduplicatedLikeCdtbSet()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/ui/icon.png" }));

            const string expected = "assets/ui/2x_icon.png";
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            int checkedCandidates = game.CheckBasenamePrefixes(
                engine,
                CancellationToken.None,
                new[] { "2x_", "2x_" });

            AssertResolved(engine, expected);
            Assert.Equal(1, checkedCandidates);
        }

        [Fact]
        public void LcuBuildWordlistUsesFullFilteredPaths()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-lol-home/global/default/navigation/play-button.png",
                "plugins/rcp-be-lol-game-data/global/default/data/characters/ahri/hidden-secret.png",
                "plugins/test/0123456789abcdef0123456789abcdef.json"
            }), null);

            IReadOnlyList<string> words = lcu.BuildWordlist();

            Assert.Contains("navigation", words);
            Assert.Contains("home", words);
            Assert.DoesNotContain("hidden", words);
            Assert.DoesNotContain("secret", words);
        }

        [Fact]
        public void LcuSpecializedWordlistsUseExpectedBasenameFilters()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-lol-home/global/default/menu/main.json",
                "plugins/rcp-fe-lol-home/global/default/menu/home.svg",
                "plugins/rcp-fe-lol-static-assets/global/default/navigation/play.svg",
                "plugins/rcp-fe-lol-static-assets/global/default/navigation/play.png",
                "plugins/rcp-fe-lol-other/global/default/images/logo.svg",
                "plugins/rcp-fe-lol-other/global/default/images/logo.jpg",
                "plugins/rcp-be-other/global/default/menu/other.json"
            }), null);

            IReadOnlyList<string> swordlist = lcu.BuildSwordlist();
            IReadOnlyList<string> sswordlist = lcu.BuildSswordlist();
            IReadOnlyList<string> pngJpgSwordlist = lcu.BuildPngJpgSwordlist();

            Assert.Contains("main", swordlist);
            Assert.DoesNotContain("home", swordlist);
            Assert.DoesNotContain("play", swordlist);
            Assert.DoesNotContain("other", swordlist);

            Assert.Contains("home", sswordlist);
            Assert.Contains("play", sswordlist);
            Assert.Contains("logo", sswordlist);
            Assert.DoesNotContain("main", sswordlist);

            Assert.Contains("play", pngJpgSwordlist);
            Assert.Contains("logo", pngJpgSwordlist);
            Assert.DoesNotContain("main", pngJpgSwordlist);

            Assert.Same(swordlist, lcu.BuildSwordlist());
            Assert.Same(sswordlist, lcu.BuildSswordlist());
            Assert.Same(pngJpgSwordlist, lcu.BuildPngJpgSwordlist());
        }

        [Fact]
        public void LcuCustomRunsScopedPluginAttacks()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-lol-home/global/default/navigation/old-icon.svg",
                "plugins/rcp-fe-lol-home/global/default/navigation/new-icon.svg",
                "plugins/rcp-fe-lol-other/global/default/navigation/old-icon.svg",
                "plugins/rcp-fe-lol-other/global/default/navigation/new-icon.svg"
            }), null);
            string[] expectedPaths =
            {
                "plugins/rcp-fe-lol-home/global/default/navigation/old-icon-new.svg",
                "plugins/rcp-fe-lol-home/global/default/navigation/new-icon-old.svg",
                "plugins/rcp-fe-lol-other/global/default/navigation/old-icon-new.svg",
                "plugins/rcp-fe-lol-other/global/default/navigation/new-icon-old.svg"
            };
            var engine = new HashGuessEngine(
                HashGuessDomain.Lcu,
                expectedPaths.Select(path => XxHash64Ext.Hash(path)).ToHashSet());

            int checkedCandidates = lcu.RunCustomAttacks(engine, null, CancellationToken.None);

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.All(expectedPaths, expected =>
                Assert.Contains(engine.Matches.Values, match => match.Path == expected));
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void LcuExplicitCdtbMethodsUseFullWordlistAndExactNumbers()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-one/global/default/navigation/old-icon1.png",
                "plugins/rcp-fe-two/global/default/other.json"
            }), null);
            const string substituted = "plugins/rcp-fe-one/global/default/navigation/new-icon1.png";
            var substitutionEngine = CreateEngine(HashGuessDomain.Lcu, substituted);

            lcu.SubstituteBasenameWords(
                substitutionEngine,
                CancellationToken.None,
                plugin: "rcp-fe-one",
                fileExtension: ".png",
                words: new[] { "new" });

            AssertResolved(substitutionEngine, substituted);
            const string expectedNumber = "plugins/rcp-fe-one/global/default/navigation/old-icon2.png";
            var numberEngine = CreateEngine(HashGuessDomain.Lcu, expectedNumber);
            int checkedNumbers = lcu.SubstituteNumbers(numberEngine, CancellationToken.None, maximum: 3);
            AssertResolved(numberEngine, expectedNumber);
            Assert.True(checkedNumbers > 0);

            const string paddedNumber = "plugins/rcp-fe-one/global/default/navigation/old-icon002.png";
            var paddedNumberEngine = CreateEngine(HashGuessDomain.Lcu, paddedNumber);
            lcu.SubstituteNumbers(paddedNumberEngine, CancellationToken.None, maximum: 3);
            Assert.Equal(1, paddedNumberEngine.RemainingUnknownCount);
            const string expectedPlugin = "plugins/rcp-fe-two/global/default/navigation/old-icon1.png";
            var pluginEngine = CreateEngine(HashGuessDomain.Lcu, expectedPlugin);
            int checkedPlugins = lcu.SubstitutePlugin(pluginEngine, CancellationToken.None);
            AssertResolved(pluginEngine, expectedPlugin);
            Assert.True(checkedPlugins > 0);

            const string inserted = "plugins/rcp-fe-one/global/default/navigation/navigation-old-icon1.png";
            var insertionEngine = CreateEngine(HashGuessDomain.Lcu, inserted);
            lcu.AddBasenameWord(insertionEngine, CancellationToken.None);
            AssertResolved(insertionEngine, inserted);
        }

        [Fact]
        public void LcuNumberSubstitutionStaysAnchoredToFileNames()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-one/global/default/skin1/old-icon1.png"
            }), null);

            const string expectedFileName = "plugins/rcp-fe-one/global/default/skin1/old-icon2.png";
            var fileNameEngine = CreateEngine(HashGuessDomain.Lcu, expectedFileName);
            int checkedNumbers = lcu.SubstituteNumbers(fileNameEngine, CancellationToken.None, maximum: 20);
            AssertResolved(fileNameEngine, expectedFileName);
            Assert.True(checkedNumbers > 0);

            const string expectedDirectory = "plugins/rcp-fe-one/global/default/skin2/old-icon1.png";
            var directoryEngine = CreateEngine(HashGuessDomain.Lcu, expectedDirectory);
            lcu.SubstituteNumbers(directoryEngine, CancellationToken.None, maximum: 20);
            Assert.Equal(1, directoryEngine.RemainingUnknownCount);
        }

        [Fact]
        public void LcuV1PathPatternsResolveLocalizedWordPairJson()
        {
            const string defaultPath = "plugins/rcp-be-lol-game-data/global/default/v1/augment-lists.json";
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[] { defaultPath }), null);
            const string expected = "plugins/rcp-be-lol-game-data/global/de_de/v1/augment-lists.json";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);

            lcu.RunV1PathPatterns(
                engine,
                progress: null,
                cancellationToken: CancellationToken.None,
                words: new[] { "augment", "list" },
                locales: new[] { "de_de" });

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuV1WithoutLocalesResolvesOnlyTheDefaultPath()
        {
            string[] paths =
            {
                "plugins/rcp-be-lol-game-data/global/default/v1/augment-lists.json",
                "plugins/rcp-be-lol-game-data/global/en_us/v1/augment-lists.json",
                "plugins/rcp-be-lol-game-data/global/es_es/v1/augment-lists.json",
                "plugins/rcp-be-lol-game-data/global/es_mx/v1/augment-lists.json"
            };
            var unknown = paths.Select(path => XxHash64Ext.Hash(path)).ToHashSet();
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, unknown);
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, Array.Empty<string>()), null);

            lcu.RunV1PathPatterns(
                engine,
                progress: null,
                cancellationToken: CancellationToken.None,
                words: new[] { "augment", "list" });

            HashGuessMatch match = Assert.Single(engine.Matches).Value;
            Assert.Equal(paths[0], match.Path);
            Assert.Equal(3, engine.RemainingUnknownCount);
        }

        [Fact]
        public void GameExplicitCdtbMethodsCoverTerminalCharactersPrefixesAndShaders()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "characters/ahri",
                "assets/ui/icon.png",
                "assets/shaders/test.ps.dx11",
                "assets/shaders/test.ps-dx11"
            }));

            Assert.Contains("ahri", game.GetCharacters());
            Assert.Contains("shaders", game.BuildWordlist());
            var prefixEngine = CreateEngine(HashGuessDomain.Game, "assets/ui/2x_icon.png");
            game.CheckBasenamePrefixes(prefixEngine, CancellationToken.None);
            AssertResolved(prefixEngine, "assets/ui/2x_icon.png");

            var characterEngine = CreateEngine(HashGuessDomain.Game, "data/characters/lux/skins/base/lux.skn");
            game.GuessCharacterFiles(characterEngine, CancellationToken.None, new[] { "lux" });
            AssertResolved(characterEngine, "data/characters/lux/skins/base/lux.skn");

            string[] shaderTargets =
            {
                "assets/shaders/test.ps.metal_19900",
                "assets/shaders/test.ps-dx11",
                "assets/shaders/test.ps-metal_19900"
            };
            var shaderEngine = new HashGuessEngine(
                HashGuessDomain.Game,
                shaderTargets.Select(path => XxHash64Ext.Hash(path)).ToHashSet());
            game.GuessShaderVariants(shaderEngine, CancellationToken.None);
            Assert.Equal(0, shaderEngine.RemainingUnknownCount);
            Assert.All(shaderTargets, expected =>
                Assert.Contains(shaderEngine.Matches.Values, match => match.Path == expected));
        }

        [Fact]
        public void GameShaderVariantsDiscoverUnseededHlslFamiliesFromExecutableReferences()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-shaders-{Guid.NewGuid():N}");
            string gameDirectory = Path.Combine(root, "Game");
            Directory.CreateDirectory(gameDirectory);
            try
            {
                File.WriteAllBytes(
                    Path.Combine(gameDirectory, "League of Legends.exe"),
                    Encoding.ASCII.GetBytes("ignored\0UI/LineGraph.ps\0UI/LineGraph.vs\0s.ps\0"));
                string[] expected =
                {
                    "assets/shaders/hlsl/ui/linegraph.ps-dx11",
                    "assets/shaders/hlsl/ui/linegraph.ps-dx11_0",
                    "assets/shaders/hlsl/ui/linegraph.ps-metal",
                    "assets/shaders/hlsl/ui/linegraph.ps-metal_0",
                    "assets/shaders/hlsl/ui/linegraph.ps.dx11",
                    "assets/shaders/hlsl/ui/linegraph.ps.dx11_0",
                    "assets/shaders/hlsl/ui/linegraph.ps.glsl",
                    "assets/shaders/hlsl/ui/linegraph.ps.glsl_0",
                    "assets/shaders/hlsl/ui/linegraph.ps.metal",
                    "assets/shaders/hlsl/ui/linegraph.ps.metal_0",
                    "assets/shaders/hlsl/ui/linegraph.vs-dx11",
                    "assets/shaders/hlsl/ui/linegraph.vs-dx11_0",
                    "assets/shaders/hlsl/ui/linegraph.vs-metal",
                    "assets/shaders/hlsl/ui/linegraph.vs-metal_0",
                    "assets/shaders/hlsl/ui/linegraph.vs.dx11",
                    "assets/shaders/hlsl/ui/linegraph.vs.dx11_0",
                    "assets/shaders/hlsl/ui/linegraph.vs.glsl",
                    "assets/shaders/hlsl/ui/linegraph.vs.glsl_0",
                    "assets/shaders/hlsl/ui/linegraph.vs.metal",
                    "assets/shaders/hlsl/ui/linegraph.vs.metal_0"
                };
                var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
                var engine = new HashGuessEngine(
                    HashGuessDomain.Game,
                    expected.Select(path => XxHash64Ext.Hash(path)).ToHashSet());

                game.GuessShaderVariants(engine, CancellationToken.None, rootDirectory: root);

                Assert.Equal(0, engine.RemainingUnknownCount);
                Assert.All(expected, path => Assert.Contains(engine.Matches.Values, match => match.Path == path));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void GameCharacterTemplatesMatchCdtbCoverage()
        {
            var game = new GameHashGuesser();
            string[] targets =
            {
                "data/characters/lux/skins/root.bin",
                "data/characters/lux/skins/skin0.bin",
                "data/characters/lux/skins/skin199.bin",
                "data/characters/lux/animations/skin0.bin",
                "data/characters/lux/animations/skin199.bin"
            };
            var engine = new HashGuessEngine(
                HashGuessDomain.Game,
                targets.Select(path => XxHash64Ext.Hash(path)).ToHashSet());

            int checkedCandidates = game.GuessCharacterFiles(engine, CancellationToken.None, new[] { "lux" });

            Assert.True(checkedCandidates > 0);
            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.All(targets, expected =>
                Assert.Contains(engine.Matches.Values, match => match.Path == expected));
        }

        [Fact]
        public void GamePetCharacterTemplatesMatchCdtbCoverage()
        {
            var game = new GameHashGuesser();
            string[] targets =
            {
                "data/characters/pet_tft/tiers/tier0.bin",
                "data/characters/pet_tft/tiers/tier9.bin"
            };
            var engine = new HashGuessEngine(
                HashGuessDomain.Game,
                targets.Select(path => XxHash64Ext.Hash(path)).ToHashSet());

            game.GuessCharacterFiles(engine, CancellationToken.None, new[] { "pet_tft" });

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.All(targets, expected =>
                Assert.Contains(engine.Matches.Values, match => match.Path == expected));
        }

        [Theory]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-dx11")]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-dx11_0")]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-metal")]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-metal_0")]
        public void WadGrepCoversHyphenatedShaderVariants(string expected)
        {
            var game = new GameHashGuesser();
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            byte[] path = Encoding.ASCII.GetBytes("Shaders/test");
            byte[] data = new byte[path.Length + 2];
            data[0] = (byte)path.Length;
            data[1] = (byte)(path.Length >> 8);
            path.CopyTo(data, 2);

            game.GrepWad(engine, data, "data/test.bin", "test.wad.client", 1);

            AssertResolved(engine, expected);
        }

        [Theory]
        [InlineData("data/characters/ahri/spells/orb.luabin64")]
        [InlineData("data/characters/ahri/npcscripts/orb.preload")]
        [InlineData("data/shared/scripts/aicomponents/sharedlogic.preload")]
        [InlineData("levels/map999/scripts/mutators/sharedlogic.luabin64")]
        public void GameGrepParsesBinaryLuaManifest(string expected)
        {
            byte[] data;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("LUAF"));
                writer.Write(1u);
                WriteManifestString(writer, "Ahri");
                writer.Write(1u);
                WriteManifestString(writer, "Orb");
                writer.Write(1u);
                WriteManifestString(writer, "SharedLogic");
                writer.Write(1u);
                writer.Write(0x0123456789abcdefUL);
                writer.Flush();
                data = stream.ToArray();
            }
            var engine = CreateEngine(HashGuessDomain.Game, expected);
            var guesser = new GameHashGuesser();

            guesser.GrepWad(engine, data, "data/all_lua_files.manifest", "scripts.wad.client", 1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuJsonUsesCdtbBranchPrecedenceAndContinuesGeneralGrepAfterPluginMetadata()
        {
            const string expected = "plugins/rcp-fe-test/global/default/images/icon.png";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(
                new[] { "plugins/rcp-fe-test/global/default/existing.json" },
                null);
            const string json = "{\"pluginDependencies\":[],\"name\":\"rcp-fe-test\",\"musicVolume\":1,\"files\":{},\"asset\":\"images/icon.png\"}";

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes(json),
                "plugins/rcp-fe-test/global/default/description.json",
                "test.wad",
                2);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void LcuGrepCombinesPluginPrefixedBasenamesWithKnownDirectories()
        {
            const string expected = "plugins/rcp-fe-test/global/default/plugins/nested/file.js";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);
            var guesser = new LcuHashGuesser(
                new[] { "plugins/rcp-fe-test/global/default/existing.json" },
                null);

            guesser.GrepWad(
                engine,
                Encoding.UTF8.GetBytes("const file = \"plugins/nested/file.js\";"),
                "plugins/rcp-fe-test/global/default/init.js",
                "test.wad",
                1);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameExplicitWordMethodsUseDirectoryWords()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/navigation/old-icon.png"
            }));
            const string substituted = "assets/navigation/navigation-icon.png";
            const string inserted = "assets/navigation/navigation-old-icon.png";
            var substitutionEngine = CreateEngine(HashGuessDomain.Game, substituted);
            var insertionEngine = CreateEngine(HashGuessDomain.Game, inserted);

            game.SubstituteBasenameWords(substitutionEngine, CancellationToken.None);
            game.AddBasenameWord(insertionEngine, CancellationToken.None);

            AssertResolved(substitutionEngine, substituted);
            AssertResolved(insertionEngine, inserted);
        }

        [Fact]
        public void GameGrepFileUsesByteOffsetsAndSupportsFileOrData()
        {
            const string shortened = "ASSETS/test.lua";
            const string expected = "assets/test.luabin64";
            byte[] full = Encoding.ASCII.GetBytes(shortened + "trail");
            byte[] utf8Prefix = Encoding.UTF8.GetBytes("é");
            byte[] data = new byte[utf8Prefix.Length + 2 + full.Length];
            utf8Prefix.CopyTo(data, 0);
            data[utf8Prefix.Length] = (byte)shortened.Length;
            data[utf8Prefix.Length + 1] = 0;
            full.CopyTo(data, utf8Prefix.Length + 2);
            var game = new GameHashGuesser();
            var dataEngine = CreateEngine(HashGuessDomain.Game, expected);

            game.GrepFile(dataEngine, data: data);
            AssertResolved(dataEngine, expected);

            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, data);
                var fileEngine = CreateEngine(HashGuessDomain.Game, expected);
                game.GrepFile(fileEngine, path: file);
                AssertResolved(fileEngine, expected);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void FromWadsFiltersArchivesByGuesserDomain()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string lcuPath = Path.Combine(directory, "plugins.wad");
            string gamePath = Path.Combine(directory, "assets.wad.client");
            File.WriteAllBytes(lcuPath, Array.Empty<byte>());
            File.WriteAllBytes(gamePath, Array.Empty<byte>());
            try
            {
                var game = new GameHashGuesser();
                var lcu = new LcuHashGuesser(Array.Empty<string>(), null);

                HashWadInventory gameInventory = game.FromWads(new[] { lcuPath, gamePath }, CancellationToken.None);
                HashWadInventory lcuInventory = lcu.FromWads(new[] { lcuPath, gamePath }, CancellationToken.None);

                Assert.Equal(gamePath, Assert.Single(gameInventory.WadPaths));
                Assert.Equal(lcuPath, Assert.Single(lcuInventory.WadPaths));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void GameSpecificSuffixAndCharacterSubstitutionStayInGameGuesser()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/hud/ahri_circle.en_us.dds",
                "assets/characters/lux/hud/lux_circle.dds"
            }));

            Assert.Contains(game.SubstituteCharacter(), candidate =>
                candidate.Path == "assets/characters/lux/hud/lux_circle.en_us.dds");
            Assert.Contains(game.SubstituteSuffixes(), candidate =>
                candidate.Path == "assets/characters/ahri/hud/ahri_circle.dds");
        }

        [Fact]
        public void GameSuffixSubstitutionKeepsDistinctKnownSuffixes()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/test/icon.en_us.dds",
                "assets/test/icon.fr_fr.dds"
            }));

            var candidates = game.SubstituteSuffixes().Select(candidate => candidate.Path).ToHashSet(StringComparer.Ordinal);

            Assert.Contains("assets/test/icon.en_us.dds", candidates);
            Assert.Contains("assets/test/icon.fr_fr.dds", candidates);
        }

        [Fact]
        public async System.Threading.Tasks.Task GameSkinNumberLanguageAndLocalGroupMethodsMatchCdtb()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/skins/skin1/foo_skin2.en_us.dds",
                "assets/characters/ahri/skins/skin2/other.dds"
            }));

            Assert.Contains(game.SubstituteSkinNumbers(), candidate =>
                candidate.Path == "assets/characters/ahri/skins/skin1/foo_skin2.en_us.dds");
            const string expectedLocale = "assets/characters/ahri/skins/skin1/foo_skin2.fr_fr.dds";
            var languageEngine = CreateEngine(HashGuessDomain.Game, expectedLocale);
            game.SubstituteLang(languageEngine, CancellationToken.None);
            AssertResolved(languageEngine, expectedLocale);

            const string expectedGroup = "data/ahri_skins_skin0_skins_skin1.bin";
            var engine = CreateEngine(HashGuessDomain.Game, expectedGroup);
            await game.GuessSkinGroupsBin(engine, CancellationToken.None);
            AssertResolved(engine, expectedGroup);
        }

        [Fact]
        public async System.Threading.Tasks.Task GameChromaGroupsPreferInstalledSkinsJsonWadChunk()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "lol-game-data.wad");
            const string json = "{\"103000\":{\"id\":103000,\"loadScreenPath\":\"/lol-game-data/assets/assets/characters/ahri/skins/skin0/ahri_loadscreen.jpg\",\"chromas\":[{\"id\":103001}]}}";
            var entries = new[]
            {
                new WadBakeEntry(
                    RiotCatalogDefinitions.SkinsJsonPath,
                    () => new MemoryStream(Encoding.UTF8.GetBytes(json)),
                    WadChunkCompression.None)
            };
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());
            const string expected = "data/ahri_skins_skin0_skins_skin1.bin";

            try
            {
                var game = new GameHashGuesser();
                var engine = CreateEngine(HashGuessDomain.Game, expected);

                await game.GuessSkinGroupsBinUsingChromas(engine, directory, CancellationToken.None);

                AssertResolved(engine, expected);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void GameSkinNumberSubstitutionUsesCombinationsInsteadOfPermutations()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/characters/annie/skins/skin1/annie_skin1.skn",
                "data/characters/annie/skins/skin2/annie_skin2.skn",
                "data/characters/annie/skins/skin3/annie_skin3.skn"
            }));

            var candidates = game.SubstituteSkinNumbers()
                .Select(candidate => candidate.Path)
                .Where(path => path.EndsWith(".skn", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(3, candidates.Count);
            Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("data/characters/annie/skins/skin1/annie_skin2.skn", candidates);
            Assert.Contains("data/characters/annie/skins/skin1/annie_skin3.skn", candidates);
            Assert.Contains("data/characters/annie/skins/skin2/annie_skin3.skn", candidates);
            Assert.DoesNotContain("data/characters/annie/skins/skin3/annie_skin1.skn", candidates);
        }

        [Fact]
        public async System.Threading.Tasks.Task GameChromaGroupsSkipCharactersUnknownToCorpus()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "lol-game-data.wad");
            const string json = "{\"103000\":{\"id\":103000,\"loadScreenPath\":\"/lol-game-data/assets/assets/characters/ahri/skins/skin0/ahri_loadscreen.jpg\",\"chromas\":[{\"id\":103001}]},\"570000\":{\"id\":570000,\"loadScreenPath\":\"/lol-game-data/assets/assets/characters/teemo/skins/skin5/teemo_loadscreen.jpg\"}}";
            var entries = new[]
            {
                new WadBakeEntry(
                    RiotCatalogDefinitions.SkinsJsonPath,
                    () => new MemoryStream(Encoding.UTF8.GetBytes(json)),
                    WadChunkCompression.None)
            };
            WadBuilder.Bake(entries, wadPath, new WadBakeSettings());
            const string expectedAhri = "data/ahri_skins_skin0_skins_skin1.bin";
            const string unknownTeemo = "data/teemo_skins_skin5.bin";

            try
            {
                var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/characters/ahri/hud/ahri_circle.dds" }));
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
                {
                    XxHash64Ext.Hash(expectedAhri),
                    XxHash64Ext.Hash(unknownTeemo)
                });
                Assert.Equal(2, engine.RemainingUnknownCount);

                await game.GuessSkinGroupsBinUsingChromas(engine, directory, CancellationToken.None);

                Assert.Equal(1, engine.RemainingUnknownCount);
                Assert.Contains(engine.Matches.Values, match => match.Path == expectedAhri);
                Assert.DoesNotContain(engine.Matches.Values, match => match.Path == unknownTeemo);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void GameBasenameWordSubstitutionKeepsRareWordCoverage()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/hud/ahri_circle.dds",
                "assets/characters/lux/hud/lux_square.dds"
            }));
            const string expected = "assets/characters/ahri/hud/ahri_square.dds";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            game.SubstituteBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
        }

        [Fact]
        public void GameCustomBinWordlistAttackUsesBinPathsAndBasenames()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.bin",
                "assets/characters/lux/lux.bin",
                "assets/characters/ahri/ahri.dds"
            }));
            const string expected = "assets/characters/ahri/lux.bin";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteBinBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomDataBinWordlistAttackUsesOnlyDataBinPaths()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/characters/ahri/ahri.bin",
                "data/characters/lux/lux.bin",
                "assets/characters/ahri/ahri.bin",
                "data/characters/ahri/ahri.dds"
            }));
            const string expected = "data/characters/ahri/lux.bin";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteDataBinBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomCharacterDdsWordlistAttackUsesOnlyCharacterDdsPaths()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.dds",
                "assets/characters/lux/lux.dds",
                "assets/characters/ahri/ahri.bin",
                "assets/maps/ahri/ahri.dds"
            }));
            const string expected = "assets/characters/ahri/lux.dds";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteCharacterDdsBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomCharacterTexWordlistAttackUsesOnlyCharacterTexPaths()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.tex",
                "assets/characters/lux/lux.tex",
                "assets/characters/ahri/ahri.dds",
                "assets/maps/ahri/ahri.tex"
            }));
            const string expected = "assets/characters/ahri/lux.tex";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteCharacterTexBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomWordAdditionUsesDeterministicGameLists()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.bin",
                "assets/characters/lux/lux.bin"
            }));
            const string expected = "assets/characters/ahri/ahri_lux.bin";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.AddCustomBasenameWord(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomSwordlistSubstitutionUsesTheSpecializedBinWords()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.dds",
                "assets/characters/lux/lux.bin",
                "assets/characters/zed/zed.bin"
            }));
            const string expected = "assets/characters/ahri/lux.dds";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteSwordlistBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void GameCustomWordlistSubstitutionUsesTheGeneralGameWords()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/ahri.dds",
                "assets/characters/lux/lux.dds"
            }));
            const string expected = "assets/characters/ahri/lux.dds";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            int checkedCandidates = game.SubstituteWordlistBasenameWords(engine, CancellationToken.None);

            AssertResolved(engine, expected);
            Assert.True(checkedCandidates > 0);
        }

        [Fact]
        public void HashFileLoadsUnknownExportsWithoutOwningPersistence()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "sample.unknown.txt"), "00000000000000aa\n00000000000000bb\n");
                HashSet<ulong> hashes = HashGuesser.UnknownFromExport(directory);

                Assert.Contains(0xaaUL, hashes);
                Assert.Contains(0xbbUL, hashes);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static HashGuessEngine CreateEngine(HashGuessDomain domain, string expected)
        {
            return new HashGuessEngine(domain, new HashSet<ulong> { XxHash64Ext.Hash(expected) });
        }

        private static void WriteManifestString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        private static void AssertResolved(HashGuessEngine engine, string expected)
        {
            HashGuessMatch match = Assert.Single(engine.Matches).Value;
            Assert.Equal(expected, match.Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        private static byte[] CreateAnimationBin(string clipName, ulong pathHash)
        {
            var resource = new BinTreeStruct(
                0,
                Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), pathHash)
                });
            var clip = new BinTreeStruct(0, Fnv1a.HashLower("AtomicClipData"), new[] { resource });
            var map = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, Fnv1a.HashLower(clipName)),
                        clip)
                });
            var tree = new BinTree(
                new[] { new BinTreeObject(1, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[] { map }) },
                Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            return stream.ToArray();
        }

        [Fact]
        public void GameNumberSubstitutionOnlyChangesNumbersInFileNames()
        {
            string knownPath = "assets/maps/map11/scene.dds";
            string targetPath = "assets/maps/map12/scene.dds";

            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { knownPath }));
            var candidates = game.SubstituteNumbers(maximum: 20).ToList();

            Assert.DoesNotContain(candidates, candidate => candidate.Path == targetPath);
        }

        [Fact]
        public void GameSkinNumberCandidatesRespectBudget()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/characters/annie/skins/skin1/annie_skin1.skn",
                "data/characters/annie/skins/skin2/annie_skin2.skn",
                "data/characters/annie/skins/skin3/annie_skin3.skn"
            }));

            Assert.Equal(3, game.GenerateSkinNumberCandidates(3).Count());
        }

        [Fact]
        public void GameCharacterSubstitutionCandidatesRespectBudget()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/hud/ahri_circle.dds",
                "assets/characters/lux/hud/lux_square.dds"
            }));

            Assert.Equal(3, game.GenerateCharacterSubstitutionCandidates(3).Count());
        }

        [Fact]
        public void GameWordAttacksRespectCandidateBudget()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/spells/ahri_test.bin",
                "data/items/boot_1.bin"
            }));
            var neverMatching = new HashSet<ulong> { 1UL, 2UL, 3UL, 4UL };

            var substitutionEngine = new HashGuessEngine(HashGuessDomain.Game, neverMatching.ToHashSet());
            game.SubstituteBasenameWords(substitutionEngine, CancellationToken.None, candidateBudget: 3);
            Assert.Equal(3, substitutionEngine.CheckedCandidates);


            var additionEngine = new HashGuessEngine(HashGuessDomain.Game, neverMatching.ToHashSet());
            game.AddBasenameWord(additionEngine, CancellationToken.None, candidateBudget: 3);
            Assert.Equal(3, additionEngine.CheckedCandidates);
        }

        [Fact]
        public async System.Threading.Tasks.Task GameSkinGroupsBinLocalGeneratesGroupsBeyondEightSkins()
        {
            var corpus = new[] { "assets/characters/test/skins/skin0/a.dds" }
                .Concat(Enumerable.Range(1, 9).Select(skin => $"assets/characters/test/skins/skin{skin}/a.dds"))
                .ToArray();
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, corpus));
            const string tooDeep = "data/test_skins_skin0_skins_skin1_skins_skin2_skins_skin3_skins_skin4_skins_skin5_skins_skin6_skins_skin7_skins_skin8.bin";
            const string withinCap = "data/test_skins_skin0_skins_skin1_skins_skin2_skins_skin3_skins_skin4_skins_skin5_skins_skin6_skins_skin7.bin";

            var deepEngine = CreateEngine(HashGuessDomain.Game, tooDeep);
            await game.GuessSkinGroupsBin(deepEngine, CancellationToken.None);
            AssertResolved(deepEngine, tooDeep);

            var capEngine = CreateEngine(HashGuessDomain.Game, withinCap);
            await game.GuessSkinGroupsBin(capEngine, CancellationToken.None);
            AssertResolved(capEngine, withinCap);
        }

        [Fact]
        public void GameCharacterFilesResolvesSkinFilesAndPetTiers()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/skins/skin0/ahri.dds",
                "assets/characters/petdssquid/tiers/root.bin"
            }));

            const string skinPath = "data/characters/ahri/skins/skin5.bin";
            var skinEngine = CreateEngine(HashGuessDomain.Game, skinPath);
            game.GuessCharacterFiles(skinEngine, CancellationToken.None);
            AssertResolved(skinEngine, skinPath);

            const string petTierPath = "data/characters/petdssquid/tiers/tier2.bin";
            var petEngine = CreateEngine(HashGuessDomain.Game, petTierPath);
            game.GuessCharacterFiles(petEngine, CancellationToken.None);
            AssertResolved(petEngine, petTierPath);
        }

    }
}

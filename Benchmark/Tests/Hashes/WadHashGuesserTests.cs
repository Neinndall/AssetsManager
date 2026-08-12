using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
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
        public void GameAnimationCandidatesCarryCharacterSkinNumberVariants()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/seraphine/skins/skin10/animations/seraphine_skin10_spell1.anm"
            }));

            Assert.Contains(
                game.GenerateAnimationContextCandidates("seraphine", "skin69"),
                path => path == "assets/characters/seraphine/skins/skin69/animations/seraphine_skin69_spell1.anm");
        }

        [Fact]
        public void GameAnimationCandidatesDeriveReusableCharacterPrefixes()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/seraphine/skins/skin10/animations/spell1_to_idle.anm",
                "assets/characters/seraphine/skins/skin10/animations/p_spell1_to_run.anm",
                "assets/characters/seraphine/skins/skin10/animations/spell1_to_run.anm"
            }));

            Assert.Contains(
                game.GenerateAnimationContextCandidates("seraphine", "skin69"),
                path => path == "assets/characters/seraphine/skins/skin69/animations/p_spell1_to_idle.anm");
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
        public void SpecializedGuessersOwnCanonicalAndCrossDomainStrategies()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/hud/ahri_square.dds"
            }));
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-be-lol-game-data/global/default/assets/characters/ahri/hud/ahri_square.png"
            }), null);

            Assert.Contains(game.GenerateCanonicalCandidates(lcu), candidate =>
                candidate.Path == "data/characters/ahri/skins/base/ahri.skn");
            Assert.Contains(lcu.GuessFromGameHashes(game), candidate =>
                candidate.Path == "plugins/rcp-be-lol-game-data/global/default/assets/characters/ahri/hud/ahri_square.png");
            Assert.Contains(game.GuessFromLcuHashes(lcu), candidate =>
                candidate.Path == "assets/characters/ahri/hud/ahri_square.dds");
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

            var lcuCandidates = lcu.GuessFromGameHashes(game).Select(candidate => candidate.Path).ToHashSet();
            Assert.Contains("plugins/rcp-be-lol-game-data/global/default/assets/game/source.png", lcuCandidates);
            Assert.Contains("plugins/rcp-be-lol-game-data/global/default/assets/game/source.jpg", lcuCandidates);
            Assert.Contains("plugins/rcp-be-lol-game-data/global/default/data/game/config.json", lcuCandidates);
            Assert.DoesNotContain("plugins/rcp-be-lol-game-data/global/default/assets/game/source.dds", lcuCandidates);
            Assert.DoesNotContain("plugins/rcp-be-lol-game-data/global/default/assets/game/already.png", lcuCandidates);

            var gameCandidates = game.GuessFromLcuHashes(lcu).Select(candidate => candidate.Path).ToHashSet();
            Assert.Contains("assets/client/icon.dds", gameCandidates);
            Assert.Contains("assets/client/icon.tex", gameCandidates);
            Assert.Contains("data/client/config.json", gameCandidates);
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

            int checkedCandidates = game.RunCrossDomainAttacks(engine, lcu, CancellationToken.None);

            Assert.Equal(3, checkedCandidates);
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
            Assert.Contains(game.GenerateExtensionCandidates(), candidate => candidate.Path == "assets/test/icon1.png");
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

            HashGuesser.RunBasenameWordSubstitution(
                engine,
                new[] { knownPath },
                new[] { "red", "blue", "new" },
                oldWordCount,
                newWordCount,
                CancellationToken.None);

            AssertResolved(engine, expected);
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
            HashGuesser.RunWordAdditionAttack(
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

            Assert.Contains(lcu.SubstitutePlugin(), candidate =>
                candidate.Path == "plugins/rcp-fe-two/global/en_us/assets/icon.png");
            Assert.Contains(lcu.SubstituteRegionLang(), candidate =>
                candidate.Path == "plugins/rcp-fe-one/pbe/default/assets/icon.png");
            Assert.Contains(lcu.SubstituteRegionLang(), candidate =>
                candidate.Path == "plugins/rcp-fe-one/global/default/assets/icon.png");
            Assert.Contains(lcu.GuessPatterns(), candidate =>
                candidate.Path == "plugins/rcp-fe-lol-perks/global/default/images/construct/8000/environment.jpg");
        }

        [Fact]
        public void LcuExtensionSubstitutionUsesEveryKnownPathLikeCdtb()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "root/source.one",
                "plugins/rcp-fe-test/global/default/asset.two"
            }), null);

            var candidates = lcu.GenerateLcuExtensionCandidates().Select(candidate => candidate.Path).ToHashSet();

            Assert.Contains("root/source.two", candidates);
        }

        [Fact]
        public void GameBasenamePrefixesAreDeduplicatedLikeCdtbSet()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { "assets/ui/icon.png" }));

            var candidates = game.CheckBasenamePrefixes(new[] { "2x_", "2x_" }).Select(candidate => candidate.Path).ToList();

            Assert.Equal("assets/ui/2x_icon.png", Assert.Single(candidates));
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
            Assert.Contains(lcu.SubstituteNumbers(3), candidate =>
                candidate.Path == "plugins/rcp-fe-one/global/default/navigation/old-icon2.png");
            Assert.DoesNotContain(lcu.SubstituteNumbers(3), candidate =>
                candidate.Path == "plugins/rcp-fe-one/global/default/navigation/old-icon002.png");
            Assert.Contains(lcu.SubstitutePlugin(), candidate =>
                candidate.Path == "plugins/rcp-fe-two/global/default/navigation/old-icon1.png");

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

            Assert.Contains(lcu.SubstituteNumbers(20), candidate =>
                candidate.Path == "plugins/rcp-fe-one/global/default/skin1/old-icon2.png");
            Assert.DoesNotContain(lcu.SubstituteNumbers(20), candidate =>
                candidate.Path == "plugins/rcp-fe-one/global/default/skin2/old-icon1.png");
        }

        [Fact]
        public void LcuAdvancedPartiesAttackUsesReducedPngWordsForOneToTwoSubstitution()
        {
            var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, new[]
            {
                "plugins/rcp-fe-lol-parties/global/default/assets/old-icon.png",
                "plugins/rcp-fe-lol-static-assets/global/default/images/new-alpha.png"
            }), null);
            const string expected = "plugins/rcp-fe-lol-parties/global/default/assets/new-alpha-icon.png";
            var engine = CreateEngine(HashGuessDomain.Lcu, expected);

            lcu.SubstitutePartiesBasenameWordPairs(engine, CancellationToken.None);

            AssertResolved(engine, expected);
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
                "assets/shaders/test.ps.dx11"
            }));

            Assert.Contains("ahri", game.GetCharacters());
            Assert.Contains("shaders", game.BuildWordlist());
            Assert.Contains(game.CheckBasenamePrefixes(), candidate => candidate.Path == "assets/ui/2x_icon.png");
            Assert.Contains(game.GuessCharacterFiles(new[] { "lux" }), candidate =>
                candidate.Path == "data/characters/lux/skins/base/lux.skn");
            Assert.Contains(game.GuessShaderVariants(), candidate => candidate.Path == "assets/shaders/test.ps.metal_19900");
            Assert.Contains(game.GuessShaderVariants(), candidate => candidate.Path == "assets/shaders/test.ps-dx11");
            Assert.Contains(game.GuessShaderVariants(), candidate => candidate.Path == "assets/shaders/test.ps-metal_19900");
        }

        [Theory]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-dx11")]
        [InlineData("assets/shaders/generated/shaders/test.ps_2_0-metal")]
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
        public async System.Threading.Tasks.Task GameSkinNumberLanguageAndLocalGroupMethodsMatchCdtb()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/ahri/skins/skin1/foo_skin2.en_us.dds",
                "assets/characters/ahri/skins/skin2/other.dds"
            }));

            Assert.Contains(game.SubstituteSkinNumbers(), candidate =>
                candidate.Path == "assets/characters/ahri/skins/skin2/foo_skin1.en_us.dds");
            Assert.Contains(game.SubstituteLang(), candidate =>
                candidate.Path == "assets/characters/ahri/skins/skin1/foo_skin2.fr_fr.dds");

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
        public void GameSkinNumberEqualTokensGenerateRealPairs()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/characters/annie/skins/skin1/annie_skin1.skn",
                "data/characters/annie/skins/skin3/annie_skin3.dds"
            }));
            const string expected = "data/characters/annie/skins/skin3/annie_skin3.skn";
            var engine = CreateEngine(HashGuessDomain.Game, expected);

            foreach (var candidate in game.SubstituteSkinNumbers())
            {
                game.Check(engine, candidate.Path, candidate.Strategy);
                if (engine.RemainingUnknownCount == 0) break;
            }

            AssertResolved(engine, expected);
            Assert.DoesNotContain(game.SubstituteSkinNumbers(), candidate =>
                candidate.Path == "data/characters/annie/skins/skin3/annie_skin1.skn");
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

        [Fact]
        public void FolderNumberSubstitutionResolvesDirectoryHashes()
        {
            string knownPath = "assets/maps/map11/scene.dds";
            string targetPath = "assets/maps/map12/scene.dds";
            ulong targetHash = XxHash64Ext.Hash(targetPath);

            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { knownPath }));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash });
            var candidates = game.SubstituteNumbers(maximum: 20).ToList();

            Assert.Contains(candidates, candidate => candidate.Path == targetPath);

            int checkedCandidates = 0;
            foreach (var candidate in candidates)
            {
                checkedCandidates++;
                game.Check(engine, candidate.Path, candidate.Strategy);
                if (engine.RemainingUnknownCount == 0) break;
            }

            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Equal(targetPath, engine.Matches[targetHash].Path);
            Assert.True(checkedCandidates <= 40);
        }

        [Fact]
        public void GameSkinNumberCandidatesRespectBudget()
        {
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "data/characters/annie/skins/skin1/annie_skin1.skn",
                "data/characters/annie/skins/skin3/annie_skin3.dds"
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
        public async System.Threading.Tasks.Task GameSkinGroupsBinLocalCapsCombinationLength()
        {
            var corpus = new[] { "assets/characters/test/skins/skin0/a.dds" }
                .Concat(Enumerable.Range(1, 9).Select(skin => $"assets/characters/test/skins/skin{skin}/a.dds"))
                .ToArray();
            var game = new GameHashGuesser(new HashFile(HashGuessDomain.Game, corpus));
            const string tooDeep = "data/test_skins_skin0_skins_skin1_skins_skin2_skins_skin3_skins_skin4_skins_skin5_skins_skin6_skins_skin7_skins_skin8.bin";
            const string withinCap = "data/test_skins_skin0_skins_skin1_skins_skin2_skins_skin3_skins_skin4_skins_skin5_skins_skin6_skins_skin7.bin";

            var deepEngine = CreateEngine(HashGuessDomain.Game, tooDeep);
            await game.GuessSkinGroupsBin(deepEngine, CancellationToken.None);
            Assert.Equal(1, deepEngine.RemainingUnknownCount);

            var capEngine = CreateEngine(HashGuessDomain.Game, withinCap);
            await game.GuessSkinGroupsBin(capEngine, CancellationToken.None);
            AssertResolved(capEngine, withinCap);
        }
    }
}

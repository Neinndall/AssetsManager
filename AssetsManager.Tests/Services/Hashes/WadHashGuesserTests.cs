using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.Services.Hashes
{
    public sealed class WadHashGuesserTests
    {
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
        public void NormalizationPreservesLongRiotCandidates()
        {
            string candidate = "assets/" + new string('a', 600) + ".bin";

            Assert.Equal(candidate, HashGuessEngine.NormalizePath(candidate));
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
            game.SubstituteBasenames(engine, CancellationToken.None);

            AssertResolved(engine, expected);
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
            game.CheckMany(manyEngine, new[] { "assets/missing.bin", many }, HashGuessStrategy.WordlistVariant);
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
            Assert.Contains(lcu.GuessPatterns(), candidate =>
                candidate.Path == "plugins/rcp-fe-lol-perks/global/default/images/construct/8000/environment.jpg");
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
            const string shortened = "assets/test.lua";
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

        private static void AssertResolved(HashGuessEngine engine, string expected)
        {
            HashGuessMatch match = Assert.Single(engine.Matches).Value;
            Assert.Equal(expected, match.Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class GameTextureFamilyTests
    {
        private const string Seed = "assets/characters/example/skins/skin80/example_skin80_body_f1_tx_cm.tex";
        private const string Target = "assets/characters/example/skins/skin80/example_skin80_body_f1_scrollmask_tx_cm.tex";

        private static string[] Corpus => new[]
        {
            Seed,
            "assets/characters/teacher/skins/skin1/teacher_scrollmask_tx_cm.tex",
            "assets/characters/teacher/skins/skin2/teacher_scrollmask_tx_cm.tex"
        };

        private static BinTree Tree(ulong target, uint classHash = 0xff9d3409) => new(
            new[]
            {
                new BinTreeObject(1, classHash, new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(2, XxHash64Ext.Hash(Seed)),
                    new BinTreeOptional(3, new BinTreeWadChunkLink(0, target))
                })
            }, Array.Empty<string>());

        [Fact]
        public void LearnsCompoundSuffixAndPreservesVerifiedProvenance()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
            index.Guess(engine, Tree(XxHash64Ext.Hash(Target)), "unknown.bin", "example.wad.client", 123, CancellationToken.None);
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(Target, match.Path);
            Assert.Equal("example.wad.client", match.SourceWadPath);
            Assert.Equal(123UL, match.SourceChunkHash);
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void ScansFamilyOncePerEngineButAllowsIndependentRuns()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var tree = Tree(42);
            var first = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            index.Guess(first, tree, "unknown.bin", "example", 1, CancellationToken.None);
            long candidates = first.CheckedCandidates;
            Assert.True(candidates > 0);
            index.Guess(first, tree, "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Equal(candidates, first.CheckedCandidates);
            var next = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            index.Guess(next, Tree(XxHash64Ext.Hash(Target)), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Single(next.Matches);
        }

        [Fact]
        public void UnrelatedClassDoesNotTriggerTextureAttack()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            index.Guess(engine, Tree(XxHash64Ext.Hash(Target), 99), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.Equal(0, engine.CheckedCandidates);
        }

        [Fact]
        public void CancellationDoesNotPoisonFamilyRetry()
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 }, _ => cancellation.Cancel());
            var tree = Tree(XxHash64Ext.Hash(Target));
            Assert.Throws<OperationCanceledException>(() =>
                index.Guess(engine, tree, "unknown.bin", "example", 1, cancellation.Token));
            long candidates = engine.CheckedCandidates;
            index.Guess(engine, Tree(42), "unknown.bin", "example", 1, CancellationToken.None);
            Assert.True(engine.CheckedCandidates > candidates);
        }

        [Fact]
        public void TextureBuildListResolvesTargetAcrossFamilies()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Corpus));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            long candidates = guesser.SubstituteTextureBuildListWords(engine, CancellationToken.None);
            Assert.True(candidates > 0);
            Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(600)]
        public void EncodedBuildListRetainsEveryCombinationAndAttemptIncludingUnicodeAndLongPaths(int padding)
        {
            string directory = "assets/characters/example/skins/skin1/" + new string('x', padding);
            string first = directory + "first", second = directory + "second";
            string[] prefixes = { first + "_\u00e9toile_tx_", first + "_\u00e9toile_", first + "_",
                second + "_\u00e9toile_tx_", second + "_\u00e9toile_", second + "_" };
            string[] suffixes = { "cm.tex", "tx_cm.tex", "\u00e9toile_tx_cm.tex" };
            var expected = new HashSet<string>();
            foreach (string prefix in prefixes)
            foreach (string suffix in suffixes) expected.Add(prefix + suffix);
            var targets = new HashSet<ulong>();
            foreach (string path in expected) targets.Add(XxHash64Ext.Hash(path));
            targets.Add(42);
            var engine = new HashGuessEngine(HashGuessDomain.Game, targets);
            var index = new GameTextureFamilyIndex(new[] { first + "_\u00e9toile_tx_cm.tex", second + "_\u00e9toile_tx_cm.tex" }, CancellationToken.None);
            long count = index.RunBuildList(engine, CancellationToken.None);
            Assert.Equal(18L, count);
            Assert.Equal(count, engine.CheckedCandidates);
            Assert.Equal(expected.Count, engine.Matches.Count);
            foreach (var match in engine.Matches.Values) Assert.Contains(match.Path, expected);
            Assert.Equal(1, engine.RemainingUnknownCount);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        public void TextureBuildListChecksEntireBudgetAndReportsActualAttempts(long budget)
        {
            var index = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            long reported = -1;

            long attempts = index.RunBuildList(engine, CancellationToken.None, budget, count => reported = count);

            Assert.Equal(budget, engine.CheckedCandidates);
            Assert.Equal(engine.CheckedCandidates, attempts);
            Assert.Equal(attempts, reported);
        }

        [Theory]
        [InlineData("assets/characters/petexample/themes/summer/petexample_summer")]
        [InlineData("assets/maps/kitpieces/tft/set18/textures/example")]
        public void TextureBuildListIncludesThemesAndMapKitpieces(string prefix)
        {
            string target = prefix + "_wall_a_tx.tex";
            string[] paths =
            {
                prefix + "_floor_a_tx.tex",
                "assets/maps/kitpieces/tft/set1/textures/teacher_wall_a_tx.tex",
                "assets/maps/kitpieces/tft/set2/textures/teacher_wall_a_tx.tex"
            };
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, paths));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            guesser.SubstituteTextureBuildListWords(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }
        [Fact]
        public void GameCustomAttacksResolvesTextureBuildListSubMethod()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Corpus));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target) });
            guesser.RunCustomAttacks(
                engine,
                null,
                CancellationToken.None,
                new HashSet<string> { "game-custom-textures" });
            Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        }

        [Theory]
        [InlineData("assets/maps/particles/tft/example/rocket", ".tex")]
        [InlineData("assets/characters/example/skins/skin1/particles/rocket", ".dds")]
        public void TextureBuildListChecksSmallParticleRoleFamilies(string stem, string extension)
        {
            string target = stem + "_m2" + extension;
            var index = new GameTextureFamilyIndex(new[] { stem + "_tx" + extension }, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });
            long attempts = index.RunBuildList(engine, CancellationToken.None);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            Assert.InRange(attempts, 1, 5);
        }

        [Fact]
        public void ParticleRolePassPreservesBudgetAccountingAndCancellation()
        {
            var index = new GameTextureFamilyIndex(new[] { "assets/maps/particles/example_tx.tex" }, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
            long reported = -1;
            Assert.Equal(2, index.RunBuildList(engine, CancellationToken.None, 2, count => reported = count));
            Assert.Equal(2, reported);
            Assert.Equal(2, engine.CheckedCandidates);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => index.RunBuildList(engine, cancellation.Token));
            Assert.Equal(2, engine.CheckedCandidates);
        }

        [Fact]
        public void TextureBuildListLearnsNumberedParticleTextureFromMatchingModelAndTexturePrefix()
        {
            const string folder = "assets/characters/petchibijhin/skins/skin2/particles/";
            const string target = folder + "petchibijhin_bloodmoonfinisher_flower01.tex";
            var knownPaths = new[]
            {
                folder + "petchibijhin_bloodmoon_finisher_flower.skl",
                folder + "petchibijhin_bloodmoonfinisher_colorbase.tex",
                folder + "petchibijhin_bloodmoonfinisher_colorbase01.tex"
            };
            Assert.Equal(0x99145fe983438445UL, XxHash64Ext.Hash(target));
            var index = new GameTextureFamilyIndex(knownPaths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });

            long attempts = index.RunBuildList(engine, CancellationToken.None, 200_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            Assert.Equal(attempts, engine.CheckedCandidates);
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void TextureBuildListRecombinesCompanionClassicMaterialRolesAcrossCharacters()
        {
            const string target = "assets/characters/petstyletwomordekaiser/themes/base/petstyletwomordekaiser_base_classic_scapula_material_tx_cm.tex";
            var knownPaths = new[]
            {
                "assets/characters/petstyletwomordekaiser/themes/base/petstyletwomordekaiser_base_classic_body_material_tx_cm.tex",
                "assets/characters/petstyletwogaren/themes/base/petstyletwogaren_base_classic_scapula_tx_cm.tex",
                "assets/characters/petstyletwogaren/themes/base/petstyletwogaren_base_classic_scapula_mask_tx_cm.tex"
            };
            Assert.Equal(0x706e3efbe8aaec50UL, XxHash64Ext.Hash(target));
            var index = new GameTextureFamilyIndex(knownPaths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });

            long attempts = index.RunBuildList(engine, CancellationToken.None, 200_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            Assert.Equal(attempts, engine.CheckedCandidates);
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void TextureBuildListLearnsCompanionVariantStateFromRepeatedCharacterTextureWords()
        {
            const string target = "assets/characters/petpineconehedgehog/themes/base/petpineconehedgehog_base_variant1_charred_tx_cm.tex";
            var knownPaths = new[]
            {
                "assets/characters/petpineconehedgehog/themes/base/petpineconehedgehog_base_variant1_body_tx_cm.tex",
                "assets/characters/petpineconehedgehog/themes/base/petpineconehedgehog_base_variant1_face_tx_cm.tex",
                "assets/characters/maokai/skins/skin01/particles/maokai_charred_w_tar_root_tx.tex",
                "assets/characters/rumble/skins/base/particles/charred_billow_03.tex"
            };
            Assert.Equal(0xc47130955064c823UL, XxHash64Ext.Hash(target));
            var index = new GameTextureFamilyIndex(knownPaths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });

            long attempts = index.RunBuildList(engine, CancellationToken.None, 200_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
            Assert.Equal(attempts, engine.CheckedCandidates);
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void TextureBuildListDoesNotGuessNewPatternsWithoutCorroboratingKnownNames()
        {
            const string flower = "assets/characters/petchibijhin/skins/skin2/particles/petchibijhin_bloodmoonfinisher_flower01.tex";
            const string charred = "assets/characters/petpineconehedgehog/themes/base/petpineconehedgehog_base_variant1_charred_tx_cm.tex";
            var knownPaths = new[]
            {
                "assets/characters/petchibijhin/skins/skin2/particles/petchibijhin_bloodmoon_finisher_flower.skl",
                "assets/characters/unrelated/skins/skin2/particles/unrelated_bloodmoonfinisher_colorbase.tex",
                "assets/characters/petpineconehedgehog/themes/base/petpineconehedgehog_base_variant1_body_tx_cm.tex",
                "assets/characters/maokai/skins/skin01/particles/maokai_charred_w_tar_root_tx.tex"
            };
            var index = new GameTextureFamilyIndex(knownPaths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
            {
                XxHash64Ext.Hash(flower), XxHash64Ext.Hash(charred)
            });

            index.RunBuildList(engine, CancellationToken.None, 200_000);

            Assert.Empty(engine.Matches);
            Assert.Equal(2, engine.RemainingUnknownCount);
        }

        [Fact]
        public void NewTextureRulesDoNotDisplaceLegacyParticleRolesWithinTightBudget()
        {
            const string folder = "assets/characters/companion/skins/skin2/particles/";
            string[] corpus =
            {
                folder + "companion_bloodmoon_finisher_flower.skl",
                folder + "companion_bloodmoonfinisher_colorbase01.tex",
                folder + "companion_rocket_tx.tex"
            };
            string target = folder + "companion_rocket_m2.tex";
            var index = new GameTextureFamilyIndex(corpus, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });

            long attempts = index.RunBuildList(engine, CancellationToken.None, 4);

            Assert.Equal(4L, attempts);
            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void NumberedParticleModelTextureLearnsObservedSuffixesInsteadOfFixedRange()
        {
            const string folder = "assets/characters/fox/skins/skin1/particles/";
            string[] paths =
            {
                folder + "fox_night_festival_flower.skl",
                folder + "fox_nightfestival_colorbase07.tex",
                folder + "fox_nightfestival_colorbase12.tex"
            };
            const string target = folder + "fox_nightfestival_flower07.tex";
            var index = new GameTextureFamilyIndex(paths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });

            index.RunBuildList(engine, CancellationToken.None, 100_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void ClassicMaterialRolesGeneralizeBeyondSharedCharacterNamePrefixes()
        {
            const string target = "assets/characters/petcobalt/themes/base/petcobalt_base_classic_crest_material_tx_cm.tex";
            string[] paths =
            {
                "assets/characters/petcobalt/themes/base/petcobalt_base_classic_body_material_tx_cm.tex",
                "assets/characters/petamber/themes/base/petamber_base_classic_crest_tx_cm.tex",
                "assets/characters/petamber/themes/base/petamber_base_classic_crest_mask_tx_cm.tex"
            };
            var index = new GameTextureFamilyIndex(paths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });

            index.RunBuildList(engine, CancellationToken.None, 100_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void LegacyLearnedSuffixMatchesKeepTheirOriginalSmallBudget()
        {
            long minimumBudget = -1;
            for (long budget = 1; budget <= 100; budget++)
            {
                var legacyIndex = new GameTextureFamilyIndex(Corpus, CancellationToken.None);
                var legacyEngine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
                legacyIndex.RunBuildList(legacyEngine, CancellationToken.None, budget);
                if (!legacyEngine.Matches.ContainsKey(XxHash64Ext.Hash(Target))) continue;
                minimumBudget = budget;
                break;
            }
            Assert.True(minimumBudget > 0, "The existing suffix pattern should fit within a small budget");

            string[] withNewPatterns = Corpus.Concat(new[]
            {
                "assets/characters/companion/skins/skin2/particles/companion_bloodmoon_finisher_flower.skl",
                "assets/characters/companion/skins/skin2/particles/companion_bloodmoonfinisher_colorbase01.tex"
            }).ToArray();
            var index = new GameTextureFamilyIndex(withNewPatterns, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });

            long checkedCandidates = index.RunBuildList(engine, CancellationToken.None, minimumBudget);

            Assert.Equal(minimumBudget, checkedCandidates);
            Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void VariantModifierVocabularyRequiresEvidenceFromDifferentCharacters()
        {
            const string target = "assets/characters/petcobalt/themes/base/petcobalt_base_variant1_cracked_tx_cm.tex";
            string[] paths =
            {
                "assets/characters/petcobalt/themes/base/petcobalt_base_variant1_body_tx_cm.tex",
                "assets/characters/maokai/skins/skin01/particles/maokai_cracked_cracked_01.tex"
            };
            var index = new GameTextureFamilyIndex(paths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });

            index.RunBuildList(engine, CancellationToken.None, 100_000);

            Assert.Empty(engine.Matches);
        }

        [Fact]
        public void VariantModifiersGeneralizeToCrossCharacterWordsWithoutEdSuffix()
        {
            const string target = "assets/characters/petcobalt/themes/base/petcobalt_base_variant1_frozen_tx_cm.tex";
            string[] paths =
            {
                "assets/characters/petcobalt/themes/base/petcobalt_base_variant1_body_tx_cm.tex",
                "assets/characters/maokai/skins/skin01/particles/maokai_frozen_thorns_01.tex",
                "assets/characters/rumble/skins/base/particles/frozen_billow_03.tex"
            };
            var index = new GameTextureFamilyIndex(paths, CancellationToken.None);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target) });

            index.RunBuildList(engine, CancellationToken.None, 100_000);

            Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        }

        [Fact]
        public void ParticleIndexConstructionHasBoundedAllocationWhenNoModelsNeedPrefixes()
        {
            string[] paths = Enumerable.Range(0, 20_000)
                .Select(index => $"assets/characters/example/skins/skin1/particles/example_skin1_effect_{index:D5}_trail_mask_tx.tex")
                .ToArray();
            // No .skl references: the extra model-derived index has no work to do.
            long before = GC.GetAllocatedBytesForCurrentThread();

            _ = new GameTextureFamilyIndex(paths, CancellationToken.None);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 15_000_000, $"Index construction allocated {allocated:N0} bytes without model evidence");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public class GameGrepOptimizationTests
    {
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

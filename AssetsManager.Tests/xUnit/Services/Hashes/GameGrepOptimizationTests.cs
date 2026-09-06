using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
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
                Assert.Equal(expected.Distinct(), GameHashGuesser.EnumerateAnimationNameVariants(character, skin, name, modifiers, suffixes, theme).Distinct());
            }
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
    }
}

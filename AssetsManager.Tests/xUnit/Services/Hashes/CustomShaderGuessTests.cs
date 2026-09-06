using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class CustomShaderGuessTests
    {
        [Fact]
        public void SubstituteShaderVocabWords_DiscoversNewShadersFromVocabulary()
        {
            var knownShaders = new[]
            {
                "assets/shaders/generated/shaders/environment/srx_blend_chemtech_ground.ps.dx11",
                "assets/shaders/generated/shaders/environment/srx_blend_hextech_island.ps.dx11"
            };
            var hashFile = new HashFile(HashGuessDomain.Game, knownShaders);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var targets = new HashSet<ulong>
            {
                XxHash64Ext.Hash("assets/shaders/generated/shaders/environment/srx_blend_hextech_ground.ps.dx11"),
                XxHash64Ext.Hash("assets/shaders/generated/shaders/environment/srx_blend_chemtech_island.ps.dx11")
            };

            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, targets, m => matches.Add(m));

            int checkedCount = guesser.SubstituteShaderVocabWords(engine, System.Threading.CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.Contains(matches, m => m.Path == "assets/shaders/generated/shaders/environment/srx_blend_hextech_ground.ps.dx11");
            Assert.Contains(matches, m => m.Path == "assets/shaders/generated/shaders/environment/srx_blend_chemtech_island.ps.dx11");
        }

        [Fact]
        public void SubstituteShaderVocabWords_RespectsCancellation()
        {
            var knownShaders = new[]
            {
                "assets/shaders/generated/shaders/environment/srx_blend_chemtech_ground.ps.dx11",
                "assets/shaders/generated/shaders/environment/srx_blend_hextech_island.ps.dx11"
            };
            var hashFile = new HashFile(HashGuessDomain.Game, knownShaders);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 12345UL });

            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                guesser.SubstituteShaderVocabWords(engine, cts.Token));
        }

        [Fact]
        public void GenericBasenameWordSubstitution_RespectsCandidateBudget()
        {
            var paths = new[] { "assets/ui/icons/test_item.png" };
            var words = new[] { "alpha", "beta", "gamma", "delta", "omega" };
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, paths));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 99999UL });

            int checkedCount = guesser._SubstituteBasenameWords(
                engine,
                paths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                System.Threading.CancellationToken.None,
                candidateBudget: 2);

            Assert.Equal(2, checkedCount);
            Assert.Equal(2L, engine.CheckedCandidates);
        }

        [Fact]
        public void SubstituteShaderVocabWords_IncludesComputeShadersAndDataShaders()
        {
            var knownShaders = new[]
            {
                "assets/shaders/hlsl/ssao/ssao_depth.cs-dx11",
                "data/shaders/hlsl/compute/blur_filter.cs-dx11"
            };
            var hashFile = new HashFile(HashGuessDomain.Game, knownShaders);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var targets = new HashSet<ulong>
            {
                XxHash64Ext.Hash("assets/shaders/hlsl/ssao/ssao_filter.cs-dx11"),
                XxHash64Ext.Hash("data/shaders/hlsl/compute/blur_depth.cs-dx11")
            };

            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, targets, m => matches.Add(m));

            int checkedCount = guesser.SubstituteShaderVocabWords(engine, System.Threading.CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/ssao/ssao_filter.cs-dx11");
            Assert.Contains(matches, m => m.Path == "data/shaders/hlsl/compute/blur_depth.cs-dx11");
        }

        [Fact]
        public void GuessShaderVariants_GeneratesComputeShaderVariants()
        {
            var knownShaders = new[]
            {
                "assets/shaders/hlsl/ssao/ssao.cs"
            };
            var hashFile = new HashFile(HashGuessDomain.Game, knownShaders);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var targets = new HashSet<ulong>
            {
                0x908d337c904f3ba2, // assets/shaders/hlsl/ssao/ssao.cs-dx11
                0x0711bc84e55d08ba  // assets/shaders/hlsl/ssao/ssao.cs-dx11_0
            };

            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, targets, m => matches.Add(m));

            int checkedCount = guesser.GuessShaderVariants(engine, System.Threading.CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.Equal(2, matches.Count);
            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/ssao/ssao.cs-dx11");
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/ssao/ssao.cs-dx11_0");
        }

        [Fact]
        public void GuessShaderVariants_DiscoversSmaaAndSsaoCrackedShaders()
        {
            var baseShaders = new[]
            {
                "assets/shaders/hlsl/smaa/smaa_edgedetection.ps",
                "assets/shaders/hlsl/smaa/smaa_edgedetection.vs",
                "assets/shaders/hlsl/smaa/smaa_blendweightcalc.ps",
                "assets/shaders/hlsl/smaa/smaa_blendweightcalc.vs",
                "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.ps",
                "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.vs",
                "assets/shaders/hlsl/ssao/ssao.cs"
            };
            var hashFile = new HashFile(HashGuessDomain.Game, baseShaders);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var expected = new Dictionary<ulong, string>
            {
                [0x4a1c639d250b6648] = "assets/shaders/hlsl/smaa/smaa_edgedetection.ps-dx11",
                [0x55b4c8b32e98fdde] = "assets/shaders/hlsl/smaa/smaa_edgedetection.vs-dx11",
                [0x7662d6b8122bda79] = "assets/shaders/hlsl/smaa/smaa_edgedetection.ps-dx11_0",
                [0x05253ff0da3a175b] = "assets/shaders/hlsl/smaa/smaa_edgedetection.vs-dx11_0",
                [0xdc8de7bf0ab650f7] = "assets/shaders/hlsl/smaa/smaa_blendweightcalc.ps-dx11",
                [0xdff003a7f0162c2e] = "assets/shaders/hlsl/smaa/smaa_blendweightcalc.vs-dx11",
                [0x22b458f03fe127c2] = "assets/shaders/hlsl/smaa/smaa_blendweightcalc.ps-dx11_0",
                [0xe00b7663504c7e25] = "assets/shaders/hlsl/smaa/smaa_blendweightcalc.vs-dx11_0",
                [0x41e9cbf1b06fe2f8] = "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.ps-dx11",
                [0xe25b3ec021a161ec] = "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.vs-dx11",
                [0x7d4ea7feb74bdebd] = "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.ps-dx11_0",
                [0x84d9eb55e946f5c0] = "assets/shaders/hlsl/smaa/smaa_neighborhoodblend.vs-dx11_0",
                [0x908d337c904f3ba2] = "assets/shaders/hlsl/ssao/ssao.cs-dx11",
                [0x0711bc84e55d08ba] = "assets/shaders/hlsl/ssao/ssao.cs-dx11_0"
            };

            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(expected.Keys), m => matches.Add(m));

            int checkedCount = guesser.GuessShaderVariants(engine, System.Threading.CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.Equal(14, matches.Count);
            Assert.Equal(0, engine.RemainingUnknownCount);

            foreach (var match in matches)
            {
                Assert.True(expected.TryGetValue(match.Hash, out string expectedPath));
                Assert.Equal(expectedPath, match.Path);
            }
        }
    }
}

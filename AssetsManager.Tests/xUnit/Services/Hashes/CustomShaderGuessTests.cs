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
    }
}

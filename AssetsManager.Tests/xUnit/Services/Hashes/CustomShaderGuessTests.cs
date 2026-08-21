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
        public void GuessCustomShaders_DiscoversNewShadersFromVocabularyWithoutExistingHash()
        {
            // Empty KnownPaths - no ssao in known paths!
            var hashFile = new HashFile(HashGuessDomain.Game, Array.Empty<string>());
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var targets = new HashSet<ulong>
            {
                XxHash64Ext.Hash("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11"),
                XxHash64Ext.Hash("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11_0"),
                XxHash64Ext.Hash("assets/shaders/hlsl/hud/compositesdf.ps.dx11"),
                XxHash64Ext.Hash("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11")
            };

            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, targets, m => matches.Add(m));

            int checkedCount = guesser.GuessCustomShaders(engine, System.Threading.CancellationToken.None);

            Assert.True(checkedCount > 0);
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/ssao/ssaosimple.ps.dx11");
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/ssao/ssaosimple.ps.dx11_0");
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/hud/compositesdf.ps.dx11");
            Assert.Contains(matches, m => m.Path == "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11");
        }
    }
}

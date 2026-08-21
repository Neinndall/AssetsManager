using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class SsaoDiscoverySimulationTests
    {
        [Fact]
        public void IfBasePathIsKnown_GuessShaderVariantsFindsAll8Variants()
        {
            // Create a temporary hash file with only the base path
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(tempFile, new[]
                {
                    "0000000000000000 assets/shaders/hlsl/ssao/ssaosimple.ps"
                });

                var hashFile = new HashFile(HashGuessDomain.Game, tempFile);
                var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

                // Target unknowns: all 8 variants
                var targets = new HashSet<ulong>
                {
                    0xd5d560bcac645d86, // .ps-dx11
                    0x52723a44b10860b6, // .ps-dx11_0
                    0xa6528589022caecb, // .ps-metal
                    0x07c15b487d5c7f6f, // .ps-metal_0
                    0xa1334986aab25f30, // .ps.dx11
                    0x6f1e1f2d87597743, // .ps.dx11_0
                    0x6486e3ec66201432, // .ps.metal
                    0xf8368589f560cadf  // .ps.metal_0
                };

                var matches = new List<HashGuessMatch>();
                var engine = new HashGuessEngine(HashGuessDomain.Game, targets, m => matches.Add(m));

                int checkedCount = guesser.GuessShaderVariants(engine, System.Threading.CancellationToken.None);

                // Assert that all 8 were successfully discovered!
                Assert.Equal(8, matches.Count);
                Assert.Equal(0, engine.RemainingUnknownCount);

                var discoveredPaths = matches.Select(m => m.Path).ToHashSet();
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11_0", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps-dx11", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps-dx11_0", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps.metal", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps.metal_0", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps-metal", discoveredPaths);
                Assert.Contains("assets/shaders/hlsl/ssao/ssaosimple.ps-metal_0", discoveredPaths);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Hashes
{
    public sealed class CommunityDragonPr42VerificationTests
    {
        [Fact]
        public void VerifyAll40ShadersFromPr42AreDiscovered()
        {
            var pr42Expected = new (ulong Hash, string Path)[]
            {
                // enveffectors (8)
                (0xc7625f2563af8987, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11"),
                (0x1dbe745cf9bc7582, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11_0"),
                (0xedca0edbc3f5766f, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-metal"),
                (0x62cd01552cc9d58f, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-metal_0"),
                (0x106ac268c130a65f, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.dx11"),
                (0xa8ebb5724074b166, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.dx11_0"),
                (0x8981df02e998d7b1, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.metal"),
                (0xdb8d6a41a5968cb8, "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.metal_0"),

                // filters/gauss5_edge_aware (8)
                (0x818206085a0f9923, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps-dx11"),
                (0x608ead2a066a9fec, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps-dx11_0"),
                (0x7a7d550b04ff66da, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps-metal"),
                (0xefa9d4164a73485c, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps-metal_0"),
                (0x670d732a43dc3f94, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps.dx11"),
                (0x0d69b0bd90f3ceca, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps.dx11_0"),
                (0xb41aa1ac18d6c3fd, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps.metal"),
                (0x7ad4cb206bc8463c, "assets/shaders/hlsl/filters/gauss5_edge_aware.ps.metal_0"),

                // hud/compositesdf (16)
                (0x7ff1827c011854a0, "assets/shaders/hlsl/hud/compositesdf.ps-dx11"),
                (0xa1482b1c83e03e75, "assets/shaders/hlsl/hud/compositesdf.ps-dx11_0"),
                (0x930f877c00c9c65b, "assets/shaders/hlsl/hud/compositesdf.ps-metal"),
                (0x9246556adab900c8, "assets/shaders/hlsl/hud/compositesdf.ps-metal_0"),
                (0x5ad58f3f3c1f317b, "assets/shaders/hlsl/hud/compositesdf.ps.dx11"),
                (0xacbf952fce40ffb0, "assets/shaders/hlsl/hud/compositesdf.ps.dx11_0"),
                (0xbef6b6fffc9c54b0, "assets/shaders/hlsl/hud/compositesdf.ps.metal"),
                (0x76b8dd893c6b1e98, "assets/shaders/hlsl/hud/compositesdf.ps.metal_0"),
                (0x1e512ea7c06b33ae, "assets/shaders/hlsl/hud/compositesdf.vs-dx11"),
                (0xf325b08d539488c7, "assets/shaders/hlsl/hud/compositesdf.vs-dx11_0"),
                (0xf00e58514656cb6f, "assets/shaders/hlsl/hud/compositesdf.vs-metal"),
                (0x0896f00721272c3e, "assets/shaders/hlsl/hud/compositesdf.vs-metal_0"),
                (0x7105bf9a396e2793, "assets/shaders/hlsl/hud/compositesdf.vs.dx11"),
                (0x201edb460ca50288, "assets/shaders/hlsl/hud/compositesdf.vs.dx11_0"),
                (0xc57dacf66450311c, "assets/shaders/hlsl/hud/compositesdf.vs.metal"),
                (0x25b0ae3df5db01f9, "assets/shaders/hlsl/hud/compositesdf.vs.metal_0"),

                // ssao/ssaosimple (8)
                (0xd5d560bcac645d86, "assets/shaders/hlsl/ssao/ssaosimple.ps-dx11"),
                (0x52723a44b10860b6, "assets/shaders/hlsl/ssao/ssaosimple.ps-dx11_0"),
                (0xa6528589022caecb, "assets/shaders/hlsl/ssao/ssaosimple.ps-metal"),
                (0x07c15b487d5c7f6f, "assets/shaders/hlsl/ssao/ssaosimple.ps-metal_0"),
                (0xa1334986aab25f30, "assets/shaders/hlsl/ssao/ssaosimple.ps.dx11"),
                (0x6f1e1f2d87597743, "assets/shaders/hlsl/ssao/ssaosimple.ps.dx11_0"),
                (0x6486e3ec66201432, "assets/shaders/hlsl/ssao/ssaosimple.ps.metal"),
                (0xf8368589f560cadf, "assets/shaders/hlsl/ssao/ssaosimple.ps.metal_0")
            };

            // 1. Verify exact mathematical xxHash64 calculation for all 40 paths
            foreach (var item in pr42Expected)
            {
                ulong computedHash = XxHash64Ext.Hash(item.Path);
                Assert.Equal(item.Hash, computedHash);
            }

            // 2. Verify that GuessShaderVariants discovers all 40 given their base paths
            var basePaths = new[]
            {
                "assets/shaders/hlsl/enveffectors/ps_env_effectors.ps",
                "assets/shaders/hlsl/filters/gauss5_edge_aware.ps",
                "assets/shaders/hlsl/hud/compositesdf.ps",
                "assets/shaders/hlsl/hud/compositesdf.vs",
                "assets/shaders/hlsl/ssao/ssaosimple.ps"
            };

            var hashFile = new HashFile(HashGuessDomain.Game, basePaths);
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);

            var targetSet = new HashSet<ulong>(pr42Expected.Select(x => x.Hash));
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, targetSet, m => matches.Add(m));

            int checkedCount = guesser.GuessShaderVariants(engine, System.Threading.CancellationToken.None);

            // Check that ALL 40 matches are found!
            Assert.Equal(40, matches.Count);
            Assert.Equal(0, engine.RemainingUnknownCount);

            var discoveredPaths = matches.Select(m => m.Path).ToHashSet();
            foreach (var item in pr42Expected)
            {
                Assert.Contains(item.Path, discoveredPaths);
            }
        }
    }
}

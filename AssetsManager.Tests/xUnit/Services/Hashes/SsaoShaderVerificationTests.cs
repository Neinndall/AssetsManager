using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class SsaoShaderVerificationTests
    {
        [Fact]
        public void SsaoShaderVariants_MatchExpectedHashes()
        {
            var expected = new (string Path, ulong Hash)[]
            {
                ("assets/shaders/hlsl/ssao/ssaosimple.ps-dx11", 0xd5d560bcac645d86),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps-dx11_0", 0x52723a44b10860b6),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps-metal", 0xa6528589022caecb),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps-metal_0", 0x07c15b487d5c7f6f),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11", 0xa1334986aab25f30),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps.dx11_0", 0x6f1e1f2d87597743),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps.metal", 0x6486e3ec66201432),
                ("assets/shaders/hlsl/ssao/ssaosimple.ps.metal_0", 0xf8368589f560cadf)
            };

            foreach (var item in expected)
            {
                ulong hash = XxHash64Ext.Hash(item.Path);
                Assert.Equal(item.Hash, hash);
            }
        }
    }
}

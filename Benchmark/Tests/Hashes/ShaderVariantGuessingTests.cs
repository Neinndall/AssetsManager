using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace AssetsManager.BenchmarkTests.Hashes
{
    public sealed class ShaderVariantGuessingTests
    {
        [Theory]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11", "c7625f2563af8987")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-dx11_0", "1dbe745cf9bc7582")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-metal", "edca0edbc3f5766f")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps-metal_0", "62cd01552cc9d58f")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.dx11", "106ac268c130a65f")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.dx11_0", "a8ebb5724074b166")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.metal", "8981df02e998d7b1")]
        [InlineData("assets/shaders/hlsl/enveffectors/ps_env_effectors.ps.metal_0", "db8d6a41a5968cb8")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps-dx11", "818206085a0f9923")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps-dx11_0", "608ead2a066a9fec")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps-metal", "7a7d550b04ff66da")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps-metal_0", "efa9d4164a73485c")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps.dx11", "670d732a43dc3f94")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps.dx11_0", "0d69b0bd90f3ceca")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps.metal", "b41aa1ac18d6c3fd")]
        [InlineData("assets/shaders/hlsl/filters/gauss5_edge_aware.ps.metal_0", "7ad4cb206bc8463c")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps-dx11", "7ff1827c011854a0")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps-dx11_0", "a1482b1c83e03e75")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps-metal", "930f877c00c9c65b")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps-metal_0", "9246556adab900c8")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps.dx11", "5ad58f3f3c1f317b")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps.dx11_0", "acbf952fce40ffb0")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps.metal", "bef6b6fffc9c54b0")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.ps.metal_0", "76b8dd893c6b1e98")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs-dx11", "1e512ea7c06b33ae")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs-dx11_0", "f325b08d539488c7")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs-metal", "f00e58514656cb6f")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs-metal_0", "0896f00721272c3e")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs.dx11", "7105bf9a396e2793")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs.dx11_0", "201edb460ca50288")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs.metal", "c57dacf66450311c")]
        [InlineData("assets/shaders/hlsl/hud/compositesdf.vs.metal_0", "25b0ae3df5db01f9")]
        public void ExpectedShaderHashesMatchXxHash64(string path, string expectedHex)
        {
            ulong hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            Assert.Equal(expectedHex, hash.ToString("x16"));
        }
    }
}

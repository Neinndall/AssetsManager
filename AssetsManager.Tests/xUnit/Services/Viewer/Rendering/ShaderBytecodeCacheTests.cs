using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class ShaderBytecodeCacheTests
    {
        [Fact]
        public void EquivalentCodeSharesRequestWithoutSharingMaterialState()
        {
            GameMaterialPass first = Pass(new("A", "1", GameMaterialDefineSource.Material), new("B", "2", GameMaterialDefineSource.Feature));
            GameMaterialPass other = first with
            {
                Defines = new[] { first.Defines[1], first.Defines[0] },
                Parameters = new[] { new GameMaterialParameter("Color", Vector4.One, GameMaterialParamSource.Material) },
                RuntimeSwitches = new[] { new KeyValuePair<string, bool>("USE_COLOR", true) },
                State = first.State with { BlendEnabled = true }
            };
            Assert.Equal(GameShaderRuntime.BytecodeRequestFor(first, GameMaterialKind.Particles),
                GameShaderRuntime.BytecodeRequestFor(other, GameMaterialKind.Particles));
            Assert.NotEqual(first.Parameters, other.Parameters);
            Assert.NotEqual(first.State, other.State);
        }

        [Fact]
        public void ShaderStagesKindsAndCompileDefinesRemainIndependent()
        {
            GameMaterialPass pass = Pass(new GameMaterialDefine("A", "1", GameMaterialDefineSource.Material));
            var request = GameShaderRuntime.BytecodeRequestFor(pass, GameMaterialKind.Particles);
            Assert.NotEqual(request, GameShaderRuntime.BytecodeRequestFor(pass, GameMaterialKind.SkinnedMesh));
            Assert.NotEqual(request, GameShaderRuntime.BytecodeRequestFor(pass with { VertexShaderPath = "OtherVertex" }, GameMaterialKind.Particles));
            Assert.NotEqual(request, GameShaderRuntime.BytecodeRequestFor(pass with { PixelShaderPath = "OtherPixel" }, GameMaterialKind.Particles));
            Assert.NotEqual(request, GameShaderRuntime.BytecodeRequestFor(Pass(new GameMaterialDefine("A", "0", GameMaterialDefineSource.Material)), GameMaterialKind.Particles));
            Assert.Equal(request, GameShaderRuntime.BytecodeRequestFor(pass with { VertexShaderPath = pass.ShaderPath, PixelShaderPath = pass.ShaderPath }, GameMaterialKind.Particles));
        }

        [Fact]
        public void AuthoredSeparatorsCannotAliasAnotherPermutation()
        {
            GameMaterialPass one = Pass(new GameMaterialDefine("A", "1;B=2", GameMaterialDefineSource.Material));
            GameMaterialPass two = Pass(new("A", "1", GameMaterialDefineSource.Material), new("B", "2", GameMaterialDefineSource.Material));
            Assert.NotEqual(GameShaderRuntime.BytecodeRequestFor(one, GameMaterialKind.Particles),
                GameShaderRuntime.BytecodeRequestFor(two, GameMaterialKind.Particles));
        }

        private static GameMaterialPass Pass(params GameMaterialDefine[] defines) => new(0, "VFX/Test", defines,
            Array.Empty<KeyValuePair<string, bool>>(), Array.Empty<GameMaterialTexture>(),
            Array.Empty<GameMaterialParameter>(), GameMaterialPassState.Default);
    }
}

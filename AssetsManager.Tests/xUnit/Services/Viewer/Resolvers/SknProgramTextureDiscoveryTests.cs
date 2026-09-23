using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers
{
    public sealed class SknProgramTextureDiscoveryTests
    {
        [Fact]
        public void ReferencedTexturePathsIncludesEveryProgramPassTexture()
        {
            const string first = "ASSETS/Characters/Test/First.tex";
            const string second = "ASSETS/Characters/Test/Second.tex";
            var program = new GameMaterialProgram(
                GameMaterialKind.SkinnedMesh,
                false,
                new[]
                {
                    Pass("First", first),
                    Pass("Second", second)
                });
            var material = new SknMaterialDefinition(
                Array.Empty<SknMaterialSampler>(),
                new Dictionary<string, System.Numerics.Vector4>())
            {
                Program = program
            };
            var metadata = new SknMaterialTextureMetadata(
                null,
                material,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase));

            string[] paths = metadata.ReferencedTexturePaths.ToArray();

            Assert.Contains(paths, path => string.Equals(first, path, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(paths, path => string.Equals(second, path, StringComparison.OrdinalIgnoreCase));
        }

        private static GameMaterialPass Pass(string name, string path) =>
            new(
                0,
                name,
                Array.Empty<GameMaterialDefine>(),
                Array.Empty<KeyValuePair<string, bool>>(),
                new[]
                {
                    new GameMaterialTexture(
                        "Diffuse_Texture",
                        new MapTextureReference(path, 0),
                        GameMaterialTextureSource.ShaderDefault,
                        new GameMaterialSamplerState(
                            null,
                            MapTextureWrap.Repeat,
                            MapTextureWrap.Repeat,
                            MapTextureWrap.Repeat,
                            true,
                            true))
                },
                Array.Empty<GameMaterialParameter>(),
                new GameMaterialPassState(
                    false,
                    MapBlendFactor.One,
                    MapBlendFactor.Zero,
                    MapBlendFactor.One,
                    MapBlendFactor.Zero,
                    true,
                    GameMaterialWinding.CounterClockwise,
                    true,
                    3,
                    31));
    }
}

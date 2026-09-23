using System;
using System.IO;
using System.Linq;
using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Shaders
{
    public sealed class GameShaderTranslatorTests
    {
        [Fact]
        public void SpirvStringKeepsTheFinalPaddedWordLikeCurrentLtkMain()
        {
            uint[] words = GameShaderTranslator.SpirvString("abcde");

            Assert.Equal(2, words.Length);
            Assert.Equal(0x64636261u, words[0]);
            Assert.Equal(0x00000065u, words[1]);
        }

        [Fact]
        public void NativeCompilerRejectsNonDxbcWithItsOwnError()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                GameShaderTranslator.CompileSpirv(new byte[] { 1, 2, 3, 4 }));

            Assert.Contains("vkd3d-shader", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DefaultEnvFlatTranslatesRealDxbcToSpirvAndGlsl()
        {
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = root;
            settings.LolLiveDirectory = null;
            var pass = new GameMaterialPass(
                1,
                "Shaders/StaticMesh/DefaultEnv_Flat",
                Array.Empty<GameMaterialDefine>(),
                Array.Empty<System.Collections.Generic.KeyValuePair<string, bool>>(),
                Array.Empty<GameMaterialTexture>(),
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

            GameShaderProgramResolver.ShaderBytecodeRead read =
                GameShaderProgramResolver.Read(pass, GameMaterialKind.StaticMesh, settings);
            Assert.True(read.Ready, read.Failure);

            GameShaderTranslator.TranslationRead translated =
                GameShaderTranslator.Translate(
                    read.Program.Vertex,
                    read.Program.VertexReflection,
                    read.Program.Pixel,
                    read.Program.PixelReflection);

            Assert.True(translated.Ready, translated.Failure);
            Assert.Equal(0x07230203u, translated.Program.Vertex.Spirv[0]);
            Assert.Equal(0x07230203u, translated.Program.Pixel.Spirv[0]);
            Assert.Contains("#version 300 es", translated.Program.Vertex.Glsl, StringComparison.Ordinal);
            Assert.Contains("#version 300 es", translated.Program.Pixel.Glsl, StringComparison.Ordinal);
            Assert.Contains("void main", translated.Program.Vertex.Glsl, StringComparison.Ordinal);
            Assert.Contains("void main", translated.Program.Pixel.Glsl, StringComparison.Ordinal);
            Assert.NotEmpty(translated.Program.Vertex.Sidecar.Attributes);
            Assert.All(
                translated.Program.Vertex.Sidecar.Blocks.Concat(translated.Program.Pixel.Sidecar.Blocks),
                block => Assert.True(block.Size > 0));
        }

        private static string FindInstalledShaderCacheRoot() =>
            new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));
    }
}


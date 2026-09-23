using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class GameShaderProgramResolverTests
    {
        [Fact]
        public void GeneratedShaderCachePathsMatchCurrentLtkMain()
        {
            Assert.Equal(
                "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.vs-dx11",
                GameShaderProgramResolver.TocPath("Shaders/StaticMesh/DefaultEnv_Flat", "vs"));
            Assert.Equal(
                "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.vs-dx11_300",
                GameShaderProgramResolver.BundlePath(
                    "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.vs-dx11",
                    347));
        }

        [Fact]
        public void StudioDefinesOnlyFillMissingValues()
        {
            var pass = Pass(
                new GameMaterialDefine("DISABLE_FOW", "0", GameMaterialDefineSource.Pass),
                new GameMaterialDefine("OWN", "7", GameMaterialDefineSource.Material));

            IReadOnlyList<GameMaterialDefine> defines =
                GameShaderProgramResolver.BuildDefineList(pass, GameMaterialKind.SkinnedMesh, lowQuality: true);

            Assert.Equal("0", defines.Single(item => item.Name == "DISABLE_FOW").Value);
            Assert.Equal("1", defines.Single(item => item.Name == "DISABLE_SHADOWS").Value);
            Assert.Equal("4", defines.Single(item => item.Name == "NUM_BLEND_WEIGHTS").Value);
            Assert.Equal("1", defines.Single(item => item.Name == "LOW_QUALITY_MODE").Value);
            Assert.Equal("7", defines.Single(item => item.Name == "OWN").Value);
            Assert.Equal(defines.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name), defines.Select(item => item.Name));
        }

        [Fact]
        public void BundleRecordTrimsTheExtraBytePastDxbcContainer()
        {
            byte[] dxbc = new byte[36];
            dxbc[0] = (byte)'D';
            dxbc[1] = (byte)'X';
            dxbc[2] = (byte)'B';
            dxbc[3] = (byte)'C';
            BitConverter.GetBytes(32u).CopyTo(dxbc, 24);
            dxbc[32] = 0xCC;
            dxbc[33] = 0xDD;
            dxbc[34] = 0xEE;
            dxbc[35] = 0xFF;

            byte[] bundle = new byte[4 + dxbc.Length];
            BitConverter.GetBytes((uint)dxbc.Length).CopyTo(bundle, 0);
            dxbc.CopyTo(bundle, 4);

            byte[] trimmed = GameShaderProgramResolver.ReadBundleRecord(bundle, 0);

            Assert.Equal(32, trimmed.Length);
            Assert.Equal("DXBC", System.Text.Encoding.ASCII.GetString(trimmed, 0, 4));
        }

        [Fact]
        public void DefaultEnvFlatResolvesRealDxbcWhenAConfiguredStyleInstallExists()
        {
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = root;
            settings.LolLiveDirectory = null;
            var pass = Pass();
            pass = pass with { ShaderPath = "Shaders/StaticMesh/DefaultEnv_Flat" };

            GameShaderProgramResolver.ShaderBytecodeRead read =
                GameShaderProgramResolver.Read(pass, GameMaterialKind.StaticMesh, settings);

            Assert.True(read.Ready, read.Failure);
            Assert.StartsWith("DXBC", System.Text.Encoding.ASCII.GetString(read.Program.Vertex, 0, 4));
            Assert.StartsWith("DXBC", System.Text.Encoding.ASCII.GetString(read.Program.Pixel, 0, 4));
            Assert.Equal(5, read.Program.VertexReflection.ShaderModelMajor);
            Assert.Equal(5, read.Program.PixelReflection.ShaderModelMajor);
            Assert.NotEmpty(read.Program.VertexReflection.Inputs);
            Assert.NotEmpty(read.Program.PixelReflection.Outputs);
        }

        [Fact]
        public void ProgramResolvesEveryPassAndKeepsFailureOnThePassThatOwnsIt()
        {
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = root;
            settings.LolLiveDirectory = null;
            GameMaterialPass ready = Pass();
            GameMaterialPass missing = Pass() with { ShaderHash = 0, ShaderPath = null };
            var program = new GameMaterialProgram(
                GameMaterialKind.StaticMesh,
                true,
                new[] { ready, missing });

            GameShaderProgramResolver.ShaderBytecodeMaterialProgram read =
                GameShaderProgramResolver.ReadProgram(program, settings);

            Assert.False(read.Ready);
            Assert.True(read.Animated);
            Assert.Equal(2, read.Passes.Count);
            Assert.True(read.Passes[0].Bytecode.Ready, read.Passes[0].Bytecode.Failure);
            Assert.False(read.Passes[1].Bytecode.Ready);
            Assert.Equal("The pass links no shader the defs declare.", read.Passes[1].Bytecode.Failure);
        }

        private static string FindInstalledShaderCacheRoot() =>
            new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));

        private static GameMaterialPass Pass(params GameMaterialDefine[] defines) =>
            new(
                1,
                "Shaders/StaticMesh/DefaultEnv_Flat",
                defines,
                Array.Empty<KeyValuePair<string, bool>>(),
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
    }
}

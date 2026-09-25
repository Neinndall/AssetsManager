using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
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
            Assert.Equal(
                "assets/shaders/hlsl/skinnedmesh/lit_uber_vs.vs-dx11",
                GameShaderProgramResolver.TocPath(GameShaderProgramResolver.LitUberShaderName, "vs"));
            Assert.Equal(
                "assets/shaders/hlsl/skinnedmesh/lit_uber_ps.ps-dx11",
                GameShaderProgramResolver.TocPath(GameShaderProgramResolver.LitUberShaderName, "ps"));
        }

        [Fact]
        public void DefaultSkinnedProgramMatchesLitUberSpecification()
        {
            GameMaterialProgram program = GameShaderProgramResolver.CreateDefaultSkinnedProgram("base_color", "base_emissive");
            Assert.Equal(GameMaterialKind.SkinnedMesh, program.Kind);
            Assert.False(program.Animated);
            Assert.Single(program.Passes);

            GameMaterialPass pass = program.Passes[0];
            Assert.Equal(GameShaderProgramResolver.LitUberShaderName, pass.ShaderPath);
            Assert.Equal(2, pass.Textures.Count);
            Assert.Equal(GameShaderProgramResolver.LitUberDiffuseTexture, pass.Textures[0].Name);
            Assert.Equal("base_color", pass.Textures[0].Texture.VirtualPath);
            Assert.Equal(GameShaderProgramResolver.LitUberEmissiveTexture, pass.Textures[1].Name);
            Assert.Equal("base_emissive", pass.Textures[1].Texture.VirtualPath);
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

        [Fact]
        public void ShadersBinCoverageMatchesInstalledShaderCache()
        {
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            string shadersWadPath = Path.Combine(root, @"Game\DATA\FINAL\Shaders\Shaders.wad.client");
            string cachePath = Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client");
            if (!File.Exists(shadersWadPath) || !File.Exists(cachePath))
                return;

            using var wad = new WadFile(shadersWadPath);
            ulong shadersBinHash = XxHash64Ext.Hash("data/shaders/shaders.bin");
            if (!wad.Chunks.ContainsKey(shadersBinHash))
                return;

            using var decompressed = wad.LoadChunkDecompressed(shadersBinHash);
            using var stream = new MemoryStream(decompressed.Span.ToArray(), writable: false);
            var binTree = new BinTree(stream);

            uint customShaderClass = Fnv1a.HashLower("CustomShaderDef");
            var customShaders = binTree.Objects.Values.Where(o => o.ClassHash == customShaderClass).ToList();
            Assert.NotEmpty(customShaders);

            using var cacheWad = new WadFile(cachePath);
            ulong litUberVsHash = XxHash64Ext.Hash(GameShaderProgramResolver.TocPath(GameShaderProgramResolver.LitUberShaderName, "vs"));
            ulong litUberPsHash = XxHash64Ext.Hash(GameShaderProgramResolver.TocPath(GameShaderProgramResolver.LitUberShaderName, "ps"));
            Assert.True(cacheWad.Chunks.ContainsKey(litUberVsHash), "LIT_UBER VS TOC must exist in shader cache");
            Assert.True(cacheWad.Chunks.ContainsKey(litUberPsHash), "LIT_UBER PS TOC must exist in shader cache");

            uint objectPathPropHash = Fnv1a.HashLower("objectPath");
            int verified = 0;
            foreach (var shader in customShaders.Take(25))
            {
                if (shader.Properties.TryGetValue(objectPathPropHash, out var prop) && prop is BinTreeString str)
                {
                    string vsToc = GameShaderProgramResolver.TocPath(str.Value, "vs");
                    string psToc = GameShaderProgramResolver.TocPath(str.Value, "ps");
                    Assert.True(cacheWad.Chunks.ContainsKey(XxHash64Ext.Hash(vsToc)), $"VS TOC missing for {str.Value}");
                    Assert.True(cacheWad.Chunks.ContainsKey(XxHash64Ext.Hash(psToc)), $"PS TOC missing for {str.Value}");
                    verified++;
                }
            }
            Assert.True(verified > 0);
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

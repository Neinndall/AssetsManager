using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Numerics;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Tests.Diagnostics.Viewer;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Mesh;
using Silk.NET.OpenGL;
using AssetsManager.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GameShaderSkinPaletteGpuTests
    {
        private readonly ITestOutputHelper _output;

        public GameShaderSkinPaletteGpuTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Spell4ChangesJanna67BodyAndHairTexturesInTheGameShaders()
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            var settings = InstalledSkins.Settings(install);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var log = new LogService(logger);
            var asset = await InstalledSkins.CreateLoader(settings, log)
                .LoadAsync("Characters/Janna/Skins/Skin67", null);
            Assert.NotNull(asset);

            var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(provider, settings);
            var root = await resolver.ResolveVirtualAsync("data/characters/janna/janna.bin", null);
            using Stream stream = await resolver.OpenReadAsync(root);
            var spells = new BinTree(stream).Objects.Values
                .Where(obj => obj.ClassHash == LeagueToolkit.Hashing.Fnv1a.HashLower("SpellObject"))
                .ToDictionary(obj => obj.PathHash, VfxSpellPreviewReader.Read);
            AnimationClipDefinition definition = asset.AnimationGraph.Clips.Single(clip =>
                clip.OwnerPathHash == GameMaterialState.AnimationHash("Spell4"));
            var clip = new AnimationClipCatalogItem("Spell4", "Spell4", null, 1, null, definition, null,
                Array.Empty<AnimationClipTimedCue>(), 0, false, null);
            GameMaterialState cast = GameMaterialState.Preview(null, clip, spells);
            Assert.NotNull(cast);

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var context = new HiddenWglContext();
                    using GL gl = GL.GetApi(context.GetProcAddress);
                    using var renderer = new SkinSubmeshRenderer(gl, settings, null);
                    Matrix4x4[] bones = Enumerable.Repeat(Matrix4x4.Identity, asset.Skeleton.Joints.Count).ToArray();
                    foreach (string name in new[] { "Body", "Cloths", "Hair", "WingsHair", "R_WingsHair" })
                    {
                        var range = asset.Mesh.Ranges.Single(range => range.Name == name);
                        var material = asset.Materials.ResolveMaterialDefinition(name);
                        string sampler = name.Contains("Hair") ? "Main_Texture" : "Diffuse_Color";
                        Assert.Null(material.ResolveTextureSwap(sampler, GameMaterialState.Resting));
                        Assert.Equal("fd92a016617e35f6", material.ResolveTextureSwap(sampler, cast));
                        var normal = renderer.Render(asset, range, material, state: GameMaterialState.Resting, bones: bones);
                        var transformed = renderer.Render(asset, range, material, state: cast, bones: bones);
                        foreach (var result in new[] { normal, transformed })
                        {
                            Assert.True(result.Bound);
                            Assert.True(result.Covered > 0);
                            Assert.Equal(0, result.NonFinite);
                            Assert.Empty(result.MissingTextures);
                        }
                        float difference = Vector4.Distance(normal.Mean, transformed.Mean);
                        _output.WriteLine($"{name}: normal={normal.Mean} Spell4={transformed.Mean} difference={difference}");
                        Assert.True(difference > 0.01f, $"{name} kept the resting shader output during Spell4.");
                        Assert.Null(material.ResolveTextureSwap(sampler, GameMaterialState.Resting));
                    }
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public async Task JointIndicesAbove255PreserveJannaSkin67Geometry()
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            var settings = InstalledSkins.Settings(install);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var log = new LogService(logger);
            var asset = await InstalledSkins.CreateLoader(settings, log)
                .LoadAsync("Characters/Janna/Skins/Skin67", null);
            Assert.NotNull(asset);
            Assert.True(asset.Skeleton.Influences.Max() > 255);
            var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(provider, settings);
            var source = await resolver.ResolveReferenceAsync(asset.Skin.Mesh, null);
            using Stream stream = await resolver.OpenReadAsync(source);
            using SkinnedMesh skin = SkinnedMesh.ReadFromSimpleSkin(stream);
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Matrix4x4[] pose = Enumerable.Range(0, asset.Skeleton.Joints.Count)
                        .Select(joint => joint > 255 ? Matrix4x4.CreateTranslation(8f, -2f, 0f) : Matrix4x4.Identity).ToArray();
                    foreach (bool posed in new[] { false, true })
                    foreach (string submesh in new[] { "Body", "Cloths", "Hair", "Weapon", "Wing", "Cloak01", "Cloak02", "Skirt", "WingsHair" })
                    {
                        int reference = SkinPartRenderDiagnostic.Render(asset, submesh, "front", null, 256, 3.2f,
                            settings, skin, shaders: false, isolateFocus: true, pose: posed ? pose : null);
                        int game = SkinPartRenderDiagnostic.Render(asset, submesh, "front", null, 256, 3.2f,
                            settings, skin, shaders: true, isolateFocus: true, pose: posed ? pose : null);
                        _output.WriteLine($"{submesh} posed={posed}: stock coverage={reference}, shader coverage={game}");
                        Assert.True(reference > 0);
                        Assert.InRange(game, reference * 0.98, reference * 1.02);
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

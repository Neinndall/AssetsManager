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
using AssetsManager.Tests.Diagnostics.Viewer;
using AssetsManager.Utils;
using LeagueToolkit.Core.Mesh;
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

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.Support;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class CharacterOutlineInstalledTests
{
    [InstalledChampionFact("Ahri")]
    public async Task JadeAhriResolvesItsMapCategoryAndDrawsTheActualTranslatedOutlineShader()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        var settings = InstalledSkins.Settings(install);
        var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
        var directories = new DirectoriesCreator();
        using var hashes = new HashResolverService(directories, log);
        hashes.LoadHashes(); hashes.LoadBinHashes();
        var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), directories, new SvgParser());
        var loader = new MapCharacterLoadingService(new MapAssetResolver(provider, settings),
            new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), hashes, log);
        var asset = await loader.LoadAsync("Characters/Jade_Ahri/Skins/Skin0", null);
        Assert.NotNull(asset);
        Assert.True(asset.Materials.Outlines.Count >= 4);
        var outline = asset.Materials.Outlines["Body"];
        Assert.Equal(5, outline.Width); Assert.Equal(4500, outline.CameraDistance); Assert.Equal(-20, outline.DepthOffset);
        Assert.EndsWith("Jade_CharacterOutline", outline.Material.ShaderPath);
        Assert.NotEmpty(outline.Material.Program.Passes);
        Assert.NotNull(SknMaterialTextureResolver.MatchTextureKey(outline.MaskPath, asset.Textures.Keys.ToArray()));
        Assert.Empty(outline.Material.DynamicParameters);
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new HiddenWglContext();
                using var gl = GL.GetApi(context.GetProcAddress);
                using var draw = new SkinSubmeshRenderer(gl, settings, null);
                var result = draw.Render(asset, asset.Mesh.Ranges.First(range => range.Name.Equals("Body", StringComparison.OrdinalIgnoreCase)), outline.Material);
                Assert.True(result.Bound);
                Assert.True(result.Covered > 20);
                Assert.Equal(0, result.NonFinite);
                Assert.Empty(result.MissingTextures);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

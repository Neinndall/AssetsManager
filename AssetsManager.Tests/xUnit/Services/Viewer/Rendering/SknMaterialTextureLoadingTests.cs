using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class SknMaterialTextureLoadingTests
    {
        [Fact]
        public async Task StudioLoadsJannaMatcapAndMasksWithoutTheirHashNames()
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            var settings = InstalledSkins.Settings(install);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var log = new LogService(logger);
            var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(provider, settings);
            var bin = await resolver.ResolveVirtualAsync("data/characters/janna/skins/skin67.bin", null);
            Assert.NotNull(bin);
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-studio-textures-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string binPath = Path.Combine(root, "skin67.bin");
                await using (Stream stream = await resolver.OpenReadAsync(bin))
                await using (var file = File.Create(binPath)) await stream.CopyToAsync(file);
                var textures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                var material = await new SknLoadingService(log, null, provider, settings)
                    .LoadMaterialTexturesAsync(Path.Combine(root, "janna_skin67.skn"), textures, true,
                        explicitSkinBinPath: binPath, projectRoot: root);
                Assert.NotNull(material);
                // These authored references have no path names in the current hash catalogue.
                foreach (string hash in new[] { "74768cb3b99896e5", "307e6e3934be2818", "13374fccd62518fc" })
                {
                    Assert.True(textures.TryGetValue(hash, out BitmapSource texture), $"Studio did not load texture {hash}.");
                    Assert.True(texture.PixelWidth > 1 && texture.PixelHeight > 1);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}

using System;
using System.IO;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class VfxLoadingServiceTests
    {
        [Fact]
        public void FollowsDeclaredTruncatedDependencyWithoutLoadingSimilarSkinNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxLoading", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);

            string extractedStem = new string('a', 236);
            string sharedBin = Path.Combine(championDirectory, extractedStem + ".bin");
            string skin1Bin = Path.Combine(skinsDirectory, "skin1.bin");
            string skin11Bin = Path.Combine(skinsDirectory, "skin11.bin");

            WriteBin(sharedBin, new[] { CreateSystem("Effects/Shared", "Shared") }, Array.Empty<string>());
            WriteBin(skin1Bin, Array.Empty<BinTreeObject>(), new[]
            {
                $"DATA/Characters/Hero/{extractedStem}_skins_skin28.bin"
            });
            WriteBin(skin11Bin, new[] { CreateSystem("Effects/WrongSkin", "WrongSkin") }, Array.Empty<string>());

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skin1Bin, new LogService(logger));

                VfxSystemDefinition system = Assert.Single(bundle.Systems).Value;
                Assert.Equal("Shared", system.Name);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static BinTreeObject CreateSystem(string path, string name)
            => new(
                path,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), name),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), path)
                });

        private static void WriteBin(string path, BinTreeObject[] objects, string[] dependencies)
        {
            var tree = new BinTree(objects, dependencies);
            using var stream = File.Create(path);
            tree.Write(stream);
        }
    }
}

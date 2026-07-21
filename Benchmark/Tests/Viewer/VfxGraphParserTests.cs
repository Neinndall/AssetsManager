using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class VfxGraphParserTests
    {
        [Fact]
        public void ParsesEveryVfxProjectionFromOneBinDocument()
        {
            var effectObject = new BinTreeObject(
                "Effects/Test",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "TestEffect"),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), "Effects/Test"),
                });
            var tree = new BinTree(new[] { effectObject }, new[] { "data/effects/shared.bin" });
            using var stream = new MemoryStream();
            tree.Write(stream);
            byte[] bytes = stream.ToArray();

            VfxBinDocument document = VfxGraphParser.ParseDocument(bytes);

            var system = Assert.Single(document.Systems).Value;
            Assert.Equal("TestEffect", system.Name);
            Assert.Equal("Effects/Test", system.ParticlePath);
            Assert.Empty(system.Emitters);
            Assert.Empty(document.AnimationClips);
            Assert.Empty(document.ResourceMap);
            Assert.Equal("data/effects/shared.bin", Assert.Single(document.Dependencies));
        }
    }
}

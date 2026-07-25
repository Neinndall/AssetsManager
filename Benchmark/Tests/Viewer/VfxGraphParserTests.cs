using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
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

        [Fact]
        public void AppliesRiotEmitterDefaultsWhenOptionalBinFieldsAreAbsent()
        {
            uint textureMultHash = Fnv1a.HashLower("textureMult");
            var textureMult = new BinTreeStruct(
                textureMultHash,
                Fnv1a.HashLower("VfxTextureMultDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(textureMultHash, "Effects/TestMult.dds")
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "DefaultEmitter"),
                    textureMult
                });
            var effectObject = new BinTreeObject(
                "Effects/Defaults",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "Defaults"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { effectObject }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument document = VfxGraphParser.ParseDocument(stream.ToArray());

            var parsed = Assert.Single(Assert.Single(document.Systems).Value.Emitters);
            Assert.Equal("ASSETS/Shared/Particles/DefaultColorOverlifetime.dds", parsed.ParticleColorTexturePath);
            Assert.Equal(1, parsed.ColorLookUpTypeX);
            Assert.Equal(0, parsed.ColorLookUpTypeY);
            Assert.True(parsed.TextureMultFlipV);
            Assert.True(parsed.TextureMultRandomStartFrame);
            Assert.Equal(0.5f, parsed.UvTransformCenter.X);
            Assert.Equal(0.5f, parsed.TextureMultTransformCenter.Y);
        }
    }
}

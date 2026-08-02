using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
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
                    new BinTreeMatrix44(
                        Fnv1a.HashLower("transform"),
                        Matrix4x4.CreateScale(0.5f)),
                });
            var tree = new BinTree(new[] { effectObject }, new[] { "data/effects/shared.bin" });
            using var stream = new MemoryStream();
            tree.Write(stream);
            byte[] bytes = stream.ToArray();

            VfxBinDocument document = VfxGraphParser.ParseDocument(bytes);

            var system = Assert.Single(document.Systems).Value;
            Assert.Equal("TestEffect", system.Name);
            Assert.Equal("Effects/Test", system.ParticlePath);
            Assert.Equal(Matrix4x4.CreateScale(0.5f), system.Transform);
            Assert.Empty(system.Emitters);
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
            var alphaErosion = new BinTreeStruct(
                Fnv1a.HashLower("alphaErosionDefinition"),
                Fnv1a.HashLower("VfxAlphaErosionDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("erosionMapName"), "Effects/TestErosion.dds")
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "DefaultEmitter"),
                    new BinTreeU8(Fnv1a.HashLower("blendMode"), 2),
                    new BinTreeU8(Fnv1a.HashLower("importance"), 3),
                    new BinTreeU8(Fnv1a.HashLower("colorRenderFlags"), 1),
                    new BinTreeU8(Fnv1a.HashLower("miscRenderFlags"), 1),
                    new BinTreeU8(Fnv1a.HashLower("meshRenderFlags"), 2),
                    new BinTreeBool(Fnv1a.HashLower("useNavmeshMask"), true),
                    new BinTreeVector2(Fnv1a.HashLower("depthBiasFactors"), new Vector2(-1f, -200f)),
                    new BinTreeBool(Fnv1a.HashLower("isRotationEnabled"), false),
                    new BinTreeU8(Fnv1a.HashLower("uvMode"), 2),
                    new BinTreeF32(Fnv1a.HashLower("directionVelocityScale"), 0.002f),
                    new BinTreeVector4(
                        Fnv1a.HashLower("modulationFactor"),
                        new Vector4(0.25f, 0.5f, 0.75f, 0.8f)),
                    new BinTreeStruct(
                        Fnv1a.HashLower("FlexShapeDefinition"),
                        Fnv1a.HashLower("VfxFlexShapeDefinitionData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeF32(Fnv1a.HashLower("scaleBirthScaleByBoundObjectSize"), 0.004f)
                        }),
                    new BinTreeStruct(
                        Fnv1a.HashLower("paletteDefinition"),
                        Fnv1a.HashLower("VfxPaletteDefinitionData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeI32(Fnv1a.HashLower("paletteCount"), 16),
                            new BinTreeStruct(
                                Fnv1a.HashLower("paletteSelector"),
                                Fnv1a.HashLower("ValueVector3"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeVector3(Fnv1a.HashLower("constantValue"), new Vector3(4f, 0f, 0f))
                                })
                        }),
                    new BinTreeVector4(Fnv1a.HashLower("birthColor"), new Vector4(0.1f, 0.2f, 0.3f, 0.4f)),
                    textureMult,
                    alphaErosion
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
            Assert.Null(parsed.ParticleColorTexturePath);
            Assert.Equal(0, parsed.ColorLookUpTypeX);
            Assert.Equal(0, parsed.ColorLookUpTypeY);
            Assert.True(parsed.TextureMultFlipV);
            Assert.True(parsed.TextureMultRandomStartFrame);
            Assert.Equal(3, parsed.Importance);
            Assert.Equal(2, parsed.BlendMode);
            Assert.Equal(1, parsed.ColorRenderFlags);
            Assert.Equal(1, parsed.MiscRenderFlags);
            Assert.Equal(2, parsed.MeshRenderFlags);
            Assert.True(parsed.UseNavmeshMask);
            Assert.Equal(new Vector2(-1f, -200f), parsed.DepthBiasFactors);
            Assert.False(parsed.IsRotationEnabled);
            Assert.Equal(2, parsed.UvMode);
            Assert.Equal(0.002f, parsed.DirectionVelocityScale);
            Assert.Equal(
                new Vector4(0.25f, 0.5f, 0.75f, 0.8f),
                parsed.ModulationFactor);
            Assert.Equal(0.004f, parsed.FlexShape.ScaleBirthScaleByBoundObjectSize);
            Assert.Equal(16, parsed.PaletteDefinition.PaletteCount);
            Assert.Equal(4f, parsed.PaletteDefinition.PaletteSelector.Constant.X);
            Assert.Equal(1f, parsed.BirthScale.Constant.X);
            Assert.Equal(1f, parsed.Rate.Constant);
            Assert.Equal(1f, parsed.ParticleLifetime.Constant);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), parsed.BirthColor.Constant);
            Assert.Equal(0.5f, parsed.UvTransformCenter.X);
            Assert.Equal(0.5f, parsed.TextureMultTransformCenter.Y);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherIn);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherOut);
            Assert.Equal(2, parsed.AlphaErosion.AddressMode);
        }

        [Fact]
        public void LeavesModulationFactorNeutralWhenBinOmitsIt()
        {
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "NoModulation")
                });
            var system = new BinTreeObject(
                "Effects/NoModulation",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "NoModulation"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Null(parsed.ModulationFactor);
        }
    }
}

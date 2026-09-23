using System;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapMaterialParserHashResolutionTests
    {
        [Fact]
        public void ProgramResolvesShaderDefaultWadChunkLinksThroughInjectedResolver()
        {
            const string materialPath = "Characters/Test/Materials/Body";
            const string shaderPath = "Shaders/SkinnedMesh/Test";
            const string texturePath = "ASSETS/Characters/Test/Test_TX_CM.tex";
            uint shaderHash = Fnv1a.HashLower(shaderPath);
            ulong textureHash = XxHash64Ext.Hash(texturePath.ToLowerInvariant());

            var material = new BinTreeObject(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("techniques"),
                        BinPropertyType.Embedded,
                        new[]
                        {
                            new BinTreeEmbedded(
                                0,
                                Fnv1a.HashLower("StaticMaterialTechniqueDef"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeUnorderedContainer(
                                        Fnv1a.HashLower("passes"),
                                        BinPropertyType.Embedded,
                                        new[]
                                        {
                                            new BinTreeEmbedded(
                                                0,
                                                Fnv1a.HashLower("StaticMaterialPassDef"),
                                                new BinTreeProperty[]
                                                {
                                                    new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash)
                                                })
                                        })
                                })
                        })
                });
            var shader = new BinTreeObject(
                shaderPath,
                "CustomShaderDef",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("objectPath"), shaderPath),
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("textures"),
                        BinPropertyType.Embedded,
                        new[]
                        {
                            new BinTreeEmbedded(
                                0,
                                Fnv1a.HashLower("ShaderTextureDef"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeString(Fnv1a.HashLower("name"), "Diffuse_Texture"),
                                    new BinTreeWadChunkLink(Fnv1a.HashLower("defaultTexturePath"), textureHash)
                                })
                        })
                });
            var shaderTree = new BinTree(new[] { shader }, Array.Empty<string>());
            var parser = new MapMaterialParser(
                hash => hash == textureHash ? texturePath : null,
                hash => hash == shaderHash ? shaderPath : null);

            GameMaterialProgram program = parser.ParseProgram(material, new[] { shaderTree });

            Assert.NotNull(program);
            GameMaterialTexture texture = Assert.Single(Assert.Single(program.Passes).Textures);
            Assert.Equal(texturePath, texture.Texture.VirtualPath, ignoreCase: true);
            Assert.Equal(textureHash, texture.Texture.PathHash);
        }
    }
}

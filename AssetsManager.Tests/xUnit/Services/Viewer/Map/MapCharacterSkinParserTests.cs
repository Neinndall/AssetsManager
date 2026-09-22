using System;
using AssetsManager.Services.Viewer.Map.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterSkinParserTests
    {
        private const string SkinPath = "Characters/Turret/Skins/Skin0";

        [Fact]
        public void ParserKeepsPathAndHashAssetsAndAuthoredSkinState()
        {
            const ulong skeletonHash = 0x123456789abcdef0;
            uint graphHash = Fnv1a.HashLower("Characters/Turret/Animations/Graph");
            BinTree tree = Tree(new BinTreeObject(
                Fnv1a.HashLower(SkinPath),
                MapCharacterSkinParser.SkinClass,
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(
                        MapCharacterSkinParser.MeshPropertiesField,
                        Fnv1a.HashLower("SkinMeshDataProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(MapCharacterSkinParser.SimpleSkinField, "assets/characters/turret/turret.skn"),
                            new BinTreeWadChunkLink(MapCharacterSkinParser.SkeletonField, skeletonHash),
                            new BinTreeF32(MapCharacterSkinParser.SkinScaleField, 1.5f),
                            new BinTreeString(MapCharacterSkinParser.HiddenSubmeshesField, "Wings, Cape\tHat")
                        }),
                    new BinTreeEmbedded(
                        MapCharacterSkinParser.AnimationPropertiesField,
                        Fnv1a.HashLower("SkinAnimationProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(MapCharacterSkinParser.AnimationGraphField, graphHash)
                        })
                }));

            MapCharacterSkinData skin = new MapCharacterSkinParser().Parse(tree, SkinPath);

            Assert.NotNull(skin);
            Assert.Equal("assets/characters/turret/turret.skn", skin.Mesh.VirtualPath);
            Assert.Equal(0ul, skin.Mesh.PathHash);
            Assert.Null(skin.Skeleton.VirtualPath);
            Assert.Equal(skeletonHash, skin.Skeleton.PathHash);
            Assert.Equal(1.5f, skin.Scale);
            Assert.Equal(new[] { "Wings", "Cape", "Hat" }, skin.HiddenSubmeshes);
            Assert.Equal(graphHash, skin.AnimationGraphHash);
        }

        [Fact]
        public void BareSkinUsesLtkDefaults()
        {
            BinTree tree = Tree(new BinTreeObject(
                Fnv1a.HashLower(SkinPath),
                MapCharacterSkinParser.SkinClass,
                Array.Empty<BinTreeProperty>()));

            MapCharacterSkinData skin = new MapCharacterSkinParser().Parse(tree, SkinPath);

            Assert.NotNull(skin);
            Assert.Null(skin.Mesh);
            Assert.Null(skin.Skeleton);
            Assert.Equal(1f, skin.Scale);
            Assert.Empty(skin.HiddenSubmeshes);
            Assert.Equal(0u, skin.AnimationGraphHash);
        }

        [Fact]
        public void MissingOrWrongClassSkinIsNotResolved()
        {
            BinTree tree = Tree(new BinTreeObject(
                Fnv1a.HashLower(SkinPath),
                Fnv1a.HashLower("StaticMaterialDef"),
                Array.Empty<BinTreeProperty>()));
            var parser = new MapCharacterSkinParser();

            Assert.Null(parser.Parse(tree, SkinPath));
            Assert.Null(parser.Parse(tree, "Characters/Turret/Skins/Skin1"));
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());
    }
}

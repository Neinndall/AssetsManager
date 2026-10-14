using System;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    /// <summary>
    /// Map structures (esports banners) link StaticMaterialDefs that live in the Map BIN; the scene keeps
    /// only those definitions for structure material resolution.
    /// </summary>
    public class MapSharedMaterialsTests
    {
        private const string FlagMaterial = "Maps/Shipping/Map11/Esports/Materials/ENV_Skinned_VertexWave_Base_Flag_inst";

        [Fact]
        public void SharedMaterialDocumentKeepsOnlyStaticMaterialDefinitions()
        {
            var map = new BinTree(new[]
            {
                new BinTreeObject(0x8333105e, MapVariantParser.MapClass, Array.Empty<BinTreeProperty>()),
                new BinTreeObject(Fnv1a.HashLower(FlagMaterial), Fnv1a.HashLower("StaticMaterialDef"), new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), FlagMaterial)
                }),
                new BinTreeObject(0x4cdaa84d, Fnv1a.HashLower("EsportsBannerOptions"), Array.Empty<BinTreeProperty>())
            }, Array.Empty<string>());

            BinTree shared = MapSceneLoadingService.SharedMaterialDocument(map);

            BinTreeObject material = Assert.Single(shared.Objects.Values);
            Assert.Equal(Fnv1a.HashLower(FlagMaterial), material.PathHash);
        }

        [Fact]
        public void MapWithoutMaterialsSharesNothing()
        {
            var map = new BinTree(new[]
            {
                new BinTreeObject(0x8333105e, MapVariantParser.MapClass, Array.Empty<BinTreeProperty>())
            }, Array.Empty<string>());

            Assert.Null(MapSceneLoadingService.SharedMaterialDocument(map));
            Assert.Null(MapSceneLoadingService.SharedMaterialDocument(null));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.MapGeometry;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.MapGeometry
{
    public class MapGeometryMaterialResolverTests
    {
        [Fact]
        public void TryResolve_UsesMaterialPathHashWithoutExternalHashCatalog()
        {
            const string materialPath = "Maps/KitPieces/Jade/Materials/Unknown_To_Catalog";
            var material = CreateMaterial(
                materialPath,
                CreateSampler("DiffuseTexture", "ASSETS/Maps/Jade/diffuse.tex"));
            var resolver = new MapGeometryMaterialResolver(new BinTree(new[] { material }, Array.Empty<string>()));

            bool resolved = resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition);

            Assert.True(resolved);
            Assert.Equal("ASSETS/Maps/Jade/diffuse.tex", definition.PrimarySampler.TexturePath);
        }

        [Fact]
        public void TryResolve_LayeredMaterialSelectsColorLayerInsteadOfMask()
        {
            const string materialPath = "Maps/KitPieces/Jade/Materials/Ground";
            var material = CreateMaterial(
                materialPath,
                CreateSampler("Mask_Texture", "ASSETS/Maps/Jade/mask.tex"),
                CreateSampler("Bottom_Texture", "ASSETS/Maps/Jade/dirt.tex"),
                CreateSampler("Top_Texture", "ASSETS/Maps/Jade/grass.tex"));
            var resolver = new MapGeometryMaterialResolver(new BinTree(new[] { material }, Array.Empty<string>()));

            Assert.True(resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition));
            Assert.Equal("Bottom_Texture", definition.PrimarySampler.TextureName);
            Assert.Equal(3, definition.Samplers.Count);
        }

        [Fact]
        public void TryResolve_ColorOnlyMaterialPreservesTint()
        {
            const string materialPath = "Maps/MapGeometry/Materials/FaeLights";
            Vector4 tint = new(0.25f, 0.5f, 0.75f, 1f);
            var parameter = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("StaticMaterialShaderParamDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "TintColor"),
                    new BinTreeVector4(Fnv1a.HashLower("value"), tint)
                });
            var parameters = new BinTreeUnorderedContainer(
                Fnv1a.HashLower("paramValues"),
                BinPropertyType.Embedded,
                new[] { parameter });
            var material = new BinTreeObject(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[] { parameters });
            var resolver = new MapGeometryMaterialResolver(new BinTree(new[] { material }, Array.Empty<string>()));

            Assert.True(resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition));
            Assert.Equal(tint, definition.TintColor);
            Assert.Null(definition.PrimarySampler);
        }

        [Fact]
        public void TryResolve_AuxiliarySamplerIsNotUsedAsDiffuseColor()
        {
            const string materialPath = "Maps/Materials/NormalOnly";
            var material = CreateMaterial(
                materialPath,
                CreateSampler("NormalTexture", "ASSETS/Maps/normal.tex"));
            var resolver = new MapGeometryMaterialResolver(new BinTree(new[] { material }, Array.Empty<string>()));

            Assert.True(resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition));
            Assert.Null(definition.PrimarySampler);
            Assert.Single(definition.Samplers);
        }

        [Fact]
        public void BuildTextureKeys_DisambiguatesEqualFileNamesFromDifferentFolders()
        {
            Dictionary<string, string> keys = MapGeometryLoadingService.BuildTextureKeys(new[]
            {
                @"ASSETS\Maps\Jade\rock.tex",
                "ASSETS/Shared/rock.tex",
                "ASSETS/Maps/Jade/grass.tex"
            });

            Assert.Equal("grass", keys["assets/maps/jade/grass.tex"]);
            Assert.Equal("assets/maps/jade/rock.tex", keys["assets/maps/jade/rock.tex"]);
            Assert.Equal("assets/shared/rock.tex", keys["assets/shared/rock.tex"]);
        }

        private static BinTreeObject CreateMaterial(string path, params BinTreeEmbedded[] samplers)
        {
            var samplerContainer = new BinTreeUnorderedContainer(
                Fnv1a.HashLower("samplerValues"),
                BinPropertyType.Embedded,
                samplers);
            return new BinTreeObject(
                path,
                "StaticMaterialDef",
                new BinTreeProperty[] { samplerContainer });
        }

        private static BinTreeEmbedded CreateSampler(string textureName, string texturePath) =>
            new(
                0,
                Fnv1a.HashLower("StaticMaterialShaderSamplerDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("textureName"), textureName),
                    new BinTreeString(Fnv1a.HashLower("texturePath"), texturePath),
                    new BinTreeU32(Fnv1a.HashLower("addressU"), 0),
                    new BinTreeU32(Fnv1a.HashLower("addressV"), 0)
                });
    }
}

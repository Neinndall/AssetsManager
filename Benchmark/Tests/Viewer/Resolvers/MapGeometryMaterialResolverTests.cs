using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Resolvers
{
    public class MapGeometryMaterialResolverTests
    {
        [Fact]
        public void TryResolve_UsesMaterialPathHashAndModernTextureLink()
        {
            const string materialPath = "Maps/KitPieces/Jade/Materials/Unknown_To_Catalog";
            const string texturePath = "assets/maps/jade/diffuse.tex";
            var material = CreateMaterial(
                materialPath,
                CreateSampler("DiffuseTexture", texturePath));
            var resolver = CreateResolver(material, texturePath);

            bool resolved = resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition);

            Assert.True(resolved);
            MapGeometryMaterialPlan plan = MapGeometryMaterialResolver.CreateRenderPlan(definition);
            Assert.Equal(MapGeometryMaterialKind.Diffuse, plan.Kind);
            Assert.Equal(texturePath, plan.PrimarySampler.TexturePath);
        }

        [Fact]
        public void TryResolve_LayeredMaterialSelectsColorLayerInsteadOfMask()
        {
            const string materialPath = "Maps/KitPieces/Jade/Materials/Ground";
            string[] texturePaths =
            {
                "ASSETS/Maps/Jade/mask.tex",
                "ASSETS/Maps/Jade/dirt.tex",
                "ASSETS/Maps/Jade/rock.tex",
                "ASSETS/Maps/Jade/grass.tex",
                "ASSETS/Maps/Jade/details.tex"
            };
            var material = CreateMaterial(
                materialPath,
                CreateSampler("Mask_Texture", texturePaths[0]),
                CreateSampler("Bottom_Texture", texturePaths[1]),
                CreateSampler("Middle_Texture", texturePaths[2]),
                CreateSampler("Top_Texture", texturePaths[3]),
                CreateSampler("Extras_Texture", texturePaths[4]));
            var resolver = CreateResolver(material, texturePaths);

            Assert.True(resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition));
            MapGeometryMaterialPlan plan = MapGeometryMaterialResolver.CreateRenderPlan(definition);
            Assert.Equal(MapGeometryMaterialKind.TerrainBlend, plan.Kind);
            Assert.Equal("Bottom_Texture", plan.PrimarySampler.TextureName);
            Assert.Equal(5, definition.Samplers.Count);
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
            Assert.Equal(
                MapGeometryMaterialKind.SolidColor,
                MapGeometryMaterialResolver.CreateRenderPlan(definition).Kind);
        }

        [Fact]
        public void TryResolve_AuxiliarySamplerIsNotUsedAsDiffuseColor()
        {
            const string materialPath = "Maps/Materials/NormalOnly";
            const string texturePath = "ASSETS/Maps/normal.tex";
            var material = CreateMaterial(
                materialPath,
                CreateSampler("NormalTexture", texturePath));
            var resolver = CreateResolver(material, texturePath);

            Assert.True(resolver.TryResolve(materialPath, out MapGeometryMaterialDefinition definition));
            Assert.Equal(
                MapGeometryMaterialKind.Unsupported,
                MapGeometryMaterialResolver.CreateRenderPlan(definition).Kind);
            Assert.Single(definition.Samplers);
        }

        [Theory]
        [InlineData("Flow_Map|Flowing_Normal_Map|Diffuse_Texture", (int)MapGeometryMaterialKind.FlowMap, "diffuse_texture.tex")]
        [InlineData("BAKED_DIFFUSE_TEXTURE", (int)MapGeometryMaterialKind.BakedDiffuse, null)]
        [InlineData("Custom_Texture", (int)MapGeometryMaterialKind.Diffuse, "custom_texture.tex")]
        [InlineData("First_Texture|Second_Texture", (int)MapGeometryMaterialKind.Unsupported, null)]
        public void CreateRenderPlan_ClassifiesSamplerRoles(
            string samplerNames,
            int expectedKind,
            string expectedTexture)
        {
            var material = new MapGeometryMaterialDefinition(
                "Material",
                samplerNames.Split('|').Select(name => Sampler(name, $"{name.ToLowerInvariant()}.tex")).ToArray(),
                null,
                new Dictionary<string, Vector4>(),
                0);
            MapGeometryMaterialPlan plan = MapGeometryMaterialResolver.CreateRenderPlan(material);

            Assert.Equal((MapGeometryMaterialKind)expectedKind, plan.Kind);
            Assert.Equal(expectedTexture, plan.PrimarySampler?.TexturePath);
        }

        [Fact]
        public void CreateRenderPlan_UsesShaderAlphaTestCutoff()
        {
            var material = new MapGeometryMaterialDefinition(
                "Brush",
                new[] { Sampler("Diffuse_Texture", "brush.tex") },
                null,
                new Dictionary<string, Vector4>(),
                Fnv1a.HashLower("Shaders/Environment/SRX_Brush"));

            Assert.Equal(0.3f, MapGeometryMaterialResolver.CreateRenderPlan(material).AlphaCutoff);
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
                    new BinTreeWadChunkLink(
                        Fnv1a.HashLower("texturePath"),
                        XxHash64Ext.Hash(texturePath)),
                    new BinTreeU32(Fnv1a.HashLower("addressU"), 0),
                    new BinTreeU32(Fnv1a.HashLower("addressV"), 0)
                });

        private static MapGeometryMaterialResolver CreateResolver(
            BinTreeObject material,
            params string[] texturePaths)
        {
            Dictionary<ulong, string> paths = texturePaths.ToDictionary(
                path => XxHash64Ext.Hash(path),
                path => path);
            return new MapGeometryMaterialResolver(
                new BinTree(new[] { material }, Array.Empty<string>()),
                hash => paths.TryGetValue(hash, out string path) ? path : hash.ToString("x16"));
        }

        private static MapGeometryTextureSampler Sampler(string name, string path) =>
            new(name, string.Empty, path, 0, 0);
    }
}

using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Resolvers
{
    public class SknMaterialTextureResolverTests
    {
        [Fact]
        public void Resolve_UsesSkinMeshTextureAsDefaultWithoutStaticMaterials()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Base/Belveth_Base_Main_TX.tex");

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belvethloadscreen_0", "belveth_base_main_tx" });

            Assert.Equal("belveth_base_main_tx", resolution.DefaultTextureKey);
            Assert.Empty(resolution.Overrides);
        }

        [Fact]
        public void Resolve_UsesDirectTextureOverrideForSubmesh()
        {
            BinTreeEmbedded pixieOverride = CreateOverride(
                "Autumn_Pixie",
                new BinTreeString(
                    Fnv1a.HashLower("texture"),
                    "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_Pixies_Autumn_TX_CM.tex"));
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                pixieOverride);

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belveth_skin29_tx_cm", "belveth_skin29_pixies_autumn_tx_cm" });

            Assert.Equal(
                "belveth_skin29_pixies_autumn_tx_cm",
                resolution.Overrides["autumnpixie"]);
        }

        [Fact]
        public void Resolve_UsesLinkedStaticMaterialForSubmesh()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Head";
            BinTreeEmbedded headOverride = CreateOverride(
                "Head",
                new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath)));
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                headOverride,
                CreateMaterial(
                    materialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belveth_skin29_tx_cm" });

            Assert.Equal("belveth_skin29_tx_cm", resolution.Overrides["head"]);
        }

        [Fact]
        public void Resolve_FallsBackToLinkedMaterialWhenDirectTextureIsUnavailable()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Head";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                CreateOverride(
                    "Head",
                    new BinTreeString(Fnv1a.HashLower("texture"), "missing.tex"),
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterial(
                    materialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belveth_skin29_tx_cm" });

            Assert.Equal("belveth_skin29_tx_cm", resolution.Overrides["head"]);
        }

        [Theory]
        [InlineData(
            "Alune",
            "PetStyleTwoAphelios_Alune",
            "PetStyleTwoAphelios_Skin2_Alune_TX",
            "alune")]
        [InlineData(
            "Alune_Head",
            "PetStyleTwoAphelios_Alune_Face",
            "PetStyleTwoAphelios_Skin2_Alune_Face_TX",
            "alunehead")]
        public void Resolve_UsesLayerTex01ForCompanionMaterial(
            string submesh,
            string materialName,
            string textureName,
            string expectedMaterialKey)
        {
            string materialPath =
                $"Characters/PetStyleTwoAphelios/Skins/Skin2/Materials/{materialName}";
            string texturePath =
                $"ASSETS/Characters/PetStyleTwoAphelios/Skins/Skin2/Particles/{textureName}.tex";
            BinTreeEmbedded materialOverride = CreateOverride(
                submesh,
                new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath)));
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/PetStyleTwoAphelios/Themes/SpiritBlossomSprings/" +
                "PetStyleTwoAphelios_SpiritBlossomSprings_Tier1_TX_CM.tex",
                materialOverride,
                CreateMaterial(
                    materialPath,
                    CreateSampler("_LayerTex02", "ASSETS/Characters/Shared/Overlay.tex"),
                    CreateSampler("_LayerTex01", texturePath)));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { textureName.ToLowerInvariant() });
            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);

            Assert.Equal(textureName.ToLowerInvariant(), resolution.Overrides[expectedMaterialKey]);
            Assert.Contains(texturePath, metadata.ReferencedTexturePaths);
            Assert.DoesNotContain(
                "ASSETS/Characters/Shared/Overlay.tex",
                metadata.ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_PrefersMainTextureOverDiffuseMask()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Creaturebody";
            BinTreeEmbedded creatureOverride = CreateOverride(
                "Creaturebody",
                new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath)));
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                creatureOverride,
                CreateMaterial(
                    materialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_Mask_TX_CM.tex"),
                    CreateSampler(
                        "Main_Texture",
                        "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belveth_skin29_mask_tx_cm", "belveth_skin29_tx_cm" });

            Assert.Equal("belveth_skin29_tx_cm", resolution.Overrides["creaturebody"]);
        }

        [Fact]
        public void FindUnambiguousFallback_RejectsPresentationAndAmbiguousTextures()
        {
            Assert.Null(SknMaterialTextureResolver.FindUnambiguousFallback(
                new[] { "belvethloadscreen_0" }));
            Assert.Null(SknMaterialTextureResolver.FindUnambiguousFallback(
                new[] { "belveth_skin29_tx_cm", "belveth_skin29_ult_tx_cm" }));
            Assert.Equal(
                "belveth_skin29_tx_cm",
                SknMaterialTextureResolver.FindUnambiguousFallback(
                    new[] { "belvethloadscreen_0", "belveth_skin29_tx_cm" }));
        }

        [Fact]
        public void TryResolveBinPath_UsesExpectedHashNamedSkinBin()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-skn-{Guid.NewGuid():N}");
            string sknPath = Path.Combine(
                root,
                "assets",
                "characters",
                "belveth",
                "skins",
                "skin29",
                "belveth_skin29.skn");
            string virtualBinPath = "data/characters/belveth/skins/skin29.bin";
            string hashedBinPath = Path.Combine(root, $"{XxHash64Ext.Hash(virtualBinPath):x16}.bin");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                File.WriteAllBytes(hashedBinPath, Array.Empty<byte>());

                Assert.Equal(hashedBinPath, SknMaterialTextureResolver.TryResolveBinPath(sknPath));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Fact]
        public void TryResolveBinPath_UsesChromaDirectoryInsteadOfInheritedModelPath()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-chroma-{Guid.NewGuid():N}");
            string chromaDirectory = Path.Combine(
                root,
                "assets",
                "characters",
                "belveth",
                "skins",
                "skin02");
            string skinBinPath = Path.Combine(
                root,
                "data",
                "characters",
                "belveth",
                "skins",
                "skin2.bin");

            try
            {
                Directory.CreateDirectory(chromaDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(skinBinPath)!);
                File.WriteAllBytes(skinBinPath, Array.Empty<byte>());

                Assert.Equal(
                    skinBinPath,
                    SknMaterialTextureResolver.TryResolveBinPath(chromaDirectory));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TryResolveBinPath_UsesCompanionSkinBinReferencingThemeModel(bool retainsAssetPrefix)
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-companion-{Guid.NewGuid():N}");
            string characterRoot = retainsAssetPrefix
                ? Path.Combine(root, "assets", "characters", "petstyletwoaphelios")
                : Path.Combine(root, "petstyletwoaphelios");
            string sknPath = Path.Combine(
                characterRoot,
                "themes",
                "spiritblossomsprings",
                "tier1",
                "petstyletwoaphelios_spiritblossomsprings_tier1.skn");
            string skinBinPath = Path.Combine(characterRoot, "skins", "skin2.bin");
            string virtualSknPath =
                "ASSETS/Characters/PetStyleTwoAphelios/Themes/SpiritBlossomSprings/Tier1/" +
                "PetStyleTwoAphelios_SpiritBlossomSprings_Tier1.skn";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(skinBinPath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());

                BinTree tree = CreateSkinTree(
                    "ASSETS/Characters/PetStyleTwoAphelios/Themes/SpiritBlossomSprings/" +
                    "PetStyleTwoAphelios_SpiritBlossomSprings_Tier1_TX_CM.tex",
                    simpleSkinPath: virtualSknPath);
                using (var stream = File.Create(skinBinPath))
                {
                    tree.Write(stream);
                }

                Assert.Equal(skinBinPath, SknMaterialTextureResolver.TryResolveBinPath(sknPath));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Fact]
        public void ResolveTextureDirectory_UsesCompanionThemeParent()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-companion-textures-{Guid.NewGuid():N}");
            string themeDirectory = Path.Combine(root, "pet", "themes", "theme");
            string tierDirectory = Path.Combine(themeDirectory, "tier1");
            string sknPath = Path.Combine(tierDirectory, "pet_theme_tier1.skn");

            try
            {
                Directory.CreateDirectory(tierDirectory);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(themeDirectory, "pet_theme_body_tx_cm.tex"), Array.Empty<byte>());

                Assert.Equal(themeDirectory, SknLoadingService.ResolveTextureDirectory(sknPath));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TryResolveReferencedTexturePath_FindsCompanionParticleTexture(bool retainsAssetPrefix)
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-companion-reference-{Guid.NewGuid():N}");
            string characterRoot = retainsAssetPrefix
                ? Path.Combine(root, "assets", "characters", "petstyletwoaphelios")
                : Path.Combine(root, "petstyletwoaphelios");
            string sknPath = Path.Combine(
                characterRoot,
                "themes",
                "spiritblossomsprings",
                "tier1",
                "petstyletwoaphelios_spiritblossomsprings_tier1.skn");
            string texturePath = Path.Combine(
                characterRoot,
                "skins",
                "skin2",
                "particles",
                "petstyletwoaphelios_skin2_alune_tx.tex");
            const string assetTexturePath =
                "ASSETS/Characters/PetStyleTwoAphelios/Skins/Skin2/Particles/" +
                "PetStyleTwoAphelios_Skin2_Alune_TX.tex";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(texturePath, Array.Empty<byte>());

                Assert.Equal(
                    texturePath,
                    SknMaterialTextureResolver.TryResolveTexturePath(sknPath, assetTexturePath),
                    ignoreCase: true);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static BinTree CreateSkinTree(
            string defaultTexturePath,
            BinTreeEmbedded materialOverride = null,
            BinTreeObject material = null,
            string simpleSkinPath = null)
        {
            var meshPropertyList = new System.Collections.Generic.List<BinTreeProperty>
            {
                new BinTreeString(Fnv1a.HashLower("texture"), defaultTexturePath)
            };
            if (!string.IsNullOrWhiteSpace(simpleSkinPath))
            {
                meshPropertyList.Add(new BinTreeString(Fnv1a.HashLower("simpleSkin"), simpleSkinPath));
            }
            if (materialOverride != null)
            {
                meshPropertyList.Add(
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("materialOverride"),
                        BinPropertyType.Embedded,
                        new[] { materialOverride }));
            }

            var meshProperties = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                meshPropertyList);
            var skin = new BinTreeObject(
                "Characters/Belveth/Skins/Skin",
                "SkinCharacterDataProperties",
                new BinTreeProperty[] { meshProperties });

            return material == null
                ? new BinTree(new[] { skin }, Array.Empty<string>())
                : new BinTree(new[] { skin, material }, Array.Empty<string>());
        }

        private static BinTreeEmbedded CreateOverride(
            string submesh,
            params BinTreeProperty[] textureOrMaterial) =>
            new(
                0,
                Fnv1a.HashLower("SkinMeshDataProperties_MaterialOverride"),
                new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("submesh"), submesh) }
                    .Concat(textureOrMaterial));

        private static BinTreeObject CreateMaterial(string path, params BinTreeEmbedded[] samplers) =>
            new(
                path,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        samplers)
                });

        private static BinTreeEmbedded CreateSampler(string textureName, string texturePath) =>
            new(
                0,
                Fnv1a.HashLower("StaticMaterialShaderSamplerDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("textureName"), textureName),
                    new BinTreeString(Fnv1a.HashLower("texturePath"), texturePath)
                });
    }
}

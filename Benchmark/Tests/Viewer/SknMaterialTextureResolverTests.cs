using System;
using System.IO;
using AssetsManager.Services.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer
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

        private static BinTree CreateSkinTree(
            string defaultTexturePath,
            BinTreeEmbedded materialOverride = null,
            BinTreeObject material = null)
        {
            var meshProperties = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                0,
                materialOverride == null
                    ? new BinTreeProperty[]
                    {
                        new BinTreeString(Fnv1a.HashLower("texture"), defaultTexturePath)
                    }
                    : new BinTreeProperty[]
                    {
                        new BinTreeString(Fnv1a.HashLower("texture"), defaultTexturePath),
                        new BinTreeUnorderedContainer(
                            Fnv1a.HashLower("materialOverride"),
                            BinPropertyType.Embedded,
                            new[] { materialOverride })
                    });
            var skin = new BinTreeObject(
                "Characters/Belveth/Skins/Skin",
                "SkinCharacterDataProperties",
                new BinTreeProperty[] { meshProperties });

            return material == null
                ? new BinTree(new[] { skin }, Array.Empty<string>())
                : new BinTree(new[] { skin, material }, Array.Empty<string>());
        }

        private static BinTreeEmbedded CreateOverride(string submesh, BinTreeProperty textureOrMaterial) =>
            new(
                0,
                Fnv1a.HashLower("SkinMeshDataProperties_MaterialOverride"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("submesh"), submesh),
                    textureOrMaterial
                });

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

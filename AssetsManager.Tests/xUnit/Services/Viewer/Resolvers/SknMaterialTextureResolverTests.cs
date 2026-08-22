using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using SknResolver = AssetsManager.Services.Viewer.Resolvers.SknMaterialTextureResolver;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers
{
    public class SknMaterialTextureResolverTests
    {
        private static readonly ConcurrentDictionary<ulong, string> TestTexturePaths = new();
        private static readonly string[] SeraphineBodyTextureKeys =
        {
            "seraphine_skin69_body_tx_cm",
            "seraphine_skin69_cloth_iridescent",
            "seraphine_skin69_cloth_tx_cm_mask",
            "white",
            "black"
        };

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
        public void Resolve_AcceptsWadChunkLinksForSkinAndMaterialTextures()
        {
            const string defaultTexturePath =
                "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_TX_CM.tex";
            const string materialTexturePath =
                "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_Sword_TX_CM.tex";
            const string materialPath = "Characters/Aatrox/Skins/Base/Materials/Sword";
            ulong defaultTextureHash = XxHash64Ext.Hash(defaultTexturePath.ToLowerInvariant());
            ulong materialTextureHash = XxHash64Ext.Hash(materialTexturePath.ToLowerInvariant());

            BinTree tree = CreateSkinTree(
                defaultTexturePath,
                CreateOverride(
                    "Sword",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterial(
                    materialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), materialTextureHash))),
                defaultTextureProperty: new BinTreeWadChunkLink(
                    Fnv1a.HashLower("texture"),
                    defaultTextureHash));

            Func<ulong, string> resolvePath = pathHash => pathHash == defaultTextureHash
                ? defaultTexturePath
                : pathHash == materialTextureHash
                    ? materialTexturePath
                    : $"{pathHash:x16}";
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "aatrox_base_tx_cm", "aatrox_base_sword_tx_cm" },
                resolvePath);

            Assert.Equal("aatrox_base_tx_cm", resolution.DefaultTextureKey);
            Assert.Equal("aatrox_base_sword_tx_cm", resolution.Overrides["sword"]);
            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree, resolvePath);
            AssertContainsPath(defaultTexturePath, metadata.ReferencedTexturePaths);
            AssertContainsPath(materialTexturePath, metadata.ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_UsesDirectTextureOverrideForSubmesh()
        {
            BinTreeEmbedded pixieOverride = CreateOverride(
                "Autumn_Pixie",
                CreateTextureLink(
                    "texture",
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
        public void Resolve_UsesLinkedSkinMaterialAsDefaultBeforeLegacyTexture()
        {
            const string materialPath = "Characters/Aatrox/Skins/Skin33/Materials/Default_Head";
            const string legacyTexturePath =
                "ASSETS/Characters/Aatrox/Skins/Skin33/Aatrox_Skin33_Sword_VFX_TX_CM.tex";
            const string materialTexturePath =
                "ASSETS/Characters/Aatrox/Skins/Skin33/Aatrox_Skin33_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                legacyTexturePath,
                material: CreateMaterial(
                    materialPath,
                    CreateSampler("Diffuse_Texture", materialTexturePath)),
                defaultMaterialPath: materialPath);

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "aatrox_skin33_sword_vfx_tx_cm", "aatrox_skin33_tx_cm" });

            Assert.Equal("aatrox_skin33_tx_cm", resolution.DefaultTextureKey);
            AssertContainsPath(
                materialTexturePath,
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_HandlesMultiSubmeshModelWithDefaultMaterialAndOverrides()
        {
            const string defaultMaterialPath = "Characters/Aatrox/Skins/Skin33/Materials/Base_Body";
            const string swordMaterialPath = "Characters/Aatrox/Skins/Skin33/Materials/Sword";
            const string bodyTexturePath = "ASSETS/Characters/Aatrox/Skins/Skin33/Aatrox_Skin33_TX_CM.tex";
            const string swordTexturePath = "ASSETS/Characters/Aatrox/Skins/Skin33/Aatrox_Skin33_Sword_TX_CM.tex";
            const string legacyTexturePath = "ASSETS/Characters/Aatrox/Skins/Skin33/Aatrox_Skin33_VFX_TX_CM.tex";

            BinTreeEmbedded swordOverride = CreateOverride(
                "Sword",
                new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(swordMaterialPath)));

            BinTree tree = CreateSkinTree(
                legacyTexturePath,
                swordOverride,
                CreateMaterial(
                    defaultMaterialPath,
                    CreateSampler("Diffuse_Texture", bodyTexturePath)),
                defaultMaterialPath: defaultMaterialPath,
                materialOverride2: null);

            // Add the sword material object to the bin tree
            BinTreeObject swordMaterialObj = CreateMaterial(
                swordMaterialPath,
                CreateSampler("Diffuse_Texture", swordTexturePath));
            tree.Objects[Fnv1a.HashLower(swordMaterialPath)] = swordMaterialObj;

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "aatrox_skin33_tx_cm", "aatrox_skin33_sword_tx_cm", "aatrox_skin33_vfx_tx_cm" });

            // Submesh 0 (Body/Default) falls back to DefaultTextureKey
            Assert.Equal("aatrox_skin33_tx_cm", resolution.DefaultTextureKey);

            // Submesh 1 (Sword) resolves via MaterialOverride
            Assert.True(resolution.Overrides.ContainsKey("sword"));
            Assert.Equal("aatrox_skin33_sword_tx_cm", resolution.Overrides["sword"]);

            // Verified metadata contains both referenced texture paths
            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);
            AssertContainsPath(bodyTexturePath, metadata.ReferencedTexturePaths);
            AssertContainsPath(swordTexturePath, metadata.ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_FallsBackToLinkedMaterialWhenDirectTextureIsUnavailable()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Head";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                CreateOverride(
                    "Head",
                    CreateTextureLink("texture", "missing.tex"),
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
            AssertContainsPath(texturePath, metadata.ReferencedTexturePaths);
            AssertDoesNotContainPath(
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
        public void Resolve_ScopesAdditiveScrollToItsSubmesh()
        {
            const string materialPath = "Characters/Aurora/Skins/Base/Materials/Aurora";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_TX_CM.tex",
                CreateOverride(
                    "Base",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_TX_CM.tex"),
                        CreateSampler("AdditiveScrollTex", "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_Mat_Tile01.tex"),
                        CreateSampler("AdditiveScroll_Mask", "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_Mat_HatMask.tex")
                    },
                    CreateParameter("AdditiveTexTile", new Vector4(3f, 2f, 0f, 0f)),
                    CreateParameter("AdditiveTexScrollSpeed_R", new Vector4(-0.1f, 0.1f, 0f, 0f)),
                    CreateParameter("AdditiveScroll_ColorTint_R", new Vector4(0.18f, 0.67f, 1f, 0f)),
                    CreateParameter("AdditiveStrength_R", Vector4.One)),
                materialOverride2: CreateOverride(
                    "Hat",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "aurora_base_tx_cm",
                    "aurora_base_mat_tile01",
                    "aurora_base_mat_hatmask"
                });

            Assert.False(resolution.Effects.ContainsKey("base"));
            ModelMaterialEffectDefinition effect = resolution.Effects["hat"];
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, effect.Kind);
            Assert.Equal("aurora_base_mat_tile01", effect.TextureName);
            Assert.Equal("aurora_base_mat_hatmask", effect.MaskTextureName);
            Assert.Equal(new Vector2(-0.1f, 0.1f), effect.ScrollSpeed);
            Assert.Equal(new Vector2(3f, 2f), effect.Tiling);
            Assert.Equal(new Vector4(0.18f, 0.67f, 1f, 0f), effect.Color);
            AssertContainsPath(
                "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_Mat_HatMask.tex",
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_IgnoresUntintedStaticWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(includeAdditiveTint: false),
                SeraphineBodyTextureKeys);

            Assert.Equal("seraphine_skin69_body_tx_cm", resolution.Overrides["body"]);
            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.False((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Iridescence) != 0);
            Assert.Equal("seraphine_skin69_cloth_iridescent", effect.Iridescence.LutTextureName);
            Assert.Equal("seraphine_skin69_cloth_tx_cm_mask", effect.Iridescence.MaskTextureName);
            Assert.Equal(new Vector4(1.1f, 1f, 3f, 0f), effect.Iridescence.Control);
            Assert.Equal(new Vector2(1f, 0f), effect.Iridescence.PulseSpeedMin);
            Assert.Equal(new Vector2(0f, 1f), effect.Iridescence.FresnelAlphaMinMax);
            Assert.True(effect.Iridescence.UsesPulse);
            Assert.True(effect.Iridescence.UsesLocalizedAlpha);
            Assert.True(effect.RequiresAlphaBlend);
        }

        [Fact]
        public void Resolve_PreservesTintedWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(includeAdditiveTint: true),
                SeraphineBodyTextureKeys);

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.True((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Iridescence) != 0);
            Assert.Equal("seraphine_skin69_cloth_tx_cm_mask", effect.Iridescence.MaskTextureName);
        }

        [Fact]
        public void Resolve_PreservesExplicitWhiteAdditiveTint()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(
                    includeAdditiveTint: false,
                    includeExplicitWhiteTint: true),
                SeraphineBodyTextureKeys);

            Assert.True((resolution.Effects["body"].Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
        }

        [Fact]
        public void Resolve_IgnoresZeroSpeedWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(
                    includeAdditiveTint: false,
                    includeZeroAdditiveSpeed: true),
                SeraphineBodyTextureKeys);

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.False((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.Equal("seraphine_skin69_cloth_tx_cm_mask", effect.Iridescence.MaskTextureName);
        }

        [Fact]
        public void Resolve_DoesNotEnableOptionalIridescenceFeaturesWithoutSwitches()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(
                    includeAdditiveTint: false,
                    includeIridescenceSwitches: false),
                SeraphineBodyTextureKeys);

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.False(effect.Iridescence.UsesPulse);
            Assert.False(effect.Iridescence.UsesLocalizedAlpha);
            Assert.False(effect.RequiresAlphaBlend);
        }

        [Fact]
        public void Resolve_LeavesMissingIridescenceMaskForWhiteFallback()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(
                        "iridescentTex",
                        "ASSETS/Characters/Seraphine/Skins/Skin69/Seraphine_Skin69_Cloth_Iridescent.tex")
                },
                new Dictionary<string, Vector4>());

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "seraphine_skin69_cloth_iridescent" },
                new[] { "Body" });

            Assert.Null(effect.Iridescence.MaskTextureName);
        }

        [Fact]
        public void Resolve_CombinesFresnelAndBloomWithMaterialMask()
        {
            const string materialPath = "Characters/Brand/Skins/Skin53/Materials/Hair";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Brand/Skins/Skin53/Brand_Skin53_TX_CM.tex",
                CreateOverride(
                    "Hair",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Brand/Skins/Skin53/Brand_Skin53_Hair_TX_CM.tex"),
                        CreateSampler("Mask", "ASSETS/Characters/Brand/Skins/Skin53/Brand_Skin53_HairAlpha_TX_CM.tex")
                    },
                    CreateParameter("Fresnel_Color_Intensity", new Vector4(0.8f, 0f, 0f, 0f)),
                    CreateParameter("FresnelPower", new Vector4(3f, 0f, 0f, 0f)),
                    CreateParameter("Fresnel_Noise_Tiling_Speed", new Vector4(1f, 3f, 0.2f, -0.1f)),
                    CreateParameter("Anim_Wave_Speed", new Vector4(0.8f, 0f, 0f, 0f)),
                    CreateParameter("Anim_Wave_Dir", new Vector4(50f, 40f, 30f, 0f)),
                    CreateParameter("Anim_Wave_Frequency", new Vector4(0.7f, 0f, 0f, 0f)),
                    CreateParameter("Anim_Wave_Dir_Intensity", new Vector4(0.15f, 0f, 0f, 0f)),
                    CreateParameter("BloomColor", new Vector4(1f, 0.2f, 0.05f, 1f)),
                    CreateParameter("BloomColorIntensity", new Vector4(0.6f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "brand_skin53_tx_cm",
                    "brand_skin53_hair_tx_cm",
                    "brand_skin53_hairalpha_tx_cm"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["hair"];
            Assert.Equal(
                ModelMaterialEffectKind.Fresnel |
                ModelMaterialEffectKind.Bloom |
                ModelMaterialEffectKind.FresnelNoise |
                ModelMaterialEffectKind.AnimatedWave,
                effect.Kind);
            Assert.Equal("brand_skin53_hairalpha_tx_cm", effect.MaskTextureName);
            Assert.Equal(3f, effect.FresnelPower);
            Assert.Equal(0.8f, effect.FresnelStrength);
            Assert.Equal(new Vector4(1f, 0.2f, 0.05f, 1f), effect.BloomColor);
            Assert.Equal(0.6f, effect.BloomIntensity);
            Assert.Equal(new Vector3(50f, 40f, 30f), effect.WaveDirection);
            Assert.Equal(0.8f, effect.WaveSpeed);
            Assert.Equal(0.7f, effect.WaveFrequency);
            Assert.Equal(0.15f, effect.WaveIntensity);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.FresnelNoiseSpeed);
        }

        [Fact]
        public void Resolve_DoesNotInventBloomForGradientDissolveMaterial()
        {
            const string materialPath = "Characters/Aatrox/Skins/Base/Materials/Wings";
            const string diffusePath = "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_Wings_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Wings",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Mask_Texture", "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_R_wing_mask.tex"),
                        CreateSampler("Gradient_Texture", "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_R_mat_gradient.tex")
                    },
                    CreateParameter("Mask_Intensity", new Vector4(0.94f, 0f, 0f, 0f)),
                    CreateParameter("Bloom_Intensity", new Vector4(5f, 0f, 0f, 0f)),
                    CreateParameter("Dissolve_Bias", new Vector4(0.785f, 0f, 0f, 0f)),
                    CreateParameter("Dissolve_SmoothStep", new Vector4(0f, 0.5f, 0f, 0f)),
                    CreateParameter("Gradient_Sharpness", new Vector4(2f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "aatrox_base_wings_tx_cm",
                    "aatrox_base_r_wing_mask",
                    "aatrox_base_r_mat_gradient"
                });

            Assert.DoesNotContain("wings", resolution.Effects);
        }

        [Fact]
        public void Resolve_RecognizesMaskedGradientPulseMaterial()
        {
            const string materialPath = "Characters/Aatrox/Skins/Skin0/Materials/Aatrox_VFXBase";
            const string diffusePath = "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_TX_CM.tex";
            const string maskPath = "ASSETS/Characters/Aatrox/Skins/Base/Particles/Aatrox_Base_R_body_mask.tex";
            const string gradientPath = "ASSETS/Shared/Materials/Gradient_test_01.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Mask_Texture", maskPath),
                        CreateSampler("Gradient_Texture", gradientPath)
                    },
                    CreateParameter("Pulse_Rate", new Vector4(3f, 0f, 0f, 0f)),
                    CreateParameter("Pulse_Max", new Vector4(0.4f, 0f, 0f, 0f)),
                    CreateParameter("Pulse_Offset", new Vector4(0.3f, 0f, 0f, 0f)),
                    CreateParameter("Gradient_Sharpness", new Vector4(0.5f, 0f, 0f, 0f)),
                    CreateParameter("Mask_Intensity", Vector4.One),
                    CreateParameter("Dissolve_Bias", new Vector4(-0.2f, 0f, 0f, 0f)),
                    CreateParameter("Dissolve_SmoothStep", new Vector4(0f, 0.15f, 0f, 0f)),
                    CreateParameter("Bloom_Intensity", new Vector4(10f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "aatrox_base_tx_cm",
                    "aatrox_base_r_body_mask",
                    "gradient_test_01"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.Equal(ModelMaterialEffectKind.GradientPulse, effect.Kind);
            Assert.Equal("gradient_test_01", effect.TextureName);
            Assert.Equal("aatrox_base_r_body_mask", effect.MaskTextureName);
            Assert.Equal(3f, effect.PulseRate);
            Assert.Equal(0.4f, effect.PulseMax);
            Assert.Equal(0.3f, effect.PulseOffset);
            Assert.Equal(0.5f, effect.GradientSharpness);
            Assert.Equal(10f, effect.BloomIntensity);
            Assert.Equal(-0.2f, effect.DissolveThreshold);
            Assert.Equal(0.075f, effect.DissolveSoftness);
            AssertContainsPath(gradientPath, SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_RecognizesGradientScrollEnabledByMaterialSwitch()
        {
            const string materialPath = "Characters/Aatrox/Skins/Base/Materials/Wings";
            const string diffusePath = "ASSETS/Characters/Aatrox/Skins/Base/Aatrox_Base_Wings_TX_CM.tex";
            const string maskPath = "ASSETS/Characters/Aatrox/Skins/Base/Particles/Aatrox_Base_R_wing_mask.tex";
            const string gradientPath = "ASSETS/Shared/Materials/Gradient_test_01.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Wings",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithSwitches(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Mask_Texture", maskPath),
                        CreateSampler("Gradient_Texture", gradientPath)
                    },
                    new[] { "USE_ADDATIVE" },
                    CreateParameter("Scrolling_Rate", new Vector4(-0.5f, -0.5f, 0f, 0f)),
                    CreateParameter("Scrolling_Scale", Vector4.One),
                    CreateParameter("Dissolve_Bias", new Vector4(0.785f, 0f, 0f, 0f)),
                    CreateParameter("Dissolve_SmoothStep", new Vector4(0f, 0.5f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "aatrox_base_wings_tx_cm",
                    "aatrox_base_r_wing_mask",
                    "gradient_test_01"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["wings"];
            Assert.Equal(ModelMaterialEffectKind.GradientPulse, effect.Kind);
            Assert.Equal(new Vector2(-0.5f, -0.5f), effect.ScrollSpeed);
            Assert.Equal(0.785f, effect.DissolveThreshold);
            Assert.Equal(0.25f, effect.DissolveSoftness);
        }

        [Fact]
        public void Resolve_RecognizesRealScrollSamplerAliases()
        {
            const string materialPath = "Characters/Lux/Skins/Skin58/Materials/Body";
            const string diffusePath = "ASSETS/Characters/Lux/Skins/Skin58/Lux_Skin58_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Scroll_Tex", "ASSETS/Characters/Lux/Skins/Skin58/Lux_Scroll.tex"),
                        CreateSampler("Scroll_Tex_Mask", "ASSETS/Characters/Lux/Skins/Skin58/Lux_Scroll_Mask.tex")
                    },
                    CreateParameter("ScrollSpeed_R", new Vector4(0.2f, -0.1f, 0f, 0f)),
                    CreateParameter("ScrollStrength_R", new Vector4(0.75f, 0f, 0f, 0f)),
                    CreateParameter("ScrollTexTile", new Vector4(2f, 3f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "lux_skin58_tx_cm",
                    "lux_scroll",
                    "lux_scroll_mask"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, effect.Kind);
            Assert.Equal("lux_scroll", effect.TextureName);
            Assert.Equal("lux_scroll_mask", effect.MaskTextureName);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.ScrollSpeed);
            Assert.Equal(new Vector2(2f, 3f), effect.Tiling);
        }

        [Fact]
        public void Resolve_RecognizesRealDissolveSamplerAliases()
        {
            const string materialPath = "Characters/MissFortune/Skins/Skin48/Materials/Body";
            const string diffusePath = "ASSETS/Characters/MissFortune/Skins/Skin48/MissFortune_Skin48_TX_CM.tex";
            const string dissolvePath = "ASSETS/Characters/MissFortune/Skins/Skin48/Dissolve_Texture.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Dissolve_Texture", dissolvePath)
                    },
                    CreateParameter("DissolveBias", new Vector4(0.35f, 0f, 0f, 0f)),
                    CreateParameter("DissolveWidth", new Vector4(0.2f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "missfortune_skin48_tx_cm", "dissolve_texture" });

            ModelMaterialEffectDefinition effect = resolution.Effects["body"];
            Assert.Equal(ModelMaterialEffectKind.Dissolve, effect.Kind);
            Assert.Equal("dissolve_texture", effect.TextureName);
            Assert.Equal(0.35f, effect.DissolveThreshold);
            Assert.Equal(0.2f, effect.DissolveSoftness);
            AssertContainsPath(dissolvePath, SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_IgnoresOutOfRangeDissolveBiasFromOpaqueSkinMaterial()
        {
            const string bodyTexturePath =
                "ASSETS/Characters/Gwen/Skins/Base/Gwen_Base_Main_TX_CM.tex";
            const string dissolvePath =
                "ASSETS/Characters/Gwen/Skins/Base/Particles/Gwen_Base_R_SmokeErode.tex";
            const string materialPath = "Characters/Gwen/Skins/Base/Materials/Body";

            BinTree tree = CreateSkinTree(
                bodyTexturePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", bodyTexturePath),
                        CreateSampler("Dissolve_Texture", dissolvePath),
                        CreateSampler("Alt_Diffuse_Texture", bodyTexturePath)
                    },
                    CreateParameter("DissolveIntensity", new Vector4(10f, 0f, 0f, 0f)),
                    CreateParameter("DissolveBias", new Vector4(1.5575f, 0f, 0f, 0f)),
                    CreateParameter("DissolveWidth", new Vector4(0.1f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "gwen_base_main_tx_cm", "gwen_base_r_smokeerode" });

            Assert.Equal("gwen_base_main_tx_cm", resolution.Overrides["body"]);
            Assert.DoesNotContain("body", resolution.Effects);
        }

        [Fact]
        public void Resolve_RecognizesRedChannelEmissionTexture()
        {
            const string materialPath = "Characters/Aatrox/Skins/Skin37/Materials/Sword";
            const string diffusePath = "ASSETS/Characters/Aatrox/Skins/Skin37/Aatrox_Skin37_Sword_TX_CM.tex";
            const string emissionPath = "ASSETS/Characters/Aatrox/Skins/Skin37/Aatrox_Skin37_Sword_Distortion.tex";
            const string maskPath = "ASSETS/Characters/Aatrox/Skins/Skin37/Aatrox_Skin37_Sword_EmissionMask.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Sword",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("EmissionR_DistortionG_Texture", emissionPath),
                        CreateSampler("EmissionMask", maskPath)
                    },
                    CreateParameter("EmissionR_Strength", new Vector4(1.25f, 0f, 0f, 0f)),
                    CreateParameter("EmissionColor", new Vector4(1f, 0.63f, 0f, 1f)),
                    CreateParameter("VFX_ScrollTex_R_UV_Tile", new Vector4(15f, 3f, 0f, 0f)),
                    CreateParameter("VFX_ScrollTex_R_UV_Scroll_Speed", new Vector4(0f, -2f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "aatrox_skin37_sword_tx_cm",
                    "aatrox_skin37_sword_distortion",
                    "aatrox_skin37_sword_emissionmask"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["sword"];
            Assert.True((effect.Kind & ModelMaterialEffectKind.Emission) != 0);
            Assert.False((effect.Kind & ModelMaterialEffectKind.Bloom) != 0);
            Assert.Equal("aatrox_skin37_sword_distortion", effect.EmissionTextureName);
            Assert.Equal("aatrox_skin37_sword_emissionmask", effect.EmissionMaskTextureName);
            Assert.Equal(0, effect.EmissionChannel);
            Assert.Equal(new Vector2(15f, 3f), effect.EmissionTiling);
            Assert.Equal(new Vector2(0f, -2f), effect.EmissionScrollSpeed);
            Assert.Equal(1.25f, effect.EmissionStrength);
            Assert.Equal(new Vector4(1f, 0.63f, 0f, 1f), effect.EmissionColor);
            AssertContainsPath(emissionPath, SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
            AssertContainsPath(maskPath, SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_IgnoresNeutralEmissionTexture()
        {
            const string materialPath = "Characters/Aatrox/Skins/Skin37/Materials/Sword";
            const string diffusePath = "ASSETS/Characters/Aatrox/Skins/Skin37/Aatrox_Skin37_Sword_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Sword",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("EmissionR_DistortionG_Texture", "ASSETS/Shared/Materials/black.tex")
                    },
                    CreateParameter("EmissionR_Strength", Vector4.One),
                    CreateParameter("EmissionColor", Vector4.One)));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "aatrox_skin37_sword_tx_cm" });

            Assert.DoesNotContain("sword", resolution.Effects);
        }

        [Fact]
        public void Resolve_DoesNotApproximateComplexVertexDeformationAsSimpleWave()
        {
            const string materialPath = "Characters/MissFortune/Skins/Skin69/Materials/Gun25Gold";
            const string texturePath = "ASSETS/Characters/MissFortune/Skins/Skin69/Gun25_Gold_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                texturePath,
                CreateOverride(
                    "C_Gun25",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", texturePath),
                        CreateSampler("DeformNoise", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("DeformMask", "ASSETS/Shared/Materials/black.tex")
                    },
                    CreateParameter("Anim_Wave_Speed", new Vector4(0.3f, 0f, 0f, 0f)),
                    CreateParameter("Anim_Wave_Dir", new Vector4(5f, 5f, 0.5f, 0f)),
                    CreateParameter("Anim_Wave_Frequency", Vector4.One),
                    CreateParameter("Anim_Wave_Dir_Intensity", Vector4.One),
                    CreateParameter("VertexDeformFeatureStrength", Vector4.One),
                    CreateParameter("DeformIntensity", new Vector4(6f, 0f, 0f, 0f)),
                    CreateParameter("DeformProtection", new Vector4(2f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "gun25_gold_tx_cm" });

            Assert.Equal("gun25_gold_tx_cm", resolution.Overrides["cgun25"]);
            Assert.DoesNotContain("cgun25", resolution.Effects);
        }

        [Fact]
        public void Resolve_ResolvesMaskTextureRedForBelvethBloom()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Ult";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_ULT_TX_CM.tex",
                CreateOverride(
                    "Ult",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_ULT_TX_CM.tex"),
                        CreateSampler("Mask_Texture_red", "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_Ult_BloomMask_TX_CM.tex")
                    },
                    CreateParameter("Bloom_Color", new Vector4(0.89f, 0.95f, 0.56f, 1f)),
                    CreateParameter("Bloom_Intensity", new Vector4(5f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "belveth_skin29_ult_tx_cm",
                    "belveth_skin29_ult_bloommask_tx_cm"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["ult"];
            Assert.Equal(ModelMaterialEffectKind.Bloom, effect.Kind);
            Assert.Equal("belveth_skin29_ult_bloommask_tx_cm", effect.MaskTextureName);
            Assert.Equal(5f, effect.BloomIntensity);
            AssertContainsPath(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_Ult_BloomMask_TX_CM.tex",
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_UsesTransitionTextureForSimpleDissolve()
        {
            const string materialPath = "Characters/Belveth/Skins/Skin29/Materials/Transition";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex",
                CreateOverride(
                    "Armor",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Skin29_TX_CM.tex"),
                        CreateSampler("Transition_PatternTexture", "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Transition_Noise.tex"),
                        CreateSampler("Transition_State2", "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Transition_State.tex")
                    },
                    CreateParameter("Dissolve", new Vector4(0.35f, 0f, 0f, 0f)),
                    CreateParameter("DissolveSoftness", new Vector4(0.08f, 0f, 0f, 0f)),
                    CreateParameter("Transition_Speed", new Vector4(0.1f, -0.2f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[]
                {
                    "belveth_skin29_tx_cm",
                    "belveth_transition_noise",
                    "belveth_transition_state"
                });

            ModelMaterialEffectDefinition effect = resolution.Effects["armor"];
            Assert.Equal(ModelMaterialEffectKind.Dissolve, effect.Kind);
            Assert.Equal("belveth_transition_noise", effect.TextureName);
            Assert.Equal("belveth_transition_state", effect.MaskTextureName);
            Assert.Equal(new Vector2(0.1f, -0.2f), effect.ScrollSpeed);
            Assert.Equal(0.35f, effect.DissolveThreshold);
            Assert.Equal(0.08f, effect.DissolveSoftness);
            AssertContainsPath(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Transition_Noise.tex",
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Theory]
        [InlineData("FlowMap")]
        [InlineData("Flow_Map")]
        [InlineData("Flowmap")]
        public void Resolve_RecognizesFlowMapSamplerAlias(string samplerName)
        {
            const string materialPath = "Characters/Brand/Skins/Skin53/Materials/Hair";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Brand/Skins/Skin53/Brand_Skin53_TX_CM.tex",
                CreateOverride(
                    "Hair",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Brand/Skins/Skin53/Brand_Skin53_Hair_TX_CM.tex"),
                        CreateSampler(samplerName, "ASSETS/Characters/Brand/Skins/Skin53/CloudFM_TX_CM.tex")
                    },
                    CreateParameter("FlowSpeed", new Vector4(0.2f, -0.1f, 0f, 0f)),
                    CreateParameter("FlowmapIntensity", new Vector4(0.15f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "brand_skin53_tx_cm", "brand_skin53_hair_tx_cm", "cloudfm_tx_cm" });

            ModelMaterialEffectDefinition effect = resolution.Effects["hair"];
            Assert.Equal(ModelMaterialEffectKind.FlowMap, effect.Kind);
            Assert.Equal("cloudfm_tx_cm", effect.TextureName);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.ScrollSpeed);
        }

        [Fact]
        public void Resolve_DoesNotApproximateCompositeOnsenMaterial()
        {
            const string materialPath = "Characters/Locke/Skins/Base/Materials/Onsen";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Locke/Skins/Base/Locke_Base_Main_TX_CM.tex",
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Locke/Skins/Base/Locke_Base_Main_TX_CM.tex"),
                        CreateSampler("NoiseDisturb", "ASSETS/Characters/Locke/Skins/Base/Locke_Coat_Mask.tex"),
                        CreateSampler("FlowmapTex", "ASSETS/Shared/Materials/flowmap.tex"),
                        CreateSampler("WaterShape", "ASSETS/Characters/Locke/Skins/Base/WaterShape.tex"),
                        CreateSampler("Transition_State2", "ASSETS/Characters/Locke/Skins/Base/Locke_State.tex"),
                        CreateSampler("AdditiveScrollTex", "ASSETS/Characters/Locke/Skins/Base/Locke_AdditionalScrollCombo.tex")
                    },
                    CreateParameter("FlowSpeed", new Vector4(-0.2f, 0f, 0f, 0f)),
                    CreateParameter("Fresnel", Vector4.One),
                    CreateParameter("Bloom", new Vector4(0.5f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "locke_base_main_tx_cm", "flowmap", "locke_additionalscrollcombo" });

            Assert.Empty(resolution.Effects);
        }

        [Fact]
        public void IsReferencedSampler_RejectsSuffixedBlackTexture()
        {
            Assert.False(SknMaterialTextureResolver.IsReferencedSampler(
                new SknMaterialSampler(
                    "Mask",
                    "ASSETS/Characters/Brand/Skins/Skin53/black.SKINS_Brand_Skin53.tex")));
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

        [Fact]
        public void TryResolveReferencedTexturePath_FindsSharedMaterialTexture()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-shared-reference-{Guid.NewGuid():N}");
            string sknPath = Path.Combine(
                root,
                "assets",
                "characters",
                "aurora",
                "skins",
                "skin0",
                "aurora_base.skn");
            string texturePath = Path.Combine(root, "assets", "shared", "materials", "flowmap.tex");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(texturePath, Array.Empty<byte>());

                Assert.Equal(
                    texturePath,
                    SknMaterialTextureResolver.TryResolveTexturePath(
                        sknPath,
                        "ASSETS/Shared/Materials/flowmap.tex"),
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

        private static BinTree CreateSeraphineIridescentBodyTree(
            bool includeAdditiveTint,
            bool includeZeroAdditiveSpeed = false,
            bool includeIridescenceSwitches = true,
            bool includeExplicitWhiteTint = false)
        {
            const string materialPath =
                "Characters/Seraphine/Skins/Skin69/Materials/Seraphine_Cloth_Iridescent";
            const string bodyTexturePath =
                "ASSETS/Characters/Seraphine/Skins/Skin69/Seraphine_Skin69_Body_TX_CM.tex";
            const string iridescenceTexturePath =
                "ASSETS/Characters/Seraphine/Skins/Skin69/Seraphine_Skin69_Cloth_Iridescent.tex";
            const string additiveMaskPath =
                "ASSETS/Characters/Seraphine/Skins/Skin69/Seraphine_Skin69_Cloth_TX_CM_Mask.tex";

            var parameters = new List<BinTreeEmbedded>
            {
                CreateParameter("AdditiveTexTile", Vector4.One),
                CreateParameter("AdditiveStrength_R", Vector4.One),
                CreateParameter("IridescentControl", new Vector4(1.1f, 1f, 3f, 0f)),
                CreateParameter("Iridescence_Pulse_Speed_Min", new Vector4(1f, 0f, 0f, 0f)),
                CreateParameter("fresnelAlpha_minmax", new Vector4(0f, 1f, 0f, 0f)),
                CreateParameter("Diffuse_Fade_Mask_Value", Vector4.One)
            };
            if (includeAdditiveTint || includeExplicitWhiteTint)
            {
                parameters.Add(
                    CreateParameter(
                        "AdditiveScroll_ColorTint_R",
                        includeAdditiveTint
                            ? new Vector4(0.5f, 0.75f, 1f, 0f)
                            : Vector4.One));
            }
            if (includeZeroAdditiveSpeed)
            {
                parameters.Add(
                    CreateParameter(
                        "AdditiveTexScrollSpeed_R",
                        Vector4.Zero));
            }

            return CreateSkinTree(
                bodyTexturePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower(materialPath))),
                CreateMaterialWithSwitches(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Pattern_Mask", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("ScreenSpace_Texture", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("MatCap_Tex", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("Color_Mask_Texture", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("Diffuse_Texture", bodyTexturePath),
                        CreateSampler("iridescentTex", iridescenceTexturePath),
                        CreateSampler("AdditiveScrollTex", "ASSETS/Shared/Materials/white.tex"),
                        CreateSampler("AdditiveScroll_Mask", additiveMaskPath),
                        CreateSampler("Diffuse_Texture2", "ASSETS/Shared/Materials/black.tex")
                    },
                    includeIridescenceSwitches
                        ? new[] { "IRIDESCENCE_PULSE", "USE_FRESNEL_ALPHA", "ALPHA_BLEND_ON" }
                        : Array.Empty<string>(),
                    parameters.ToArray()));
        }

        private static BinTreeWadChunkLink CreateTextureLink(string propertyName, string texturePath)
        {
            ulong pathHash = XxHash64Ext.Hash(texturePath.ToLowerInvariant());
            TestTexturePaths[pathHash] = texturePath;
            return new BinTreeWadChunkLink(Fnv1a.HashLower(propertyName), pathHash);
        }

        private static string ResolveTestTexturePath(ulong pathHash) =>
            TestTexturePaths.TryGetValue(pathHash, out string texturePath)
                ? texturePath
                : $"{pathHash:x16}";

        private static void AssertContainsPath(string expected, IEnumerable<string> paths) =>
            Assert.Contains(paths, path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));

        private static void AssertDoesNotContainPath(string expected, IEnumerable<string> paths) =>
            Assert.DoesNotContain(paths, path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));

        private static BinTree CreateSkinTree(
            string defaultTexturePath,
            BinTreeEmbedded materialOverride = null,
            BinTreeObject material = null,
            string simpleSkinPath = null,
            BinTreeEmbedded materialOverride2 = null,
            string defaultMaterialPath = null,
            BinTreeProperty defaultTextureProperty = null)
        {
            var meshPropertyList = new System.Collections.Generic.List<BinTreeProperty>
            {
                defaultTextureProperty ?? CreateTextureLink("texture", defaultTexturePath)
            };
            if (!string.IsNullOrWhiteSpace(simpleSkinPath))
            {
                meshPropertyList.Add(new BinTreeString(Fnv1a.HashLower("simpleSkin"), simpleSkinPath));
            }
            if (!string.IsNullOrWhiteSpace(defaultMaterialPath))
            {
                meshPropertyList.Add(new BinTreeObjectLink(
                    Fnv1a.HashLower("material"),
                    Fnv1a.HashLower(defaultMaterialPath)));
            }
            BinTreeEmbedded[] materialOverrides = new[] { materialOverride, materialOverride2 }
                .Where(overrideValue => overrideValue != null)
                .ToArray();
            if (materialOverrides.Length > 0)
            {
                meshPropertyList.Add(
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("materialOverride"),
                        BinPropertyType.Embedded,
                        materialOverrides));
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

        private static BinTreeObject CreateMaterialWithParameters(
            string path,
            BinTreeEmbedded[] samplers,
            params BinTreeEmbedded[] parameters) =>
            new(
                path,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        samplers),
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("paramValues"),
                        BinPropertyType.Embedded,
                        parameters)
                });

        private static BinTreeObject CreateMaterialWithSwitches(
            string path,
            BinTreeEmbedded[] samplers,
            string[] enabledSwitches,
            params BinTreeEmbedded[] parameters) =>
            new(
                path,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        samplers),
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("paramValues"),
                        BinPropertyType.Embedded,
                        parameters),
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("switches"),
                        BinPropertyType.Embedded,
                        enabledSwitches.Select(CreateSwitch).ToArray())
                });

        private static BinTreeEmbedded CreateSwitch(string name) =>
            new(
                0,
                Fnv1a.HashLower("StaticMaterialSwitchDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), name),
                    new BinTreeBool(Fnv1a.HashLower("on"), true)
                });

        private static BinTreeEmbedded CreateParameter(string name, Vector4 value) =>
            new(
                0,
                Fnv1a.HashLower("StaticMaterialShaderParamDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), name),
                    new BinTreeVector4(Fnv1a.HashLower("value"), value)
                });

        private static BinTreeEmbedded CreateSampler(string textureName, string texturePath) =>
            CreateSampler(
                textureName,
                CreateTextureLink("texturePath", texturePath));

        private static BinTreeEmbedded CreateSampler(string textureName, BinTreeProperty texturePath) =>
            new(
                0,
                Fnv1a.HashLower("StaticMaterialShaderSamplerDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("textureName"), textureName),
                    texturePath
                });

        private static class SknMaterialTextureResolver
        {
            internal static SknMaterialTextureResolution Resolve(
                BinTree binTree,
                IEnumerable<string> availableTextureKeys) =>
                SknResolver.Resolve(
                    binTree,
                    availableTextureKeys,
                    SknMaterialTextureResolverTests.ResolveTestTexturePath);

            internal static SknMaterialTextureResolution Resolve(
                BinTree binTree,
                IEnumerable<string> availableTextureKeys,
                Func<ulong, string> wadChunkPathResolver) =>
                SknResolver.Resolve(binTree, availableTextureKeys, wadChunkPathResolver);

            internal static SknMaterialTextureMetadata ReadMetadata(BinTree binTree) =>
                SknResolver.ReadMetadata(
                    binTree,
                    SknMaterialTextureResolverTests.ResolveTestTexturePath);

            internal static SknMaterialTextureMetadata ReadMetadata(
                BinTree binTree,
                Func<ulong, string> wadChunkPathResolver) =>
                SknResolver.ReadMetadata(binTree, wadChunkPathResolver);

            internal static bool IsReferencedSampler(SknMaterialSampler sampler) =>
                SknResolver.IsReferencedSampler(sampler);

            internal static string FindUnambiguousFallback(IEnumerable<string> availableTextureKeys) =>
                SknResolver.FindUnambiguousFallback(availableTextureKeys);

            internal static string TryResolveBinPath(string sknPath) =>
                SknResolver.TryResolveBinPath(sknPath);

            internal static string TryResolveTexturePath(string sknPath, string assetTexturePath) =>
                SknResolver.TryResolveTexturePath(sknPath, assetTexturePath);
        }
    }
}

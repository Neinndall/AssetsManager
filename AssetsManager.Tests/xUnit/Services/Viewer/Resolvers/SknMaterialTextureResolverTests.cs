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
        public void ReadMetadata_ParsesInitialHiddenSubmeshesLikeLtk()
        {
            var meshProperties = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(
                        Fnv1a.HashLower("initialSubmeshToHide"),
                        "Hair, Weapon   Cape\tExtra Hair")
                });
            var skin = new BinTreeObject(
                "Characters/Test/Skins/Skin0",
                "SkinCharacterDataProperties",
                new BinTreeProperty[] { meshProperties });
            var tree = new BinTree(new[] { skin }, Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(tree);
            SknMaterialTextureResolution resolution = SknResolver.Resolve(tree, Array.Empty<string>());

            Assert.Equal(new[] { "Hair", "Weapon", "Cape", "Extra", "Hair" }, metadata.InitialHiddenSubmeshes);
            Assert.Equal(metadata.InitialHiddenSubmeshes, resolution.InitialHiddenSubmeshes);
        }

        [Fact]
        public void ReadMetadata_AcceptsStringSkinTextureLikeAssetLocator()
        {
            const string texturePath = "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                texturePath,
                defaultTextureProperty: new BinTreeString(
                    Fnv1a.HashLower("texture"),
                    texturePath));

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(tree);
            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "test_tx_cm" });

            Assert.Equal(texturePath.Replace('\\', '/'), metadata.DefaultTexturePath, ignoreCase: true);
            Assert.Equal("test_tx_cm", resolution.DefaultMaterialDefinition.BaseTextureName);
        }

        [Fact]
        public void ReadMetadata_ZeroFileLinkMeansNoSkinTexture()
        {
            BinTree tree = CreateSkinTree(
                "unused.tex",
                defaultTextureProperty: new BinTreeWadChunkLink(
                    Fnv1a.HashLower("texture"),
                    0));

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(tree);

            Assert.Null(metadata.DefaultTexturePath);
        }

        [Fact]
        public void Resolve_UsesSkinMeshTextureAsDefaultWithoutStaticMaterials()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Belveth/Skins/Base/Belveth_Base_Main_TX.tex");

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "belvethloadscreen_0", "belveth_base_main_tx" });

            Assert.Equal("belveth_base_main_tx", resolution.DefaultMaterialDefinition.BaseTextureName);
            Assert.Empty(resolution.MaterialDefinitions);
        }

        [Fact]
        public void Resolve_PreservesAuthoredSkinScaleLikeLtkCharacter()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                skinScale: 1.25f);

            SknMaterialTextureResolution resolution = SknResolver.Resolve(tree, Array.Empty<string>());

            Assert.Equal(1.25f, resolution.SkinScale);
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

            Assert.Equal("aatrox_base_tx_cm", resolution.DefaultMaterialDefinition.BaseTextureName);
            Assert.Equal("aatrox_base_sword_tx_cm", resolution.ResolveMaterialDefinition("sword").BaseTextureName);
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
                resolution.ResolveMaterialDefinition("autumn_pixie").BaseTextureName);
        }

        [Fact]
        public void Resolve_DoesNotCollapseDistinctSubmeshNames()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    "R_Wing",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/R_Wing_TX_CM.tex")),
                materialOverride2: CreateOverride(
                    "RWing",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/RWing_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "r_wing_tx_cm", "rwing_tx_cm" },
                ResolveTestTexturePath);

            Assert.Equal("r_wing_tx_cm", resolution.ResolveMaterialDefinition("r_wing").BaseTextureName);
            Assert.Equal("rwing_tx_cm", resolution.ResolveMaterialDefinition("RWING").BaseTextureName);
        }

        [Fact]
        public void Resolve_AllowsEmptySubmeshOverrideForUnnamedLegacyRange()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    string.Empty,
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Unnamed_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "unnamed_tx_cm" },
                ResolveTestTexturePath);

            Assert.Equal("unnamed_tx_cm", resolution.ResolveMaterialDefinition(string.Empty).BaseTextureName);
        }

        [Fact]
        public void Resolve_DuplicateOverridesUseLastAvailableTexture()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    "Wing",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Wing_First_TX_CM.tex")),
                materialOverride2: CreateOverride(
                    "WING",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Wing_Last_TX_CM.tex")));

            SknMaterialTextureResolution bothAvailable = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "wing_first_tx_cm", "wing_last_tx_cm" },
                ResolveTestTexturePath);
            SknMaterialTextureResolution lastMissing = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "wing_first_tx_cm" },
                ResolveTestTexturePath);

            Assert.Equal("wing_last_tx_cm", bothAvailable.ResolveMaterialDefinition("wing").BaseTextureName);
            Assert.Equal("wing_first_tx_cm", lastMissing.ResolveMaterialDefinition("wing").BaseTextureName);
        }

        [Fact]
        public void Resolve_DuplicateOverridesKeepFirstOverridesMaterialChoice()
        {
            const string laterMaterialPath = "Characters/Test/Skins/Skin1/Materials/Later";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    "Wing",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Wing_TX_CM.tex")),
                materialOverride2: CreateOverride(
                    "wing",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower(laterMaterialPath))));
            tree.Objects[Fnv1a.HashLower(laterMaterialPath)] = CreateMaterial(
                laterMaterialPath,
                CreateSampler(
                    "Diffuse_Texture",
                    "ASSETS/Characters/Test/Skins/Skin1/Later_TX_CM.tex"));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "wing_tx_cm", "later_tx_cm" },
                ResolveTestTexturePath);

            ModelMaterialDefinition wing = resolution.ResolveMaterialDefinition("WING");
            Assert.Equal(ModelMaterialBindingKind.TextureOnly, wing.BindingKind);
            Assert.Equal("wing_tx_cm", wing.BaseTextureName);
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

            Assert.Equal("belveth_skin29_tx_cm", resolution.ResolveMaterialDefinition("head").BaseTextureName);
        }

        [Fact]
        public void ReadMetadata_ResolvesStaticMaterialFromDependencyTree()
        {
            const string defaultTexturePath =
                "ASSETS/Characters/Pyke/Skins/Skin45/Pyke_Skin45_TX_CM.tex";
            const string materialPath = "Characters/Pyke/Skins/Skin45/Materials/W_Blades";
            const string materialTexturePath =
                "ASSETS/Characters/Pyke/Skins/Skin45/Particles/Pyke_Skin45_Z_Material_Colors_03.tex";

            BinTree primaryTree = CreateSkinTree(
                defaultTexturePath,
                CreateOverride(
                    "W_Fins",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))));
            BinTree dependencyTree = new(
                new[]
                {
                    CreateMaterial(
                        materialPath,
                        CreateSampler("Diffuse_Texture", materialTexturePath))
                },
                Array.Empty<string>());

            SknMaterialTextureMetadata metadata =
                SknMaterialTextureResolver.ReadMetadata(new[] { primaryTree, dependencyTree });

            Assert.Contains("W_Fins", metadata.OverrideMaterials.Keys);
            Assert.Contains(
                materialTexturePath,
                metadata.ReferencedTexturePaths,
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReadMetadata_MaterialLinkReadsObjectAtHashRegardlessOfClass()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Variant";
            const string materialTexturePath =
                "ASSETS/Characters/Test/Skins/Skin1/Test_Variant_TX_CM.tex";
            BinTreeObject materialLikeObject = new(
                materialPath,
                "DerivedMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        new[] { CreateSampler("Diffuse_Texture", materialTexturePath) })
                });
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                material: materialLikeObject,
                defaultMaterialPath: materialPath);

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);

            Assert.NotNull(metadata.DefaultMaterial);
            Assert.Contains(
                materialTexturePath,
                metadata.ReferencedTexturePaths,
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReadMetadata_DoesNotResolveMaterialLinksFromShaderDocument()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Missing";
            BinTree skinTree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                defaultMaterialPath: materialPath);
            BinTree shaderTree = new(
                new[]
                {
                    CreateMaterial(
                        materialPath,
                        CreateSampler(
                            "Diffuse_Texture",
                            "ASSETS/Characters/Test/Skins/Skin1/Wrong_TX_CM.tex"))
                },
                Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(
                new[] { skinTree },
                new[] { shaderTree });

            Assert.True(metadata.HasDefaultMaterialLink);
            Assert.Null(metadata.DefaultMaterial);
        }

        [Fact]
        public void Resolve_DoesNotGuessSkinTextureWhenBinNamesNone()
        {
            var metadata = new SknMaterialTextureMetadata(
                null,
                null,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                metadata,
                new[] { "tempting_body_tx_cm" });

            Assert.Equal(ModelMaterialBindingKind.TextureOnly, resolution.DefaultMaterialDefinition.BindingKind);
            Assert.Null(resolution.DefaultMaterialDefinition.BaseTextureName);
        }

        [Fact]
        public void Resolve_DoesNotReplaceUnavailableAuthoredSkinTextureWithAnotherFile()
        {
            var metadata = new SknMaterialTextureMetadata(
                "ASSETS/Characters/Test/Skins/Skin1/Missing_TX_CM.tex",
                null,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                metadata,
                new[] { "different_body_tx_cm" });

            Assert.Null(resolution.DefaultMaterialDefinition.BaseTextureName);
        }


        [Fact]
        public void ReadMetadata_DependencySkinPropertiesDoNotOverridePrimarySkinBindings()
        {
            BinTree primaryTree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex");
            BinTree dependencyTree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin2/Dependency_TX_CM.tex",
                CreateOverride(
                    "Hat",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin2/Dependency_Hat_TX_CM.tex")));

            SknMaterialTextureMetadata metadata =
                SknMaterialTextureResolver.ReadMetadata(new[] { primaryTree, dependencyTree });

            Assert.Equal(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                metadata.DefaultTexturePath,
                ignoreCase: true);
            Assert.Empty(metadata.DirectOverrideTexturePaths);
            Assert.Empty(metadata.OverrideMaterialLinkKeys);
        }

        [Fact]
        public void ReadMetadata_TargetSknPathSelectsOnlyMatchingPrimarySkin()
        {
            const string wrongSkn = "ASSETS/Characters/Test/Skins/Skin1/Target.skn";
            const string targetSkn = "ASSETS/Characters/Test/Skins/Skin2/Target.skn";
            const string wrongTexture = "ASSETS/Characters/Test/Skins/Skin1/Wrong_TX_CM.tex";
            const string targetTexture = "ASSETS/Characters/Test/Skins/Skin2/Target_TX_CM.tex";

            var wrongMesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("simpleSkin"), wrongSkn),
                    CreateTextureLink("texture", wrongTexture),
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("materialOverride"),
                        BinPropertyType.Embedded,
                        new[]
                        {
                            CreateOverride(
                                "Hat",
                                CreateTextureLink(
                                    "texture",
                                    "ASSETS/Characters/Test/Skins/Skin1/Wrong_Hat_TX_CM.tex"))
                        })
                });
            var targetMesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("simpleSkin"), targetSkn),
                    CreateTextureLink("texture", targetTexture)
                });
            var primaryTree = new BinTree(
                new[]
                {
                    new BinTreeObject(
                        "Characters/Test/Skins/Skin1",
                        "SkinCharacterDataProperties",
                        new BinTreeProperty[] { wrongMesh }),
                    new BinTreeObject(
                        "Characters/Test/Skins/Skin2",
                        "SkinCharacterDataProperties",
                        new BinTreeProperty[] { targetMesh })
                },
                Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(
                new[] { primaryTree },
                ResolveTestTexturePath,
                null,
                $"C:/Extract/{targetSkn}");

            Assert.Equal(targetTexture, metadata.DefaultTexturePath, ignoreCase: true);
            Assert.Empty(metadata.DirectOverrideTexturePaths);
            Assert.Empty(metadata.OverrideMaterialLinkKeys);
        }

        [Fact]
        public void ReadMetadata_AmbiguousBasenameDoesNotMergeDifferentPrimarySkins()
        {
            var firstMesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(
                        Fnv1a.HashLower("simpleSkin"),
                        "ASSETS/Characters/Test/Skins/Skin1/Shared.skn"),
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/First_TX_CM.tex")
                });
            var secondMesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeString(
                        Fnv1a.HashLower("simpleSkin"),
                        "ASSETS/Characters/Test/Skins/Skin2/Shared.skn"),
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin2/Second_TX_CM.tex")
                });
            var tree = new BinTree(
                new[]
                {
                    new BinTreeObject(
                        "Characters/Test/Skins/Skin1",
                        "SkinCharacterDataProperties",
                        new BinTreeProperty[] { firstMesh }),
                    new BinTreeObject(
                        "Characters/Test/Skins/Skin2",
                        "SkinCharacterDataProperties",
                        new BinTreeProperty[] { secondMesh })
                },
                Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknResolver.ReadMetadata(
                new[] { tree },
                ResolveTestTexturePath,
                null,
                "C:/Extract/Unknown/Shared.skn");

            Assert.Null(metadata.DefaultTexturePath);
            Assert.Empty(metadata.DirectOverrideTexturePaths);
        }

        [Fact]
        public void Resolve_EmptyStaticMaterialExistsAndFallsBackToSkinTextureWhenOpaque()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Empty";
            var emptyMaterial = new BinTreeObject(
                materialPath,
                "StaticMaterialDef",
                Array.Empty<BinTreeProperty>());
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                material: emptyMaterial,
                defaultMaterialPath: materialPath);

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm" });

            Assert.Equal(ModelMaterialBindingKind.Authored, resolution.DefaultMaterialDefinition.BindingKind);
            Assert.Equal("test_tx_cm", resolution.DefaultMaterialDefinition.BaseTextureName);
        }

        [Fact]
        public void Resolve_FirstNonEmptyDuplicateOverrideWinsLikeLtkBindingOf()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Hat";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                CreateOverride(
                    "Hat",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/First_Hat_TX_CM.tex")),
                CreateMaterial(
                    materialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Second_Hat_TX_CM.tex")),
                materialOverride2: CreateOverride(
                    "hat",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm", "first_hat_tx_cm", "second_hat_tx_cm" });
            ModelMaterialDefinition hat = resolution.ResolveMaterialDefinition("hat");

            Assert.Equal(ModelMaterialBindingKind.TextureOnly, hat.BindingKind);
            Assert.Equal("first_hat_tx_cm", hat.BaseTextureName);
        }
        [Fact]
        public void Resolve_DoesNotSubstituteDefaultForMissingMaterialOverride()
        {
            const string defaultTexturePath =
                "ASSETS/Characters/Pyke/Skins/Skin45/Pyke_Skin45_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                defaultTexturePath,
                CreateOverride(
                    "W_Fins",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower(
                            "Characters/Pyke/Skins/Skin45/Materials/Missing"))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "pyke_skin45_tx_cm" });

            Assert.Contains("W_Fins", resolution.MaterialDefinitions.Keys);
            Assert.Equal(ModelMaterialBindingKind.Missing, resolution.ResolveMaterialDefinition("w_fins").BindingKind);
            Assert.Null(resolution.ResolveMaterialDefinition("W_FINS").BaseTextureName);
        }

        [Fact]
        public void ReadMetadata_PreservesEveryDeclaredSamplerTexture()
        {
            const string diffusePath = "ASSETS/Characters/Test/Skins/Skin1/Test_Diffuse.tex";
            const string maskPath = "ASSETS/Characters/Test/Skins/Skin1/Test_Mask.tex";
            const string customLayerPath = "ASSETS/Characters/Test/Skins/Skin1/Test_CustomLayer.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower("Characters/Test/Skins/Skin1/Materials/Body"))),
                CreateMaterial(
                    "Characters/Test/Skins/Skin1/Materials/Body",
                    CreateSampler("Diffuse_Texture", diffusePath),
                    CreateSampler("Mask_Texture_green", maskPath),
                    CreateSampler("CustomLayer_Input", customLayerPath)));

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);

            Assert.Contains(diffusePath, metadata.ReferencedTexturePaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(maskPath, metadata.ReferencedTexturePaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(customLayerPath, metadata.ReferencedTexturePaths, StringComparer.OrdinalIgnoreCase);
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

            Assert.Equal("aatrox_skin33_tx_cm", resolution.DefaultMaterialDefinition.BaseTextureName);
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

            // Submesh 0 (Body/Default) falls back to the default material texture.
            Assert.Equal("aatrox_skin33_tx_cm", resolution.DefaultMaterialDefinition.BaseTextureName);

            // Submesh 1 (Sword) resolves via MaterialOverride.
            Assert.True(resolution.MaterialDefinitions.ContainsKey("sword"));
            Assert.Equal("aatrox_skin33_sword_tx_cm", resolution.ResolveMaterialDefinition("sword").BaseTextureName);

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

            Assert.Equal("belveth_skin29_tx_cm", resolution.ResolveMaterialDefinition("head").BaseTextureName);
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
        public void Resolve_DoesNotPromoteLayerTex01AuxiliaryMaterialToBase(
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

            Assert.Null(resolution.ResolveMaterialDefinition(expectedMaterialKey).BaseTextureName);
            AssertContainsPath(texturePath, metadata.ReferencedTexturePaths);
            AssertContainsPath(
                "ASSETS/Characters/Shared/Overlay.tex",
                metadata.ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_PrioritizesExactDiffuseSamplerLikeLtk()
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

            Assert.Equal("belveth_skin29_mask_tx_cm", resolution.ResolveMaterialDefinition("creaturebody").BaseTextureName);
        }

        [Fact]
        public void Resolve_UsesLtkTintRgbWithoutTreatingTintAlphaAsOpacity()
        {
            const string materialPath = "Characters/Aurora/Skins/Base/Materials/Tinted";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_TX_CM.tex",
                CreateOverride(
                    "Base",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler(
                            "Diffuse_Texture",
                            "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_TX_CM.tex")
                    },
                    CreateParameter("TintColor", new Vector4(0.65f, 0.8f, 0.9f, 0.75f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "aurora_base_tx_cm" });

            ModelMaterialDefinition material = resolution.ResolveMaterialDefinition("base");
            Assert.Equal(ModelMaterialEffectKind.None, material.Effect.Kind);
            Assert.Equal(new Vector4(0.65f, 0.8f, 0.9f, 1f), material.Color);
            Assert.Equal(Vector4.One, material.Effect.MaterialTint);
            Assert.Equal(ModelMaterialBlendMode.Opaque, material.RenderState.Blending);
            Assert.False(material.UsesTextureAlpha);
            Assert.False(new ModelPart { MaterialDefinition = material }.IsAlphaBlended);
        }


        [Fact]
        public void Resolve_BuildsDefaultMaterialDefinitionFromStaticMaterial()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Body";
            const string texturePath = "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                texturePath,
                material: CreateMaterialWithParameters(
                    materialPath,
                    new[] { CreateSampler("Diffuse_Texture", texturePath) },
                    CreateParameter("TintColor", new Vector4(0.4f, 0.6f, 0.8f, 1f)),
                    CreateParameter("Opacity", new Vector4(0.65f, 0f, 0f, 0f))),
                defaultMaterialPath: materialPath);

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm" });

            ModelMaterialDefinition definition = resolution.DefaultMaterialDefinition;
            Assert.Equal("test_tx_cm", definition.BaseTextureName);
            Assert.Equal(new Vector4(0.4f, 0.6f, 0.8f, 0.65f), definition.Color);
            Assert.Equal(ModelMaterialBlendMode.Opaque, definition.RenderState.Blending);
            Assert.Same(definition, resolution.ResolveMaterialDefinition("unoverriddenpart"));
        }

        [Fact]
        public void Resolve_BuildsDirectTextureOverrideWithoutInheritingSkinDefault()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                CreateOverride(
                    "Accessory",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Test_Accessory_TX_CM.tex")));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm", "test_accessory_tx_cm" });

            ModelMaterialDefinition definition = resolution.ResolveMaterialDefinition("accessory");
            Assert.Equal("test_accessory_tx_cm", definition.BaseTextureName);
            Assert.Equal(ModelMaterialBlendMode.Opaque, definition.RenderState.Blending);
            Assert.Equal("test_tx_cm", resolution.ResolveMaterialDefinition("body").BaseTextureName);
        }

        [Fact]
        public void Resolve_TextureOnlyOverrideDoesNotInheritSkinMaterial()
        {
            const string defaultMaterialPath = "Characters/Test/Skins/Skin1/Materials/Body";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    "Cape",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Cape_TX_CM.tex")),
                CreateMaterial(
                    defaultMaterialPath,
                    CreateSampler(
                        "Diffuse_Texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Material_TX_CM.tex")),
                defaultMaterialPath: defaultMaterialPath);

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "cape_tx_cm", "material_tx_cm" },
                ResolveTestTexturePath);

            ModelMaterialDefinition cape = resolution.ResolveMaterialDefinition("cape");
            Assert.Equal(ModelMaterialBindingKind.TextureOnly, cape.BindingKind);
            Assert.Equal("cape_tx_cm", cape.BaseTextureName);
            Assert.Equal("material_tx_cm", resolution.ResolveMaterialDefinition("body").BaseTextureName);
        }

        [Fact]
        public void Resolve_MaterialOverrideUsesItsTextureAsOpaqueFallbackWhenItHasNoBase()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/Cape";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Base_TX_CM.tex",
                CreateOverride(
                    "Cape",
                    CreateTextureLink(
                        "texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Cape_TX_CM.tex"),
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower(materialPath))),
                CreateMaterial(materialPath));

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                tree,
                new[] { "base_tx_cm", "cape_tx_cm" },
                ResolveTestTexturePath);

            ModelMaterialDefinition cape = resolution.ResolveMaterialDefinition("CAPE");
            Assert.Equal(ModelMaterialBindingKind.Authored, cape.BindingKind);
            Assert.Equal("cape_tx_cm", cape.BaseTextureName);
        }

        [Fact]
        public void Resolve_MissingLinkedOverrideKeepsMaterialBaseEmpty()
        {
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                CreateOverride(
                    "Missing",
                    new BinTreeObjectLink(
                        Fnv1a.HashLower("Material"),
                        Fnv1a.HashLower("Characters/Test/Skins/Skin1/Materials/DoesNotExist"))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm" });

            Assert.Null(resolution.ResolveMaterialDefinition("missing").BaseTextureName);
            Assert.Equal("test_tx_cm", resolution.ResolveMaterialDefinition("body").BaseTextureName);
        }

        [Fact]
        public void GetSelectableTextureCandidatesIncludesAuxiliaryTexturesWithoutPresentationMaps()
        {
            IReadOnlyList<string> candidates =
                SknMaterialTextureResolver.GetSelectableTextureCandidates(
                    new[]
                    {
                        "pyke_skin45_tx_cm",
                        "pyke_skin45_empoweredform_tx_cm",
                        "pyke_skin45_emote_trail_mult",
                        "pyke_skin45_generic_noise",
                        "pykeloadscreen_45",
                        "pykeloadscreen_45_le"
                    });

            Assert.Equal(4, candidates.Count);
            Assert.Contains("pyke_skin45_tx_cm", candidates);
            Assert.Contains("pyke_skin45_empoweredform_tx_cm", candidates);
            Assert.Contains("pyke_skin45_emote_trail_mult", candidates);
            Assert.Contains("pyke_skin45_generic_noise", candidates);
            Assert.DoesNotContain("pykeloadscreen_45", candidates);
            Assert.DoesNotContain("pykeloadscreen_45_le", candidates);
        }

        [Fact]
        public void GetSelectableTextureCandidatesIncludesResolvedPrimaryMaterialTextures()
        {
            var resolution = new SknMaterialTextureResolution(
                ModelMaterialDefinition.TextureOnly("pyke_skin01_tx_cm"),
                new Dictionary<string, ModelMaterialDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pykeskin01scrollmat"] = ModelMaterialDefinition.TextureOnly("pyke_base_scroll_tx_cm")
                });

            IReadOnlyList<string> candidates =
                SknResolver.GetSelectableTextureCandidates(
                    new[] { "pyke_skin01_tx_cm", "pykeloadscreen_1" },
                    resolution);

            Assert.Contains("pyke_skin01_tx_cm", candidates);
            Assert.Contains("pyke_base_scroll_tx_cm", candidates);
            Assert.DoesNotContain("pykeloadscreen_1", candidates);
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
        public void TryResolveShaderBinPath_UsesNamedOrHashedShaderDefinitions(bool hashed)
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-shaders-{Guid.NewGuid():N}");
            string sknPath = Path.Combine(
                root,
                "assets",
                "characters",
                "test",
                "skins",
                "skin0",
                "test.skn");
            string shaderPath = hashed
                ? Path.Combine(root, $"{XxHash64Ext.Hash("data/shaders/shaders.bin"):x16}.bin")
                : Path.Combine(root, "data", "shaders", "shaders.bin");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(shaderPath, Array.Empty<byte>());

                Assert.Equal(
                    shaderPath,
                    SknResolver.TryResolveShaderBinPath(sknPath),
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
        public void TryResolveBinPath_UsesCompanionSkinBinReferencingWadChunkLinkModel()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-companion-link-{Guid.NewGuid():N}");
            string characterRoot = Path.Combine(root, "assets", "characters", "petstyletwoaphelios");
            string sknPath = Path.Combine(
                characterRoot,
                "themes",
                "spiritblossomsprings",
                "tier1",
                "petstyletwoaphelios_spiritblossomsprings_tier1.skn");
            string skinBinPath = Path.Combine(characterRoot, "skins", "skin2.bin");
            string virtualSknPath =
                "assets/characters/petstyletwoaphelios/themes/spiritblossomsprings/tier1/" +
                "petstyletwoaphelios_spiritblossomsprings_tier1.skn";
            ulong sknHash = XxHash64Ext.Hash(virtualSknPath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(skinBinPath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());

                BinTree tree = CreateSkinTree(
                    "assets/characters/petstyletwoaphelios/themes/spiritblossomsprings/tier1/petstyletwoaphelios_spiritblossomsprings_tier1_tx_cm.tex",
                    simpleSkinProperty: new BinTreeWadChunkLink(Fnv1a.HashLower("simpleSkin"), sknHash));
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
        public void MatchTextureKey_ResolvesWadChunkHashAgainstAvailableKeys()
        {
            var availableKeys = new[]
            {
                "petchibizoe_base_tx_cm",
                "petchibizoe_base_face_tx_cm",
                "petchibizoe_base_hair_tx_cm",
                "petchibizoe_base_tool_tx_cm",
                "petchibizoe_base_speedline_tx_cm"
            };

            ulong bodyHash = XxHash64Ext.Hash("assets/characters/petchibizoe/themes/base/petchibizoe_base_tx_cm.tex");
            ulong faceHash = XxHash64Ext.Hash("assets/characters/petchibizoe/themes/base/petchibizoe_base_face_tx_cm.tex");
            ulong hairHash = XxHash64Ext.Hash("assets/characters/petchibizoe/themes/base/petchibizoe_base_hair_tx_cm.tex");

            Assert.Equal("petchibizoe_base_tx_cm", SknResolver.MatchTextureKey($"{bodyHash:x16}", availableKeys));
            Assert.Equal("petchibizoe_base_face_tx_cm", SknResolver.MatchTextureKey($"{faceHash:x16}", availableKeys));
            Assert.Equal("petchibizoe_base_hair_tx_cm", SknResolver.MatchTextureKey($"{hairHash:x16}", availableKeys));
        }

        [Fact]
        public void MatchTextureKey_PreservesFullPathDictionaryKeyWhenBasenameMatches()
        {
            const string authored = "ASSETS/Characters/Turret/Skins/Base/Turret_Base_TX_CM.tex";
            const string available = "assets/characters/turret/skins/base/turret_base_tx_cm.tex";

            Assert.Equal(
                available,
                SknResolver.MatchTextureKey(authored, new[] { available }),
                ignoreCase: true);
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

        [Fact]
        public void TryResolveTexturePath_FindsUnhashedChunkAtWadRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-wad-root-{Guid.NewGuid():N}");
            string sknPath = Path.Combine(
                root,
                "assets",
                "characters",
                "janna",
                "skins",
                "skin67",
                "janna_skin67.skn");
            string chunkTexturePath = Path.Combine(root, "cd174f650ce9caee.tex");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(chunkTexturePath, Array.Empty<byte>());

                Assert.Equal(
                    chunkTexturePath,
                    SknMaterialTextureResolver.TryResolveTexturePath(
                        sknPath,
                        "cd174f650ce9caee"),
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
        public void TryResolveTexturePath_FindsUnresolvedChunkBesideFlatHashNamedSkn()
        {
            string root = Path.Combine(Path.GetTempPath(), $"janna-{Guid.NewGuid():N}.wad.client");
            string sknPath = Path.Combine(root, "0123456789abcdef.skn");
            string chunkTexturePath = Path.Combine(root, "cd174f650ce9caee.tex");

            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(chunkTexturePath, Array.Empty<byte>());

                Assert.Equal(
                    chunkTexturePath,
                    SknMaterialTextureResolver.TryResolveTexturePath(
                        sknPath,
                        "cd174f650ce9caee"),
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
        public void TryResolveTexturePath_RehashesResolvedVirtualPathForHashNamedWadRootFile()
        {
            string root = Path.Combine(Path.GetTempPath(), $"assetsmanager-wad-root-{Guid.NewGuid():N}");
            string sknPath = Path.Combine(
                root,
                "assets",
                "characters",
                "janna",
                "skins",
                "skin67",
                "janna_skin67.skn");
            const string virtualTexturePath =
                "assets/characters/janna/skins/skin67/janna_skin67_tx_cm.tex";
            string chunkTexturePath = Path.Combine(
                root,
                $"{XxHash64Ext.Hash(virtualTexturePath):x16}.tex");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sknPath)!);
                File.WriteAllBytes(sknPath, Array.Empty<byte>());
                File.WriteAllBytes(chunkTexturePath, Array.Empty<byte>());

                Assert.Equal(
                    chunkTexturePath,
                    SknMaterialTextureResolver.TryResolveTexturePath(
                        sknPath,
                        virtualTexturePath),
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
            BinTreeProperty defaultTextureProperty = null,
            BinTreeProperty simpleSkinProperty = null,
            float? skinScale = null)
        {
            var meshPropertyList = new System.Collections.Generic.List<BinTreeProperty>
            {
                defaultTextureProperty ?? CreateTextureLink("texture", defaultTexturePath)
            };
            if (simpleSkinProperty != null)
            {
                meshPropertyList.Add(simpleSkinProperty);
            }
            else if (!string.IsNullOrWhiteSpace(simpleSkinPath))
            {
                meshPropertyList.Add(new BinTreeString(Fnv1a.HashLower("simpleSkin"), simpleSkinPath));
            }
            if (skinScale.HasValue)
            {
                meshPropertyList.Add(new BinTreeF32(Fnv1a.HashLower("skinScale"), skinScale.Value));
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

        private static BinTreeObject CreateMaterialWithParametersAndShader(
            string path,
            string shaderPath,
            BinTreeEmbedded[] samplers,
            params BinTreeEmbedded[] parameters)
        {
            var properties = new List<BinTreeProperty>
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
                                                new BinTreeObjectLink(
                                                    Fnv1a.HashLower("shader"),
                                                    Fnv1a.HashLower(shaderPath))
                                            })
                                    })
                            })
                    })
            };

            return new BinTreeObject(path, "StaticMaterialDef", properties);
        }

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

            internal static SknMaterialTextureMetadata ReadMetadata(IEnumerable<BinTree> binTrees) =>
                SknResolver.ReadMetadata(
                    binTrees,
                    SknMaterialTextureResolverTests.ResolveTestTexturePath);

            internal static IReadOnlyList<string> GetSelectableTextureCandidates(
                IEnumerable<string> textureKeys) =>
                SknResolver.GetSelectableTextureCandidates(textureKeys);

            internal static string TryResolveBinPath(string sknPath) =>
                SknResolver.TryResolveBinPath(sknPath);

            internal static string TryResolveTexturePath(string sknPath, string assetTexturePath) =>
                SknResolver.TryResolveTexturePath(sknPath, assetTexturePath);
        }
    }
}

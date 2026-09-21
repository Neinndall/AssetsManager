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
        public void Resolve_EffectLayersConsumeShaderDefaultSamplersAndParameters()
        {
            const uint shaderHash = 0x12345678;
            var material = new SknMaterialDefinition(
                Array.Empty<SknMaterialSampler>(),
                new Dictionary<string, Vector4>(StringComparer.Ordinal))
            {
                ShaderHash = shaderHash
            };
            var shader = new SknShaderDefinition(
                "Shaders/SkinnedMesh/TestEmission",
                new[]
                {
                    new SknMaterialSampler(
                        "Emission_Texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Test_Emission.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.Ordinal)
                {
                    ["EmissionColor"] = new Vector4(1f, 0.5f, 0.25f, 1f),
                    ["EmissionStrength"] = new Vector4(1.5f, 0f, 0f, 0f)
                },
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
            var metadata = new SknMaterialTextureMetadata(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                material,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase))
            {
                HasDefaultMaterialLink = true,
                ShaderDefinitions = new Dictionary<uint, SknShaderDefinition>
                {
                    [shaderHash] = shader
                }
            };

            SknMaterialTextureResolution resolution = SknResolver.Resolve(
                metadata,
                new[] { "test_tx_cm", "test_emission" });

            Assert.True((resolution.DefaultMaterialDefinition.Effect.Kind & ModelMaterialEffectKind.Emission) != 0);
            Assert.Equal("test_emission", resolution.DefaultMaterialDefinition.Effect.Emission.TextureName);
            Assert.Equal(1.5f, resolution.DefaultMaterialDefinition.Effect.Emission.Strength);
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

            Assert.Equal(
                ModelMaterialEffectKind.None,
                resolution.ResolveMaterialDefinition("base").Effect.Kind);
            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("hat").Effect;
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, effect.Kind);
            Assert.Equal("aurora_base_mat_tile01", effect.AdditiveScroll.TextureName);
            Assert.Equal("aurora_base_mat_hatmask", effect.AdditiveScroll.MaskTextureName);
            Assert.Equal(new Vector2(-0.1f, 0.1f), effect.AdditiveScroll.ScrollSpeed);
            Assert.Equal(new Vector2(3f, 2f), effect.AdditiveScroll.Tiling);
            Assert.Equal(new Vector4(0.18f, 0.67f, 1f, 0f), effect.AdditiveScroll.Color);

            ModelMaterialDefinition materialDefinition = resolution.ResolveMaterialDefinition("hat");
            Assert.Equal("aurora_base_tx_cm", materialDefinition.BaseTextureName);
            Assert.Equal(ModelMaterialBlendMode.Opaque, materialDefinition.RenderState.Blending);
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, materialDefinition.Effect.Kind);
            Assert.Equal("aurora_base_mat_tile01", materialDefinition.Effect.AdditiveScroll.TextureName);
            AssertContainsPath(
                "ASSETS/Characters/Aurora/Skins/Base/Aurora_Base_Mat_HatMask.tex",
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_PreservesPackedAdditiveGreenAndAlphaChannels()
        {
            const string materialPath = "Characters/Test/Skins/Skin1/Materials/PackedScroll";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex",
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", "ASSETS/Characters/Test/Skins/Skin1/Test_TX_CM.tex"),
                        CreateSampler("AdditiveScrollTex", "ASSETS/Characters/Test/Skins/Skin1/Packed.tex"),
                        CreateSampler("AdditiveScroll_Mask", "ASSETS/Characters/Test/Skins/Skin1/PackedMask.tex")
                    },
                    CreateParameter("AdditiveTexTile", new Vector4(3f, 2f, 0f, 0f)),
                    CreateParameter("AdditiveStrength_R", Vector4.Zero),
                    CreateParameter("AdditiveTexScrollSpeed_R", Vector4.Zero),
                    CreateParameter("AdditiveStrength_G", new Vector4(0.675f, 0f, 0f, 0f)),
                    CreateParameter("AdditiveTexScrollSpeed_G", new Vector4(0f, -0.2f, 0f, 0f)),
                    CreateParameter("AdditiveScroll_ColorTint_G", new Vector4(0.9f, 0.88f, 0.89f, 1f)),
                    CreateParameter("AdditiveStrength_A", new Vector4(1.5f, 0f, 0f, 0f)),
                    CreateParameter("AdditiveTexScrollSpeed_A", new Vector4(0.1f, 0.05f, 0f, 0f)),
                    CreateParameter("AdditiveScroll_ColorTint_A", new Vector4(0.88f, 0.17f, 0.8f, 1f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm", "packed", "packedmask" });

            ModelTextureLayerDefinition additive = resolution.ResolveMaterialDefinition("body").Effect.AdditiveScroll;
            Assert.NotNull(additive);
            Assert.Equal(0f, additive.Strength);
            Assert.NotNull(additive.GreenChannel);
            Assert.Equal(1, additive.GreenChannel.TextureChannel);
            Assert.Equal(0.675f, additive.GreenChannel.Strength);
            Assert.Equal(new Vector2(0f, -0.2f), additive.GreenChannel.ScrollSpeed);
            Assert.Equal(new Vector4(0.9f, 0.88f, 0.89f, 1f), additive.GreenChannel.Color);
            Assert.NotNull(additive.AlphaChannel);
            Assert.Equal(3, additive.AlphaChannel.TextureChannel);
            Assert.Equal(1.5f, additive.AlphaChannel.Strength);
            Assert.Equal(new Vector2(0.1f, 0.05f), additive.AlphaChannel.ScrollSpeed);
            Assert.Equal(new Vector4(0.88f, 0.17f, 0.8f, 1f), additive.AlphaChannel.Color);
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
        public void Resolve_IgnoresUntintedStaticWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(includeAdditiveTint: false),
                SeraphineBodyTextureKeys);

            Assert.Equal("seraphine_skin69_body_tx_cm", resolution.ResolveMaterialDefinition("body").BaseTextureName);
            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.False((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Iridescence) != 0);
            Assert.Equal("seraphine_skin69_cloth_iridescent", effect.Iridescence.LutTextureName);
            Assert.Equal("seraphine_skin69_cloth_tx_cm_mask", effect.Iridescence.MaskTextureName);
            Assert.Equal(new Vector4(1.1f, 1f, 3f, 0f), effect.Iridescence.Control);
            Assert.Equal(new Vector2(1f, 0f), effect.Iridescence.PulseSpeedMin);
            Assert.Equal(new Vector2(0f, 1f), effect.Iridescence.FresnelAlphaMinMax);
            Assert.True(effect.Iridescence.UsesPulse);
            Assert.True(effect.Iridescence.UsesLocalizedAlpha);
            Assert.True(effect.Iridescence.RequiresAlphaBlend);
            Assert.True(effect.RequiresAlphaBlend);
            ModelMaterialDefinition body = resolution.ResolveMaterialDefinition("body");
            Assert.Equal(ModelMaterialBlendMode.Opaque, body.RenderState.Blending);
            Assert.False(body.UsesTextureAlpha);
            Assert.True(new ModelPart { MaterialDefinition = body }.IsAlphaBlended);
        }

        [Fact]
        public void Resolve_PreservesTintedWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(includeAdditiveTint: true),
                SeraphineBodyTextureKeys);

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
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

            Assert.True((resolution.ResolveMaterialDefinition("body").Effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
        }

        [Fact]
        public void Resolve_IgnoresZeroSpeedWhiteAdditiveSource()
        {
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                CreateSeraphineIridescentBodyTree(
                    includeAdditiveTint: false,
                    includeZeroAdditiveSpeed: true),
                SeraphineBodyTextureKeys);

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.False(effect.Iridescence.UsesPulse);
            Assert.False(effect.Iridescence.UsesLocalizedAlpha);
            Assert.False(effect.RequiresAlphaBlend);
        }

        [Fact]
        public void IridescenceRequiresAlphaBlendForAuthoredFresnelMaskWithoutSwitches()
        {
            var iridescence = new ModelIridescenceDefinition(
                "iridescent-lut",
                "alpha-mask",
                Vector4.One,
                Vector2.Zero,
                new Vector2(0.9f, 0.9f),
                1f,
                UsesPulse: false,
                UsesLocalizedAlpha: false);

            Assert.True(iridescence.RequiresAlphaBlend);
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
        public void Resolve_PreservesAuthoredMaskForIridescenceWhenTextureUnresolved()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(
                        "iridescentTex",
                        "ASSETS/Shared/Materials/default_gradient.tex"),
                    new SknMaterialSampler(
                        "Mask",
                        "cd174f650ce9caee")
                },
                new Dictionary<string, Vector4>());

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "default_gradient" },
                new[] { "Body" });

            Assert.Equal("cd174f650ce9caee", effect.Iridescence.MaskTextureName);
        }

        [Theory]
        [InlineData("FresnelMask")]
        [InlineData("Fresnel_Mask")]
        [InlineData("FresnelMask_Texture")]
        public void Resolve_BlackDedicatedFresnelMaskDisablesTheLayer(string maskSamplerName)
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(maskSamplerName, "ASSETS/Shared/Materials/black.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FresnelIntensity"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                Array.Empty<string>(),
                new[] { "Body" });

            Assert.Null(effect.Fresnel);
            Assert.False((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
        }

        [Fact]
        public void Resolve_FresnelMaskChannelFollowsSelectedSamplerWhenTextureIsShared()
        {
            const string packedPath = "ASSETS/Test/packed_mask.tex";
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("Mask_Texture_green", packedPath),
                    new SknMaterialSampler("Mask_Texture_red", packedPath)
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FresnelIntensity"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "packed_mask" },
                new[] { "Body" });

            Assert.Equal("packed_mask", effect.Fresnel.MaskTextureName);
            Assert.Equal(0, effect.Fresnel.MaskChannel);
        }

        [Fact]
        public void Resolve_IridescenceMaskChannelFollowsSelectedSamplerWhenTextureIsShared()
        {
            const string packedPath = "ASSETS/Test/packed_mask.tex";
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("iridescentTex", "ASSETS/Test/iridescence.tex"),
                    new SknMaterialSampler("Mask_Texture_green", packedPath),
                    new SknMaterialSampler("Mask_Texture_red", packedPath)
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase));

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "iridescence", "packed_mask" },
                new[] { "Body" });

            Assert.Equal("packed_mask", effect.Iridescence.MaskTextureName);
            Assert.Equal(0, effect.Iridescence.MaskChannel);
        }

        [Theory]
        [InlineData("Iridescence_Mask")]
        [InlineData("AdditiveScroll_Mask")]
        public void Resolve_BlackDedicatedIridescenceMaskDisablesTheLayer(string maskSamplerName)
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("iridescentTex", "ASSETS/Test/iridescence.tex"),
                    new SknMaterialSampler(maskSamplerName, "ASSETS/Shared/Materials/black.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase));

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "iridescence" },
                new[] { "Body" });

            Assert.Null(effect.Iridescence);
            Assert.False((effect.Kind & ModelMaterialEffectKind.Iridescence) != 0);
        }

        [Theory]
        [InlineData("black", false)]
        [InlineData("white", true)]
        public void Resolve_HonorsAuthoredFresnelMaskSemantics(string maskName, bool expectFresnel)
        {
            const string materialPath = "Characters/Test/Skins/Base/Materials/Fresnel";
            const string diffusePath = "ASSETS/Characters/Test/Skins/Base/Test_TX_CM.tex";
            string maskPath = $"ASSETS/Shared/Materials/{maskName}.tex";
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
                        CreateSampler("Mask_Texture", maskPath)
                    },
                    CreateParameter("FresnelIntensity", Vector4.One)));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm", maskName });

            if (!expectFresnel)
            {
                Assert.Equal(
                    ModelMaterialEffectKind.None,
                    resolution.ResolveMaterialDefinition("body").Effect.Kind);
                return;
            }

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.True((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
            Assert.Equal(maskName, effect.Fresnel.MaskTextureName);
        }

        [Fact]
        public void Resolve_UsesWhiteFresnelWhenMaskIsAbsent()
        {
            const string materialPath = "Characters/Test/Skins/Base/Materials/Fresnel";
            const string diffusePath = "ASSETS/Characters/Test/Skins/Base/Test_TX_CM.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "Body",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    new[] { CreateSampler("Diffuse_Texture", diffusePath) },
                    CreateParameter("FresnelIntensity", Vector4.One)));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm" });

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.True((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
            Assert.Null(effect.Fresnel.MaskTextureName);
        }

        [Fact]
        public void Resolve_PreservesExistingEffectMaskWhenFresnelMaskIsBlack()
        {
            const string materialPath = "Characters/Test/Skins/Base/Materials/Fresnel";
            const string diffusePath = "ASSETS/Characters/Test/Skins/Base/Test_TX_CM.tex";
            const string scrollPath = "ASSETS/Characters/Test/Skins/Base/Test_Scroll.tex";
            const string scrollMaskPath = "ASSETS/Characters/Test/Skins/Base/Test_Scroll_Mask.tex";
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
                        CreateSampler("Mask_Texture", "ASSETS/Shared/Materials/black.tex"),
                        CreateSampler("AdditiveScrollTex", scrollPath),
                        CreateSampler("AdditiveScroll_Mask", scrollMaskPath)
                    },
                    CreateParameter("ScrollSpeed", new Vector4(0.2f, -0.1f, 0f, 0f)),
                    CreateParameter("ScrollTexTile", Vector4.One),
                    CreateParameter("FresnelIntensity", Vector4.One)));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "test_tx_cm", "test_scroll", "test_scroll_mask" });

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.True((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
            Assert.Equal("test_scroll_mask", effect.Fresnel.MaskTextureName);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("hair").Effect;
            Assert.Equal(
                ModelMaterialEffectKind.Fresnel |
                ModelMaterialEffectKind.Bloom |
                ModelMaterialEffectKind.FresnelNoise |
                ModelMaterialEffectKind.AnimatedWave,
                effect.Kind);
            Assert.Equal("brand_skin53_hairalpha_tx_cm", effect.Fresnel.MaskTextureName);
            Assert.Equal(3f, effect.Fresnel.Power);
            Assert.Equal(0.8f, effect.Fresnel.Strength);
            Assert.Equal(new Vector4(1f, 0.2f, 0.05f, 1f), effect.Bloom.Color);
            Assert.Equal(0.6f, effect.Bloom.Intensity);
            Assert.Equal(new Vector3(50f, 40f, 30f), effect.Wave.Direction);
            Assert.Equal(0.8f, effect.Wave.Speed);
            Assert.Equal(0.7f, effect.Wave.Frequency);
            Assert.Equal(0.15f, effect.Wave.Intensity);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.Fresnel.NoiseSpeed);
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

            Assert.Equal(
                ModelMaterialEffectKind.None,
                resolution.ResolveMaterialDefinition("wings").Effect.Kind);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.Equal(ModelMaterialEffectKind.GradientPulse, effect.Kind);
            Assert.Equal("gradient_test_01", effect.GradientPulse.TextureName);
            Assert.Equal("aatrox_base_r_body_mask", effect.GradientPulse.MaskTextureName);
            Assert.Equal(3f, effect.GradientPulse.PulseRate);
            Assert.Equal(0.4f, effect.GradientPulse.PulseMax);
            Assert.Equal(0.3f, effect.GradientPulse.PulseOffset);
            Assert.Equal(0.5f, effect.GradientPulse.Sharpness);
            Assert.Equal(10f, effect.GradientPulse.BloomIntensity);
            Assert.Equal(-0.2f, effect.GradientPulse.MaskThreshold);
            Assert.Equal(0.075f, effect.GradientPulse.MaskSoftness);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("wings").Effect;
            Assert.Equal(ModelMaterialEffectKind.GradientPulse, effect.Kind);
            Assert.Equal(new Vector2(-0.5f, -0.5f), effect.GradientPulse.ScrollSpeed);
            Assert.Equal(0.785f, effect.GradientPulse.MaskThreshold);
            Assert.Equal(0.25f, effect.GradientPulse.MaskSoftness);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, effect.Kind);
            Assert.Equal("lux_scroll", effect.AdditiveScroll.TextureName);
            Assert.Equal("lux_scroll_mask", effect.AdditiveScroll.MaskTextureName);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.AdditiveScroll.ScrollSpeed);
            Assert.Equal(new Vector2(2f, 3f), effect.AdditiveScroll.Tiling);
        }

        [Fact]
        public void Resolve_RecognizesPanningMaterialSamplerWithoutAuthoredMask()
        {
            const string materialPath = "Characters/Pyke/Skins/Skin45/Materials/W_Blades";
            const string diffusePath =
                "ASSETS/Characters/Pyke/Skins/Skin45/Particles/Pyke_Skin45_Z_Material_Colors_03.tex";
            const string panningPath =
                "ASSETS/Characters/Pyke/Skins/Skin45/Particles/Pyke_Skin45_Z_Material_Flames_03.tex";
            BinTree tree = CreateSkinTree(
                diffusePath,
                CreateOverride(
                    "W_Fins",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParametersAndShader(
                    materialPath,
                    "Shaders/SkinnedMesh/ScrollingMaskedDiffuseBloom",
                    new[]
                    {
                        CreateSampler("Diffuse_Texture", diffusePath),
                        CreateSampler("Panning_Texture", panningPath)
                    },
                    CreateParameter("Panning_Scale", new Vector4(1f, 1f, 0f, 0f)),
                    CreateParameter("Panning_Speed", new Vector4(0f, 0.7f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "pyke_skin45_z_material_colors_03", "pyke_skin45_z_material_flames_03" });

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("w_fins").Effect;
            Assert.Equal(ModelMaterialEffectKind.AdditiveScroll, effect.Kind);
            Assert.Equal("pyke_skin45_z_material_flames_03", effect.AdditiveScroll.TextureName);
            Assert.Null(effect.AdditiveScroll.MaskTextureName);
            Assert.Equal(new Vector2(0f, 0.7f), effect.AdditiveScroll.ScrollSpeed);
            Assert.Equal(Vector2.One, effect.AdditiveScroll.Tiling);
            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);
            Assert.Equal(
                Fnv1a.HashLower("Shaders/SkinnedMesh/ScrollingMaskedDiffuseBloom"),
                metadata.OverrideMaterials["W_Fins"].ShaderHash);
            AssertContainsPath(panningPath, metadata.ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_DoesNotInferPanningBlendWithoutShaderIdentity()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(
                        "Panning_Texture",
                        "ASSETS/Characters/Test/Skins/Skin1/Panning.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Panning_Speed"] = new(0f, 1f, 0f, 0f),
                    ["Panning_Scale"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "body",
                new[] { "panning" },
                new[] { "body" });

            Assert.Equal(ModelMaterialEffectKind.None, effect.Kind);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.Equal(ModelMaterialEffectKind.Dissolve, effect.Kind);
            Assert.Equal("dissolve_texture", effect.Dissolve.PatternTextureName);
            Assert.Equal(0.35f, effect.Dissolve.Threshold);
            Assert.Equal(0.2f, effect.Dissolve.Softness);
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

            Assert.Equal("gwen_base_main_tx_cm", resolution.ResolveMaterialDefinition("body").BaseTextureName);
            Assert.Equal(
                ModelMaterialEffectKind.None,
                resolution.ResolveMaterialDefinition("body").Effect.Kind);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("sword").Effect;
            Assert.True((effect.Kind & ModelMaterialEffectKind.Emission) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Distortion) != 0);
            Assert.False((effect.Kind & ModelMaterialEffectKind.Bloom) != 0);
            Assert.Equal("aatrox_skin37_sword_distortion", effect.Emission.TextureName);
            Assert.Equal("aatrox_skin37_sword_emissionmask", effect.Emission.MaskTextureName);
            Assert.Equal(0, effect.Emission.TextureChannel);
            Assert.Equal(new Vector2(15f, 3f), effect.Emission.Tiling);
            Assert.Equal(new Vector2(0f, -2f), effect.Emission.ScrollSpeed);
            Assert.Equal(1.25f, effect.Emission.Strength);
            Assert.Equal(new Vector4(1f, 0.63f, 0f, 1f), effect.Emission.Color);
            Assert.Equal("aatrox_skin37_sword_distortion", effect.Distortion.TextureName);
            Assert.Equal(1, effect.Distortion.ChannelX);
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

            Assert.Equal(
                ModelMaterialEffectKind.None,
                resolution.ResolveMaterialDefinition("sword").Effect.Kind);
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

            Assert.Equal("gun25_gold_tx_cm", resolution.ResolveMaterialDefinition("cgun25").BaseTextureName);
            Assert.Equal(
                ModelMaterialEffectKind.None,
                resolution.ResolveMaterialDefinition("cgun25").Effect.Kind);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("ult").Effect;
            Assert.Equal(ModelMaterialEffectKind.Bloom, effect.Kind);
            Assert.Equal("belveth_skin29_ult_bloommask_tx_cm", effect.Bloom.MaskTextureName);
            Assert.Equal(5f, effect.Bloom.Intensity);
            Assert.Equal(0, effect.Bloom.MaskChannel);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("armor").Effect;
            Assert.Equal(ModelMaterialEffectKind.Dissolve, effect.Kind);
            Assert.Equal("belveth_transition_noise", effect.Dissolve.PatternTextureName);
            Assert.Equal("belveth_transition_state", effect.Dissolve.StateTextureName);
            Assert.Null(effect.Dissolve.MaskTextureName);
            Assert.Equal(new Vector2(0.1f, -0.2f), effect.Dissolve.ScrollSpeed);
            Assert.Equal(0.35f, effect.Dissolve.Threshold);
            Assert.Equal(0.08f, effect.Dissolve.Softness);
            AssertContainsPath(
                "ASSETS/Characters/Belveth/Skins/Skin29/Belveth_Transition_Noise.tex",
                SknMaterialTextureResolver.ReadMetadata(tree).ReferencedTexturePaths);
        }

        [Fact]
        public void Resolve_UsesAuthoredTransitionValueWidthAndPatternTillingAliases()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("Transition_PatternTexture", "ASSETS/Test/transition_pattern.tex"),
                    new SknMaterialSampler("Transition_State2", "ASSETS/Test/transition_state.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Transition_Value"] = new(0.65f, 0f, 0f, 0f),
                    ["Transition_Width"] = new(0.2f, 0f, 0f, 0f),
                    ["Transition_Pattern_Tilling"] = new(0.02f, 0.5f, 0f, 0f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "transition_pattern", "transition_state" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.Dissolve, effect.Kind);
            Assert.Equal(0.65f, effect.Dissolve.Threshold);
            Assert.Equal(0.2f, effect.Dissolve.Softness);
            Assert.Equal(new Vector2(0.02f, 0.5f), effect.Dissolve.Tiling);
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

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("hair").Effect;
            Assert.Equal(ModelMaterialEffectKind.FlowMap, effect.Kind);
            Assert.Equal("cloudfm_tx_cm", effect.FlowMap.TextureName);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.FlowMap.ScrollSpeed);
        }

        [Fact]
        public void Resolve_DoesNotInferFlowMapFromSamplerWithoutAuthoredDriver()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("FlowmapTex", "ASSETS/Test/flow.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase));

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "flow" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.None, effect.Kind);
        }

        [Fact]
        public void Resolve_PreservesAuthoredAuxiliarySamplerWrapping()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(
                        "AdditiveScrollTex",
                        "ASSETS/Test/scroll.tex",
                        ModelMaterialWrapMode.Clamp,
                        ModelMaterialWrapMode.Mirror),
                    new SknMaterialSampler("AdditiveScroll_Mask", "ASSETS/Test/scroll_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AdditiveTexScrollSpeed_R"] = new(0.1f, 0f, 0f, 0f),
                    ["AdditiveTexTile"] = Vector4.One,
                    ["AdditiveStrength_R"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "scroll", "scroll_mask" },
                new[] { "Body" });

            Assert.True(effect.TextureSampling.TryGetValue("scroll", out ModelEffectTextureSamplingDefinition sampling));
            Assert.Equal(ModelMaterialWrapMode.Clamp, sampling.WrapU);
            Assert.Equal(ModelMaterialWrapMode.Mirror, sampling.WrapV);
        }

        [Fact]
        public void Resolve_PrefersDedicatedFresnelMaskOverAnotherLayerMask()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("AdditiveScrollTex", "ASSETS/Test/scroll.tex"),
                    new SknMaterialSampler("AdditiveScroll_Mask", "ASSETS/Test/scroll_mask.tex"),
                    new SknMaterialSampler("FresnelMask", "ASSETS/Test/fresnel_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AdditiveTexScrollSpeed_R"] = new(0.1f, 0f, 0f, 0f),
                    ["AdditiveTexTile"] = Vector4.One,
                    ["AdditiveStrength_R"] = Vector4.One,
                    ["FresnelIntensity"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "scroll", "scroll_mask", "fresnel_mask" },
                new[] { "Body" });

            Assert.Equal("scroll_mask", effect.AdditiveScroll.MaskTextureName);
            Assert.Equal("fresnel_mask", effect.Fresnel.MaskTextureName);
        }

        [Fact]
        public void Resolve_PrefersStandaloneDistortionOverPackedEmissionDistortion()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("Distortion_Texture", "ASSETS/Test/standalone_distortion.tex"),
                    new SknMaterialSampler("EmissionR_DistortionG_Texture", "ASSETS/Test/packed_emission_distortion.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DistortionStrength"] = new(0.08f, 0f, 0f, 0f),
                    ["EmissionStrength"] = Vector4.One,
                    ["EmissionColor"] = Vector4.One
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "standalone_distortion", "packed_emission_distortion" },
                new[] { "Body" });

            Assert.Equal("standalone_distortion", effect.Distortion.TextureName);
            Assert.Equal("packed_emission_distortion", effect.Emission.TextureName);
        }

        [Fact]
        public void Resolve_ComposesIndependentAuthoredEffectLayers()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("AdditiveScrollTex", "ASSETS/Test/scroll.tex"),
                    new SknMaterialSampler("AdditiveScroll_Mask", "ASSETS/Test/scroll_mask.tex"),
                    new SknMaterialSampler("FlowmapTex", "ASSETS/Test/flow.tex"),
                    new SknMaterialSampler("FresnelNoise", "ASSETS/Test/fresnel_noise.tex"),
                    new SknMaterialSampler("Emission_Texture", "ASSETS/Test/emission.tex"),
                    new SknMaterialSampler("EmissionMask", "ASSETS/Test/emission_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AdditiveTexScrollSpeed_R"] = new(0.1f, 0.2f, 0f, 0f),
                    ["AdditiveTexTile"] = new(2f, 2f, 0f, 0f),
                    ["AdditiveStrength_R"] = Vector4.One,
                    ["FlowSpeed"] = new(-0.15f, 0.05f, 0f, 0f),
                    ["FlowmapIntensity"] = new(0.2f, 0f, 0f, 0f),
                    ["FresnelIntensity"] = new(0.7f, 0f, 0f, 0f),
                    ["Fresnel_Noise_Tiling_Speed"] = new(2f, 3f, 0.4f, -0.2f),
                    ["EmissionStrength"] = new(1.4f, 0f, 0f, 0f),
                    ["EmissionColor"] = new(1f, 0.5f, 0.2f, 1f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "scroll", "scroll_mask", "flow", "fresnel_noise", "emission", "emission_mask" },
                new[] { "Body" });

            Assert.True((effect.Kind & ModelMaterialEffectKind.AdditiveScroll) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.FlowMap) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.FresnelNoise) != 0);
            Assert.True((effect.Kind & ModelMaterialEffectKind.Emission) != 0);
            Assert.Equal("scroll", effect.AdditiveScroll.TextureName);
            Assert.Equal("flow", effect.FlowMap.TextureName);
            Assert.Equal("fresnel_noise", effect.Fresnel.NoiseTextureName);
            Assert.Equal("emission", effect.Emission.TextureName);
        }

        [Fact]
        public void Resolve_PreservesAuthoredMaskChannel()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("Mask_Texture_blue", "ASSETS/Test/bloom_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bloom_Color"] = Vector4.One,
                    ["Bloom_Intensity"] = new(2f, 0f, 0f, 0f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "bloom_mask" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.Bloom, effect.Kind);
            Assert.Equal("bloom_mask", effect.Bloom.MaskTextureName);
            Assert.Equal(2, effect.Bloom.MaskChannel);
        }

        [Theory]
        [InlineData("Bloom_Texture")]
        [InlineData("Bloom_Mask")]
        public void Resolve_UsesAuthoredBloomTextureAliasesAsSpatialMask(string samplerName)
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler(samplerName, "ASSETS/Test/bloom.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bloom_Color"] = new(0.45f, 0.06f, 0f, 1f),
                    ["Bloom_Intensity"] = new(1f, 0f, 0f, 0f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "bloom" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.Bloom, effect.Kind);
            Assert.Equal("bloom", effect.Bloom.MaskTextureName);
            Assert.Equal(0, effect.Bloom.MaskChannel);
        }

        [Fact]
        public void Resolve_UsesAuthoredNoiseForComplexVertexDeformation()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("DeformNoise", "ASSETS/Test/deform_noise.tex"),
                    new SknMaterialSampler("DeformMask", "ASSETS/Test/deform_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["VertexDeformFeatureStrength"] = new(0.8f, 0f, 0f, 0f),
                    ["DeformProtection"] = new(1.5f, 0f, 0f, 0f),
                    ["DeformDirection"] = new(0f, 1f, 0.2f, 0f),
                    ["DeformScrollSpeed"] = new(0.1f, -0.2f, 0f, 0f),
                    ["DeformTiling"] = new(3f, 2f, 0f, 0f),
                    ["DeformSpeed"] = new(0.7f, 0f, 0f, 0f),
                    ["DeformFrequency"] = new(1.3f, 0f, 0f, 0f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "deform_noise", "deform_mask" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.VertexDeformation, effect.Kind);
            Assert.Equal("deform_noise", effect.VertexDeformation.NoiseTextureName);
            Assert.Equal("deform_mask", effect.VertexDeformation.MaskTextureName);
            Assert.Equal(0.8f, effect.VertexDeformation.Intensity);
            Assert.Equal(1.5f, effect.VertexDeformation.Protection);
        }

        [Fact]
        public void Resolve_PreservesSupportedLayersFromCompositeOnsenMaterial()
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
                new[]
                {
                    "locke_base_main_tx_cm",
                    "locke_coat_mask",
                    "flowmap",
                    "locke_additionalscrollcombo"
                });

            ModelMaterialEffectDefinition effect = resolution.ResolveMaterialDefinition("body").Effect;
            Assert.Equal(
                ModelMaterialEffectKind.FlowMap |
                ModelMaterialEffectKind.Fresnel |
                ModelMaterialEffectKind.Distortion,
                effect.Kind);
            Assert.Equal("flowmap", effect.FlowMap.TextureName);
            Assert.Equal(new Vector2(-0.2f, 0f), effect.FlowMap.ScrollSpeed);
            Assert.Equal(1f, effect.Fresnel.Strength);
            Assert.Equal("locke_coat_mask", effect.Distortion.TextureName);
            Assert.Equal(0, effect.Distortion.ChannelX);
            Assert.Equal(1, effect.Distortion.ChannelY);
        }

        [Fact]
        public void Resolve_RecognizesStandaloneDistortionLayer()
        {
            var material = new SknMaterialDefinition(
                new[]
                {
                    new SknMaterialSampler("Distortion_Texture", "ASSETS/Test/distortion.tex"),
                    new SknMaterialSampler("DistortionMask", "ASSETS/Test/distortion_mask.tex")
                },
                new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DistortionStrength"] = new(0.035f, 0f, 0f, 0f),
                    ["DistortionScrollSpeed"] = new(0.2f, -0.1f, 0f, 0f),
                    ["DistortionTiling"] = new(2f, 3f, 0f, 0f)
                });

            ModelMaterialEffectDefinition effect = SknMaterialEffectResolver.Resolve(
                material,
                "Body",
                new[] { "distortion", "distortion_mask" },
                new[] { "Body" });

            Assert.Equal(ModelMaterialEffectKind.Distortion, effect.Kind);
            Assert.Equal("distortion", effect.Distortion.TextureName);
            Assert.Equal("distortion_mask", effect.Distortion.MaskTextureName);
            Assert.Equal(new Vector2(0.2f, -0.1f), effect.Distortion.ScrollSpeed);
            Assert.Equal(new Vector2(2f, 3f), effect.Distortion.Tiling);
            Assert.Equal(0.035f, effect.Distortion.Strength);
            Assert.Equal(0, effect.Distortion.ChannelX);
            Assert.Equal(1, effect.Distortion.ChannelY);
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

        [Fact]
        public void Resolve_ResolvesProceduralGlassMaterialWithoutSamplers()
        {
            const string materialPath = "Characters/Janna/Skins/Skin67/Materials/Eyes_mat";
            BinTree tree = CreateSkinTree(
                "ASSETS/Characters/Janna/Skins/Skin67/Janna_Skin67_TX_CM.tex",
                CreateOverride(
                    "Glass",
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))),
                CreateMaterialWithParameters(
                    materialPath,
                    Array.Empty<BinTreeEmbedded>(),
                    CreateParameter("Glass_Color1", new Vector4(0.31f, 0.15f, 0.31f, 0f)),
                    CreateParameter("Glass_Color2", new Vector4(1f, 0.2f, 0.33f, 0f)),
                    CreateParameter("Alpha_Bias", new Vector4(0.055f, 0f, 0f, 0f)),
                    CreateParameter("Fresnel_Size_Inner", new Vector4(64f, 0f, 0f, 0f)),
                    CreateParameter("Fresnel_Size_Outer", new Vector4(1.13f, 0f, 0f, 0f))));

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                tree,
                new[] { "janna_skin67_tx_cm" });

            Assert.True(resolution.MaterialDefinitions.ContainsKey("glass"));
            ModelMaterialDefinition material = resolution.ResolveMaterialDefinition("glass");
            ModelMaterialEffectDefinition effect = material.Effect;
            Assert.True((effect.Kind & ModelMaterialEffectKind.Fresnel) != 0);
            Assert.True(material.Color.W < 0.1f);
            Assert.Equal(0.31f, material.Color.X, 2);
            Assert.Equal(1f, effect.Fresnel.Color.X, 2);
            Assert.Equal(ModelMaterialBlendMode.Opaque, material.RenderState.Blending);
            Assert.False(material.UsesTextureAlpha);
            Assert.True(effect.RequiresAlphaBlend);
            Assert.True(new ModelPart { MaterialDefinition = material }.IsAlphaBlended);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Resolvers
{
    public class SknStaticMaterialResolverTests
    {
        private const string SwitchedShaderPath = "Shaders/SkinnedMesh/AlphaBlend_Additive_Scroll_Packed";

        [Fact]
        public void Resolve_DemotesPlainNormalBlendWithoutAlphaToOpaque()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[] { Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex") },
                pass: Pass(blendEnabled: true));

            ModelMaterialDefinition resolved = Resolve(material, new[] { "test_tx_cm" });

            Assert.Equal(ModelMaterialBlendMode.Opaque, resolved.RenderState.Blending);
        }

        [Fact]
        public void Resolve_UsesShaderDefaultForStringAuthoredSamplerPath()
        {
            SknShaderDefinition shader = new(
                "Shaders/Test/Body",
                new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex")
                },
                new Dictionary<string, Vector4>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>());
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    new SknMaterialSampler(
                        "Diffuse_Texture",
                        null,
                        UsesShaderDefaultTexture: true)
                });

            ModelMaterialDefinition resolved = Resolve(
                material,
                new[] { "test_tx_cm" },
                shader: shader);

            Assert.Equal("test_tx_cm", resolved.BaseTextureName);
        }

        [Fact]
        public void ResolveMetadata_PreservesMissingLinkedDefaultMaterial()
        {
            var metadata = new SknMaterialTextureMetadata(
                "ASSETS/Characters/Test/Test_TX_CM.tex",
                null,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase))
            {
                HasDefaultMaterialLink = true
            };

            SknMaterialTextureResolution resolved = SknMaterialTextureResolver.Resolve(
                metadata,
                new[] { "test_tx_cm" });

            Assert.Equal(ModelMaterialBindingKind.Missing, resolved.DefaultMaterialDefinition.BindingKind);
            Assert.Null(resolved.DefaultMaterialDefinition.BaseTextureName);
        }

        [Fact]
        public void ResolveMetadata_UsesTextureOnlyForDirectOverrideWithoutMaterial()
        {
            var metadata = new SknMaterialTextureMetadata(
                "ASSETS/Characters/Test/Test_TX_CM.tex",
                null,
                new Dictionary<string, SknMaterialDefinition>(StringComparer.OrdinalIgnoreCase))
            {
                DirectOverrideTexturePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hat"] = "ASSETS/Characters/Test/Test_Hat_TX_CM.tex"
                }
            };

            SknMaterialTextureResolution resolved = SknMaterialTextureResolver.Resolve(
                metadata,
                new[] { "test_tx_cm", "test_hat_tx_cm" });
            ModelMaterialDefinition hat = resolved.ResolveMaterialDefinition("hat");

            Assert.Equal(ModelMaterialBindingKind.TextureOnly, hat.BindingKind);
            Assert.Equal("test_hat_tx_cm", hat.BaseTextureName);
        }

        [Fact]
        public void ModelPart_RuntimeOpacityCanBlendAnAuthoredOpaqueMaterial()
        {
            var part = new ModelPart
            {
                ColorTint = new Vector4(1f, 1f, 1f, 0.25f),
                MaterialDefinition = ModelMaterialDefinition.TextureOnly("test_tx_cm")
            };

            Assert.True(part.IsAlphaBlended);
        }

        [Fact]
        public void Resolve_PreservesNormalBlendWhenOpacitySlotExistsAtOne()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[] { Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex") },
                parameters: new Dictionary<string, Vector4>
                {
                    ["Opacity"] = new Vector4(1f, 0f, 0f, 0f)
                },
                pass: Pass(blendEnabled: true));

            ModelMaterialDefinition resolved = Resolve(material, new[] { "test_tx_cm" });

            Assert.Equal(ModelMaterialBlendMode.Normal, resolved.RenderState.Blending);
            Assert.Equal(1f, resolved.Color.W);
        }

        [Fact]
        public void Resolve_ReadsAuthoredAdditiveCullAndDepthState()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[] { Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex") },
                pass: Pass(
                    blendEnabled: true,
                    destinationBlendFactor: 1,
                    cullEnabled: false,
                    windingToCull: 0,
                    depthEnabled: false,
                    writeMask: 15));

            ModelMaterialDefinition resolved = Resolve(material, new[] { "test_tx_cm" });

            Assert.Equal(ModelMaterialBlendMode.Additive, resolved.RenderState.Blending);
            Assert.True(resolved.RenderState.DoubleSided);
            Assert.True(resolved.RenderState.Inverted);
            Assert.False(resolved.RenderState.DepthWrite);
            Assert.False(resolved.RenderState.DepthTest);
        }

        [Fact]
        public void Resolve_SelectsLaterExactTextureBeforePlaceholderFallback()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Shared/Materials/black.tex"),
                    Sampler("Main_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex")
                });

            ModelMaterialDefinition resolved = Resolve(material, new[] { "black", "test_tx_cm" });

            Assert.Equal("test_tx_cm", resolved.BaseTextureName);
        }

        [Fact]
        public void Resolve_RescuesPlaceholderWithColorMapPath()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Shared/Materials/white.tex"),
                    Sampler("Layer_Tex", "ASSETS/Characters/Test/Test_TX_CM.tex")
                });

            ModelMaterialDefinition resolved = Resolve(material, new[] { "white", "test_tx_cm" });

            Assert.Equal("test_tx_cm", resolved.BaseTextureName);
        }

        [Fact]
        public void Resolve_DoesNotSubstituteSkinTextureForMissingMaterialBaseAsset()
        {
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Missing_TX_CM.tex")
                });

            ModelMaterialDefinition resolved = Resolve(
                material,
                new[] { "skin_tx_cm" },
                fallbackTextureKey: "skin_tx_cm");

            Assert.Null(resolved.BaseTextureName);
            Assert.Equal(ModelMaterialBlendMode.Opaque, resolved.RenderState.Blending);
        }

        [Fact]
        public void Resolve_UsesShaderDeclarationsAndPassParametersLikeLtk()
        {
            SknShaderDefinition shader = new(
                "Shaders/Test/Body",
                Array.Empty<SknMaterialSampler>(),
                new Dictionary<string, Vector4>
                {
                    ["Alpha"] = new Vector4(0.75f, 0f, 0f, 0f),
                    ["TintColor"] = Vector4.One
                },
                new Dictionary<string, bool>(),
                new Dictionary<string, string>());
            SknMaterialDefinition material = CreateMaterial(
                parameters: new Dictionary<string, Vector4>
                {
                    // LTK ignores this because the shader does not declare Opacity.
                    ["Opacity"] = new Vector4(0.25f, 0f, 0f, 0f),
                    ["TintColor"] = new Vector4(0.5f, 0.5f, 0.5f, 1f)
                },
                pass: Pass(
                    blendEnabled: true,
                    parameters: new Dictionary<string, Vector4>
                    {
                        ["TintColor"] = new Vector4(2f, 2f, 2f, 1f)
                    }));

            ModelMaterialDefinition resolved = Resolve(material, Array.Empty<string>(), shader: shader);

            Assert.Equal(new Vector4(2f, 2f, 2f, 0.75f), resolved.Color);
            Assert.Equal(ModelMaterialBlendMode.Normal, resolved.RenderState.Blending);
        }

        [Fact]
        public void Resolve_AppliesMacroPrecedenceMaterialThenShaderThenPass()
        {
            SknShaderDefinition shader = new(
                "Shaders/Test/Body",
                Array.Empty<SknMaterialSampler>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>
                {
                    ["PREMULTIPLIED_ALPHA"] = "0"
                });
            SknMaterialDefinition withoutPassOverride = CreateMaterial(
                macros: new Dictionary<string, string>
                {
                    ["PREMULTIPLIED_ALPHA"] = "1"
                });
            SknMaterialDefinition withPassOverride = CreateMaterial(
                macros: new Dictionary<string, string>
                {
                    ["PREMULTIPLIED_ALPHA"] = "1"
                },
                pass: Pass(
                    macros: new Dictionary<string, string>
                    {
                        ["PREMULTIPLIED_ALPHA"] = "1"
                    }));

            ModelMaterialDefinition shaderWins = Resolve(withoutPassOverride, Array.Empty<string>(), shader: shader);
            ModelMaterialDefinition passWins = Resolve(withPassOverride, Array.Empty<string>(), shader: shader);

            Assert.False(shaderWins.RenderState.PremultipliedAlpha);
            Assert.True(passWins.RenderState.PremultipliedAlpha);
        }

        [Fact]
        public void Resolve_SwitchedShaderUsesMainTextureAndAdditiveSwitch()
        {
            uint shaderHash = Fnv1a.HashLower(SwitchedShaderPath);
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Shared/Materials/black.tex"),
                    Sampler("Main_Texture", "ASSETS/Characters/Test/Test_TX_CM.tex")
                },
                switches: new Dictionary<string, bool>
                {
                    ["MAINTEX_ON"] = true,
                    ["ADDITIVEALPHA_ON"] = true
                },
                pass: Pass(shaderHash: shaderHash, blendEnabled: true),
                shaderHash: shaderHash);

            ModelMaterialDefinition resolved = Resolve(material, new[] { "black", "test_tx_cm" });

            Assert.Equal("test_tx_cm", resolved.BaseTextureName);
            Assert.Equal(ModelMaterialBlendMode.Additive, resolved.RenderState.Blending);
        }

        [Fact]
        public void Resolve_SwitchedShaderWithMainTextureDisabledKeepsDiffuse()
        {
            uint shaderHash = Fnv1a.HashLower(SwitchedShaderPath);
            SknMaterialDefinition material = CreateMaterial(
                samplers: new[]
                {
                    Sampler("Diffuse_Texture", "ASSETS/Characters/Test/Diffuse_TX_CM.tex"),
                    Sampler("Main_Texture", "ASSETS/Characters/Test/Main_TX_CM.tex")
                },
                switches: new Dictionary<string, bool>
                {
                    ["MAINTEX_ON"] = false
                },
                pass: Pass(shaderHash: shaderHash),
                shaderHash: shaderHash);

            ModelMaterialDefinition resolved = Resolve(
                material,
                new[] { "diffuse_tx_cm", "main_tx_cm" });

            Assert.Equal("diffuse_tx_cm", resolved.BaseTextureName);
            Assert.Equal(ModelMaterialBaseRule.Exact, resolved.BaseRule);
        }

        [Fact]
        public void Resolve_AppliesTintAndUvGuardsLikeLtk()
        {
            SknMaterialDefinition material = CreateMaterial(
                parameters: new Dictionary<string, Vector4>
                {
                    ["TintColor"] = new Vector4(0.1f, -0.03f, -0.06f, 1f),
                    ["MainTex_Tile"] = Vector4.One,
                    ["ScrollSpeedMainTex"] = new Vector4(0.5f, 0f, 0f, 0f)
                });

            ModelMaterialDefinition resolved = Resolve(material, Array.Empty<string>());

            Assert.Equal(Vector4.One, resolved.Color);
            Assert.Equal(Vector2.One, resolved.UvRepeat);
            Assert.Equal(new Vector2(0.5f, 0f), resolved.UvScroll);
        }

        [Fact]
        public void Resolve_MissingPassUsesClassRenderDefaults()
        {
            SknMaterialDefinition material = CreateMaterial();

            ModelMaterialDefinition resolved = Resolve(material, Array.Empty<string>());

            Assert.Equal(ModelMaterialRenderState.Default, resolved.RenderState);
        }

        [Fact]
        public void ReadMetadata_ParsesSamplerPassSwitchMacroAndDynamicMaterial()
        {
            const string materialPath = "Characters/Test/Skins/Base/Materials/Body";
            const string shaderPath = "Shaders/Test/Body";
            const string texturePath = "ASSETS/Characters/Test/Skins/Base/Test_TX_CM.tex";
            ulong textureHash = 0x1234567890abcdef;
            uint shaderHash = Fnv1a.HashLower(shaderPath);

            BinTreeEmbedded sampler = Embedded(
                "StaticMaterialShaderSamplerDef",
                new BinTreeString(Fnv1a.HashLower("textureName"), "Diffuse_Texture"),
                new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), textureHash),
                new BinTreeU32(Fnv1a.HashLower("addressU"), 1),
                new BinTreeU32(Fnv1a.HashLower("addressV"), 2));
            BinTreeEmbedded switchDefinition = Embedded(
                "StaticMaterialSwitchDef",
                new BinTreeString(Fnv1a.HashLower("name"), "MAINTEX_ON"));
            BinTreeEmbedded pass = Embedded(
                "StaticMaterialPassDef",
                new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash),
                new BinTreeBool(Fnv1a.HashLower("blendEnable"), true),
                new BinTreeU32(Fnv1a.HashLower("dstColorBlendFactor"), 1),
                new BinTreeBool(Fnv1a.HashLower("cullEnable"), false),
                new BinTreeU32(Fnv1a.HashLower("windingToCull"), 0),
                new BinTreeBool(Fnv1a.HashLower("depthEnable"), false),
                new BinTreeU32(Fnv1a.HashLower("writeMask"), 15));
            BinTreeEmbedded technique = Embedded(
                "StaticMaterialTechniqueDef",
                new BinTreeString(Fnv1a.HashLower("name"), "normal"),
                Container("passes", pass));
            BinTreeObject material = new(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    Container("samplerValues", sampler),
                    Container("switches", switchDefinition),
                    StringMap("shaderMacros", ("PREMULTIPLIED_ALPHA", "1")),
                    Container("techniques", technique),
                    new BinTreeStruct(
                        Fnv1a.HashLower("dynamicMaterial"),
                        Fnv1a.HashLower("DynamicMaterialDef"),
                        Array.Empty<BinTreeProperty>())
                });
            BinTreeObject skin = Skin(materialPath);
            BinTree tree = new(new[] { skin, material }, Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(
                tree,
                hash => hash == textureHash ? texturePath : $"{hash:x16}",
                hash => hash == shaderHash ? shaderPath : $"{hash:x8}");
            SknMaterialDefinition parsed = metadata.DefaultMaterial;

            Assert.NotNull(parsed);
            Assert.Equal(shaderPath, parsed.ShaderPath);
            Assert.True(parsed.IsAnimated);
            Assert.True(parsed.SwitchStates["MAINTEX_ON"]);
            Assert.Equal("1", parsed.ShaderMacros["PREMULTIPLIED_ALPHA"]);
            Assert.Equal(ModelMaterialWrapMode.Clamp, parsed.Samplers[0].WrapU);
            Assert.Equal(ModelMaterialWrapMode.Mirror, parsed.Samplers[0].WrapV);
            Assert.True(parsed.Pass.BlendEnabled);
            Assert.Equal((uint)1, parsed.Pass.DestinationColorBlendFactor);
            Assert.False(parsed.Pass.CullEnabled);
            Assert.Equal((uint)0, parsed.Pass.WindingToCull);
            Assert.False(parsed.Pass.DepthEnabled);
            Assert.Equal((uint)15, parsed.Pass.WriteMask);
        }

        [Fact]
        public void ReadMetadata_CollectsShaderDefinitionsAndUsesTheirDefaults()
        {
            const string materialPath = "Characters/Test/Skins/Base/Materials/Body";
            const string shaderPath = "Shaders/Test/Body";
            const string texturePath = "ASSETS/Characters/Test/Skins/Base/Test_TX_CM.tex";
            uint shaderHash = Fnv1a.HashLower(shaderPath);

            BinTreeEmbedded sampler = Embedded(
                "StaticMaterialShaderSamplerDef",
                new BinTreeString(Fnv1a.HashLower("textureName"), "Diffuse_Texture"),
                new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), 0x1234UL));
            BinTreeEmbedded pass = Embedded(
                "StaticMaterialPassDef",
                new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash),
                new BinTreeBool(Fnv1a.HashLower("blendEnable"), true));
            BinTreeEmbedded technique = Embedded(
                "StaticMaterialTechniqueDef",
                new BinTreeString(Fnv1a.HashLower("name"), "normal"),
                Container("passes", pass));
            BinTreeObject material = new(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    Container("samplerValues", sampler),
                    Container("techniques", technique)
                });
            BinTree skinTree = new(new[] { Skin(materialPath), material }, Array.Empty<string>());

            BinTreeEmbedded shaderParameter = Embedded(
                "ShaderPhysicalParameter",
                new BinTreeString(Fnv1a.HashLower("name"), "Alpha"),
                new BinTreeVector4(Fnv1a.HashLower("data"), new Vector4(0.75f, 0f, 0f, 0f)));
            BinTreeObject shader = new(
                shaderPath,
                "CustomShaderDef",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("objectPath"), shaderPath),
                    Container("parameters", shaderParameter)
                });
            BinTree shaderTree = new(new[] { shader }, Array.Empty<string>());

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(
                new[] { skinTree, shaderTree },
                hash => hash == 0x1234UL ? texturePath : $"{hash:x16}",
                hash => hash == shaderHash ? shaderPath : $"{hash:x8}");
            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(
                metadata,
                new[] { "test_tx_cm" });

            Assert.True(metadata.ShaderDefinitions.ContainsKey(shaderHash));
            Assert.Equal(shaderPath, resolution.DefaultMaterialDefinition.ShaderPath);
            Assert.Equal(0.75f, resolution.DefaultMaterialDefinition.Color.W);
            Assert.Equal(ModelMaterialBlendMode.Normal, resolution.DefaultMaterialDefinition.RenderState.Blending);
        }

        [Fact]
        public void ReadShaderDefinitions_ParsesDefaultsLogicalParametersAndFeatureDefines()
        {
            const string shaderPath = "Shaders/Test/Body";
            const string texturePath = "ASSETS/Characters/Test/Default_TX_CM.tex";
            ulong textureHash = 0xfedcba0987654321;
            BinTreeEmbedded texture = Embedded(
                "ShaderTexture",
                new BinTreeString(Fnv1a.HashLower("name"), "Diffuse_Texture"),
                new BinTreeWadChunkLink(Fnv1a.HashLower("defaultTexturePath"), textureHash));
            BinTreeEmbedded logicalParameter = Embedded(
                "ShaderLogicalParameter",
                new BinTreeString(Fnv1a.HashLower("name"), "OpacityAlias"));
            BinTreeEmbedded parameter = Embedded(
                "ShaderPhysicalParameter",
                new BinTreeString(Fnv1a.HashLower("name"), "Alpha"),
                new BinTreeVector4(Fnv1a.HashLower("data"), new Vector4(0.75f, 0f, 0f, 0f)),
                Container("logicalParameters", logicalParameter));
            BinTreeEmbedded switchDefinition = Embedded(
                "ShaderStaticSwitch",
                new BinTreeString(Fnv1a.HashLower("name"), "USE_ALPHA"),
                new BinTreeBool(Fnv1a.HashLower("onByDefault"), true));
            BinTreeObject shader = new(
                shaderPath,
                "CustomShaderDef",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("objectPath"), shaderPath),
                    Container("textures", texture),
                    Container("parameters", parameter),
                    Container("staticSwitches", switchDefinition),
                    StringMap("featureDefines", ("FEATURE_MASKED", "1"))
                });
            BinTree tree = new(new[] { shader }, Array.Empty<string>());

            IReadOnlyDictionary<uint, SknShaderDefinition> definitions =
                SknMaterialTextureResolver.ReadShaderDefinitions(
                    tree,
                    hash => hash == textureHash ? texturePath : $"{hash:x16}");
            SknShaderDefinition parsed = definitions[Fnv1a.HashLower(shaderPath)];

            Assert.Equal(shaderPath, parsed.Path);
            Assert.Equal(
                texturePath.Replace('\\', '/'),
                parsed.DefaultSamplers[0].TexturePath,
                ignoreCase: true);
            Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), parsed.DefaultParameters["Alpha"]);
            Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), parsed.DefaultParameters["OpacityAlias"]);
            Assert.True(parsed.DefaultSwitches["USE_ALPHA"]);
            Assert.Equal("1", parsed.FeatureDefines["FEATURE_MASKED"]);
        }

        private static ModelMaterialDefinition Resolve(
            SknMaterialDefinition material,
            IReadOnlyList<string> textureKeys,
            string fallbackTextureKey = null,
            SknShaderDefinition shader = null) =>
            SknStaticMaterialResolver.Resolve(
                material,
                shader,
                textureKeys,
                fallbackTextureKey,
                ModelMaterialEffectDefinition.None);

        private static SknMaterialDefinition CreateMaterial(
            IReadOnlyList<SknMaterialSampler> samplers = null,
            IReadOnlyDictionary<string, Vector4> parameters = null,
            IReadOnlyDictionary<string, bool> switches = null,
            IReadOnlyDictionary<string, string> macros = null,
            SknMaterialPassDefinition pass = null,
            uint shaderHash = 0,
            string shaderPath = null) =>
            new(
                samplers ?? Array.Empty<SknMaterialSampler>(),
                parameters ?? new Dictionary<string, Vector4>())
            {
                SwitchStates = switches ?? new Dictionary<string, bool>(),
                ShaderMacros = macros ?? new Dictionary<string, string>(),
                Pass = pass,
                ShaderHash = shaderHash != 0 ? shaderHash : pass?.ShaderHash ?? 0,
                ShaderPath = shaderPath
            };

        private static SknMaterialSampler Sampler(string name, string path) => new(name, path);

        private static SknMaterialPassDefinition Pass(
            uint shaderHash = 0,
            bool? blendEnabled = null,
            uint? destinationBlendFactor = null,
            bool? cullEnabled = null,
            uint? windingToCull = null,
            bool? depthEnabled = null,
            uint? writeMask = null,
            IReadOnlyDictionary<string, Vector4> parameters = null,
            IReadOnlyDictionary<string, string> macros = null) =>
            new(
                shaderHash,
                parameters ?? new Dictionary<string, Vector4>(),
                macros ?? new Dictionary<string, string>(),
                blendEnabled,
                destinationBlendFactor,
                cullEnabled,
                windingToCull,
                depthEnabled,
                writeMask);

        private static BinTreeObject Skin(string materialPath)
        {
            var mesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))
                });
            return new BinTreeObject(
                "Characters/Test/Skins/Skin0",
                "SkinCharacterDataProperties",
                new BinTreeProperty[] { mesh });
        }

        private static BinTreeEmbedded Embedded(string className, params BinTreeProperty[] properties) =>
            new(0, Fnv1a.HashLower(className), properties);

        private static BinTreeUnorderedContainer Container(string name, params BinTreeProperty[] elements) =>
            new(Fnv1a.HashLower(name), BinPropertyType.Embedded, elements);

        private static BinTreeMap StringMap(string name, params (string Key, string Value)[] values) =>
            new(
                Fnv1a.HashLower(name),
                BinPropertyType.String,
                BinPropertyType.String,
                values.Select(pair => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeString(0, pair.Key),
                    new BinTreeString(0, pair.Value))));
    }
}

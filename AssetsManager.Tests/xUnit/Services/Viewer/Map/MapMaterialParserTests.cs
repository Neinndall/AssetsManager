using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapMaterialParserTests
    {
        [Fact]
        public void ProgramPreservesAllPassesDefinesRuntimeSwitchesLogicalParamsAndSamplerState()
        {
            const string materialPath = "Maps/Test/Materials/Surface";
            const string shaderPath = "Shaders/StaticMesh/TestSurface";
            uint shaderHash = Fnv1a.HashLower(shaderPath);

            BinTreeEmbedded logical = Embedded(
                "ShaderLogicalParameter",
                new BinTreeString(Fnv1a.HashLower("name"), "Tint"),
                new BinTreeU32(Fnv1a.HashLower("fields"), 0b0101));
            BinTreeEmbedded physical = Embedded(
                "ShaderPhysicalParameter",
                new BinTreeString(Fnv1a.HashLower("name"), "Globals0"),
                new BinTreeVector4(Fnv1a.HashLower("data"), new Vector4(1f, 2f, 3f, 4f)),
                Container("logicalParameters", logical));
            BinTreeEmbedded texture = Embedded(
                "ShaderTexture",
                new BinTreeString(Fnv1a.HashLower("name"), "Diffuse_Texture"),
                new BinTreeString(Fnv1a.HashLower("defaultTexturePath"), "ASSETS/Maps/Test/Default.tex"),
                new BinTreeString(Fnv1a.HashLower("samplerName"), "LinearShared"));
            BinTreeEmbedded compileSwitch = Embedded(
                "ShaderStaticSwitch",
                new BinTreeString(Fnv1a.HashLower("name"), "COMPILE_ON"),
                new BinTreeBool(Fnv1a.HashLower("onByDefault"), false));
            BinTreeEmbedded runtimeSwitch = Embedded(
                "ShaderStaticSwitch",
                new BinTreeString(Fnv1a.HashLower("name"), "RUNTIME_ON"),
                new BinTreeBool(Fnv1a.HashLower("onByDefault"), true),
                new BinTreeBool(0x066e669c, true));
            var shader = new BinTreeObject(
                shaderHash,
                Fnv1a.HashLower("CustomShaderDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("objectPath"), shaderPath),
                    Container("textures", texture),
                    Container("parameters", physical),
                    Container("staticSwitches", compileSwitch, runtimeSwitch),
                    StringMap("featureDefines", ("FEATURE_TEST", "7"))
                });

            BinTreeEmbedded sampler = Embedded(
                "StaticMaterialShaderSamplerDef",
                new BinTreeString(Fnv1a.HashLower("TextureName"), "Diffuse_Texture"),
                new BinTreeU32(Fnv1a.HashLower("addressU"), 1),
                new BinTreeU32(Fnv1a.HashLower("addressV"), 2),
                new BinTreeU32(Fnv1a.HashLower("addressW"), 3),
                new BinTreeU32(Fnv1a.HashLower("filterMin"), 0),
                new BinTreeU32(Fnv1a.HashLower("filterMag"), 1));
            BinTreeEmbedded materialParam = Embedded(
                "StaticMaterialShaderParamDef",
                new BinTreeString(Fnv1a.HashLower("name"), "Tint"),
                new BinTreeVector4(Fnv1a.HashLower("value"), new Vector4(9f, 8f, 7f, 6f)));
            BinTreeEmbedded passParam = Embedded(
                "StaticMaterialShaderParamDef",
                new BinTreeString(Fnv1a.HashLower("name"), "Tint"),
                new BinTreeVector4(Fnv1a.HashLower("value"), new Vector4(5f, 6f, 0f, 0f)));
            BinTreeEmbedded switchValue = Embedded(
                "StaticMaterialSwitchDef",
                new BinTreeString(Fnv1a.HashLower("name"), "RUNTIME_ON"),
                new BinTreeBool(Fnv1a.HashLower("on"), false));
            BinTreeEmbedded pass0 = Embedded(
                "StaticMaterialPassDef",
                new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash),
                Container("paramValues", passParam),
                StringMap("shaderMacros", ("MAT_DEFINE", "pass")),
                new BinTreeBool(Fnv1a.HashLower("blendEnable"), true),
                new BinTreeU32(Fnv1a.HashLower("srcColorBlendFactor"), 6),
                new BinTreeU32(Fnv1a.HashLower("dstColorBlendFactor"), 7),
                new BinTreeU32(Fnv1a.HashLower("srcAlphaBlendFactor"), 1),
                new BinTreeU32(Fnv1a.HashLower("dstAlphaBlendFactor"), 0),
                new BinTreeBool(Fnv1a.HashLower("cullEnable"), false),
                new BinTreeU32(Fnv1a.HashLower("windingToCull"), 0),
                new BinTreeBool(Fnv1a.HashLower("depthEnable"), true),
                new BinTreeU32(Fnv1a.HashLower("depthCompareFunc"), 3),
                new BinTreeU32(Fnv1a.HashLower("writeMask"), 15));
            BinTreeEmbedded pass1 = Embedded(
                "StaticMaterialPassDef",
                new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash));
            BinTreeEmbedded technique = Embedded(
                "StaticMaterialTechniqueDef",
                new BinTreeString(Fnv1a.HashLower("name"), "normal"),
                Container("passes", pass0, pass1));
            var material = new BinTreeObject(
                Fnv1a.HashLower(materialPath),
                Fnv1a.HashLower("StaticMaterialDef"),
                new BinTreeProperty[]
                {
                    new BinTreeU32(Fnv1a.HashLower("type"), 0),
                    Container("samplerValues", sampler),
                    Container("paramValues", materialParam),
                    Container("switches", switchValue),
                    StringMap("shaderMacros", ("MAT_DEFINE", "material")),
                    Container("techniques", technique)
                });

            var parser = new MapMaterialParser();
            MapMaterialDefinition parsed = parser.ParseOne(
                Tree(material),
                Tree(shader),
                materialPath);

            Assert.NotNull(parsed.Program);
            Assert.Equal(GameMaterialKind.StaticMesh, parsed.Program.Kind);
            Assert.Equal(2, parsed.Program.Passes.Count);

            GameMaterialPass first = parsed.Program.Passes[0];
            Assert.Equal(shaderPath, first.ShaderPath);
            Assert.Equal(new[] { "COMPILE_ON", "FEATURE_TEST", "MAT_DEFINE" }, first.Defines.Select(item => item.Name));
            Assert.Equal("0", first.Defines.Single(item => item.Name == "COMPILE_ON").Value);
            Assert.Equal("pass", first.Defines.Single(item => item.Name == "MAT_DEFINE").Value);
            Assert.Equal(GameMaterialDefineSource.Pass, first.Defines.Single(item => item.Name == "MAT_DEFINE").Source);
            Assert.DoesNotContain(first.Defines, item => item.Name == "RUNTIME_ON");
            Assert.Equal(new KeyValuePair<string, bool>("RUNTIME_ON", false), Assert.Single(first.RuntimeSwitches));

            GameMaterialTexture resolvedTexture = Assert.Single(first.Textures);
            Assert.Equal("assets/maps/test/default.tex", resolvedTexture.Texture.VirtualPath);
            Assert.Equal(GameMaterialTextureSource.ShaderDefault, resolvedTexture.Source);
            Assert.Equal("LinearShared", resolvedTexture.Sampler.SharedSampler);
            Assert.Equal(MapTextureWrap.Clamp, resolvedTexture.Sampler.WrapU);
            Assert.Equal(MapTextureWrap.Mirror, resolvedTexture.Sampler.WrapV);
            Assert.Equal(MapTextureWrap.Border, resolvedTexture.Sampler.WrapW);
            Assert.False(resolvedTexture.Sampler.FilterMin);
            Assert.True(resolvedTexture.Sampler.FilterMag);

            GameMaterialParameter parameter = Assert.Single(first.Parameters);
            Assert.Equal("Globals0", parameter.Name);
            Assert.Equal(new Vector4(9f, 2f, 8f, 4f), parameter.Value);
            Assert.Equal(GameMaterialParamSource.Material, parameter.Source);

            Assert.True(first.State.BlendEnabled);
            Assert.Equal(MapBlendFactor.SourceAlpha, first.State.SourceColor);
            Assert.Equal(MapBlendFactor.OneMinusSourceAlpha, first.State.DestinationColor);
            Assert.Equal(GameMaterialWinding.Clockwise, first.State.WindingToCull);
            Assert.False(first.State.CullEnabled);
            Assert.Equal((uint)15, first.State.WriteMask);
        }

        [Fact]
        public void Program_UndeclaredShader_MaterialParamsWinOverPassParams()
        {
            const string materialPath = "Maps/Test/Materials/Undeclared";
            const string shaderPath = "Shaders/StaticMesh/UndeclaredShader";
            uint shaderHash = Fnv1a.HashLower(shaderPath);

            BinTreeEmbedded materialParam = Embedded(
                "StaticMaterialShaderParamDef",
                new BinTreeString(Fnv1a.HashLower("name"), "TintColor"),
                new BinTreeVector4(Fnv1a.HashLower("value"), new Vector4(1f, 0.5f, 0.25f, 1f)));
            BinTreeEmbedded passParam = Embedded(
                "StaticMaterialShaderParamDef",
                new BinTreeString(Fnv1a.HashLower("name"), "TintColor"),
                new BinTreeVector4(Fnv1a.HashLower("value"), new Vector4(2f, 2f, 2f, 1f)));
            BinTreeEmbedded pass0 = Embedded(
                "StaticMaterialPassDef",
                new BinTreeObjectLink(Fnv1a.HashLower("shader"), shaderHash),
                Container("paramValues", passParam));
            BinTreeEmbedded technique = Embedded(
                "StaticMaterialTechniqueDef",
                new BinTreeString(Fnv1a.HashLower("name"), "normal"),
                Container("passes", pass0));
            var material = new BinTreeObject(
                Fnv1a.HashLower(materialPath),
                Fnv1a.HashLower("StaticMaterialDef"),
                new BinTreeProperty[]
                {
                    Container("paramValues", materialParam),
                    Container("techniques", technique)
                });

            var parser = new MapMaterialParser();
            MapMaterialDefinition parsed = parser.ParseOne(
                Tree(material),
                null,
                materialPath);

            Assert.NotNull(parsed.Program);
            GameMaterialPass first = Assert.Single(parsed.Program.Passes);
            GameMaterialParameter parameter = Assert.Single(first.Parameters);
            Assert.Equal("TintColor", parameter.Name);
            Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), parameter.Value);
            Assert.Equal(GameMaterialParamSource.Material, parameter.Source);
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());

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

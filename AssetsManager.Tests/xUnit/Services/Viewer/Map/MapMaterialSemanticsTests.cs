using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapMaterialSemanticsTests
    {
        [Theory]
        [InlineData(1u, 7u, 1, true)]
        [InlineData(6u, 7u, 1, false)]
        [InlineData(1u, 0u, 0, false)]
        [InlineData(6u, 1u, 2, false)]
        [InlineData(1u, 1u, 2, false)]
        [InlineData(3u, 0u, 3, false)]
        public void BlendFactorsMatchLtkClassification(
            uint source,
            uint destination,
            int expectedMode,
            bool expectedPremultiplied)
        {
            MapMaterialDefinition material = Resolve(
                pass: Pass(blend: true, source, destination),
                parameters: new Dictionary<string, Vector4>
                {
                    ["Opacity"] = Vector4.One
                });

            Assert.Equal((MapMaterialBlendMode)expectedMode, material.RenderState.Blending);
            Assert.Equal(expectedPremultiplied, material.RenderState.PremultipliedAlpha);
        }

        [Fact]
        public void NormalBlendWithoutAuthoredAlphaFallsBackToOpaque()
        {
            MapMaterialDefinition material = Resolve(
                pass: Pass(blend: true, 6, 7));

            Assert.Equal(MapMaterialBlendMode.Opaque, material.RenderState.Blending);
        }

        [Fact]
        public void AuthoredAlphaTestWithDepthWriteCreatesCutout()
        {
            MapMaterialDefinition material = Resolve(
                pass: Pass(blend: true, 6, 7, writeMask: 31),
                parameters: new Dictionary<string, Vector4>
                {
                    ["AlphaTestValue"] = new Vector4(0.35f, 0f, 0f, 0f)
                });

            Assert.Equal(MapMaterialBlendMode.Normal, material.RenderState.Blending);
            Assert.True(material.RenderState.Cutout);
            Assert.Equal(0.35f, material.AlphaTest);
        }

        [Fact]
        public void StaticMeshDefaultEnvironmentDoublesTint()
        {
            MapMaterialDefinition material = Resolve(
                shader: Shader("Shaders/StaticMesh/DefaultEnv_Flat"),
                parameters: new Dictionary<string, Vector4>
                {
                    ["TintColor"] = new Vector4(0.5f, 0.25f, 0.75f, 1f)
                });

            Assert.Equal(new Vector3(1f, 0.5f, 1.5f), material.Tint);
        }

        [Fact]
        public void UndeclaredShaderKeepsAuthoredMaterialParameters()
        {
            var shader = new MapShaderDefinitionData(
                "Shaders/Missing",
                Array.Empty<MapMaterialSamplerData>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                false);

            MapMaterialDefinition material = Resolve(
                shader: shader,
                parameters: new Dictionary<string, Vector4>
                {
                    ["TintColor"] = new Vector4(0.25f, 0.5f, 0.75f, 1f),
                    ["Opacity"] = new Vector4(0.6f, 0f, 0f, 0f)
                });

            Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), material.Tint);
            Assert.Equal(0.6f, material.Opacity);
        }

        [Fact]
        public void SwitchedShaderIsRecognizedByPassHashWithoutShaderDefs()
        {
            var samplers = new[]
            {
                new MapMaterialSamplerData(
                    "Main_Texture",
                    new MapTextureReference("assets/main_cm.tex", 0),
                    MapTextureWrap.Repeat,
                    MapTextureWrap.Repeat)
            };
            var switches = new Dictionary<string, bool>
            {
                ["MAINTEX_ON"] = true
            };
            var shader = new MapShaderDefinitionData(
                null,
                Array.Empty<MapMaterialSamplerData>(),
                new Dictionary<string, Vector4>(),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                false);

            MapMaterialDefinition material = Resolve(
                pass: Pass(
                    blend: true,
                    source: 6,
                    destination: 7,
                    shaderHash: Fnv1a.HashLower("Shaders/SkinnedMesh/AlphaBlend_Additive_Scroll_Packed")),
                shader: shader,
                samplers: samplers,
                switches: switches);

            Assert.NotNull(material.BaseTexture);
            Assert.Equal(MapMaterialBaseRule.SwitchOverride, material.BaseTexture.Rule);
        }

        [Fact]
        public void ExactBaseSamplerWinsAndKeepsAuthoredWrap()
        {
            var samplers = new[]
            {
                new MapMaterialSamplerData(
                    "Noise_Texture",
                    new MapTextureReference("assets/noise.tex", 0),
                    MapTextureWrap.Repeat,
                    MapTextureWrap.Repeat),
                new MapMaterialSamplerData(
                    "Diffuse_Texture",
                    new MapTextureReference("assets/rock_cm.tex", 0),
                    MapTextureWrap.Clamp,
                    MapTextureWrap.Mirror)
            };

            MapMaterialDefinition material = Resolve(samplers: samplers);

            Assert.NotNull(material.BaseTexture);
            Assert.Equal("Diffuse_Texture", material.BaseTexture.Name);
            Assert.Equal(MapMaterialBaseRule.Exact, material.BaseTexture.Rule);
            Assert.Equal(MapTextureWrap.Clamp, material.BaseTexture.WrapU);
            Assert.Equal(MapTextureWrap.Mirror, material.BaseTexture.WrapV);
        }

        private static MapMaterialDefinition Resolve(
            MapMaterialPassData pass = null,
            MapShaderDefinitionData shader = null,
            IReadOnlyList<MapMaterialSamplerData> samplers = null,
            IReadOnlyDictionary<string, Vector4> parameters = null,
            IReadOnlyDictionary<string, bool> switches = null)
        {
            return MapMaterialSemantics.Resolve(
                "Maps/Test/Material",
                1,
                false,
                pass,
                shader,
                samplers ?? Array.Empty<MapMaterialSamplerData>(),
                parameters ?? new Dictionary<string, Vector4>(),
                switches ?? new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                Array.Empty<string>());
        }

        private static MapMaterialPassData Pass(
            bool blend,
            uint source,
            uint destination,
            uint writeMask = 31,
            uint shaderHash = 0) =>
            new(
                shaderHash,
                new Dictionary<string, Vector4>(),
                new Dictionary<string, string>(),
                blend,
                source,
                destination,
                true,
                1,
                true,
                writeMask);

        private static MapShaderDefinitionData Shader(string path) =>
            new(
                path,
                Array.Empty<MapMaterialSamplerData>(),
                new Dictionary<string, Vector4>
                {
                    ["TintColor"] = Vector4.One
                },
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                true);
    }
}

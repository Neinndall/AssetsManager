using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapGeometryRendererTests
    {
        [Fact]
        public void DrawPlanRendersOnlyDefaultVisibilityLayer()
        {
            MapSceneData scene = Scene(
                meshes: new[]
                {
                    Mesh(visibility: 1, firstSubmesh: 0, submeshCount: 1),
                    Mesh(visibility: 2, firstSubmesh: 1, submeshCount: 1)
                },
                submeshes: new[]
                {
                    new MapGeometrySubmeshData(0, 3, 0),
                    new MapGeometrySubmeshData(3, 3, 0)
                },
                materials: new[] { Material("Maps/Test/Opaque") });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene);

            MapGeometryRenderer.DrawGroup group = Assert.Single(plan.OpaqueGroups);
            Assert.Equal(0, group.StartIndex);
            Assert.Empty(plan.TransparentGroups);
        }

        [Fact]
        public void DrawPlanStacksEveryMeshWhoseVisibilitySharesAnActiveFlag()
        {
            MapSceneData scene = Scene(
                meshes: new[]
                {
                    Mesh(visibility: 0b0000_0001, firstSubmesh: 0, submeshCount: 1),
                    Mesh(visibility: 0b0000_0100, firstSubmesh: 1, submeshCount: 1),
                    Mesh(visibility: 0b0000_1000, firstSubmesh: 2, submeshCount: 1),
                    Mesh(visibility: 0b0000_0101, firstSubmesh: 3, submeshCount: 1)
                },
                submeshes: new[]
                {
                    new MapGeometrySubmeshData(0, 3, 0),
                    new MapGeometrySubmeshData(3, 3, 0),
                    new MapGeometrySubmeshData(6, 3, 0),
                    new MapGeometrySubmeshData(9, 3, 0)
                },
                materials: new[] { Material("Maps/Test/Opaque") });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene, 0b0000_0101);

            Assert.Equal(3, plan.OpaqueGroups.Count);
            Assert.Contains(plan.OpaqueGroups, group => group.StartIndex == 0);
            Assert.Contains(plan.OpaqueGroups, group => group.StartIndex == 3);
            Assert.Contains(plan.OpaqueGroups, group => group.StartIndex == 9);
            Assert.DoesNotContain(plan.OpaqueGroups, group => group.StartIndex == 6);
        }

        [Fact]
        public void IndicatorWithoutBaseTextureIsNotDrawn()
        {
            MapMaterialDefinition indicator = Material(
                "Maps/Test/Indicator",
                shader: "Shaders/StaticMesh/Indicator_Faelights",
                withBaseTexture: false);
            MapSceneData scene = Scene(
                meshes: new[] { Mesh(1, 0, 1) },
                submeshes: new[] { new MapGeometrySubmeshData(0, 3, 0) },
                materials: new[] { indicator });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene);

            Assert.Empty(plan.Materials);
            Assert.Empty(plan.OpaqueGroups);
            Assert.Empty(plan.TransparentGroups);
        }

        [Fact]
        public void MeshCullDisabledOverridesMaterialCulling()
        {
            MapSceneData scene = Scene(
                meshes: new[]
                {
                    Mesh(1, 0, 1),
                    Mesh(1, 1, 1, MapGeometryMeshFlags.CullDisabled)
                },
                submeshes: new[]
                {
                    new MapGeometrySubmeshData(0, 3, 0),
                    new MapGeometrySubmeshData(3, 3, 0)
                },
                materials: new[] { Material("Maps/Test/Opaque") });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene);

            Assert.Equal(2, plan.Materials.Count);
            Assert.False(plan.Materials[0].RenderState.DoubleSided);
            Assert.True(plan.Materials[1].RenderState.DoubleSided);
        }

        [Fact]
        public void CutoutStaysOpaqueWhileAlphaBlendUsesTransparentPass()
        {
            MapMaterialRenderState cutout = State(MapMaterialBlendMode.Normal, cutout: true);
            MapMaterialRenderState transparent = State(MapMaterialBlendMode.Normal, cutout: false);
            MapSceneData scene = Scene(
                meshes: new[] { Mesh(1, 0, 2) },
                submeshes: new[]
                {
                    new MapGeometrySubmeshData(0, 3, 0),
                    new MapGeometrySubmeshData(3, 3, 1)
                },
                materials: new[]
                {
                    Material("Maps/Test/Cutout", state: cutout),
                    Material("Maps/Test/Transparent", state: transparent)
                });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene);

            Assert.Single(plan.OpaqueGroups);
            Assert.Single(plan.TransparentGroups);
        }

        [Fact]
        public void AdditiveMaterialUsesUnlitBackdropPath()
        {
            MapSceneData scene = Scene(
                meshes: new[] { Mesh(1, 0, 1) },
                submeshes: new[] { new MapGeometrySubmeshData(0, 3, 0) },
                materials: new[]
                {
                    Material("Maps/Test/Additive", state: State(MapMaterialBlendMode.Additive))
                });

            MapGeometryRenderer.DrawPlan plan = MapGeometryRenderer.BuildDrawPlan(scene);

            Assert.False(Assert.Single(plan.Materials).Lit);
            Assert.Single(plan.TransparentGroups);
        }

        [Fact]
        public void DefaultLightMatchesCurrentLtkRiftFallback()
        {
            MapGeometryRenderer.LightState light = MapGeometryRenderer.ResolveLight(null);

            Assert.Equal(0.4f, light.SunStrength, 4);
            Assert.Equal(0.6f, light.AmbientStrength, 4);
            Assert.Equal(Vector3.One, light.SunColor);
            Assert.Equal(Vector3.One, light.SkyColor);
            Assert.Equal(Vector3.One, light.GroundColor);
            Assert.Equal(Vector3.One, light.HorizonColor);
            Assert.True(light.Direction.X > 0f, "Viewport space mirrors the authored negative X direction.");
        }

        [Fact]
        public void AuthoredLightNormalizesSunAndSkySharesLikeCurrentLtk()
        {
            var sun = new MapSunData(
                new Vector3(-0.25f, 0.75f, -0.05f),
                Vector4.One,
                1f,
                Vector4.One,
                new Vector4(0.1f, 0.1f, 0.1f, 1f),
                1.5f);

            MapGeometryRenderer.LightState light = MapGeometryRenderer.ResolveLight(sun);

            Assert.Equal(0.4f, light.SunStrength, 4);
            Assert.Equal(0.6f, light.AmbientStrength, 4);
            Assert.InRange(light.Direction.Length(), 0.9999f, 1.0001f);
            Assert.True(light.Direction.X > 0f);
            Assert.True(light.GroundColor.X < 0.02f, "Authored sun colours are converted from sRGB to linear light.");
        }

        [Fact]
        public void PreviewSunOverrideUsesItsSharesWithoutRenormalizingThem()
        {
            var authored = new MapSunData(
                new Vector3(-0.25f, 0.75f, -0.05f),
                Vector4.One,
                1f,
                Vector4.One,
                Vector4.One,
                new Vector4(0.2f, 0.3f, 0.4f, 1f),
                1.5f,
                1.25f,
                false,
                Vector4.Zero,
                Vector4.Zero,
                Vector2.Zero,
                0f);
            var preview = new MapSunPreviewOverride(
                Vector3.UnitY,
                Vector4.One,
                0.9f,
                Vector4.One,
                Vector4.One,
                0.2f);
            MapSunData effective = MapPreviewSemantics.EffectiveSun(authored, preview);

            MapGeometryRenderer.LightState light = MapGeometryRenderer.ResolveLight(effective, preview);

            Assert.Equal(0.9f, light.SunStrength, 4);
            Assert.Equal(0.2f, light.AmbientStrength, 4);
            Assert.Equal(1.25f, light.LightMapColorScale, 4);
            Assert.True(light.HorizonColor.X < 0.04f, "Authored horizon stays carried through the preview override.");
        }

        [Fact]
        public void TextureSamplingSpacesMatchLtkBackdropContracts()
        {
            Assert.Equal(
                InternalFormat.Srgb8Alpha8,
                MapGeometryRenderer.TextureInternalFormat(MapGeometryRenderer.TextureSamplingSpace.SrgbColor));
            Assert.Equal(
                InternalFormat.Rgba8,
                MapGeometryRenderer.TextureInternalFormat(MapGeometryRenderer.TextureSamplingSpace.LinearRaw));
            Assert.True(MapGeometryRenderer.PreservesDirectXRowOrder);
        }

        [Fact]
        public void StockShaderPremultipliesOnlyAtRenderTime()
        {
            Assert.Contains("if (uPremultipliedAlpha != 0)", MapGeometryShaderSource.Fragment);
            Assert.Contains("color *= alpha;", MapGeometryShaderSource.Fragment);
        }

        [Fact]
        public void UvRepeatForcesRepeatWrappingLikeLtkBinding()
        {
            MapMaterialDefinition material = Material(
                "Maps/Test/Tiled",
                uvRepeat: new Vector2(2f, 3f),
                wrapU: MapTextureWrap.Clamp,
                wrapV: MapTextureWrap.Mirror);
            MapSceneData scene = Scene(
                meshes: new[] { Mesh(1, 0, 1) },
                submeshes: new[] { new MapGeometrySubmeshData(0, 3, 0) },
                materials: new[] { material });

            MapGeometryRenderer.BoundMaterial bound = Assert.Single(
                MapGeometryRenderer.BuildDrawPlan(scene).Materials);

            Assert.Equal(new Vector2(2f, 3f), bound.UvRepeat);
            Assert.Equal(MapTextureWrap.Repeat, bound.WrapU);
            Assert.Equal(MapTextureWrap.Repeat, bound.WrapV);
        }

        private static MapSceneData Scene(
            IReadOnlyList<MapGeometryMeshData> meshes,
            IReadOnlyList<MapGeometrySubmeshData> submeshes,
            IReadOnlyList<MapMaterialDefinition> materials)
        {
            string[] names = new string[materials.Count];
            for (int i = 0; i < materials.Count; i++)
                names[i] = materials[i].Name;

            var geometry = new MapGeometryData(
                new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ },
                new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
                new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
                null,
                new uint[] { 0, 1, 2, 0, 1, 2 },
                meshes,
                submeshes,
                names);
            return new MapSceneData(
                null,
                null,
                geometry,
                null,
                materials,
                new Dictionary<string, MapTextureImage>(),
                Array.Empty<MapPlaceableChunkData>(),
                Array.Empty<MapCharacterData>(),
                Array.Empty<MapParticleData>(),
                new MapParticleSystemCatalog(
                    new Dictionary<uint, VfxSystemDefinition>(),
                    new Dictionary<uint, uint>(),
                    Array.Empty<MapParticleSystemGroupData>()));
        }

        private static MapGeometryMeshData Mesh(
            byte visibility,
            int firstSubmesh,
            int submeshCount,
            MapGeometryMeshFlags flags = MapGeometryMeshFlags.None) =>
            new(
                Vector3.Zero,
                Vector3.One,
                visibility,
                0,
                flags,
                firstSubmesh,
                submeshCount,
                default(EnvironmentAssetMeshRenderFlags),
                0,
                0);

        private static MapMaterialDefinition Material(
            string name,
            string shader = "Shaders/StaticMesh/DefaultEnv",
            bool withBaseTexture = true,
            MapMaterialRenderState state = null,
            Vector2? uvRepeat = null,
            MapTextureWrap wrapU = MapTextureWrap.Repeat,
            MapTextureWrap wrapV = MapTextureWrap.Repeat) =>
            new(
                name,
                1,
                false,
                false,
                shader,
                withBaseTexture
                    ? new MapBaseTexture(
                        "Diffuse_Texture",
                        new MapTextureReference("assets/test.tex", 1),
                        MapMaterialBaseRule.Exact,
                        wrapU,
                        wrapV)
                    : null,
                null,
                null,
                null,
                uvRepeat,
                null,
                state ?? MapMaterialRenderState.Default,
                Array.Empty<string>());

        private static MapMaterialRenderState State(
            MapMaterialBlendMode mode,
            bool cutout = false) =>
            new(
                mode,
                mode == MapMaterialBlendMode.Opaque ? MapBlendFactor.One : MapBlendFactor.SourceAlpha,
                mode == MapMaterialBlendMode.Additive ? MapBlendFactor.One : MapBlendFactor.OneMinusSourceAlpha,
                false,
                cutout,
                false,
                false,
                true,
                true);
    }
}

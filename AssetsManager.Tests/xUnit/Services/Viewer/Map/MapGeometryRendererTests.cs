using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.Map;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
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
                new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(),
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

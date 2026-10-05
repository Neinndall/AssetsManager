using AssetsManager.Services.Viewer.Resources;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Immutable decoded input of one loaded MAPGEO scene before GPU/runtime construction.
    /// </summary>
    internal sealed class MapSceneData
    {
        public MapSceneSource Source { get; }
        public MapSceneAssets Assets { get; }
        public MapGeometryData Geometry { get; }
        public BinTree MaterialsDocument { get; }
        public BinTree ShaderDefinitions { get; }

        /// <summary>
        /// StaticMaterialDef objects of the owning Map BIN (map&lt;id&gt;.bin). Map structures such as the
        /// esports banners link their materials there instead of in their own skin BIN.
        /// </summary>
        public BinTree SharedMaterials { get; }
        public IReadOnlyList<MapMaterialDefinition> Materials { get; }
        public IReadOnlyDictionary<string, MapTextureImage> Textures { get; }
        public IReadOnlyDictionary<string, MapTextureImage> ProgramTextures { get; }
        public IReadOnlyDictionary<string, MapTextureImage> Lightmaps { get; }
        public IReadOnlyList<MapPlaceableChunkData> Placeables { get; }
        public IReadOnlyList<MapCharacterData> Characters { get; }
        public IReadOnlyList<MapParticleData> Particles { get; }
        public MapParticleSystemCatalog ParticleSystems { get; }
        public IReadOnlyList<MapOutlineChunkData> Outline { get; }
        public MapSceneVisibility Visibility { get; }
        public MapVisibilityState OpeningVisibility => Visibility.Opening;
        public int OpeningVisibilityFlags => Visibility.Opening.Flags;
        public Vector3? Origin { get; }
        public MapSunData Sun { get; }
        public MapLightGridData LightGrid { get; }
        public MapPostEffectsData PostEffects { get; }
        public MapSsaoData AmbientOcclusion { get; }

        /// <summary>Terrain bounds and MapSkin environment assets read by the map shaders; null when the map declares none.</summary>
        public MapTerrainData Terrain { get; }

        /// <summary>Decoded MapSkin environment cubes, keyed by the reference each skin declares.</summary>
        public IReadOnlyDictionary<MapTextureReference, CubeMapData> EnvironmentCubes { get; }

        public MapSceneData(
            MapSceneSource source,
            MapSceneAssets assets,
            MapGeometryData geometry,
            BinTree materialsDocument,
            IReadOnlyList<MapMaterialDefinition> materials,
            IReadOnlyDictionary<string, MapTextureImage> textures,
            IReadOnlyList<MapPlaceableChunkData> placeables,
            IReadOnlyList<MapCharacterData> characters,
            IReadOnlyList<MapParticleData> particles,
            MapParticleSystemCatalog particleSystems,
            Vector3? origin = null,
            IReadOnlyList<MapOutlineChunkData> outline = null,
            MapSunData sun = null,
            MapPostEffectsData postEffects = null,
            MapSsaoData ambientOcclusion = null,
            IReadOnlyDictionary<string, MapTextureImage> lightmaps = null,
            IReadOnlyDictionary<string, MapTextureImage> programTextures = null,
            int openingVisibilityFlags = 1,
            BinTree shaderDefinitions = null,
            MapLightGridData lightGrid = null,
            MapSceneVisibility visibility = null,
            BinTree sharedMaterials = null,
            MapTerrainData terrain = null,
            IReadOnlyDictionary<MapTextureReference, CubeMapData> environmentCubes = null)
        {
            Source = source;
            Assets = assets;
            Geometry = geometry;
            MaterialsDocument = materialsDocument;
            ShaderDefinitions = shaderDefinitions;
            SharedMaterials = sharedMaterials;
            Materials = materials;
            Textures = textures;
            ProgramTextures = programTextures ?? new Dictionary<string, MapTextureImage>(System.StringComparer.Ordinal);
            Lightmaps = lightmaps ?? new Dictionary<string, MapTextureImage>(System.StringComparer.OrdinalIgnoreCase);
            Placeables = placeables;
            Characters = characters;
            Particles = particles;
            ParticleSystems = particleSystems;
            Outline = outline ?? System.Array.Empty<MapOutlineChunkData>();
            Visibility = visibility ?? MapSceneVisibility.Empty.WithOpening(
                MapVisibilityState.FromFlags(openingVisibilityFlags));
            Origin = origin;
            Sun = sun;
            LightGrid = lightGrid;
            PostEffects = postEffects;
            AmbientOcclusion = ambientOcclusion;
            Terrain = terrain;
            EnvironmentCubes = environmentCubes ?? new Dictionary<MapTextureReference, CubeMapData>();
        }
    }
}

using System.Collections.Generic;
using System.Numerics;
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
        public IReadOnlyList<MapMaterialDefinition> Materials { get; }
        public IReadOnlyDictionary<string, MapTextureImage> Textures { get; }
        public IReadOnlyDictionary<string, MapTextureImage> ProgramTextures { get; }
        public IReadOnlyDictionary<string, MapTextureImage> Lightmaps { get; }
        public IReadOnlyList<MapPlaceableChunkData> Placeables { get; }
        public IReadOnlyList<MapCharacterData> Characters { get; }
        public IReadOnlyList<MapParticleData> Particles { get; }
        public MapParticleSystemCatalog ParticleSystems { get; }
        public IReadOnlyList<MapOutlineChunkData> Outline { get; }
        public int OpeningVisibilityFlags { get; }
        public Vector3? Origin { get; }
        public MapSunData Sun { get; }
        public MapPostEffectsData PostEffects { get; }
        public MapSsaoData AmbientOcclusion { get; }

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
            int openingVisibilityFlags = 1)
        {
            Source = source;
            Assets = assets;
            Geometry = geometry;
            MaterialsDocument = materialsDocument;
            Materials = materials;
            Textures = textures;
            ProgramTextures = programTextures ?? new Dictionary<string, MapTextureImage>(System.StringComparer.Ordinal);
            Lightmaps = lightmaps ?? new Dictionary<string, MapTextureImage>(System.StringComparer.OrdinalIgnoreCase);
            Placeables = placeables;
            Characters = characters;
            Particles = particles;
            ParticleSystems = particleSystems;
            Outline = outline ?? System.Array.Empty<MapOutlineChunkData>();
            OpeningVisibilityFlags = openingVisibilityFlags;
            Origin = origin;
            Sun = sun;
            PostEffects = postEffects;
            AmbientOcclusion = ambientOcclusion;
        }
    }
}

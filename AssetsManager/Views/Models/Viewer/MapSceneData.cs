using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media.Imaging;
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
        public IReadOnlyDictionary<string, BitmapSource> Textures { get; }
        public IReadOnlyList<MapPlaceableChunkData> Placeables { get; }
        public IReadOnlyList<MapCharacterData> Characters { get; }
        public IReadOnlyList<MapParticleData> Particles { get; }
        public MapParticleSystemCatalog ParticleSystems { get; }
        public IReadOnlyList<MapOutlineChunkData> Outline { get; }
        public Vector3? Origin { get; }

        public MapSceneData(
            MapSceneSource source,
            MapSceneAssets assets,
            MapGeometryData geometry,
            BinTree materialsDocument,
            IReadOnlyList<MapMaterialDefinition> materials,
            IReadOnlyDictionary<string, BitmapSource> textures,
            IReadOnlyList<MapPlaceableChunkData> placeables,
            IReadOnlyList<MapCharacterData> characters,
            IReadOnlyList<MapParticleData> particles,
            MapParticleSystemCatalog particleSystems,
            Vector3? origin = null,
            IReadOnlyList<MapOutlineChunkData> outline = null)
        {
            Source = source;
            Assets = assets;
            Geometry = geometry;
            MaterialsDocument = materialsDocument;
            Materials = materials;
            Textures = textures;
            Placeables = placeables;
            Characters = characters;
            Particles = particles;
            ParticleSystems = particleSystems;
            Outline = outline ?? System.Array.Empty<MapOutlineChunkData>();
            Origin = origin;
        }
    }
}

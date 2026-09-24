using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Opens one concrete map scene and decodes only the assets that belong to it.
    /// </summary>
    internal sealed class MapSceneLoadingService
    {
        private const string ShaderDefinitionsPath = "data/shaders/shaders.bin";
        internal const string BackdropSkyPath = "assets/maps/skyboxes/riots_sru_skybox_cubemap.dds";

        private readonly MapAssetResolver _assetResolver;
        private readonly MapGeometryDecoder _geometryDecoder;
        private readonly MapMaterialParser _materialParser;
        private readonly MapPlaceableParser _placeableParser;
        private readonly MapCharacterParser _characterParser;
        private readonly MapParticleParser _particleParser;
        private readonly MapParticleSystemParser _particleSystemParser;
        private readonly MapTextureLoadingService _textureLoadingService;
        private readonly HashResolverService _hashResolver;
        private readonly LogService _logService;

        public MapSceneLoadingService(
            MapAssetResolver assetResolver,
            MapGeometryDecoder geometryDecoder,
            MapMaterialParser materialParser,
            MapPlaceableParser placeableParser,
            MapCharacterParser characterParser,
            MapParticleParser particleParser,
            MapParticleSystemParser particleSystemParser,
            MapTextureLoadingService textureLoadingService,
            HashResolverService hashResolver,
            LogService logService)
        {
            _assetResolver = assetResolver;
            _geometryDecoder = geometryDecoder;
            _materialParser = materialParser;
            _placeableParser = placeableParser;
            _characterParser = characterParser;
            _particleParser = particleParser;
            _particleSystemParser = particleSystemParser;
            _textureLoadingService = textureLoadingService;
            _hashResolver = hashResolver;
            _logService = logService;
        }

        internal async Task<VfxCubeMapData> LoadBackdropSkyAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            MapResolvedAsset sky = await _assetResolver.ResolveVirtualAsync(
                BackdropSkyPath,
                projectRoot,
                cancellationToken);
            if (sky == null) return null;

            await using Stream stream = await _assetResolver.OpenReadAsync(sky, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return stream == null ? null : VfxCubeMapDecoder.Decode(stream);
        }

        public Task<MapSceneData> LoadAsync(
            string mapFilePath,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadAsync(MapSceneSource.FromMapFile(mapFilePath, projectRoot), cancellationToken);

        public Task<MapSceneData> LoadAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default) =>
            LoadCoreAsync(source, includePreviewTextures: true, cancellationToken);

        internal Task<MapSceneData> LoadBackdropAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default) =>
            LoadCoreAsync(source, includePreviewTextures: false, cancellationToken);

        private async Task<MapSceneData> LoadCoreAsync(
            MapSceneSource source,
            bool includePreviewTextures,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            if (_hashResolver != null)
            {
                await _hashResolver.LoadHashesAsync();
                await _hashResolver.LoadBinHashesAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            MapSceneAssets assets = await _assetResolver.ResolveSceneAssetsAsync(source, cancellationToken);
            if (assets.Geometry == null)
            {
                _logService?.LogWarning($"MAPGEO geometry could not be resolved: {source.Map.GeometryVirtualPath}");
                return null;
            }

            MapGeometryData geometry;
            await using (Stream stream = await _assetResolver.OpenReadAsync(assets.Geometry, cancellationToken))
            {
                if (stream == null)
                {
                    _logService?.LogWarning($"MAPGEO geometry could not be opened: {assets.Geometry.VirtualPath}");
                    return null;
                }

                geometry = await Task.Run(
                    () => _geometryDecoder.Decode(stream),
                    cancellationToken);
            }

            BinTree materials = await OpenBinTreeAsync(assets.Materials, cancellationToken);
            MapResolvedAsset shaderAsset = await _assetResolver.ResolveVirtualAsync(
                ShaderDefinitionsPath,
                source.ProjectRoot,
                cancellationToken);
            BinTree shaders = await OpenOptionalBinTreeAsync(shaderAsset, cancellationToken);
            IReadOnlyList<MapMaterialDefinition> materialDefinitions = _materialParser.Parse(
                materials,
                shaders,
                geometry.Materials);
            IReadOnlyList<MapPlaceableChunkData> placeables = _placeableParser.Parse(materials);
            IReadOnlyList<MapCharacterData> characters = _characterParser.Parse(placeables);
            IReadOnlyList<MapParticleData> particles = _particleParser.Parse(placeables);
            IReadOnlyList<MapOutlineChunkData> outline = MapOutlineSemantics.Build(
                placeables,
                characters,
                particles,
                _hashResolver);
            int openingVisibilityFlags = MapGeometrySemantics.OpeningFlags(geometry);
            IReadOnlyList<MapParticleData> playedParticles = MapParticleSemantics.PlayedForFlags(
                particles,
                openingVisibilityFlags);
            MapParticleSystemCatalog particleSystems = _particleSystemParser.Parse(
                materials,
                MapParticleSemantics.GroupBySystem(playedParticles),
                _hashResolver == null ? null : _hashResolver.ResolveHash,
                _hashResolver == null ? null : _hashResolver.ResolveBinEntry);
            MapSunData sun = MapSunParser.Parse(materials, source.Map);
            MapPostEffectsData postEffects = MapPostEffectsParser.Parse(materials, source.Map);
            MapSsaoData ambientOcclusion = MapSsaoParser.Parse(materials, source.Map);
            IReadOnlyDictionary<string, MapTextureImage> textures = includePreviewTextures
                ? await _textureLoadingService.LoadPreviewAsync(
                    materialDefinitions,
                    source.ProjectRoot,
                    cancellationToken)
                : new Dictionary<string, MapTextureImage>();
            IReadOnlyDictionary<string, MapTextureImage> programTextures = includePreviewTextures
                ? await _textureLoadingService.LoadProgramPreviewAsync(
                    materialDefinitions,
                    source.ProjectRoot,
                    cancellationToken)
                : new Dictionary<string, MapTextureImage>(StringComparer.Ordinal);
            IReadOnlyDictionary<string, MapTextureImage> lightmaps = includePreviewTextures
                ? await _textureLoadingService.LoadLightmapsPreviewAsync(
                    geometry.Lightmaps,
                    source.ProjectRoot,
                    cancellationToken)
                : new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase);

            cancellationToken.ThrowIfCancellationRequested();
            string textureStatus = includePreviewTextures ? textures.Count.ToString() : "deferred";
            string programTextureStatus = includePreviewTextures ? programTextures.Count.ToString() : "deferred";
            string lightmapStatus = includePreviewTextures ? lightmaps.Count.ToString() : "deferred";
            _logService?.LogDebug(
                $"MAPGEO scene decoded: {source.Map} " +
                $"meshes={geometry.Meshes.Count}, submeshes={geometry.Submeshes.Count}, " +
                $"materials={geometry.Materials.Count}, textures={textureStatus}, programTextures={programTextureStatus}, lightmaps={lightmapStatus}, " +
                $"chunks={placeables.Count}, placeables={placeables.Sum(chunk => chunk.Items.Count)}, " +
                $"characters={characters.Count}, particles={particles.Count}, " +
                $"openingFlags=0x{openingVisibilityFlags:x2}, particleSystems={particleSystems.Groups.Count}, sun={(sun == null ? "default" : "authored")}, " +
                $"postEffects={(postEffects?.DrawsAnything == true ? "on" : "off")}, " +
                $"ssao={(ambientOcclusion?.DrawsAnything == true ? "on" : "off")}.");
            return new MapSceneData(
                source,
                assets,
                geometry,
                materials,
                materialDefinitions,
                textures,
                placeables,
                characters,
                particles,
                particleSystems,
                MapGeometrySemantics.CalculateOriginForFlags(geometry, openingVisibilityFlags),
                outline,
                sun,
                postEffects,
                ambientOcclusion,
                lightmaps,
                programTextures,
                openingVisibilityFlags);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewTexturesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadPreviewAsync(
                scene.Materials,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        public Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullTexturesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadFullAsync(
                scene.Materials,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewProgramTexturesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadProgramPreviewAsync(
                scene.Materials,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullProgramTexturesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadProgramFullAsync(
                scene.Materials,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewLightmapsAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadLightmapsPreviewAsync(
                scene.Geometry.Lightmaps,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullLightmapsAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadLightmapsFullAsync(
                scene.Geometry.Lightmaps,
                scene.Source.ProjectRoot,
                cancellationToken,
                onLoaded);
        }

        private async Task<BinTree> OpenBinTreeAsync(
            MapResolvedAsset asset,
            CancellationToken cancellationToken)
        {
            if (asset == null)
                return null;

            await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
            return stream == null
                ? null
                : await Task.Run(() => new BinTree(stream), cancellationToken);
        }

        private async Task<BinTree> OpenOptionalBinTreeAsync(
            MapResolvedAsset asset,
            CancellationToken cancellationToken)
        {
            if (asset == null)
                return null;

            try
            {
                return await OpenBinTreeAsync(asset, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogDebug(
                    $"MAP optional BIN unavailable '{asset.VirtualPath}': {ex.Message}");
                return null;
            }
        }
    }
}

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

        public async Task<MapSceneData> LoadAsync(
            string geometryFilePath,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            MapSceneSource source = MapSceneSource.FromGeometryFile(geometryFilePath, projectRoot);
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
                _logService.LogWarning($"MAPGEO geometry could not be resolved: {source.Map.GeometryVirtualPath}");
                return null;
            }

            MapGeometryData geometry;
            await using (Stream stream = await _assetResolver.OpenReadAsync(assets.Geometry, cancellationToken))
            {
                if (stream == null)
                {
                    _logService.LogWarning($"MAPGEO geometry could not be opened: {assets.Geometry.VirtualPath}");
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
            IReadOnlyList<MapParticleData> playedParticles = MapParticleSemantics.PlayedOnLayer(particles, 0);
            MapParticleSystemCatalog particleSystems = _particleSystemParser.Parse(
                materials,
                MapParticleSemantics.GroupBySystem(playedParticles));
            IReadOnlyDictionary<string, System.Windows.Media.Imaging.BitmapSource> textures =
                await _textureLoadingService.LoadPreviewAsync(
                    materialDefinitions,
                    source.ProjectRoot,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _logService.LogDebug(
                $"MAPGEO scene decoded: {source.Map} " +
                $"meshes={geometry.Meshes.Count}, submeshes={geometry.Submeshes.Count}, " +
                $"materials={geometry.Materials.Count}, textures={textures.Count}, " +
                $"chunks={placeables.Count}, placeables={placeables.Sum(chunk => chunk.Items.Count)}, " +
                $"characters={characters.Count}, particles={particles.Count}, " +
                $"particleSystems={particleSystems.Groups.Count}.");
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
                MapGeometrySemantics.CalculateOrigin(geometry),
                outline);
        }

        public Task<IReadOnlyDictionary<string, System.Windows.Media.Imaging.BitmapSource>> LoadFullTexturesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return _textureLoadingService.LoadFullAsync(
                scene.Materials,
                scene.Source.ProjectRoot,
                cancellationToken);
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

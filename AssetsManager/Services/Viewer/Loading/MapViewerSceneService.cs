using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Public DI boundary for the Viewer MAP workflow. The decoded scene and runtime stay internal;
    /// the window only needs one service to request a complete scene load.
    /// </summary>
    public sealed class MapViewerSceneService
    {
        private readonly MapSceneLoadingService _sceneLoadingService;
        private readonly MapSceneRuntimeFactory _runtimeFactory;

        internal MapViewerSceneService(
            MapSceneLoadingService sceneLoadingService,
            MapSceneRuntimeFactory runtimeFactory)
        {
            _sceneLoadingService = sceneLoadingService;
            _runtimeFactory = runtimeFactory;
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewTexturesAsync(runtime.Scene, cancellationToken);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullTexturesAsync(runtime.Scene, cancellationToken);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewProgramTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewProgramTexturesAsync(runtime.Scene, cancellationToken);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullProgramTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullProgramTexturesAsync(runtime.Scene, cancellationToken);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewLightmapsAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewLightmapsAsync(runtime.Scene, cancellationToken);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullLightmapsAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullLightmapsAsync(runtime.Scene, cancellationToken);
        }

        internal Task<VfxSceneResourceContext> CreateVfxResourcesAsync(
            MapCharacterVfxCatalog catalog,
            string projectRoot,
            CancellationToken cancellationToken = default)
            => _runtimeFactory.CreateVfxResourcesAsync(catalog, projectRoot, cancellationToken);

        internal async Task<MapSceneRuntime> LoadBackdropAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            MapSceneData scene = await _sceneLoadingService.LoadBackdropAsync(source, cancellationToken);
            return scene == null
                ? null
                : new MapSceneRuntime(
                    scene,
                    Array.Empty<MapCharacterRuntimeGroup>(),
                    new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()));
        }

        internal Task<MapSceneRuntime> LoadRuntimeAssetsAsync(
            MapSceneRuntime backdrop,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return _runtimeFactory.CreateAsync(backdrop.Scene, cancellationToken);
        }

        internal Task<MapSceneRuntime> LoadAsync(
            string mapFilePath,
            string projectRoot,
            CancellationToken cancellationToken = default) =>
            LoadAsync(MapSceneSource.FromMapFile(mapFilePath, projectRoot), cancellationToken);

        internal async Task<MapSceneRuntime> LoadAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            MapSceneData scene = await _sceneLoadingService.LoadAsync(
                source,
                cancellationToken);
            if (scene == null)
                return null;

            return await _runtimeFactory.CreateAsync(scene, cancellationToken);
        }
    }
}

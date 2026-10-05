using AssetsManager.Services.Viewer.Resources;
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
        private readonly MapTextureLoadingService _textureLoadingService;

        internal MapViewerSceneService(
            MapSceneLoadingService sceneLoadingService,
            MapSceneRuntimeFactory runtimeFactory,
            MapTextureLoadingService textureLoadingService)
        {
            _sceneLoadingService = sceneLoadingService;
            _runtimeFactory = runtimeFactory;
            _textureLoadingService = textureLoadingService;
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewTexturesAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullTexturesAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewProgramTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewProgramTexturesAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullProgramTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullProgramTexturesAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadPreviewLightmapsAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadPreviewLightmapsAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<IReadOnlyDictionary<string, MapTextureImage>> LoadFullLightmapsAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default,
            Action<string, MapTextureImage> onLoaded = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullLightmapsAsync(runtime.Scene, cancellationToken, onLoaded);
        }

        internal Task<VfxSceneResourceContext> CreateVfxResourcesAsync(
            MapCharacterVfxCatalog catalog,
            string projectRoot,
            CancellationToken cancellationToken = default)
            => _runtimeFactory.CreateVfxResourcesAsync(catalog, projectRoot, cancellationToken);

        internal Task<CubeMapData> LoadBackdropSkyAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
            => _sceneLoadingService.LoadBackdropSkyAsync(projectRoot, cancellationToken);

        internal async Task<MapSceneRuntime> LoadBackdropAsync(
            MapSceneSource source,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            MapSceneData scene = await _sceneLoadingService.LoadBackdropAsync(source, cancellationToken);
            if (scene == null)
                return null;

            return AttachTextureRetention(new MapSceneRuntime(
                scene,
                Array.Empty<MapCharacterRuntimeGroup>(),
                new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>())));
        }

        internal async Task<MapSceneRuntime> LoadRuntimeAssetsAsync(
            MapSceneRuntime backdrop,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return AttachTextureRetention(await _runtimeFactory.CreateAsync(backdrop.Scene, cancellationToken));
        }

        internal Task<IReadOnlyList<MapCharacterRuntimeGroup>> LoadCharacterAssetsAsync(
            MapSceneRuntime backdrop,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return _runtimeFactory.LoadCharactersAsync(backdrop.Scene, backdrop.Visibility, cancellationToken);
        }

        internal Task<IReadOnlyList<MapCharacterRuntimeGroup>> LoadCharacterAssetsAsync(
            MapSceneRuntime backdrop,
            MapVisibilityState visibility,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return _runtimeFactory.LoadCharactersAsync(backdrop.Scene, visibility, cancellationToken);
        }

        internal Task<MapParticleSceneRuntime> LoadParticleAssetsAsync(
            MapSceneRuntime backdrop,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return _runtimeFactory.LoadParticlesAsync(backdrop.Scene, backdrop.Visibility, cancellationToken);
        }

        internal Task<MapParticleSceneRuntime> LoadParticleAssetsAsync(
            MapSceneRuntime backdrop,
            MapVisibilityState visibility,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(backdrop);
            return _runtimeFactory.LoadParticlesAsync(backdrop.Scene, visibility, cancellationToken);
        }

        /// <summary>Plans the structures of another map state, reusing the skins the runtime already holds.</summary>
        internal Task<MapCharacterPlan> PlanCharacterAssetsAsync(
            MapSceneRuntime runtime,
            MapVisibilityState visibility,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _runtimeFactory.PlanCharactersAsync(
                runtime.Scene,
                visibility,
                runtime.CharacterReuseCandidates(),
                runtime.CharacterGeneration,
                cancellationToken);
        }

        /// <summary>Plans the placed VFX of another map state, keeping continuing placements and resources.</summary>
        internal Task<MapParticlePlan> PlanParticleAssetsAsync(
            MapSceneRuntime runtime,
            MapVisibilityState visibility,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _runtimeFactory.PlanParticlesAsync(
                runtime.Scene,
                visibility,
                runtime.Particles,
                runtime.ParticleGeneration,
                cancellationToken);
        }

        private MapSceneRuntime AttachTextureRetention(MapSceneRuntime runtime)
        {
            runtime?.SetBackdropTextureRetainer(_textureLoadingService.HoldDecodedTexture);
            return runtime;
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

            return AttachTextureRetention(await _runtimeFactory.CreateAsync(scene, cancellationToken));
        }
    }
}

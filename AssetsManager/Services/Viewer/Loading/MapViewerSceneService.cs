using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Runtime;
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

        internal Task<IReadOnlyDictionary<string, BitmapSource>> LoadFullTexturesAsync(
            MapSceneRuntime runtime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            return _sceneLoadingService.LoadFullTexturesAsync(runtime.Scene, cancellationToken);
        }

        internal async Task<MapSceneRuntime> LoadAsync(
            string geometryFilePath,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            MapSceneData scene = await _sceneLoadingService.LoadAsync(
                geometryFilePath,
                projectRoot,
                cancellationToken);
            if (scene == null)
                return null;

            return await _runtimeFactory.CreateAsync(scene, cancellationToken);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Builds the non-GPU runtime state for one decoded MAP scene. Character skins are loaded once
    /// per authored skin path and particle systems share the scene-scoped resource context.
    /// </summary>
    internal sealed class MapSceneRuntimeFactory
    {
        private readonly MapCharacterLoadingService _characterLoadingService;
        private readonly MapAssetResolver _assetResolver;
        private readonly HashResolverService _hashResolver;
        private readonly LogService _logService;

        public MapSceneRuntimeFactory(
            MapCharacterLoadingService characterLoadingService,
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService)
        {
            _characterLoadingService = characterLoadingService;
            _assetResolver = assetResolver;
            _hashResolver = hashResolver;
            _logService = logService;
        }

        internal async Task<MapSceneRuntime> CreateAsync(
            MapSceneData scene,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scene);

            Task<IReadOnlyList<MapCharacterRuntimeGroup>> characters =
                LoadCharactersAsync(scene, cancellationToken);
            Task<MapParticleSceneRuntime> particles = MapParticleSceneRuntime.CreateAsync(
                scene,
                _assetResolver,
                _hashResolver,
                _logService,
                cancellationToken);

            try
            {
                await Task.WhenAll(characters, particles);
                cancellationToken.ThrowIfCancellationRequested();
                return new MapSceneRuntime(scene, await characters, await particles);
            }
            catch
            {
                if (characters.IsCompletedSuccessfully)
                {
                    foreach (MapCharacterRuntimeGroup group in characters.Result)
                        group?.Dispose();
                }
                if (particles.IsCompletedSuccessfully)
                    particles.Result?.Dispose();
                throw;
            }
        }

        internal Task<VfxSceneResourceContext> CreateVfxResourcesAsync(
            MapCharacterVfxCatalog catalog,
            string projectRoot,
            CancellationToken cancellationToken = default)
            => VfxSceneResourceContext.CreateAsync(
                catalog?.Systems,
                projectRoot,
                _assetResolver,
                _hashResolver,
                _logService,
                cancellationToken,
                catalog?.OwnerSceneContext);

        private async Task<IReadOnlyList<MapCharacterRuntimeGroup>> LoadCharactersAsync(
            MapSceneData scene,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MapCharacterData> stood = MapCharacterSemantics.StoodOnLayer(
                scene.Characters,
                MapGeometryData.DefaultLayer);
            if (stood.Count == 0)
                return Array.Empty<MapCharacterRuntimeGroup>();

            IGrouping<string, MapCharacterData>[] skins = stood
                .Where(character => !string.IsNullOrWhiteSpace(character.Skin))
                .GroupBy(character => character.Skin, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Task<MapCharacterRuntimeGroup>[] loads = skins
                .Select(group => LoadCharacterGroupAsync(
                    group.Key,
                    group.ToArray(),
                    scene.Source.ProjectRoot,
                    cancellationToken))
                .ToArray();

            try
            {
                MapCharacterRuntimeGroup[] loaded = await Task.WhenAll(loads);
                return loaded.Where(group => group != null).ToArray();
            }
            catch
            {
                DisposeCompletedCharacterLoads(loads);
                throw;
            }
        }

        internal static void DisposeCompletedCharacterLoads(
            IEnumerable<Task<MapCharacterRuntimeGroup>> loads)
        {
            if (loads == null) return;
            foreach (Task<MapCharacterRuntimeGroup> load in loads)
            {
                if (load?.Status == TaskStatus.RanToCompletion)
                    load.Result?.Dispose();
            }
        }

        private async Task<MapCharacterRuntimeGroup> LoadCharacterGroupAsync(
            string skin,
            IReadOnlyList<MapCharacterData> placements,
            string projectRoot,
            CancellationToken cancellationToken)
        {
            try
            {
                MapCharacterAssetData asset = await _characterLoadingService.LoadAsync(
                    skin,
                    projectRoot,
                    cancellationToken);
                if (asset == null)
                    return null;

                var animation = new MapCharacterAnimationRuntime(_assetResolver, _logService);
                try
                {
                    await animation.PrepareAsync(
                        asset,
                        placements.Select(placement => placement.Animation),
                        projectRoot,
                        cancellationToken);
                    return new MapCharacterRuntimeGroup(asset, animation, placements);
                }
                catch
                {
                    animation.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"MAP structure skin unavailable '{skin}': {ex.Message}");
                return null;
            }
        }
    }
}

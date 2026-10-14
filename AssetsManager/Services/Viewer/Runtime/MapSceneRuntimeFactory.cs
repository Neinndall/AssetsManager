using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
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
                LoadCharactersAsync(scene, scene.OpeningVisibility, cancellationToken);
            Task<MapParticleSceneRuntime> particles =
                LoadParticlesAsync(scene, scene.OpeningVisibility, cancellationToken);

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

        internal Task<IReadOnlyList<MapCharacterRuntimeGroup>> LoadCharactersAsync(
            MapSceneData scene,
            CancellationToken cancellationToken)
            => LoadCharactersAsync(scene, scene?.OpeningVisibility, cancellationToken);

        internal async Task<IReadOnlyList<MapCharacterRuntimeGroup>> LoadCharactersAsync(
            MapSceneData scene,
            MapVisibilityState visibility,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scene);
            IReadOnlyList<MapCharacterData> stood = MapCharacterSemantics.StoodFor(
                scene.Characters,
                scene.Visibility,
                visibility ?? scene.OpeningVisibility);
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
                    scene,
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

        /// <summary>
        /// Plans the structures of a map state against the groups the runtime already holds: a skin
        /// is reused when its animations are prepared, and only missing skins are loaded. Decisions are
        /// taken before the first await so the live groups are only read on the calling (render) thread.
        /// </summary>
        internal async Task<MapCharacterPlan> PlanCharactersAsync(
            MapSceneData scene,
            MapVisibilityState visibility,
            IReadOnlyDictionary<string, MapCharacterRuntimeGroup> candidates,
            long generation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scene);
            IReadOnlyList<MapCharacterData> stood = MapCharacterSemantics.StoodFor(
                scene.Characters,
                scene.Visibility,
                visibility ?? scene.OpeningVisibility);
            IGrouping<string, MapCharacterData>[] skins = stood
                .Where(character => !string.IsNullOrWhiteSpace(character.Skin))
                .GroupBy(character => character.Skin, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var reused = new MapCharacterRuntimeGroup[skins.Length];
            var loads = new Task<MapCharacterRuntimeGroup>[skins.Length];
            var placements = new MapCharacterData[skins.Length][];
            for (int index = 0; index < skins.Length; index++)
            {
                placements[index] = skins[index].ToArray();
                if (candidates != null &&
                    candidates.TryGetValue(skins[index].Key, out MapCharacterRuntimeGroup candidate) &&
                    candidate is { IsDisposed: false, Asset: not null } &&
                    candidate.Animation?.IsPrepared(placements[index].Select(placement => placement.Animation)) == true)
                {
                    reused[index] = candidate;
                    loads[index] = Task.FromResult<MapCharacterRuntimeGroup>(null);
                }
                else
                {
                    loads[index] = LoadCharacterGroupAsync(
                        skins[index].Key,
                        placements[index],
                        scene,
                        cancellationToken);
                }
            }

            MapCharacterRuntimeGroup[] loaded;
            try
            {
                loaded = await Task.WhenAll(loads);
            }
            catch
            {
                DisposeCompletedCharacterLoads(loads);
                throw;
            }

            var entries = new List<MapCharacterPlan.Entry>(skins.Length);
            for (int index = 0; index < skins.Length; index++)
            {
                if (reused[index] == null && loaded[index] == null)
                    continue;
                entries.Add(new MapCharacterPlan.Entry(
                    skins[index].Key,
                    placements[index],
                    reused[index],
                    loaded[index]));
            }
            return new MapCharacterPlan(generation, entries);
        }

        /// <summary>
        /// Plans the placed VFX of a map state against the current particle runtime: its resource
        /// overlay is extended with the new systems only, continuing placements keep their simulation
        /// and only new placements get a graph.
        /// </summary>
        internal async Task<MapParticlePlan> PlanParticlesAsync(
            MapSceneData scene,
            MapVisibilityState visibility,
            MapParticleSceneRuntime current,
            long generation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scene);
            MapParticleSystemCatalog catalog = ParseParticleSystems(scene, visibility, _hashResolver);
            VfxSceneResourceContext resources = current?.Resources;
            VfxSceneResourceContext created = null;
            try
            {
                if (catalog.Groups.Count > 0)
                {
                    if (resources == null)
                    {
                        created = await VfxSceneResourceContext.CreateAsync(
                            catalog,
                            scene.Source?.ProjectRoot,
                            _assetResolver,
                            _hashResolver,
                            _logService,
                            cancellationToken);
                        resources = created;
                    }
                    else
                    {
                        await resources.EnsureMaterializedAsync(
                            catalog.Systems,
                            ownerSceneContext: null,
                            scene.Source?.ProjectRoot,
                            cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                var existing = new Dictionary<(uint Chunk, uint Key, uint System), MapParticleRuntime>();
                foreach (MapParticleRuntime runtime in current?.Runtimes ?? Array.Empty<MapParticleRuntime>())
                {
                    if (runtime?.Particle != null)
                        existing.TryAdd(runtime.ReuseKey, runtime);
                }

                var runtimes = new List<MapParticleRuntime>();
                int kept = 0;
                foreach ((MapParticleSystemGroupData group, MapParticleData particle) in MapParticleRuntime.Playable(catalog))
                {
                    if (existing.Remove(MapParticleRuntime.ReuseKeyOf(particle), out MapParticleRuntime runtime))
                    {
                        runtimes.Add(runtime);
                        kept++;
                        continue;
                    }
                    runtimes.Add(MapParticleRuntime.Create(
                        particle,
                        group.System,
                        catalog.Systems,
                        catalog.ResourceMap,
                        resources.PreparePlaybackAtWorldTransform));
                }

                return new MapParticlePlan(generation, catalog, runtimes, created, kept);
            }
            catch
            {
                created?.Dispose();
                throw;
            }
        }

        internal Task<MapParticleSceneRuntime> LoadParticlesAsync(
            MapSceneData scene,
            CancellationToken cancellationToken)
            => LoadParticlesAsync(scene, scene?.OpeningVisibility, cancellationToken);

        internal Task<MapParticleSceneRuntime> LoadParticlesAsync(
            MapSceneData scene,
            MapVisibilityState visibility,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scene);
            MapParticleSystemCatalog catalog = ParseParticleSystems(scene, visibility, _hashResolver);
            return MapParticleSceneRuntime.CreateAsync(
                catalog,
                scene.Source?.ProjectRoot,
                _assetResolver,
                _hashResolver,
                _logService,
                cancellationToken);
        }

        internal static MapParticleSystemCatalog ParseParticleSystems(
            MapSceneData scene,
            int visibilityFlags,
            HashResolverService hashResolver = null)
            => ParseParticleSystems(scene, scene?.OpeningVisibility.WithFlags(visibilityFlags), hashResolver);

        internal static MapParticleSystemCatalog ParseParticleSystems(
            MapSceneData scene,
            MapVisibilityState visibility,
            HashResolverService hashResolver = null)
        {
            ArgumentNullException.ThrowIfNull(scene);
            IReadOnlyList<MapParticleData> played = MapParticleSemantics.PlayedFor(
                scene.Particles, scene.Visibility, visibility ?? scene.OpeningVisibility);
            return new MapParticleSystemParser().Parse(
                scene.MaterialsDocument,
                MapParticleSemantics.GroupBySystem(played),
                hashResolver == null ? null : hashResolver.ResolveHash,
                hashResolver == null ? null : hashResolver.ResolveBinEntry,
                scene.ShaderDefinitions != null ? new[] { scene.ShaderDefinitions } : null);
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
            MapSceneData scene,
            CancellationToken cancellationToken)
        {
            string projectRoot = scene.Source?.ProjectRoot;
            try
            {
                MapCharacterAssetData asset = await _characterLoadingService.LoadAsync(
                    skin,
                    projectRoot,
                    cancellationToken,
                    scene.SharedMaterials);
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

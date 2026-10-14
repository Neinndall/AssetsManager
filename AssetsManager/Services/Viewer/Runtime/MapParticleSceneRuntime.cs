using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Owns the scene lifetime of placed map VFX and advances only placements held by the camera.
    /// Resource decoding remains scene-scoped while each placement keeps an independent simulation graph.
    /// </summary>
    internal sealed class MapParticleSceneRuntime : IDisposable
    {
        private IDisposable _resourceOwner;
        private IReadOnlyList<MapParticleRuntime> _runtimes;
        private readonly List<MapParticleRuntime> _visible = new();
        private bool _disposed;

        internal MapParticleSceneRuntime(
            IReadOnlyList<MapParticleRuntime> runtimes,
            IDisposable resourceOwner = null,
            MapParticleSystemCatalog catalog = null)
        {
            _runtimes = runtimes ?? Array.Empty<MapParticleRuntime>();
            _resourceOwner = resourceOwner;
            Catalog = catalog;
        }

        internal IReadOnlyList<MapParticleRuntime> Runtimes => _runtimes;

        /// <summary>Systems and placements this runtime was built for; null until placed VFX are loaded.</summary>
        internal MapParticleSystemCatalog Catalog { get; private set; }

        /// <summary>Scene-scoped resource overlay shared by every placement, reused across map states.</summary>
        internal VfxSceneResourceContext Resources => _resourceOwner as VfxSceneResourceContext;
        internal IReadOnlyList<MapParticleRuntime> VisibleRuntimes => _visible;

        internal static Task<MapParticleSceneRuntime> CreateAsync(
            MapSceneData scene,
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scene);
            return CreateAsync(
                scene.ParticleSystems,
                scene.Source?.ProjectRoot,
                assetResolver,
                hashResolver,
                logService,
                cancellationToken);
        }

        internal static async Task<MapParticleSceneRuntime> CreateAsync(
            MapParticleSystemCatalog catalog,
            string projectRoot,
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(assetResolver);

            if (catalog?.Groups == null || catalog.Groups.Count == 0)
                return new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>(), catalog: catalog);

            VfxSceneResourceContext resources = await VfxSceneResourceContext.CreateAsync(
                catalog,
                projectRoot,
                assetResolver,
                hashResolver,
                logService,
                cancellationToken);
            try
            {
                IReadOnlyList<MapParticleRuntime> runtimes = resources.CreateMapRuntimes(catalog);
                return new MapParticleSceneRuntime(runtimes, resources, catalog);
            }
            catch
            {
                resources.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Switches to another placement set in place. Kept runtimes continue their simulation; the
        /// resource overlay is kept, or adopted when this runtime had none yet.
        /// </summary>
        internal void Reconcile(
            MapParticleSystemCatalog catalog,
            IReadOnlyList<MapParticleRuntime> runtimes,
            IDisposable adoptedResources = null)
        {
            ThrowIfDisposed();
            if (adoptedResources != null && !ReferenceEquals(adoptedResources, _resourceOwner))
            {
                if (_resourceOwner != null)
                    throw new InvalidOperationException("MAP particle runtime already owns a resource overlay.");
                _resourceOwner = adoptedResources;
            }

            _runtimes = runtimes ?? Array.Empty<MapParticleRuntime>();
            Catalog = catalog;
            _visible.Clear();
        }

        internal void Restart()
        {
            ThrowIfDisposed();
            _visible.Clear();
            foreach (MapParticleRuntime runtime in _runtimes)
                runtime?.Graph?.Reset();
        }

        internal void Update(
            Matrix4x4 viewProjection,
            float deltaSeconds,
            IReadOnlySet<string> hidden = null)
        {
            ThrowIfDisposed();
            _visible.Clear();
            float step = MapParticleSemantics.ClampFrameStep(deltaSeconds);

            foreach (MapParticleRuntime runtime in _runtimes)
            {
                if (runtime?.Particle == null ||
                    (hidden != null && hidden.Count > 0 &&
                     (hidden.Contains(runtime.ChunkId) || hidden.Contains(runtime.ItemId))))
                {
                    continue;
                }
                if (!MapParticleSemantics.IsVisible(
                        viewProjection,
                        runtime.Particle.Position,
                        MapParticleSemantics.SystemReach))
                {
                    continue;
                }

                _visible.Add(runtime);
                if (step > 0f)
                    runtime.Advance(step);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MapParticleSceneRuntime));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _visible.Clear();
            _resourceOwner?.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Owns the scene lifetime of placed map VFX and advances only placements held by the camera.
    /// Resource decoding remains scene-scoped while each placement keeps an independent simulation graph.
    /// </summary>
    internal sealed class MapParticleSceneRuntime : IDisposable
    {
        private readonly IDisposable _resourceOwner;
        private readonly IReadOnlyList<MapParticleRuntime> _runtimes;
        private readonly List<MapParticleRuntime> _visible = new();
        private bool _disposed;

        internal MapParticleSceneRuntime(
            IReadOnlyList<MapParticleRuntime> runtimes,
            IDisposable resourceOwner = null)
        {
            _runtimes = runtimes ?? Array.Empty<MapParticleRuntime>();
            _resourceOwner = resourceOwner;
        }

        internal IReadOnlyList<MapParticleRuntime> Runtimes => _runtimes;
        internal IReadOnlyList<MapParticleRuntime> VisibleRuntimes => _visible;

        internal static async Task<MapParticleSceneRuntime> CreateAsync(
            MapSceneData scene,
            MapAssetResolver assetResolver,
            HashResolverService hashResolver,
            LogService logService,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(assetResolver);

            MapParticleSystemCatalog catalog = scene.ParticleSystems;
            if (catalog?.Groups == null || catalog.Groups.Count == 0)
                return new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>());

            MapParticleResourceContext resources = await MapParticleResourceContext.CreateAsync(
                catalog,
                scene.Source?.ProjectRoot,
                assetResolver,
                hashResolver,
                logService,
                cancellationToken);
            try
            {
                IReadOnlyList<MapParticleRuntime> runtimes = resources.CreateRuntimes(catalog);
                return new MapParticleSceneRuntime(runtimes, resources);
            }
            catch
            {
                resources.Dispose();
                throw;
            }
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

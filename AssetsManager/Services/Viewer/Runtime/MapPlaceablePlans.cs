using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Structures a map state stands, planned against one runtime generation. Reused groups stay
    /// owned by the runtime; loaded groups are owned by the plan until the runtime adopts it.
    /// </summary>
    internal sealed class MapCharacterPlan : IDisposable
    {
        internal sealed record Entry(
            string Skin,
            IReadOnlyList<MapCharacterData> Placements,
            MapCharacterRuntimeGroup Reused,
            MapCharacterRuntimeGroup Loaded);

        private bool _adopted;
        private bool _disposed;

        internal MapCharacterPlan(long generation, IReadOnlyList<Entry> entries)
        {
            Generation = generation;
            Entries = entries ?? Array.Empty<Entry>();
        }

        internal long Generation { get; }
        internal IReadOnlyList<Entry> Entries { get; }
        internal int ReusedCount => Entries.Count(entry => entry.Reused != null);
        internal int LoadedCount => Entries.Count(entry => entry.Loaded != null);

        internal void MarkAdopted() => _adopted = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_adopted)
                return;
            foreach (Entry entry in Entries)
                entry.Loaded?.Dispose();
        }
    }

    /// <summary>
    /// Placed VFX a map state plays, planned against one runtime generation. Kept runtimes come
    /// from the current scene runtime; a resource overlay created for the plan is released unless adopted.
    /// </summary>
    internal sealed class MapParticlePlan : IDisposable
    {
        private bool _adopted;
        private bool _disposed;

        internal MapParticlePlan(
            long generation,
            MapParticleSystemCatalog catalog,
            IReadOnlyList<MapParticleRuntime> runtimes,
            IDisposable createdResources,
            int keptCount)
        {
            Generation = generation;
            Catalog = catalog;
            Runtimes = runtimes ?? Array.Empty<MapParticleRuntime>();
            CreatedResources = createdResources;
            KeptCount = keptCount;
        }

        internal long Generation { get; }
        internal MapParticleSystemCatalog Catalog { get; }
        internal IReadOnlyList<MapParticleRuntime> Runtimes { get; }
        internal IDisposable CreatedResources { get; }
        internal int KeptCount { get; }
        internal int CreatedCount => Runtimes.Count - KeptCount;

        internal void MarkAdopted() => _adopted = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_adopted)
                CreatedResources?.Dispose();
        }
    }
}

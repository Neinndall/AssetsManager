using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// One lightweight VFX graph driven by a MapParticle placement.
    /// It owns simulation only: no seek session, checkpoints, GPU resources, or Viewer timeline state.
    /// </summary>
    internal sealed class MapParticleRuntime
    {
        private MapParticleRuntime(
            MapParticleData particle,
            VfxSystemDefinition system,
            VfxPlaybackGraphRuntime graph,
            int capacity)
        {
            Particle = particle;
            System = system;
            Graph = graph;
            Capacity = capacity;
            ChunkId = MapOutlineSemantics.ChunkId(particle.ChunkHash);
            ItemId = MapOutlineSemantics.ItemId(particle.ChunkHash, particle.KeyHash);
        }

        public MapParticleData Particle { get; }
        public VfxSystemDefinition System { get; }
        public VfxPlaybackGraphRuntime Graph { get; }
        public int Capacity { get; }
        internal string ChunkId { get; }
        internal string ItemId { get; }

        /// <summary>Identity of one placed system across map states.</summary>
        internal (uint Chunk, uint Key, uint System) ReuseKey => ReuseKeyOf(Particle);

        internal static (uint Chunk, uint Key, uint System) ReuseKeyOf(MapParticleData particle) =>
            (particle.ChunkHash, particle.KeyHash, particle.SystemHash);

        internal static MapParticleRuntime Create(
            MapParticleData particle,
            VfxSystemDefinition system,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> runtimeFactory = null)
        {
            ArgumentNullException.ThrowIfNull(particle);
            ArgumentNullException.ThrowIfNull(system);

            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();

            int capacity = VfxPlaybackGraphRuntime.ChildCapacityOf(system);
            int seed = unchecked((int)MapParticleSemantics.Seed(particle.Name));
            Matrix4x4 transform = MapParticleSemantics.RigidTransform(particle.Transform);

            VfxPlaybackRuntime Factory(
                VfxSystemDefinition definition,
                Matrix4x4 worldTransform,
                int runtimeSeed)
            {
                if (runtimeFactory != null)
                    return runtimeFactory(definition, worldTransform, runtimeSeed);

                var runtime = new VfxPlaybackRuntime(runtimeSeed);
                runtime.SetSystem(definition, worldTransform);
                return runtime;
            }

            var graph = new VfxPlaybackGraphRuntime(
                system,
                transform,
                seed,
                systems,
                resourceMap,
                Factory,
                rootParticleCapacity: capacity);
            graph.UserTag = particle;
            return new MapParticleRuntime(particle, system, graph, capacity);
        }

        internal void Advance(float deltaSeconds)
        {
            if (deltaSeconds <= 0f || !float.IsFinite(deltaSeconds))
                return;
            Graph.Update(deltaSeconds);
        }

        internal static IReadOnlyList<MapParticleRuntime> CreateAll(
            MapParticleSystemCatalog catalog,
            Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> runtimeFactory = null)
        {
            var runtimes = new List<MapParticleRuntime>();
            foreach ((MapParticleSystemGroupData group, MapParticleData particle) in Playable(catalog))
            {
                runtimes.Add(Create(
                    particle,
                    group.System,
                    catalog.Systems,
                    catalog.ResourceMap,
                    runtimeFactory));
            }
            return runtimes;
        }

        /// <summary>
        /// Placements of a catalog that get a simulation graph, in catalog order. Shared by full
        /// creation and map-state reconciliation so both produce the same placement set.
        /// </summary>
        internal static IEnumerable<(MapParticleSystemGroupData Group, MapParticleData Particle)> Playable(
            MapParticleSystemCatalog catalog)
        {
            foreach (MapParticleSystemGroupData group in catalog?.Groups ?? Array.Empty<MapParticleSystemGroupData>())
            {
                if (group?.System == null ||
                    group.System.Emitters is not { Count: > 0 } ||
                    group.Particles == null)
                {
                    continue;
                }

                foreach (MapParticleData particle in group.Particles)
                {
                    if (particle != null)
                        yield return (group, particle);
                }
            }
        }
    }
}

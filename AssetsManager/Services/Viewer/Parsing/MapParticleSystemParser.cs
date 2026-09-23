using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Resolves only the VFX systems a map can actually play from the open materials document.
    /// Roots come from visible/playable MapParticle groups and child systems are followed transitively
    /// through direct object links or the document ResourceResolver. Unplaced unrelated systems stay
    /// unparsed and therefore never trigger resource materialization for this MAP runtime.
    /// </summary>
    internal sealed class MapParticleSystemParser
    {
        public MapParticleSystemCatalog Parse(
            BinTree materials,
            IReadOnlyList<MapParticleGroupData> groups,
            Func<ulong, string> wadChunkPathResolver = null,
            Func<uint, string> binEntryResolver = null)
        {
            if (materials?.Objects == null || materials.Objects.Count == 0)
                return Empty();

            IReadOnlyDictionary<uint, uint> resourceMap = VfxResourceParser.ExtractResourceMap(materials);
            if (groups == null || groups.Count == 0)
                return new MapParticleSystemCatalog(
                    new Dictionary<uint, VfxSystemDefinition>(),
                    resourceMap,
                    Array.Empty<MapParticleSystemGroupData>());

            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var attempted = new HashSet<uint>();
            var pending = new Queue<uint>(groups
                .Where(group => group?.SystemHash != 0)
                .Select(group => group.SystemHash)
                .Distinct());

            while (pending.Count > 0)
            {
                uint pathHash = pending.Dequeue();
                if (!attempted.Add(pathHash))
                    continue;

                VfxSystemDefinition parsed = VfxSystemParser.Extract(materials, pathHash);
                if (parsed == null)
                    continue;

                parsed = VfxGraphParser.ResolveCustomMaterials(
                    parsed,
                    materials,
                    wadChunkPathResolver,
                    binEntryResolver);
                VfxSystemDefinition system = parsed with { ResourceMap = resourceMap };
                systems[pathHash] = system;
                foreach (VfxEmitterDefinition emitter in system.Emitters ?? Array.Empty<VfxEmitterDefinition>())
                {
                    foreach (VfxChildSystemReference child in emitter?.ChildParticleSet?.Children ?? Array.Empty<VfxChildSystemReference>())
                    {
                        if (child == null)
                            continue;

                        uint childHash = child.SystemHash;
                        if (childHash == 0 && child.EffectKey != 0)
                            resourceMap.TryGetValue(child.EffectKey, out childHash);
                        if (childHash != 0 && !attempted.Contains(childHash))
                            pending.Enqueue(childHash);
                    }
                }
            }

            var resolved = new List<MapParticleSystemGroupData>(groups.Count);
            foreach (MapParticleGroupData group in groups)
            {
                if (group == null ||
                    group.SystemHash == 0 ||
                    !systems.TryGetValue(group.SystemHash, out VfxSystemDefinition system))
                {
                    continue;
                }

                resolved.Add(new MapParticleSystemGroupData(
                    group.SystemHash,
                    system,
                    group.Particles ?? Array.Empty<MapParticleData>()));
            }

            return new MapParticleSystemCatalog(systems, resourceMap, resolved);
        }

        private static MapParticleSystemCatalog Empty() =>
            new(
                new Dictionary<uint, VfxSystemDefinition>(),
                new Dictionary<uint, uint>(),
                Array.Empty<MapParticleSystemGroupData>());
    }
}

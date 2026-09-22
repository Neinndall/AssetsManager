using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Viewer.Map.Parsing
{
    /// <summary>
    /// Resolves the VFX systems a map places from the same open materials document.
    /// The full system table is retained for child-system traversal while roots are limited
    /// to systems actually referenced by playable MapParticle groups.
    /// </summary>
    internal sealed class MapParticleSystemParser
    {
        public MapParticleSystemCatalog Parse(
            BinTree materials,
            IReadOnlyList<MapParticleGroupData> groups)
        {
            if (materials?.Objects == null || materials.Objects.Count == 0)
                return Empty();

            IReadOnlyDictionary<uint, uint> resourceMap = VfxResourceParser.ExtractResourceMap(materials);
            IReadOnlyDictionary<uint, VfxSystemDefinition> parsed = VfxSystemParser.ExtractAll(materials);
            var systems = parsed.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with { ResourceMap = resourceMap });

            if (groups == null || groups.Count == 0 || systems.Count == 0)
                return new MapParticleSystemCatalog(systems, resourceMap, Array.Empty<MapParticleSystemGroupData>());

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

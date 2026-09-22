using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Resolves MapParticle placeables from the shared authored snapshot exactly as LTK Manager 1.20.0.
    /// </summary>
    internal sealed class MapParticleParser
    {
        internal const uint MapParticleClass = 0x592ef6c3;
        internal const uint SystemField = 0x491e0a9c;
        internal const uint TransitionalField = 0x8d6d21cf;
        internal const uint StartDisabledField = 0x3edc338f;

        public IReadOnlyList<MapParticleData> Parse(IReadOnlyList<MapPlaceableChunkData> chunks)
        {
            if (chunks == null || chunks.Count == 0)
                return Array.Empty<MapParticleData>();

            var result = new List<MapParticleData>();
            foreach (MapPlaceableChunkData chunk in chunks)
            {
                if (chunk?.Items == null)
                    continue;

                foreach (MapPlaceableData placed in chunk.Items)
                {
                    if (placed == null || placed.ClassHash != MapParticleClass)
                        continue;
                    if (!TryReadSystem(placed.Properties, out uint systemHash))
                        continue;

                    result.Add(new MapParticleData(
                        placed,
                        systemHash,
                        ReadFlag(placed.Properties, TransitionalField),
                        ReadFlag(placed.Properties, StartDisabledField)));
                }
            }

            return result;
        }

        private static bool TryReadSystem(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            out uint systemHash)
        {
            systemHash = 0;
            if (properties == null ||
                !properties.TryGetValue(SystemField, out BinTreeProperty property) ||
                property is not BinTreeObjectLink link ||
                link.Value == 0)
            {
                return false;
            }

            systemHash = link.Value;
            return true;
        }

        private static bool ReadFlag(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint field)
        {
            if (properties == null || !properties.TryGetValue(field, out BinTreeProperty property))
                return false;

            return property switch
            {
                BinTreeBool value => value.Value,
                BinTreeU8 value => value.Value != 0,
                _ => false
            };
        }
    }
}

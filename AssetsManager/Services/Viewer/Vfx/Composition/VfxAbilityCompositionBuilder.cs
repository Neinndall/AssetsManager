using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Composition
{
    public static class VfxAbilityCompositionBuilder
    {
        public static VfxAbilityComposition Build(
            AnimationClipDefinition sequence,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            bool useEnemyEffects = false,
            bool allowEffectNameFallback = true)
        {
            ArgumentNullException.ThrowIfNull(sequence);
            ArgumentNullException.ThrowIfNull(systems);
            ArgumentNullException.ThrowIfNull(resourceMap);

            var systemsByName = systems
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Name))
                .GroupBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var particleEvents = sequence.ParticleEvents.OrderBy(item => item.StartFrame).ToArray();
            var compositionEvents = new List<VfxCompositionEvent>(particleEvents.Length);
            int resolvedCount = 0;

            foreach (VfxParticleEventDefinition particleEvent in particleEvents)
            {
                bool usesEnemyEffect = useEnemyEffects && particleEvent.EnemyEffectKey != 0;
                uint effectKey = usesEnemyEffect ? particleEvent.EnemyEffectKey : particleEvent.EffectKey;
                (uint systemHash, VfxSystemDefinition system) = Resolve(
                    effectKey,
                    particleEvent.EffectName,
                    systems,
                    resourceMap,
                    systemsByName,
                    allowEffectNameFallback);
                if (system is not null) resolvedCount++;
                compositionEvents.Add(new VfxCompositionEvent(
                    particleEvent,
                    systemHash,
                    system,
                    usesEnemyEffect));
            }

            return new VfxAbilityComposition(
                sequence.OwnerPathHash,
                sequence.OwnerClassHash,
                sequence.TickDuration > 0 ? sequence.TickDuration : 1f / 30f,
                sequence.StartFrame,
                sequence.EndFrame,
                compositionEvents,
                sequence.ClipName,
                sequence.AnimationFilePath,
                sequence.GraphPathHash,
                sequence.ChildClipHashes)
            {
                ResolvedCount = resolvedCount
            };
        }

        public static IReadOnlyList<VfxAbilityComposition> BuildAll(
            IEnumerable<AnimationClipDefinition> sequences,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            bool useEnemyEffects = false)
        {
            ArgumentNullException.ThrowIfNull(sequences);
            return sequences
                .Select(sequence => Build(sequence, systems, resourceMap, useEnemyEffects))
                .Where(composition => composition.Events.Count > 0)
                .OrderByDescending(composition => composition.ResolvedCount)
                .ThenBy(composition => composition.SequencePathHash)
                .ToArray();
        }

        /// <summary>
        /// Returns only compositions that explicitly resolve the selected system. Names and
        /// sibling prefixes are intentionally ignored because they do not encode timing or placement.
        /// </summary>
        public static IReadOnlyList<VfxAbilityComposition> FindContainingSystem(
            uint systemHash,
            IEnumerable<VfxAbilityComposition> compositions)
        {
            ArgumentNullException.ThrowIfNull(compositions);
            if (systemHash == 0) return Array.Empty<VfxAbilityComposition>();

            return compositions
                .Where(composition => composition.Events.Any(compositionEvent =>
                    compositionEvent.System is not null &&
                    compositionEvent.ResolvedSystemHash == systemHash))
                .OrderBy(composition => composition.SequencePathHash)
                .ToArray();
        }

        private static (uint Hash, VfxSystemDefinition System) Resolve(
            uint effectKey,
            string effectName,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            IReadOnlyDictionary<string, KeyValuePair<uint, VfxSystemDefinition>> systemsByName,
            bool allowEffectNameFallback)
        {
            if (TryResolveHash(effectKey, systems, resourceMap, out uint systemHash, out VfxSystemDefinition system))
                return (systemHash, system);

            if (allowEffectNameFallback && !string.IsNullOrWhiteSpace(effectName))
            {
                uint nameHash = Fnv1a.HashLower(effectName);
                if (TryResolveHash(nameHash, systems, resourceMap, out systemHash, out system))
                    return (systemHash, system);
                if (systemsByName.TryGetValue(effectName, out KeyValuePair<uint, VfxSystemDefinition> namedSystem))
                    return (namedSystem.Key, namedSystem.Value);
            }

            return (0u, null);
        }

        private static bool TryResolveHash(
            uint candidate,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            out uint systemHash,
            out VfxSystemDefinition system)
        {
            if (candidate != 0 && resourceMap.TryGetValue(candidate, out uint mappedHash) &&
                systems.TryGetValue(mappedHash, out system))
            {
                systemHash = mappedHash;
                return true;
            }
            if (candidate != 0 && systems.TryGetValue(candidate, out system))
            {
                systemHash = candidate;
                return true;
            }
            systemHash = 0u;
            system = null;
            return false;
        }
    }
}

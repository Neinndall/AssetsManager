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
            bool allowEffectNameFallback = true,
            bool resolverOnly = false)
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
                    allowEffectNameFallback,
                    resolverOnly);
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

        internal static VfxAbilityComposition BuildTimedPlaylist(
            AnimationClipDefinition root,
            IReadOnlyList<AnimationClipDefinition> playlist,
            IReadOnlyList<float> stepDurations,
            IReadOnlyList<float> frameSeconds,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentNullException.ThrowIfNull(stepDurations);
            ArgumentNullException.ThrowIfNull(frameSeconds);
            ArgumentNullException.ThrowIfNull(systems);
            ArgumentNullException.ThrowIfNull(resourceMap);

            var events = new List<VfxCompositionEvent>();
            float passTime = 0f;
            int resolvedCount = 0;
            int count = Math.Min(playlist.Count, Math.Min(stepDurations.Count, frameSeconds.Count));
            for (int index = 0; index < count; index++)
            {
                AnimationClipDefinition step = playlist[index];
                float tick = float.IsFinite(frameSeconds[index]) && frameSeconds[index] > 0f
                    ? frameSeconds[index]
                    : 1f / 30f;
                VfxAbilityComposition atomic = Build(
                    step,
                    systems,
                    resourceMap,
                    allowEffectNameFallback: false,
                    resolverOnly: true);
                foreach (VfxCompositionEvent compositionEvent in atomic.Events)
                {
                    VfxParticleEventDefinition cue = compositionEvent.Event;
                    if (cue.IsKillEvent) continue;
                    float start = passTime + cue.StartFrame * tick;
                    float end = cue.EndFrame < 0f
                        ? -1f
                        : passTime + cue.EndFrame * tick;
                    VfxCompositionEvent timed = compositionEvent with
                    {
                        Event = cue with
                        {
                            StartFrame = start,
                            EndFrame = end
                        }
                    };
                    events.Add(timed);
                    if (timed.System != null) resolvedCount++;
                }

                float duration = stepDurations[index];
                if (float.IsFinite(duration) && duration > 0f)
                    passTime += duration;
            }

            return new VfxAbilityComposition(
                root.OwnerPathHash,
                root.OwnerClassHash,
                1f,
                0f,
                passTime,
                events.OrderBy(item => item.Event.StartFrame).ToArray(),
                root.ClipName,
                root.AnimationFilePath,
                root.GraphPathHash,
                root.ChildClipHashes)
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
        internal static int CountResolvedIdleEffects(
            IReadOnlyList<VfxIdleEffectDefinition> idleEffects,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap)
        {
            if (idleEffects == null || systems == null || resourceMap == null)
                return 0;

            int count = 0;
            foreach (VfxIdleEffectDefinition idle in idleEffects)
            {
                if (idle == null || idle.EffectKey == 0 ||
                    !resourceMap.TryGetValue(idle.EffectKey, out uint systemHash) ||
                    systemHash == 0 || !systems.ContainsKey(systemHash))
                {
                    continue;
                }
                count++;
            }
            return count;
        }

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
            bool allowEffectNameFallback,
            bool resolverOnly)
        {
            if (TryResolveHash(
                    effectKey,
                    systems,
                    resourceMap,
                    out uint systemHash,
                    out VfxSystemDefinition system,
                    out bool resolverHit,
                    allowDirectSystemHashFallback: !resolverOnly))
            {
                return (systemHash, system);
            }
            if (resolverHit || resolverOnly) return (0u, null);

            if (allowEffectNameFallback && !string.IsNullOrWhiteSpace(effectName))
            {
                uint nameHash = Fnv1a.HashLower(effectName);
                if (TryResolveHash(
                        nameHash,
                        systems,
                        resourceMap,
                        out systemHash,
                        out system,
                        out resolverHit,
                        allowDirectSystemHashFallback: true))
                {
                    return (systemHash, system);
                }
                if (resolverHit) return (0u, null);
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
            out VfxSystemDefinition system,
            out bool resolverHit,
            bool allowDirectSystemHashFallback)
        {
            resolverHit = false;
            if (candidate != 0 && resourceMap.TryGetValue(candidate, out uint mappedHash))
            {
                resolverHit = true;
                if (mappedHash != 0 && systems.TryGetValue(mappedHash, out system))
                {
                    systemHash = mappedHash;
                    return true;
                }
                systemHash = 0u;
                system = null;
                return false;
            }
            if (allowDirectSystemHashFallback && candidate != 0 && systems.TryGetValue(candidate, out system))
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

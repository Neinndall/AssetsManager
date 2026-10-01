using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        public void SetSystem(VfxSystemModel system)
        {
            _waitForInitialResources = system != null;
            _initialResourceWaitStarted = null;
            ClearCheckpoints();
            _isPlaying = false;
            _usesStandaloneRig = system != null;
            _activeSystem = system;
            _ownerSceneContext = system?.OwnerSceneContext;
            _rigDuration = system?.Definition is { } definition
                ? VfxRigMotion.RunLength(_rigSettings, definition)
                : system?.TotalDuration ?? 0d;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _lastRigOrigin = null;
            _boneTransformProvider = null;
            _boneTransformSampler = null;
            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
            }
            if (system != null)
            {
                system.CurrentTime = 0;
            }

            if (_ready)
                QueueGpuResourcePurge();

            if (system?.Definition != null)
            {
                _graph = _loadingService.PreparePlaybackGraph(
                    system.Definition,
                    system.SystemCatalog,
                    system.ResourceMap,
                    system.SearchDirectory,
                    _worldTransform,
                    system.PlaybackSeed,
                    _logService,
                    system.OwnerSceneContext);
                _graph.SetPinnedBirthChance(_pinnedBirthChance);
                _graphs.Add(_graph);
                _graphPlacements[_graph] = Matrix4x4.Identity;
                ApplyRigTransform();
            }
        }

        public bool SetAbilityComposition(
            VfxAbilityComposition composition,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            int seed,
            VfxOwnerSceneContext ownerSceneContext = null)
            => SetAnimationSession(
                composition,
                null,
                systems,
                resourceMap,
                searchDirectory,
                seed,
                0,
                ownerSceneContext);

        /// <summary>
        /// A spell cast: its cast animation's clip cues and the character's idle auras, as the Clips
        /// preview plays them, plus the spell's own projectile and impact steps on the same clock.
        /// </summary>
        public bool SetSpellSession(
            IReadOnlyList<VfxSpellPlaybackStep> steps,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            double animationDuration,
            VfxOwnerSceneContext ownerSceneContext = null,
            VfxAbilityComposition castAnimation = null,
            IReadOnlyList<VfxIdleEffectDefinition> idleEffects = null)
        {
            ClearCheckpoints();
            steps ??= Array.Empty<VfxSpellPlaybackStep>();
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
            _usesStandaloneRig = false;
            _ownerSceneContext = ownerSceneContext;
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();
            _lastRigOrigin = null;
            _boneTransformProvider = null;
            _boneTransformSampler = null;

            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
                QueueGpuResourcePurge();
            }

            double duration = Math.Max(0.1d, animationDuration);
            AddIdleEffects(idleEffects, systems, resourceMap, searchDirectory, ownerSceneContext);
            duration = AddCompositionEvents(castAnimation, systems, resourceMap, searchDirectory, ownerSceneContext, duration);
            foreach (VfxSpellPlaybackStep step in steps)
            {
                if (step?.System is null) continue;
                VfxPlaybackGraphRuntime graph = _loadingService.PreparePlaybackGraph(
                    step.System,
                    systems,
                    resourceMap,
                    searchDirectory,
                    _worldTransform,
                    step.Seed,
                    _logService,
                    ownerSceneContext);
                graph.SetStartDelay((float)Math.Max(0d, step.StartTime));
                _graphs.Add(graph);
                _graphPlacements[graph] = Matrix4x4.Identity;
                _spellSteps[graph] = step;
                if (step.StopTime > step.StartTime)
                    _graphStopTimes[graph] = step.StopTime;
                _graph ??= graph;

                double active = Math.Max(0d, step.StopTime - step.StartTime);
                duration = Math.Max(
                    duration,
                    step.StopTime + VfxDurationCalculator.LingerTail(step.System, active));
            }

            _activeSystem = new VfxSystemModel
            {
                Name = "Spell Preview",
                SystemCatalog = systems,
                ResourceMap = resourceMap,
                SearchDirectory = searchDirectory,
                OwnerSceneContext = ownerSceneContext,
                TotalDuration = Math.Min(Math.Max(0.1d, duration), 60d),
                Speed = 1.0
            };
            ApplySpellTransforms(0d);
            return steps.Count > 0 || animationDuration > 0d || _graphs.Count > 0;
        }

        public bool SetAnimationSession(
            VfxAbilityComposition composition,
            IReadOnlyList<VfxIdleEffectDefinition> idleEffects,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            int seed,
            double animationDuration,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ClearCheckpoints();
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            _isPlaying = false;
            _usesStandaloneRig = false;
            _ownerSceneContext = ownerSceneContext;
            if (_ready)
            {
                _renderer.SetOwnerSkinningMatrices(null);
                _renderer.SetOwnerHiddenSubmeshes(_ownerSceneContext?.InitialHiddenSubmeshHashes);
            }
            _graph = null;
            _graphs.Clear();
            _graphPlacements.Clear();
            _scheduledEffectKills.Clear();
            _graphStopTimes.Clear();
            _graphAttachments.Clear();
            _spellSteps.Clear();

            if (_ready)
                QueueGpuResourcePurge();

            double duration = Math.Max(0.1, animationDuration);
            AddIdleEffects(idleEffects, systems, resourceMap, searchDirectory, ownerSceneContext);
            duration = AddCompositionEvents(composition, systems, resourceMap, searchDirectory, ownerSceneContext, duration);

            string sequenceName = composition != null
                ? (!string.IsNullOrEmpty(composition.ClipName) ? composition.ClipName : $"0x{composition.SequencePathHash:X8}")
                : "Animation";

            _activeSystem = new VfxSystemModel
            {
                Name = $"Session {sequenceName}",
                SystemCatalog = systems,
                ResourceMap = resourceMap,
                SearchDirectory = searchDirectory,
                OwnerSceneContext = ownerSceneContext,
                PlaybackSeed = seed,
                TotalDuration = Math.Max(0.1, duration),
                Speed = 1.0
            };

            return _graphs.Count > 0;
        }

        /// <summary>Continuous character-anchored auras (CharacterIdleEffect).</summary>
        private void AddIdleEffects(
            IReadOnlyList<VfxIdleEffectDefinition> idleEffects,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            VfxOwnerSceneContext ownerSceneContext)
        {
            if (idleEffects == null)
                return;

            foreach (VfxIdleEffectDefinition idle in idleEffects)
            {
                // CharacterIdleEffect.effectKey is a ResourceResolver key, never a direct
                // VfxSystemDefinition object id. LTK drops an idle whose key is unmapped or
                // maps outside the systems in reach instead of guessing by hash/name.
                if (idle.EffectKey == 0 ||
                    !resourceMap.TryGetValue(idle.EffectKey, out uint mappedHash) ||
                    mappedHash == 0 ||
                    !systems.TryGetValue(mappedHash, out VfxSystemDefinition idleDef))
                {
                    continue;
                }

                var idleGraph = _loadingService.PreparePlaybackGraph(
                    idleDef,
                    systems,
                    resourceMap,
                    searchDirectory,
                    _worldTransform,
                    IdleEffectSeed,
                    _logService,
                    ownerSceneContext);

                _graphs.Add(idleGraph);
                _graphPlacements[idleGraph] = Matrix4x4.Identity;
                _graphAttachments[idleGraph] = new GraphAttachmentInfo
                {
                    BoneName = idle.BoneName,
                    BoneHash = idle.BoneNameHash,
                    TargetBoneName = idle.TargetBoneName,
                    TargetBoneHash = idle.TargetBoneNameHash,
                    EffectKey = idle.EffectKey,
                    LocalOffset = idle.Position,
                    BaseTransform = Matrix4x4.Identity,
                    IsIdleEffect = true
                };
                _graph ??= idleGraph;
            }
        }

        /// <summary>
        /// The particle events an animation clip cues, attached to their joints and timed on its clock.
        /// </summary>
        /// <returns><paramref name="duration"/> extended to the last event and effect.</returns>
        private double AddCompositionEvents(
            VfxAbilityComposition composition,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            VfxOwnerSceneContext ownerSceneContext,
            double duration)
        {
            if (composition == null)
                return duration;

            foreach (VfxCompositionEvent compositionEvent in composition.Events)
            {
                var cue = compositionEvent.Event;
                float startSeconds = Math.Max(0f, cue.StartFrame * composition.TickDuration);
                uint effectKey = compositionEvent.UsesEnemyEffect ? cue.EnemyEffectKey : cue.EffectKey;
                if (cue.IsKillEvent)
                {
                    _scheduledEffectKills.Add((startSeconds, effectKey));
                    continue;
                }
                if (compositionEvent.System is null) continue;

                // LTK keeps ParticleEventData.scale in the parsed event metadata but its
                // Animation Clip viewport does not apply it to the spawned VFX system.
                // Keep playback tied to the cue rig only; skinScale is handled separately.
                var attachments = cue.Attachments is { Count: > 0 }
                    ? cue.Attachments
                    : new[] { new VfxParticleEventAttachment(0, 0) };
                foreach (var pair in attachments)
                {
                    var eventGraph = _loadingService.PreparePlaybackGraph(
                        compositionEvent.System,
                        systems,
                        resourceMap,
                        searchDirectory,
                        _worldTransform,
                        AnimationClipCueSeed,
                        _logService,
                        ownerSceneContext);

                    eventGraph.SetStartDelay(startSeconds);

                    _graphs.Add(eventGraph);
                    _graphPlacements[eventGraph] = Matrix4x4.Identity;

                    _graphAttachments[eventGraph] = new GraphAttachmentInfo
                    {
                        BoneName = null,
                        BoneHash = pair.SourceBoneHash,
                        TargetBoneHash = pair.TargetBoneHash,
                        EffectKey = effectKey,
                        StartTime = startSeconds,
                        // LTK's Animation Clip viewport keeps a cue riding its source joint;
                        // ParticleEventData's detachable field is not part of that playback contract.
                        IsDetachable = false,
                        LocalOffset = Vector3.Zero,
                        BaseTransform = Matrix4x4.Identity,
                        IsIdleEffect = false
                    };

                    if (cue.EndFrame > cue.StartFrame)
                    {
                        _graphStopTimes[eventGraph] = cue.EndFrame * composition.TickDuration;
                    }

                    _graph ??= eventGraph;
                }

                double effectDuration = VfxDurationCalculator.Calculate(
                    compositionEvent.System,
                    systems,
                    resourceMap);
                if (double.IsFinite(effectDuration))
                    duration = Math.Max(duration, startSeconds + effectDuration);
            }

            if (composition.EndFrame > composition.StartFrame)
                duration = Math.Max(duration, (composition.EndFrame - composition.StartFrame) * composition.TickDuration);
            foreach (VfxCompositionEvent compositionEvent in composition.Events)
            {
                if (compositionEvent.Event.EndFrame >= compositionEvent.Event.StartFrame)
                    duration = Math.Max(
                        duration,
                        (compositionEvent.Event.EndFrame - composition.StartFrame) * composition.TickDuration);
            }
            return duration;
        }
    }
}

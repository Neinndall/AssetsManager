using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackGraphRuntime
    {
        private readonly List<(VfxEmitterDefinition Definition, int Root, bool Hud, bool NeedsOwner)> _stencilDefinitions = new();

        internal void AddStencilClaims(VfxStencilScene scene, bool shaded)
        {
            // Draw components exist for the definition tree even when a child pool has not
            // spawned yet. Their visible writers must participate in the preview's fallback.
            foreach (var claim in _stencilDefinitions)
            {
                if (claim.NeedsOwner && _jointTransformProvider == null) continue;
                scene.Add(claim.Definition, shaded && RootIsVisible(claim.Root) && !claim.Definition.Disabled &&
                    VfxRenderPhaseSemantics.Resolve(claim.Definition, claim.Hud).Draws);
            }
        }

        private VfxPlaybackRuntime CreateRuntime(
            VfxSystemDefinition definition,
            Matrix4x4 localTransform,
            int depth,
            string path,
            int seed,
            uint? initialRandomState = null,
            int? particleCapacity = null)
        {
            Matrix4x4 effectiveLocalTransform = depth == 0
                ? Matrix4x4.Identity
                : localTransform;
            VfxPlaybackRuntime runtime = _runtimeFactory(
                definition,
                effectiveLocalTransform * _rootTransform,
                seed);
            runtime.SetTransform(
                effectiveLocalTransform * _rootTransform,
                effectiveLocalTransform * _orientationRootTransform);
            runtime.SetEmissionSurfaces(_emissionSurfaces, replayCurrentTime: false);
            runtime.SetPinnedBirthChance(_pinnedBirthChance);
            if (particleCapacity.HasValue)
                runtime.SetParticleCapacity(particleCapacity.Value);
            if (initialRandomState.HasValue)
                runtime.SetInitialRandomState(initialRandomState.Value);
            runtime.ParticleLifecycle += OnParticleLifecycle;
            runtime.ParticleUpdated += OnParticleUpdated;
            _depth[runtime] = depth;
            _localTransforms[runtime] = effectiveLocalTransform;
            _paths[runtime] = path;
            AssignRenderIdentity(runtime, path);
            if (depth == 0) runtime.WarmUp();
            else runtime.SuppressBuildUp();
            return runtime;
        }

        private void BuildRenderRanks(VfxSystemDefinition rootDefinition)
        {
            _renderRanks.Clear();
            _renderRoots.Clear();
            _stencilDefinitions.Clear();
            int nextRank = 0;
            CollectRenderRanks(rootDefinition, string.Empty, 0, -1, false, ref nextRank);
        }

        private void CollectRenderRanks(
            VfxSystemDefinition definition,
            string path,
            int depth,
            int rootSourceOrder,
            bool needsOwner,
            ref int nextRank)
        {
            if (definition is null || depth > MaximumGraphDepth) return;

            string renderPath = path ?? string.Empty;
            int[] localOrder = Enumerable.Range(0, definition.Emitters.Count).ToArray();
            Array.Sort(localOrder, (left, right) => VfxDrawOrderSemantics.Compare(
                definition.Emitters[left],
                left,
                definition.Emitters[right],
                right));
            foreach (int sourceOrder in localOrder)
            {
                _renderRanks[(renderPath, sourceOrder)] = nextRank++;
                _renderRoots[(renderPath, sourceOrder)] = depth == 0 ? sourceOrder : rootSourceOrder;
                if ((definition.Emitters[sourceOrder].RenderState?.StencilMode ?? 0) != 0)
                    _stencilDefinitions.Add((definition.Emitters[sourceOrder],
                        depth == 0 ? sourceOrder : rootSourceOrder, definition.HudLayer, needsOwner));
            }

            if (depth >= MaximumGraphDepth) return;
            for (int sourceOrder = 0; sourceOrder < definition.Emitters.Count; sourceOrder++)
            {
                VfxEmitterDefinition emitter = definition.Emitters[sourceOrder];
                VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
                if (emitter.Disabled || childSet is null || childSet.Children.Count == 0) continue;

                for (int slot = 0; slot < childSet.Children.Count; slot++)
                {
                    VfxChildSystemReference child = childSet.Children[slot];
                    VfxSystemDefinition childDefinition = ResolveSystem(
                        child,
                        _systems,
                        definition.ResourceMap,
                        _resourceMap);
                    if (childDefinition is null) continue;

                    string emitterPath = string.IsNullOrEmpty(path)
                        ? sourceOrder.ToString()
                        : $"{path}/{sourceOrder}";
                    string childPath = $"{emitterPath}.{slot}";
                    int childRoot = depth == 0 ? sourceOrder : rootSourceOrder;
                    bool childNeedsOwner = needsOwner ||
                        (childSet.Bones is { Count: > 0 } && !emitter.MeshIsSkinned);
                    CollectRenderRanks(childDefinition, childPath, depth + 1, childRoot, childNeedsOwner, ref nextRank);
                }
            }
        }

        private void AssignRenderIdentity(VfxPlaybackRuntime runtime, string path)
        {
            string renderPath = path ?? string.Empty;
            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                emitter.RenderGraphKey = this;
                emitter.RenderPath = renderPath;
                emitter.RenderRootSourceOrder = _renderRoots.GetValueOrDefault(
                    (renderPath, emitter.SourceOrder),
                    emitter.SourceOrder);
                emitter.RenderRank = _renderRanks.GetValueOrDefault((renderPath, emitter.SourceOrder), int.MaxValue);
                emitter.IsVisible = RootIsVisible(emitter.RenderRootSourceOrder);

                string emitterKey = string.IsNullOrEmpty(renderPath)
                    ? emitter.SourceOrder.ToString()
                    : $"{renderPath}:{emitter.SourceOrder}";
                if (emitter.MeshAnimationVariants is { Length: > 0 } variants)
                {
                    // main animationOf(): one stable variant per definition key, unaffected by
                    // particle RNG, seeks or resource reloads.
                    int variant = MeshAnimationVariantIndex(
                        emitter.Def.MeshPath,
                        renderPath,
                        emitter.SourceOrder,
                        variants.Length);
                    emitter.MeshAnimation = variants[variant];
                }
                else
                {
                    emitter.MeshAnimation = emitter.MeshBaseAnimation;
                }

                if (emitter.MeshAnimation is IVfxMeshJointProvider meshJoints)
                    _loadedMeshJoints[emitterKey] = meshJoints;
            }
        }

        internal static int MeshAnimationVariantIndex(
            string meshPath,
            string renderPath,
            int sourceOrder,
            int variantCount)
        {
            if (variantCount <= 0) return -1;
            string emitterKey = string.IsNullOrEmpty(renderPath)
                ? sourceOrder.ToString()
                : $"{renderPath}:{sourceOrder}";
            uint hash = Fnv1a.HashLower($"{meshPath ?? string.Empty}:{emitterKey}");
            return (int)(hash % (uint)variantCount);
        }
    }
}

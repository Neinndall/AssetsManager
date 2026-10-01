using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackGraphRuntime
    {
        private void OnParticleLifecycle(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.Children.Count == 0) return;

            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (particle.Died)
            {
                if (!childSet.EmitOnDeath)
                {
                    StopCarriedChildren(key);
                    return;
                }

                if (_deathSeen.Remove(key, out VfxPlaybackRuntime.ParticleLifecycleInfo lastSeen))
                {
                    // LTK spawns an on-death child from the last live Seen record: both its
                    // bearing and its lifetime come from the last step where the particle was
                    // still present. A linger settle may rewrite lifetime on the death step,
                    // but that rewritten value was never observed by children.step.
                    VfxPlaybackRuntime.ParticleLifecycleInfo deathBearing = lastSeen with
                    {
                        ParticleTime = lastSeen.ParticleLifetime,
                        Died = true
                    };
                    QueueChildSpawn(parentRuntime, childSet, deathBearing, carried: false);
                }
                return;
            }

            if (childSet.EmitOnDeath)
            {
                _deathSeen[key] = particle;
                return;
            }

            QueueChildSpawn(parentRuntime, childSet, particle, carried: true);
        }

        private void QueueChildSpawn(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            bool carried)
        {
            if (!_spawnRequestsByParent.TryGetValue(parentRuntime, out List<ChildSpawnRequest> requests))
            {
                requests = new List<ChildSpawnRequest>();
                _spawnRequestsByParent[parentRuntime] = requests;
            }
            requests.Add(new ChildSpawnRequest(
                parentRuntime,
                childSet,
                particle,
                carried,
                _nextSpawnRequestSequence++));
        }

        private void OnParticleUpdated(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.Children.Count == 0) return;

            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (childSet.EmitOnDeath)
            {
                _deathSeen[key] = particle;
                return;
            }
            if (!_carriedChildren.TryGetValue(key, out List<CarriedChildInfo> children)) return;

            Matrix4x4 bearing = ChildBearing(childSet, particle);
            foreach (CarriedChildInfo child in children)
            {
                Matrix4x4 placement = bearing;
                if (!string.IsNullOrEmpty(child.BoneName))
                {
                    Matrix4x4? joint = ResolveChildJoint(
                        parentRuntime,
                        particle.SourceOrder,
                        child.BoneName,
                        particle.ParticleTime);
                    if (!joint.HasValue) continue;
                    placement = ReRootOnJoint(bearing, joint.Value);
                }
                UpdateChildPlacement(child.Runtime, child.Definition, placement);
            }
        }

        private void StopCarriedChildren(
            (VfxPlaybackRuntime Parent, int SourceOrder, uint Serial) key)
        {
            if (!_carriedChildren.Remove(key, out List<CarriedChildInfo> children)) return;
            foreach (CarriedChildInfo child in children)
                child.Runtime.IsStopped = true;
        }

        private void SpawnChildren(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            bool carried)
        {
            int parentDepth = _depth.TryGetValue(parentRuntime, out int value) ? value : 0;
            if (parentDepth >= MaximumGraphDepth) return;

            string parentPath = _paths.GetValueOrDefault(parentRuntime, string.Empty);
            string emitterPath = $"{(string.IsNullOrEmpty(parentPath) ? string.Empty : parentPath + "/")}{particle.SourceOrder}";
            Matrix4x4 bearing = ChildBearing(childSet, particle);
            IReadOnlyList<string> bones = childSet.Bones ?? Array.Empty<string>();

            // LTK treats a bone set as a positional list: it spawns one child per slot,
            // bypasses childrenProbability, and rejects the set when there are too few bones.
            if (bones.Count > 0)
            {
                if (bones.Count < childSet.Children.Count) return;
                for (int slot = 0; slot < childSet.Children.Count; slot++)
                {
                    if (LiveChildSystemCount >= MaximumActiveChildSystems) break;
                    string bone = bones[slot];
                    Matrix4x4? joint = ResolveChildJoint(
                        parentRuntime,
                        particle.SourceOrder,
                        bone,
                        particle.ParticleTime);
                    if (!joint.HasValue) continue;
                    Matrix4x4 placement = ReRootOnJoint(bearing, joint.Value);
                    int childSeed = ChildSeed(_initialSeed, $"{emitterPath}.{slot}", particle.Serial);
                    SpawnChild(parentRuntime, childSet, particle, parentDepth, emitterPath, slot,
                        placement, childSeed, unchecked((uint)childSeed), carried, bone);
                }
                return;
            }

            if (LiveChildSystemCount >= MaximumActiveChildSystems) return;

            // A normal child uses one lineage stream both to select its probability slot and
            // to seed the child itself. Keep the post-selection RNG state for exact replay.
            int lineageSeed = ChildSeed(_initialSeed, emitterPath, particle.Serial);
            var rng = new VfxLtkRandom(unchecked((uint)lineageSeed));
            int selectedSlot;
            if (childSet.Children.Count == 1)
            {
                selectedSlot = 0;
            }
            else
            {
                float selected = childSet.Probability.Prob is { Length: > 0 } && !childSet.Probability.Prob[0].IsEmpty
                    ? childSet.Probability.SampleBirth(particle.ParticleTime, rng)
                    : childSet.Probability.Sample(particle.ParticleTime);
                selectedSlot = Math.Max(0, (int)MathF.Truncate(selected)) % childSet.Children.Count;
            }

            SpawnChild(parentRuntime, childSet, particle, parentDepth, emitterPath, selectedSlot,
                bearing, lineageSeed, rng.State, carried, null);
        }

        private void SpawnChild(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            int parentDepth,
            string emitterPath,
            int slot,
            Matrix4x4 childWorldTransform,
            int childSeed,
            uint initialRandomState,
            bool carried,
            string boneName)
        {
            if ((uint)slot >= (uint)childSet.Children.Count) return;
            VfxChildSystemReference child = childSet.Children[slot];
            VfxSystemDefinition definition = ResolveSystem(
                child,
                _systems,
                parentRuntime.Definition.ResourceMap ?? _resourceMap);
            if (definition is null) return;

            int particleCapacity = ChildCapacityOf(definition);
            if (LiveChildSystemCount >= MaximumActiveChildSystems ||
                _heldChildParticleCapacity + particleCapacity > MaximumChildParticleCapacityBudget)
            {
                return;
            }

            Matrix4x4 childLocalTransform = childWorldTransform;
            if (Matrix4x4.Invert(_rootTransform, out Matrix4x4 inverseRoot))
                childLocalTransform = childWorldTransform * inverseRoot;

            string childPath = $"{emitterPath}.{slot}";
            VfxPlaybackRuntime runtime = CreateRuntime(
                definition,
                childLocalTransform,
                parentDepth + 1,
                childPath,
                childSeed,
                initialRandomState,
                particleCapacity);
            RegisterChildCapacity(runtime, particleCapacity);
            RegisterParent(runtime, parentRuntime);
            _pendingChildren.Add(runtime);

            if (!carried)
            {
                _looseChildStopAfter[runtime] = (float)VfxDurationCalculator.SystemSpan(definition);
                return;
            }
            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (!_carriedChildren.TryGetValue(key, out List<CarriedChildInfo> tracked))
            {
                tracked = new List<CarriedChildInfo>();
                _carriedChildren[key] = tracked;
            }
            tracked.Add(new CarriedChildInfo
            {
                Runtime = runtime,
                Definition = definition,
                Set = childSet,
                Slot = slot,
                BoneName = boneName
            });
        }

        private void RegisterChildCapacity(VfxPlaybackRuntime runtime, int capacity)
        {
            if (_childCapacities.ContainsKey(runtime)) return;
            int held = Math.Clamp(capacity, MinimumChildParticleCapacity, MaximumChildParticleCapacity);
            _childCapacities[runtime] = held;
            _heldChildParticleCapacity += held;
        }

        /// <summary>
        /// Matches LTK childPool.capacityOf: estimate the child's peak simultaneous demand,
        /// then reserve the next power-of-two pool between 16 and 4096 particles.
        /// </summary>
        internal static int ChildCapacityOf(VfxSystemDefinition system)
        {
            if (system is null) return MinimumChildParticleCapacity;

            double wanted = 0d;
            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                double rate = VfxDurationCalculator.Peak(emitter.Rate);
                if (emitter.IsSingleParticle)
                {
                    int burst = (int)(Math.Truncate(rate) % (ushort.MaxValue + 1d));
                    wanted += Math.Max(burst, 1);
                }
                else
                {
                    double lifetime = VfxDurationCalculator.Peak(emitter.ParticleLifetime);
                    wanted += Math.Ceiling(rate * (lifetime + VfxPlaybackRuntime.LingerSeconds(emitter))) +
                              Math.Ceiling(rate) + 1d;
                }
            }

            int capacity = MinimumChildParticleCapacity;
            while (capacity < wanted && capacity < MaximumChildParticleCapacity)
                capacity *= 2;
            return capacity;
        }

        private static Matrix4x4 ChildBearing(
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            int mode = childSet.InheritanceMode;
            Matrix4x4 bearing = (mode & 0x2) != 0 ? particle.Frame : particle.Basis;
            Vector3 relativeOffset = childSet.RelativeOffset.Sample(0f);
            if ((mode & 0x1) == 0)
                relativeOffset = Vector3.TransformNormal(relativeOffset, particle.Basis);
            Vector3 childPosition = particle.Position + relativeOffset;
            bearing.M41 = childPosition.X;
            bearing.M42 = childPosition.Y;
            bearing.M43 = childPosition.Z;
            return bearing;
        }

        private Matrix4x4? ResolveChildJoint(
            VfxPlaybackRuntime parentRuntime,
            int sourceOrder,
            string boneName,
            float particleTime)
        {
            string parentPath = _paths.GetValueOrDefault(parentRuntime, string.Empty);
            string key = string.IsNullOrEmpty(parentPath)
                ? sourceOrder.ToString()
                : $"{parentPath}:{sourceOrder}";
            if (_loadedMeshJoints.TryGetValue(key, out IVfxMeshJointProvider loadedMeshJoints))
            {
                return loadedMeshJoints.TryGetJointTransform(boneName, particleTime, out Matrix4x4 transform)
                    ? transform
                    : null;
            }
            if (_meshJoints.TryGetValue(key, out IVfxMeshJointProvider meshJoints))
            {
                return meshJoints.TryGetJointTransform(boneName, particleTime, out Matrix4x4 transform)
                    ? transform
                    : null;
            }

            return _jointTransformProvider?.Invoke(boneName);
        }

        private static Matrix4x4 ReRootOnJoint(Matrix4x4 bearing, Matrix4x4 joint)
        {
            Matrix4x4 bearingTurn = VectorMathUtils.OrientationOnly(bearing);
            Matrix4x4 jointTurn = VectorMathUtils.OrientationOnly(joint);
            Vector3 jointOffset = Vector3.TransformNormal(joint.Translation, bearingTurn);
            Vector3 position = bearing.Translation + jointOffset;

            // System.Numerics uses row vectors: local joint turn followed by the particle
            // bearing is the same composition as LTK's bearingYaw * jointBasis.
            Matrix4x4 result = jointTurn * bearingTurn;
            result.M41 = position.X;
            result.M42 = position.Y;
            result.M43 = position.Z;
            return result;
        }

        private void UpdateChildPlacement(
            VfxPlaybackRuntime runtime,
            VfxSystemDefinition definition,
            Matrix4x4 childWorldTransform)
        {
            Matrix4x4 childLocalTransform = childWorldTransform;
            if (Matrix4x4.Invert(_rootTransform, out Matrix4x4 inverseRoot))
                childLocalTransform = childWorldTransform * inverseRoot;

            Matrix4x4 effectiveLocal = ComposeChildTransform(
                definition.Transform.GetValueOrDefault(Matrix4x4.Identity),
                childLocalTransform);
            _localTransforms[runtime] = effectiveLocal;
            runtime.SetTransform(
                effectiveLocal * _rootTransform,
                effectiveLocal * _orientationRootTransform);
        }

        private static Matrix4x4 ComposeChildTransform(Matrix4x4 authored, Matrix4x4 bearing)
        {
            Vector3 authoredOffset = authored.Translation;
            authored.M41 = 0f;
            authored.M42 = 0f;
            authored.M43 = 0f;
            Matrix4x4 result = authored * bearing;
            Vector3 origin = bearing.Translation + authoredOffset;
            result.M41 = origin.X;
            result.M42 = origin.Y;
            result.M43 = origin.Z;
            return result;
        }


        private static int ChildSeed(int seed, string path, uint serial)
        {
            uint pathHash = Fnv1a.HashLower(path ?? string.Empty);
            uint lineage = unchecked((serial + 1u) * 0x9e3779b1u);
            return unchecked(seed ^ (int)pathHash ^ (int)lineage);
        }

        internal static VfxSystemDefinition ResolveSystem(
            VfxChildSystemReference reference,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap)
        {
            if (reference is null) return null;
            if (reference.SystemHash != 0 && systems.TryGetValue(reference.SystemHash, out VfxSystemDefinition definition))
                return definition;
            if (reference.EffectKey != 0)
            {
                if (resourceMap.TryGetValue(reference.EffectKey, out uint mappedHash))
                {
                    // An effect key only names a child through its ResourceResolver scope.
                    // A resolver hit is authoritative even when it maps to null or to an unavailable object.
                    return mappedHash != 0 && systems.TryGetValue(mappedHash, out definition)
                        ? definition
                        : null;
                }
            }
            return null;
        }
    }
}

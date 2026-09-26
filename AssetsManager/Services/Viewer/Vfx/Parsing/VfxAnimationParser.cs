using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static class VfxAnimationParser
    {
        // AnimationGraph clip event fields
        private static readonly uint SubmeshVisibilityEventClass = 0xbcf56e70;
        private static readonly uint ParticleEventClass = VfxParsingHash.Fnv1a("ParticleEventData");
        private static readonly uint JointSnapEventClass = 0xb5c1b6ad;
        private static readonly uint ConformToPathEventClass = 0x82377a1d;
        private static readonly uint F_eventDataMap = VfxParsingHash.Fnv1a("mEventDataMap");
        private static readonly uint F_clipDataMap = VfxParsingHash.Fnv1a("mClipDataMap");
        private static readonly uint F_trackDataMap = VfxParsingHash.Fnv1a("mTrackDataMap");
        private static readonly uint F_maskDataMap = VfxParsingHash.Fnv1a("mMaskDataMap");
        private static readonly uint F_syncGroupDataMap = VfxParsingHash.Fnv1a("mSyncGroupDataMap");
        private static readonly uint F_clipTickDuration = VfxParsingHash.Fnv1a("mTickDuration");
        private static readonly uint F_clipStartFrame = VfxParsingHash.Fnv1a("startFrame");
        private static readonly uint F_clipEndFrame = VfxParsingHash.Fnv1a("EndFrame");
        private static readonly uint F_skinMeshProperties = VfxParsingHash.Fnv1a("skinMeshProperties");
        private static readonly uint F_simpleSkin = VfxParsingHash.Fnv1a("simpleSkin");
        private static readonly uint F_ownerSkeleton = VfxParsingHash.Fnv1a("skeleton");
        private static readonly uint F_skinScale = VfxParsingHash.Fnv1a("skinScale");
        private static readonly uint F_initialSubmeshToHide = VfxParsingHash.Fnv1a("initialSubmeshToHide");
        private static readonly uint F_skinAnimationProperties = VfxParsingHash.Fnv1a("skinAnimationProperties");
        private static readonly uint F_animationGraphData = VfxParsingHash.Fnv1a("animationGraphData");
        private static readonly uint F_eventName = VfxParsingHash.Fnv1a("mName");
        private static readonly uint F_eventStartFrame = VfxParsingHash.Fnv1a("mStartFrame");
        private static readonly uint F_eventEndFrame = VfxParsingHash.Fnv1a("mEndFrame");
        private static readonly uint F_eventIsSelfOnly = VfxParsingHash.Fnv1a("mIsSelfOnly");
        private static readonly uint F_eventFireIfAnimationEndsEarly = VfxParsingHash.Fnv1a("mFireIfAnimationEndsEarly");
        private static readonly uint F_eventEffectKey = VfxParsingHash.Fnv1a("mEffectKey");
        private static readonly uint F_eventEnemyEffectKey = VfxParsingHash.Fnv1a("mEnemyEffectKey");
        private static readonly uint F_eventEffectName = VfxParsingHash.Fnv1a("mEffectName");
        private static readonly uint F_eventIsLoop = VfxParsingHash.Fnv1a("mIsLoop");
        private static readonly uint F_eventIsKill = VfxParsingHash.Fnv1a("mIsKillEvent");
        private static readonly uint F_eventIsDetachable = VfxParsingHash.Fnv1a("mIsDetachable");
        private static readonly uint F_eventSkipIfPastEndFrame = VfxParsingHash.Fnv1a("SkipIfPastEndFrame");
        private static readonly uint F_eventScalePlaySpeed = VfxParsingHash.Fnv1a("mScalePlaySpeedWithAnimation");
        private static readonly uint F_eventScale = VfxParsingHash.Fnv1a("scale");
        private static readonly uint F_eventPairList = VfxParsingHash.Fnv1a("mParticleEventDataPairList");
        private static readonly uint F_eventSourceBone = VfxParsingHash.Fnv1a("mBoneName");
        private static readonly uint F_eventTargetBone = VfxParsingHash.Fnv1a("mTargetBoneName");
        private static readonly uint F_eventShowSubmeshes = 0x6d4d42d0;
        private static readonly uint F_eventHideSubmeshes = 0xbb41a45b;
        private static readonly uint F_eventJoint = 0xac70ab62;
        private static readonly uint F_eventSnapTo = 0xf6e6d893;
        private static readonly uint F_eventOffset = 0x14c8d3ca;
        private static readonly uint F_eventMaskDataName = 0x0359739b;
        private static readonly uint F_eventBlendIn = 0xdf2f42a9;
        private static readonly uint F_eventBlendOut = 0xa8c578b4;
        private static readonly uint F_parametricPairs = 0x2ec3ba66;
        private static readonly uint F_parametricPairClip = 0xca2b847d;
        private static readonly uint F_parametricPairValue = 0x24f2ec89;
        private static readonly uint F_idleParticlesEffects = 0x84186f3c;
        // SkinCharacterDataProperties_CharacterIdleEffect does not use ParticleEventData's
        // m-prefixed fields. Keep the two schemas separate, as LTK's skin reader does.
        private static readonly uint F_idleEffectKey = VfxParsingHash.Fnv1a("effectKey");
        private static readonly uint F_idleEffectName = VfxParsingHash.Fnv1a("effectName");
        private static readonly uint F_idleBoneName = VfxParsingHash.Fnv1a("boneName");
        private static readonly uint F_idleTargetBoneName = VfxParsingHash.Fnv1a("targetBoneName");
        private static readonly uint F_idlePosition = 0x934f4e0a;
        private static readonly uint F_animationResourceData = 0xb49f754e;
        private static readonly uint F_animationFilePath = 0x0329f1d7;
        private static readonly uint F_clipName = VfxParsingHash.Fnv1a("mClipName");
        private static readonly uint F_trackDataName = 0xd39243c4;
        private static readonly uint F_clipMaskDataName = 0x0359739b;
        private static readonly uint F_syncGroupDataName = 0xa09d0561;
        private static readonly uint F_interruptionGroups = 0x89d34040;
        private static readonly uint F_clipFlags = 0x8d80922b;
        private static readonly uint F_trackPriority = 0x0f717330;
        private static readonly uint F_trackBlendMode = 0x9ae6020c;
        private static readonly uint F_trackBlendWeight = 0xf4018e7f;
        private static readonly uint F_maskId = 0xc38f3be5;
        private static readonly uint F_maskWeights = 0xa3c80380;
        private static readonly uint F_syncGroupType = 0x87edaeb0;

        internal static IReadOnlyList<VfxIdleEffectDefinition> ExtractIdleEffects(BinTree tree)
        {
            var idleEffects = new List<VfxIdleEffectDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                if (owner.ClassHash != SkinCharacterDataPropertiesClass) continue;
                idleEffects.AddRange(ExtractIdleEffects(Get(owner.Properties, F_idleParticlesEffects)));
            }
            return idleEffects;
        }

        internal static IReadOnlyList<VfxIdleEffectDefinition> ExtractIdleEffects(BinTreeProperty property)
        {
            if (property is BinTreeOptional optional) property = optional.Value;
            if (property is not BinTreeContainer container)
                return Array.Empty<VfxIdleEffectDefinition>();

            var idleEffects = new List<VfxIdleEffectDefinition>();
            foreach (BinTreeStruct elem in container.Elements.OfType<BinTreeStruct>())
            {
                uint effectKey = AsU32(Get(elem.Properties, F_idleEffectKey)) ?? 0u;
                string effectName = GetString(elem.Properties, F_idleEffectName) ?? string.Empty;
                string boneName = GetString(elem.Properties, F_idleBoneName) ?? string.Empty;
                uint boneNameHash = AsU32(Get(elem.Properties, F_idleBoneName)) ??
                    (string.IsNullOrEmpty(boneName) ? 0u : VfxParsingHash.Fnv1a(boneName));
                string targetBoneName = GetString(elem.Properties, F_idleTargetBoneName) ?? string.Empty;
                uint targetBoneNameHash = AsU32(Get(elem.Properties, F_idleTargetBoneName)) ??
                    (string.IsNullOrEmpty(targetBoneName) ? 0u : VfxParsingHash.Fnv1a(targetBoneName));
                Vector3 position = AsVec3(Get(elem.Properties, F_idlePosition)) ?? Vector3.Zero;
                idleEffects.Add(new VfxIdleEffectDefinition(
                    effectKey,
                    effectName,
                    boneName,
                    boneNameHash,
                    targetBoneName,
                    targetBoneNameHash,
                    position));
            }
            return idleEffects;
        }

        internal static VfxOwnerSceneContext ExtractOwnerSceneContext(BinTree tree)
        {
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                if (owner.ClassHash != SkinCharacterDataPropertiesClass ||
                    Get(owner.Properties, F_skinMeshProperties) is not BinTreeStruct meshProperties)
                {
                    continue;
                }

                string meshPath = ReadAsset(meshProperties.Properties, F_simpleSkin, ".skn");
                if (string.IsNullOrWhiteSpace(meshPath)) continue;

                uint animationGraphPathHash = 0u;
                if (Get(owner.Properties, F_skinAnimationProperties) is BinTreeStruct animationProperties)
                    animationGraphPathHash = AsU32(Get(animationProperties.Properties, F_animationGraphData)) ?? 0u;

                return new VfxOwnerSceneContext(
                    meshPath,
                    ReadAsset(meshProperties.Properties, F_ownerSkeleton, ".skl") ?? string.Empty,
                    Math.Max(0.01f, GetF32(meshProperties.Properties, F_skinScale) ?? 1f),
                    animationGraphPathHash,
                    ReadSubmeshNameHashes(GetString(meshProperties.Properties, F_initialSubmeshToHide)));
            }
            return null;
        }

        internal static IReadOnlyList<AnimationGraphDefinition> ExtractAnimationGraphs(
            BinTree tree,
            Func<uint, string> graphHashNameResolver,
            Func<uint, string> graphClassNameResolver)
        {
            var graphs = new List<AnimationGraphDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                if (Get(owner.Properties, F_clipDataMap) is not BinTreeMap clipMap)
                    continue;

                IReadOnlyList<AnimationTrackDefinition> tracks =
                    ReadAnimationTracks(owner.Properties, graphHashNameResolver);
                IReadOnlyList<AnimationMaskDefinition> masks =
                    ReadAnimationMasks(owner.Properties, graphHashNameResolver);
                IReadOnlyList<AnimationSyncGroupDefinition> syncGroups =
                    ReadAnimationSyncGroups(owner.Properties, graphHashNameResolver);

                var clipNames = new Dictionary<uint, string>();
                foreach (var pair in clipMap)
                {
                    (uint hash, string name) = ReadGraphMapKey(pair.Key, graphHashNameResolver);
                    if (hash != 0u) clipNames.TryAdd(hash, name);
                }

                IReadOnlyDictionary<uint, string> trackNames = tracks
                    .GroupBy(item => item.Hash)
                    .ToDictionary(group => group.Key, group => group.First().Name);
                IReadOnlyDictionary<uint, string> maskNames = masks
                    .GroupBy(item => item.Hash)
                    .ToDictionary(group => group.Key, group => group.First().Name);
                IReadOnlyDictionary<uint, string> syncGroupNames = syncGroups
                    .GroupBy(item => item.Hash)
                    .ToDictionary(group => group.Key, group => group.First().Name);

                var clips = new List<AnimationClipDefinition>();
                foreach (var clipPair in clipMap)
                {
                    if (clipPair.Value is not BinTreeStruct clip) continue;

                    (uint clipHash, string clipName) =
                        ReadGraphMapKey(clipPair.Key, graphHashNameResolver);
                    if (clipHash == 0u) continue;

                    string animationFilePath = null;
                    if (Get(clip.Properties, F_animationResourceData) is BinTreeStruct animationResource)
                    {
                        animationFilePath = ReadAsset(
                            animationResource.Properties,
                            F_animationFilePath,
                            ".anm");
                    }

                    IReadOnlyList<uint> childHashes = ReadClipChildren(clip.Properties);
                    IReadOnlyList<float?> parametricValues = ReadClipParameterValues(clip.Properties);
                    AnimationClipDefinition definition = CreateAnimationClipDefinition(
                        clipHash,
                        clip.ClassHash,
                        clip.Properties,
                        clipName,
                        animationFilePath,
                        owner.PathHash,
                        childHashes,
                        parametricValues.Select(value => value ?? 0f).ToArray(),
                        CreateGraphKeyReference(
                            Get(clip.Properties, F_trackDataName),
                            trackNames,
                            graphHashNameResolver),
                        CreateGraphKeyReference(
                            Get(clip.Properties, F_clipMaskDataName),
                            maskNames,
                            graphHashNameResolver),
                        CreateGraphKeyReference(
                            Get(clip.Properties, F_syncGroupDataName),
                            syncGroupNames,
                            graphHashNameResolver),
                        childHashes
                            .Select(hash => CreateGraphKeyReference(
                                hash,
                                clipNames,
                                graphHashNameResolver))
                            .ToArray(),
                        ReadNamedHashList(
                            Get(clip.Properties, F_interruptionGroups),
                            graphHashNameResolver),
                        ReadUnsigned(Get(clip.Properties, F_clipFlags)),
                        parametricValues,
                        ResolveGraphClassName(clip.ClassHash, graphClassNameResolver),
                        includeEmpty: true);
                    if (definition != null)
                    {
                        definition = definition with
                        {
                            UsesEquippedGearParameter =
                                Get(clip.Properties, VfxParsingHash.Fnv1a("Updater")) is BinTreeStruct updater &&
                                updater.ClassHash == VfxParsingHash.Fnv1a("EquippedGearParametricUpdater")
                        };
                        clips.Add(definition);
                    }
                }

                graphs.Add(new AnimationGraphDefinition(
                    owner.PathHash,
                    clips,
                    tracks,
                    masks,
                    syncGroups));
            }
            return graphs;
        }

        private static IReadOnlyList<AnimationTrackDefinition> ReadAnimationTracks(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            Func<uint, string> graphHashNameResolver)
        {
            if (Get(properties, F_trackDataMap) is not BinTreeMap map)
                return Array.Empty<AnimationTrackDefinition>();

            var result = new List<AnimationTrackDefinition>();
            foreach (var pair in map)
            {
                if (pair.Value is not BinTreeStruct track) continue;
                (uint hash, string name) = ReadGraphMapKey(pair.Key, graphHashNameResolver);
                if (hash == 0u) continue;
                result.Add(new AnimationTrackDefinition(
                    hash,
                    name,
                    (byte)(GetU8(track.Properties, F_trackPriority) ?? 0),
                    (byte)(GetU8(track.Properties, F_trackBlendMode) ?? 0),
                    GetF32(track.Properties, F_trackBlendWeight) ?? 0f));
            }
            return result;
        }

        private static IReadOnlyList<AnimationMaskDefinition> ReadAnimationMasks(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            Func<uint, string> graphHashNameResolver)
        {
            if (Get(properties, F_maskDataMap) is not BinTreeMap map)
                return Array.Empty<AnimationMaskDefinition>();

            var result = new List<AnimationMaskDefinition>();
            foreach (var pair in map)
            {
                if (pair.Value is not BinTreeStruct mask) continue;
                (uint hash, string name) = ReadGraphMapKey(pair.Key, graphHashNameResolver);
                if (hash == 0u) continue;

                float[] weights = Get(mask.Properties, F_maskWeights) is BinTreeContainer weightList
                    ? weightList.Elements.Select(item => AsF32(item) ?? 0f).ToArray()
                    : Array.Empty<float>();
                result.Add(new AnimationMaskDefinition(
                    hash,
                    name,
                    ReadUnsigned(Get(mask.Properties, F_maskId)),
                    weights));
            }
            return result;
        }

        private static IReadOnlyList<AnimationSyncGroupDefinition> ReadAnimationSyncGroups(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            Func<uint, string> graphHashNameResolver)
        {
            if (Get(properties, F_syncGroupDataMap) is not BinTreeMap map)
                return Array.Empty<AnimationSyncGroupDefinition>();

            var result = new List<AnimationSyncGroupDefinition>();
            foreach (var pair in map)
            {
                if (pair.Value is not BinTreeStruct syncGroup) continue;
                (uint hash, string name) = ReadGraphMapKey(pair.Key, graphHashNameResolver);
                if (hash == 0u) continue;
                result.Add(new AnimationSyncGroupDefinition(
                    hash,
                    name,
                    ReadUnsigned(Get(syncGroup.Properties, F_syncGroupType))));
            }
            return result;
        }

        private static (uint Hash, string Name) ReadGraphMapKey(
            BinTreeProperty key,
            Func<uint, string> graphHashNameResolver)
        {
            uint hash = AsU32(key) ?? 0u;
            if (key is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                return (hash, text.Value);
            return (hash, ResolveGraphHashName(hash, graphHashNameResolver));
        }

        private static AnimationGraphKeyReference CreateGraphKeyReference(
            BinTreeProperty property,
            IReadOnlyDictionary<uint, string> declaredNames,
            Func<uint, string> graphHashNameResolver)
        {
            uint hash = AsU32(property) ?? 0u;
            return hash == 0u
                ? null
                : CreateGraphKeyReference(hash, declaredNames, graphHashNameResolver);
        }

        private static AnimationGraphKeyReference CreateGraphKeyReference(
            uint hash,
            IReadOnlyDictionary<uint, string> declaredNames,
            Func<uint, string> graphHashNameResolver)
        {
            string declaredName = null;
            bool declared = declaredNames != null && declaredNames.TryGetValue(hash, out declaredName);
            return new AnimationGraphKeyReference(
                hash,
                declared ? declaredName : ResolveGraphHashName(hash, graphHashNameResolver),
                declared);
        }

        private static IReadOnlyList<string> ReadNamedHashList(
            BinTreeProperty property,
            Func<uint, string> graphHashNameResolver)
        {
            if (property is not BinTreeContainer container)
                return Array.Empty<string>();

            var result = new List<string>(container.Elements.Count);
            foreach (BinTreeProperty item in container.Elements)
            {
                uint hash = AsU32(item) ?? 0u;
                if (hash == 0u) continue;
                result.Add(item is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value)
                    ? text.Value
                    : ResolveGraphHashName(hash, graphHashNameResolver));
            }
            return result;
        }

        private static string ResolveGraphHashName(
            uint hash,
            Func<uint, string> graphHashNameResolver)
        {
            if (hash == 0u) return string.Empty;
            string resolved = graphHashNameResolver?.Invoke(hash);
            return string.IsNullOrWhiteSpace(resolved)
                ? $"0x{hash:x8}"
                : resolved;
        }

        private static string ResolveGraphClassName(
            uint hash,
            Func<uint, string> graphClassNameResolver)
        {
            if (hash == 0u) return string.Empty;
            string resolved = graphClassNameResolver?.Invoke(hash);
            return string.IsNullOrWhiteSpace(resolved)
                ? $"0x{hash:x8}"
                : resolved;
        }

        private static uint ReadUnsigned(BinTreeProperty property)
        {
            long? value = AsInteger(property);
            return value is >= uint.MinValue and <= uint.MaxValue
                ? (uint)value.Value
                : 0u;
        }

        internal static IReadOnlyList<AnimationClipDefinition> ExtractEventSequences(
            BinTree tree,
            IReadOnlyList<AnimationGraphDefinition> animationGraphs)
        {
            var sequences = new List<AnimationClipDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
                AddEventSequence(sequences, owner.PathHash, owner.ClassHash, owner.Properties);

            foreach (AnimationGraphDefinition graph in animationGraphs ?? Array.Empty<AnimationGraphDefinition>())
                sequences.AddRange(graph.Clips);

            return sequences;
        }

        private static void AddEventSequence(
            ICollection<AnimationClipDefinition> sequences,
            uint ownerPathHash,
            uint ownerClassHash,
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            string clipName = null,
            string animationFilePath = null,
            uint graphPathHash = 0,
            IReadOnlyList<uint> childClipHashes = null,
            IReadOnlyList<float> childParameters = null,
            bool includeEmpty = false)
        {
            AnimationClipDefinition clip = CreateAnimationClipDefinition(
                ownerPathHash,
                ownerClassHash,
                properties,
                clipName,
                animationFilePath,
                graphPathHash,
                childClipHashes,
                childParameters,
                includeEmpty: includeEmpty);
            if (clip != null) sequences.Add(clip);
        }

        private static AnimationClipDefinition CreateAnimationClipDefinition(
            uint ownerPathHash,
            uint ownerClassHash,
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            string clipName = null,
            string animationFilePath = null,
            uint graphPathHash = 0,
            IReadOnlyList<uint> childClipHashes = null,
            IReadOnlyList<float> childParameters = null,
            AnimationGraphKeyReference track = null,
            AnimationGraphKeyReference mask = null,
            AnimationGraphKeyReference syncGroup = null,
            IReadOnlyList<AnimationGraphKeyReference> childReferences = null,
            IReadOnlyList<string> interruptionGroups = null,
            uint flags = 0,
            IReadOnlyList<float?> parametricValues = null,
            string className = null,
            bool includeEmpty = false)
        {
            var events = new List<AnimationClipEventDefinition>();
            if (Get(properties, F_eventDataMap) is BinTreeMap eventMap)
            {
                foreach (var pair in eventMap)
                {
                    if (pair.Value is not BinTreeStruct eventData) continue;
                    events.Add(ParseClipEvent(AsU32(pair.Key) ?? 0u, eventData));
                }
            }
            if (events.Count == 0 && !includeEmpty) return null;

            return new AnimationClipDefinition(
                ownerPathHash,
                ownerClassHash,
                GetF32(properties, F_clipTickDuration) is { } tick && float.IsFinite(tick) && tick > 0 ? tick : 0,
                GetF32(properties, F_clipStartFrame) ?? 0f,
                GetF32(properties, F_clipEndFrame) ?? -1f,
                events,
                clipName,
                animationFilePath,
                graphPathHash,
                childClipHashes,
                childParameters,
                track,
                mask,
                syncGroup,
                childReferences,
                interruptionGroups,
                flags,
                parametricValues,
                className);
        }

        private static IReadOnlyList<uint> ReadClipChildren(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var children = new List<uint>();
            void Add(BinTreeProperty property)
            {
                if (AsU32(property) is uint hash && hash != 0u) children.Add(hash);
            }
            foreach (var (field, itemField) in new (uint, uint)[]
            {
                (0x2ec3ba66, 0xca2b847d), (0x512c9525, 0xca2b847d),
                (0x4d7a54c0, 0), (0x24af5ac1, 0), (0x2329eec5, 0xca2b847d),
                (0x078cafd9, 0), (0xd188b400, 0x68c14f60), (0x9a7f92cb, 0),
                (0x8e5e6618, 0), (0x21328a43, 0xc6f291ed), (0x778d6dee, 0x68c14f60)
            })
            {
                BinTreeProperty value = Get(properties, field);
                if (value is BinTreeContainer container)
                {
                    foreach (BinTreeProperty item in container.Elements)
                        Add(itemField != 0 && item is BinTreeStruct pair ? Get(pair.Properties, itemField) : item);
                }
                else Add(value);
            }
            return children;
        }

        private static IReadOnlyList<float?> ReadClipParameterValues(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (Get(properties, F_parametricPairs) is not BinTreeContainer pairs)
                return Array.Empty<float?>();

            var values = new List<float?>(pairs.Elements.Count);
            foreach (BinTreeStruct pair in pairs.Elements.OfType<BinTreeStruct>())
            {
                // Keep the parameter list aligned with ReadClipChildren/LTK: a pair that does
                // not name a real child is skipped, while a missing mValue is authored as zero.
                uint childHash = AsU32(Get(pair.Properties, F_parametricPairClip)) ?? 0u;
                if (childHash == 0u) continue;
                values.Add(GetF32(pair.Properties, F_parametricPairValue) ?? 0f);
            }
            return values;
        }

        private static AnimationClipEventDefinition ParseClipEvent(uint eventHash, BinTreeStruct eventData)
        {
            IReadOnlyDictionary<uint, BinTreeProperty> properties = eventData.Properties;
            float startFrame = GetF32(properties, F_eventStartFrame) ?? 0f;
            float endFrame = GetF32(properties, F_eventEndFrame) ?? -1f;

            if (eventData.ClassHash == ParticleEventClass)
                return ParseParticleEvent(eventHash, eventData);

            if (eventData.ClassHash == SubmeshVisibilityEventClass)
            {
                return new AnimationSubmeshVisibilityEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    ReadHashList(Get(properties, F_eventShowSubmeshes)),
                    ReadHashList(Get(properties, F_eventHideSubmeshes)));
            }

            if (eventData.ClassHash == JointSnapEventClass)
            {
                return new AnimationJointSnapEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    AsU32(Get(properties, F_eventJoint)) ?? 0u,
                    AsU32(Get(properties, F_eventSnapTo)) ?? 0u,
                    AsVec3(Get(properties, F_eventOffset)) ?? Vector3.Zero);
            }

            if (eventData.ClassHash == ConformToPathEventClass)
            {
                return new AnimationConformToPathEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    AsU32(Get(properties, F_eventMaskDataName)) ?? 0u,
                    GetF32(properties, F_eventBlendIn) ?? 0f,
                    GetF32(properties, F_eventBlendOut) ?? 0f);
            }

            return new AnimationOtherClipEventDefinition(
                eventHash,
                startFrame,
                endFrame,
                eventData.ClassHash);
        }

        private static IReadOnlyList<uint> ReadHashList(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container)
                return AsU32(property) is uint single && single != 0u ? new[] { single } : Array.Empty<uint>();

            return container.Elements
                .Select(AsU32)
                .Where(value => value.HasValue && value.Value != 0u)
                .Select(value => value.Value)
                .ToArray();
        }

        private static VfxParticleEventDefinition ParseParticleEvent(uint eventHash, BinTreeStruct eventData)
        {
            IReadOnlyDictionary<uint, BinTreeProperty> properties = eventData.Properties;
            var attachments = new List<VfxParticleEventAttachment>();
            if (Get(properties, F_eventPairList) is BinTreeContainer pairs)
            {
                foreach (BinTreeStruct pair in pairs.Elements.OfType<BinTreeStruct>())
                {
                    attachments.Add(new VfxParticleEventAttachment(
                        AsU32(Get(pair.Properties, F_eventSourceBone)) ?? 0u,
                        AsU32(Get(pair.Properties, F_eventTargetBone)) ?? 0u));
                }
            }

            return new VfxParticleEventDefinition(
                eventHash,
                AsU32(Get(properties, F_eventName)) ?? 0u,
                GetF32(properties, F_eventStartFrame) ?? 0f,
                GetF32(properties, F_eventEndFrame) ?? -1f,
                AsU32(Get(properties, F_eventEffectKey)) ?? 0u,
                AsU32(Get(properties, F_eventEnemyEffectKey)) ?? 0u,
                GetString(properties, F_eventEffectName) ?? string.Empty,
                GetBool(properties, F_eventIsLoop),
                GetBool(properties, F_eventIsKill),
                GetBool(properties, F_eventIsDetachable),
                GetBool(properties, F_eventIsSelfOnly),
                GetBool(properties, F_eventFireIfAnimationEndsEarly),
                GetBool(properties, F_eventSkipIfPastEndFrame),
                GetBool(properties, F_eventScalePlaySpeed),
                GetF32(properties, F_eventScale) ?? 1f,
                attachments);
        }

    }
}

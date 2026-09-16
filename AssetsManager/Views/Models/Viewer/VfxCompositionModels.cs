using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>Base event authored on an AnimationGraph clip.</summary>
    public abstract record AnimationClipEventDefinition(uint EventHash, float StartFrame, float EndFrame);

    public sealed record AnimationSubmeshVisibilityEventDefinition(
        uint EventHash,
        float StartFrame,
        float EndFrame,
        IReadOnlyList<uint> ShowSubmeshHashes,
        IReadOnlyList<uint> HideSubmeshHashes)
        : AnimationClipEventDefinition(EventHash, StartFrame, EndFrame);

    public sealed record AnimationJointSnapEventDefinition(
        uint EventHash,
        float StartFrame,
        float EndFrame,
        uint JointHash,
        uint SnapToHash,
        Vector3 Offset)
        : AnimationClipEventDefinition(EventHash, StartFrame, EndFrame);

    public sealed record AnimationConformToPathEventDefinition(
        uint EventHash,
        float StartFrame,
        float EndFrame,
        uint MaskHash,
        float BlendInSeconds,
        float BlendOutSeconds)
        : AnimationClipEventDefinition(EventHash, StartFrame, EndFrame);

    /// <summary>Preserves unsupported authored events so event counts remain truthful.</summary>
    public sealed record AnimationOtherClipEventDefinition(
        uint EventHash,
        float StartFrame,
        float EndFrame,
        uint ClassHash)
        : AnimationClipEventDefinition(EventHash, StartFrame, EndFrame);

    /// <summary>One non-particle visual cue placed on the resolved clip pass, in seconds.</summary>
    public abstract record AnimationClipTimedCue(double AtSeconds, double? UntilSeconds);

    public sealed record AnimationSubmeshVisibilityCue(
        double AtSeconds,
        double? UntilSeconds,
        IReadOnlyList<uint> ShowSubmeshHashes,
        IReadOnlyList<uint> HideSubmeshHashes)
        : AnimationClipTimedCue(AtSeconds, UntilSeconds);

    public sealed record AnimationJointSnapCue(
        double AtSeconds,
        double? UntilSeconds,
        uint JointHash,
        uint SnapToHash,
        Vector3 Offset)
        : AnimationClipTimedCue(AtSeconds, UntilSeconds);

    public sealed record AnimationConformToPathCue(
        double AtSeconds,
        double? UntilSeconds,
        uint MaskHash,
        float BlendInSeconds,
        float BlendOutSeconds)
        : AnimationClipTimedCue(AtSeconds, UntilSeconds);

    public sealed record VfxParticleEventAttachment(uint SourceBoneHash, uint TargetBoneHash);

    public sealed record VfxParticleEventDefinition(
        uint EventHash,
        uint NameHash,
        float StartFrame,
        float EndFrame,
        uint EffectKey,
        uint EnemyEffectKey,
        string EffectName,
        bool IsLoop,
        bool IsKillEvent,
        bool IsDetachable,
        bool IsSelfOnly,
        bool FireIfAnimationEndsEarly,
        bool SkipIfPastEndFrame,
        bool ScalePlaySpeedWithAnimation,
        float Scale,
        IReadOnlyList<VfxParticleEventAttachment> Attachments)
        : AnimationClipEventDefinition(EventHash, StartFrame, EndFrame);

    /// <summary>
    /// One AnimationGraph clip. Atomic clips name an .anm; composite clips name children.
    /// Events are the complete authored event map, not only particle events.
    /// </summary>
    public sealed record AnimationClipDefinition(
        uint OwnerPathHash,
        uint OwnerClassHash,
        float TickDuration,
        float StartFrame,
        float EndFrame,
        IReadOnlyList<AnimationClipEventDefinition> Events,
        string ClipName = null,
        string AnimationFilePath = null,
        uint GraphPathHash = 0,
        IReadOnlyList<uint> ChildClipHashes = null,
        IReadOnlyList<float> ChildParameters = null)
    {
        public IEnumerable<VfxParticleEventDefinition> ParticleEvents
            => (Events ?? Array.Empty<AnimationClipEventDefinition>()).OfType<VfxParticleEventDefinition>();

        public int ParticleEventCount => ParticleEvents.Count();
    }

    public sealed record VfxIdleEffectDefinition(
        uint EffectKey,
        string EffectName,
        string BoneName,
        uint BoneNameHash,
        string TargetBoneName,
        uint TargetBoneNameHash,
        Vector3 Position);

    public sealed record VfxCompositionEvent(
        VfxParticleEventDefinition Event,
        uint ResolvedSystemHash,
        VfxSystemDefinition System,
        bool UsesEnemyEffect);

    public sealed record VfxAbilityComposition(
        uint SequencePathHash,
        uint SequenceClassHash,
        float TickDuration,
        float StartFrame,
        float EndFrame,
        IReadOnlyList<VfxCompositionEvent> Events,
        string ClipName = null,
        string AnimationFilePath = null,
        uint GraphPathHash = 0,
        IReadOnlyList<uint> ChildClipHashes = null)
    {
        public int ResolvedCount { get; init; }
        public int UnresolvedCount => Events.Count - ResolvedCount;
    }

    /// <summary>
    /// Resolved AnimationGraph clip ready for preview. The animation asset is owned by the
    /// catalog that created this entry; the entry itself only carries playback metadata.
    /// </summary>
    public sealed record AnimationClipCatalogItem(
        string Name,
        string DisplayName,
        string FilePath,
        float Duration,
        IAnimationAsset AnimationAsset,
        AnimationClipDefinition Clip,
        VfxAbilityComposition Composition,
        IReadOnlyList<AnimationClipTimedCue> TimedCues,
        int EventCount,
        bool HasVfx,
        string VfxSummary)
    {
        public int VfxEventCount => Composition?.Events.Count(eventCue => !eventCue.Event.IsKillEvent) ?? 0;
    }

}

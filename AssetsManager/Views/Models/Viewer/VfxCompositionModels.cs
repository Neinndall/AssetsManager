using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
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
        IReadOnlyList<VfxParticleEventAttachment> Attachments);

    public sealed record VfxIdleEffectDefinition(
        uint EffectKey,
        string EffectName,
        string BoneName,
        uint BoneNameHash,
        string TargetBoneName,
        uint TargetBoneNameHash,
        Vector3 Position);

    public sealed record VfxEventSequenceDefinition(
        uint OwnerPathHash,
        uint OwnerClassHash,
        float TickDuration,
        float StartFrame,
        float EndFrame,
        IReadOnlyList<VfxParticleEventDefinition> Events,
        string ClipName = null,
        string AnimationFilePath = null,
        uint GraphPathHash = 0,
        IReadOnlyList<uint> ChildClipHashes = null);

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
}

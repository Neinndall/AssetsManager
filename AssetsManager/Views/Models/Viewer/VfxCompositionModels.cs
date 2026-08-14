using System.Collections.Generic;

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

    public sealed record VfxEventSequenceDefinition(
        uint OwnerPathHash,
        uint OwnerClassHash,
        float TickDuration,
        float StartFrame,
        float EndFrame,
        IReadOnlyList<VfxParticleEventDefinition> Events);

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
        IReadOnlyList<VfxCompositionEvent> Events)
    {
        public int ResolvedCount { get; init; }
        public int UnresolvedCount => Events.Count - ResolvedCount;
    }
}

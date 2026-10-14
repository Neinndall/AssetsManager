using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Animation;

internal static class AnimationPoseCues
{
    internal static AnimationClipTimedCue Timed(AnimationClipEventDefinition cue, double at, double? until) => cue switch
    {
        AnimationSpringEventDefinition spring => new AnimationSpringCue(at, until, spring.SpringHash, spring.BlendOutSeconds),
        AnimationLockOrientationEventDefinition held => new AnimationLockOrientationCue(at, until, held.JointHash, held.BlendOutSeconds),
        AnimationOrientationEventDefinition turn => new AnimationOrientationCue(at, until, turn.BlendFromSeconds, turn.BlendToSeconds),
        _ => null
    };

    internal static bool Active(AnimationClipTimedCue cue, double time) =>
        time >= cue.AtSeconds && (!cue.UntilSeconds.HasValue || time < cue.UntilSeconds.Value);
}

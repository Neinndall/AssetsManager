using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Evaluates clip-authored visual state that is independent from particle simulation.
/// </summary>
internal static class VfxClipCueEvaluator
{
    internal static IReadOnlySet<uint> HiddenSubmeshesAt(
        IReadOnlyList<AnimationClipTimedCue> cues,
        IEnumerable<uint> initiallyHidden,
        double time)
    {
        var hidden = new HashSet<uint>(initiallyHidden ?? Array.Empty<uint>());
        if (cues == null || cues.Count == 0) return hidden;

        var changes = new List<(double At, IReadOnlyList<uint> Show, IReadOnlyList<uint> Hide)>();
        foreach (AnimationSubmeshVisibilityCue cue in cues.OfType<AnimationSubmeshVisibilityCue>())
        {
            changes.Add((cue.AtSeconds, cue.ShowSubmeshHashes, cue.HideSubmeshHashes));
            if (cue.UntilSeconds is { } until && until > cue.AtSeconds)
                changes.Add((until, cue.HideSubmeshHashes, cue.ShowSubmeshHashes));
        }

        foreach (var change in changes.OrderBy(change => change.At))
        {
            if (change.At > time) break;
            foreach (uint hash in change.Show) hidden.Remove(hash);
            foreach (uint hash in change.Hide) hidden.Add(hash);
        }

        return hidden;
    }
}

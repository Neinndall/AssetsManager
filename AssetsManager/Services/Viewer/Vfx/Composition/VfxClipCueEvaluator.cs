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
    internal sealed record VisibilityEntry(double AtSeconds, IReadOnlySet<uint> Hidden);
    private sealed record VisibilityChange(double At, int Order, IReadOnlyList<uint> Show,
        IReadOnlyList<uint> Hide, VisibilityChange Ends = null)
    {
        internal uint[] Shown { get; set; } = Array.Empty<uint>();
        internal uint[] Hidden { get; set; } = Array.Empty<uint>();
    }

    internal static IReadOnlyList<VisibilityEntry> BuildVisibilityTimeline(
        IReadOnlyList<AnimationClipTimedCue> cues,
        IEnumerable<uint> initiallyHidden)
    {
        var hidden = new HashSet<uint>(initiallyHidden ?? Array.Empty<uint>());
        var timeline = new List<VisibilityEntry>
        {
            new(0d, new HashSet<uint>(hidden))
        };
        if (cues == null || cues.Count == 0)
            return timeline;

        var changes = new List<VisibilityChange>();
        int order = 0;
        foreach (AnimationSubmeshVisibilityCue cue in cues.OfType<AnimationSubmeshVisibilityCue>())
        {
            var start = new VisibilityChange(cue.AtSeconds, order++, cue.ShowSubmeshHashes, cue.HideSubmeshHashes);
            changes.Add(start);
            if (cue.UntilSeconds is { } until)
                changes.Add(new(until, order++, Array.Empty<uint>(), Array.Empty<uint>(), start));
        }
        changes.Sort(static (left, right) =>
        {
            int time = left.At.CompareTo(right.At);
            return time != 0 ? time : left.Order.CompareTo(right.Order);
        });

        int at = 0;
        while (at < changes.Count)
        {
            double time = changes[at].At;
            do
            {
                var change = changes[at++];
                if (change.Ends == null)
                {
                    // An end must not invert commands that left the original state unchanged.
                    change.Shown = change.Show.Where(hidden.Contains).Distinct().ToArray();
                    change.Hidden = change.Hide.Where(hash => !hidden.Contains(hash)).Distinct().ToArray();
                }
                foreach (uint hash in change.Ends?.Hidden ?? change.Show) hidden.Remove(hash);
                foreach (uint hash in change.Ends?.Shown ?? change.Hide) hidden.Add(hash);
            }
            while (at < changes.Count && changes[at].At == time);

            timeline.Add(new VisibilityEntry(time, new HashSet<uint>(hidden)));
        }

        return timeline;
    }

    internal static IReadOnlySet<uint> HiddenSubmeshesAt(
        IReadOnlyList<VisibilityEntry> timeline,
        double time)
    {
        IReadOnlySet<uint> hidden = timeline is { Count: > 0 }
            ? timeline[0].Hidden
            : EmptyHidden;
        if (timeline == null) return hidden;

        for (int index = 1; index < timeline.Count; index++)
        {
            VisibilityEntry entry = timeline[index];
            if (entry.AtSeconds > time) break;
            hidden = entry.Hidden;
        }
        return hidden;
    }

    internal static double FoldedTime(double time, double duration)
    {
        if (!(duration > 0d) || !double.IsFinite(duration) || !double.IsFinite(time))
            return 0d;
        double folded = time % duration;
        return folded < 0d ? folded + duration : folded;
    }

    private static readonly IReadOnlySet<uint> EmptyHidden = new HashSet<uint>();
}

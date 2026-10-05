using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Evaluates clip-authored visual state that is independent from particle simulation.
/// </summary>
internal static class VfxClipCueEvaluator
{
    internal sealed record VisibilityEntry(double AtSeconds, IReadOnlySet<uint> Hidden);
    private sealed record VisibilityChange(double At, int Order, IReadOnlyList<uint> Show,
        IReadOnlyList<uint> Hide, AnimationSubmeshVisibilityCue StartCue = null);

    internal static IReadOnlyList<VisibilityEntry> BuildVisibilityTimeline(
        IReadOnlyList<AnimationClipTimedCue> cues,
        IEnumerable<uint> initiallyHidden,
        IEnumerable<string> submeshNames = null)
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
            changes.Add(new(cue.AtSeconds, order++, cue.ShowSubmeshHashes, cue.HideSubmeshHashes, cue));
            if (cue.UntilSeconds is { } until)
                changes.Add(new(until, order++, cue.HideSubmeshHashes, cue.ShowSubmeshHashes));
        }
        if (submeshNames != null)
            CoordinatePreviewReplacements(changes, hidden, submeshNames);
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
                foreach (uint hash in change.Show) hidden.Remove(hash);
                foreach (uint hash in change.Hide) hidden.Add(hash);
            }
            while (at < changes.Count && changes[at].At == time);

            timeline.Add(new VisibilityEntry(time, new HashSet<uint>(hidden)));
        }

        return timeline;
    }

    private static void CoordinatePreviewReplacements(List<VisibilityChange> changes,
        IReadOnlySet<uint> initiallyHidden, IEnumerable<string> submeshNames)
    {
        var names = submeshNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(Fnv1a.HashLower)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        VisibilityChange[] authored = changes.OrderBy(change => change.At).ThenBy(change => change.Order).ToArray();
        var delayed = new Dictionary<int, double>();
        var protectedOrders = new HashSet<int>();
        foreach (VisibilityChange change in authored)
        {
            AnimationSubmeshVisibilityCue cue = change.StartCue;
            if (protectedOrders.Contains(change.Order) || cue == null || !double.IsFinite(cue.FrameDurationSeconds) || cue.FrameDurationSeconds <= 0d ||
                !double.IsFinite(change.At) || change.Hide.Count == 0)
                continue;

            double nextTime = authored.Where(next => next.At > change.At).Select(next => next.At)
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            // Only a one-frame handoff is bridged; event ends and longer authored absences stay exact.
            if (nextTime - change.At > cue.FrameDurationSeconds + 0.000001d ||
                cue.UntilSeconds is { } end && end <= nextTime)
                continue;
            VisibilityChange[] nextGroup = authored.Where(next => next.At == nextTime).ToArray();
            if (nextGroup.Any(next => next.StartCue == null ||
                next.StartCue.UntilSeconds is { } until && until <= nextTime))
                continue;

            var touched = change.Show.Concat(change.Hide).ToHashSet();
            if (authored.Any(other => other.Order != change.Order && other.At == change.At &&
                other.Show.Concat(other.Hide).Any(touched.Contains)))
                continue;
            if (nextGroup.SelectMany(next => next.Hide).Any(change.Show.Contains))
                continue;

            var hiddenBefore = new HashSet<uint>(initiallyHidden);
            foreach (VisibilityChange previous in authored.TakeWhile(previous => previous.At < change.At))
            {
                foreach (uint hash in previous.Show) hiddenBefore.Remove(hash);
                foreach (uint hash in previous.Hide) hiddenBefore.Add(hash);
            }
            uint[] incoming = nextGroup.SelectMany(next => next.Show).Distinct()
                .Where(hash => hiddenBefore.Contains(hash) && !change.Show.Contains(hash) &&
                    !change.Hide.Contains(hash) && names.ContainsKey(hash) &&
                    !nextGroup.Any(next => next.Hide.Contains(hash))).ToArray();
            uint[] outgoingMeshes = change.Hide.Distinct()
                .Where(hash => !hiddenBefore.Contains(hash) && names.ContainsKey(hash)).ToArray();
            bool replacement = false;
            bool ambiguous = false;
            foreach (uint outgoing in outgoingMeshes)
            {
                // Prefix variants are identified from this SKN's names, never from a champion/effect recipe.
                int matches = incoming.Count(hash => AreSubmeshVariants(names[outgoing], names[hash]));
                replacement |= matches == 1;
                ambiguous |= matches > 1;
            }
            ambiguous |= incoming.Any(hash => outgoingMeshes.Count(outgoing => AreSubmeshVariants(names[outgoing], names[hash])) > 1);
            if (replacement && !ambiguous)
            {
                delayed[change.Order] = nextTime;
                foreach (VisibilityChange next in nextGroup) protectedOrders.Add(next.Order);
            }
        }
        for (int index = 0; index < changes.Count; index++)
            if (delayed.TryGetValue(changes[index].Order, out double time))
                changes[index] = changes[index] with { At = time };
    }

    private static bool AreSubmeshVariants(string first, string second) =>
        first.EndsWith("_" + second, StringComparison.OrdinalIgnoreCase) ||
        second.EndsWith("_" + first, StringComparison.OrdinalIgnoreCase);

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

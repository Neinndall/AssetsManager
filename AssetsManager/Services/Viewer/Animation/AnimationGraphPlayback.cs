using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Animation
{
    /// <summary>
    /// Shared AnimationGraph playback semantics used by champion/VFX and MAP structures.
    /// Composite resolution, idle selection and mTickDuration retiming mirror current LTK Manager MAIN.
    /// </summary>
    internal static class AnimationGraphPlayback
    {
        private static readonly uint SequencerClipClass = Fnv1a.HashLower("SequencerClipData");
        private static readonly uint ParametricClipClass = Fnv1a.HashLower("ParametricClipData");

        internal static IReadOnlyList<AnimationClipDefinition> SelectGraphClips(
            IReadOnlyList<AnimationClipDefinition> clips,
            uint animationGraphPathHash)
        {
            clips ??= Array.Empty<AnimationClipDefinition>();
            if (animationGraphPathHash == 0u) return clips;
            return clips.Where(clip => clip.GraphPathHash == animationGraphPathHash).ToArray();
        }

        internal static IReadOnlyList<AnimationClipDefinition> PlayableClips(
            IReadOnlyList<AnimationClipDefinition> clips)
        {
            if (clips == null || clips.Count == 0)
                return Array.Empty<AnimationClipDefinition>();
            return clips.Where(clip => ResolvePlaylist(clip, clips).Count > 0).ToArray();
        }

        internal static AnimationClipDefinition OpeningClip(IReadOnlyList<AnimationClipDefinition> clips) =>
            PlayableClips(clips)
                .FirstOrDefault(clip =>
                    DisplayNameFor(clip).StartsWith("idle", StringComparison.OrdinalIgnoreCase));

        internal static AnimationClipDefinition NamedOrOpeningClip(
            IReadOnlyList<AnimationClipDefinition> clips,
            string requested)
        {
            IReadOnlyList<AnimationClipDefinition> playable = PlayableClips(clips);
            AnimationClipDefinition named = string.IsNullOrWhiteSpace(requested)
                ? null
                : playable.FirstOrDefault(clip =>
                    DisplayNameFor(clip).Equals(requested, StringComparison.OrdinalIgnoreCase));
            return named ?? playable.FirstOrDefault(clip =>
                DisplayNameFor(clip).StartsWith("idle", StringComparison.OrdinalIgnoreCase));
        }

        internal static string DisplayNameFor(AnimationClipDefinition clip) =>
            !string.IsNullOrWhiteSpace(clip?.ClipName)
                ? clip.ClipName
                : $"0x{clip?.OwnerPathHash ?? 0u:x8}";

        internal static IReadOnlyList<float> ParameterValues(AnimationClipDefinition clip)
        {
            if (clip?.OwnerClassHash != ParametricClipClass)
                return Array.Empty<float>();

            IEnumerable<float?> authored = clip.ParametricValues ??
                (clip.ChildParameters ?? Array.Empty<float>()).Select(value => (float?)value);
            float[] values = authored
                .Where(value => value.HasValue && float.IsFinite(value.Value))
                .Select(value => value.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            return values.Length > 1 ? values : Array.Empty<float>();
        }

        internal static float NearestParameter(IReadOnlyList<float> values, float value)
        {
            if (values == null || values.Count == 0) return value;

            float best = values[0];
            float bestDistance = MathF.Abs(best - value);
            for (int index = 1; index < values.Count; index++)
            {
                float distance = MathF.Abs(values[index] - value);
                if (distance < bestDistance)
                {
                    best = values[index];
                    bestDistance = distance;
                }
            }
            return best;
        }

        internal static IReadOnlyList<AnimationClipDefinition> ResolvePlaylist(
            AnimationClipDefinition clip,
            IReadOnlyList<AnimationClipDefinition> clips,
            float? parameter = null,
            int? gearIndex = null)
        {
            if (clip == null || clips == null)
                return Array.Empty<AnimationClipDefinition>();

            var byKey = clips
                .Where(item => item.GraphPathHash == clip.GraphPathHash)
                .GroupBy(item => item.OwnerPathHash)
                .ToDictionary(group => group.Key, group => group.First());
            var path = new HashSet<uint>();
            var result = new List<AnimationClipDefinition>();

            void Visit(AnimationClipDefinition current)
            {
                if (!path.Add(current.OwnerPathHash)) return;
                try
                {
                    if (!string.IsNullOrWhiteSpace(current.AnimationFilePath))
                    {
                        result.Add(current);
                        return;
                    }

                    IReadOnlyList<uint> children = current.ChildClipHashes ?? Array.Empty<uint>();
                    IEnumerable<int> order = Enumerable.Range(0, children.Count);
                    float? selectedParameter = current.UsesEquippedGearParameter && gearIndex.HasValue
                        ? gearIndex.Value : parameter;
                    if (current.OwnerClassHash == ParametricClipClass &&
                        children.Count > 0 &&
                        selectedParameter.HasValue)
                    {
                        IReadOnlyList<float?> values = current.ParametricValues ??
                            (current.ChildParameters ?? Array.Empty<float>())
                                .Select(value => (float?)value)
                                .ToArray();
                        float selected = selectedParameter.Value;
                        order = order
                            .OrderBy(index => MathF.Abs(
                                ((index < values.Count ? values[index] : null) ?? 0f) - selected))
                            .ThenBy(index => index);
                    }

                    foreach (int index in order)
                    {
                        int before = result.Count;
                        if (byKey.TryGetValue(children[index], out AnimationClipDefinition next))
                            Visit(next);
                        if (result.Count > before && current.OwnerClassHash != SequencerClipClass)
                            break;
                    }
                }
                finally
                {
                    path.Remove(current.OwnerPathHash);
                }
            }

            Visit(clip);
            return result;
        }

        internal static IAnimationAsset RetimeForGraph(IAnimationAsset asset, float tickDuration)
        {
            ArgumentNullException.ThrowIfNull(asset);
            return new RetimedAnimationAsset(asset, tickDuration);
        }

        internal static double? TimedEventEnd(
            AnimationClipEventDefinition authoredEvent,
            double at,
            double? until)
        {
            // LTK's visibility timeline always publishes the authored inverse change at mEndFrame,
            // even when it is equal to or earlier than mStartFrame. Joint snaps instead normalize
            // a non-forward end to "hold through the pass"; keep the legacy normalization for
            // other non-rendered event kinds as well.
            if (authoredEvent is AnimationSubmeshVisibilityEventDefinition)
                return until;
            return until.HasValue && until.Value > at ? until : null;
        }

        internal static IAnimationAsset CreatePlaylist(IReadOnlyList<IAnimationAsset> steps) =>
            new AnimationPlaylistAsset(steps ?? Array.Empty<IAnimationAsset>());

        private sealed class RetimedAnimationAsset : IAnimationAsset
        {
            private readonly IAnimationAsset _source;
            private readonly float _sourceDuration;

            internal RetimedAnimationAsset(IAnimationAsset source, float tickDuration)
            {
                _source = source;
                _sourceDuration = Math.Max(0f, source.Duration);

                float sourceFps = float.IsFinite(source.Fps) && source.Fps > 0f ? source.Fps : 30f;
                float frameSeconds = float.IsFinite(tickDuration) && tickDuration > 0f
                    ? tickDuration
                    : 1f / sourceFps;
                float frameIntervals = MathF.Floor(_sourceDuration * sourceFps + 0.5f);

                Duration = frameIntervals * frameSeconds;
                Fps = 1f / frameSeconds;
            }

            public float Duration { get; }
            public float Fps { get; }
            public bool IsDisposed => _source.IsDisposed;
            public void Dispose() { }

            public void Evaluate(
                float time,
                IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
            {
                float sourceTime = Duration > 0f && _sourceDuration > 0f
                    ? Math.Clamp(time, 0f, Duration) * (_sourceDuration / Duration)
                    : 0f;
                _source.Evaluate(sourceTime, pose);
            }
        }

        private sealed class AnimationPlaylistAsset : IAnimationAsset
        {
            private readonly IReadOnlyList<IAnimationAsset> _steps;

            internal AnimationPlaylistAsset(IReadOnlyList<IAnimationAsset> steps)
            {
                _steps = steps;
                Duration = steps.Sum(step => step.Duration);
                Fps = steps.Count > 0 ? steps[0].Fps : 30f;
            }

            public float Duration { get; }
            public float Fps { get; }
            public bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;

            public void Evaluate(
                float time,
                IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
            {
                if (IsDisposed) return;
                float remaining = Math.Clamp(time, 0f, Duration);
                for (int index = 0; index < _steps.Count; index++)
                {
                    IAnimationAsset step = _steps[index];
                    if (remaining < step.Duration || index == _steps.Count - 1)
                    {
                        step.Evaluate(Math.Min(remaining, step.Duration), pose);
                        return;
                    }
                    remaining -= step.Duration;
                }
            }
        }
    }
}

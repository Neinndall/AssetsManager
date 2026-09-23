using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Services.Viewer.Animation
{
    /// <summary>
    /// Shared animation runtime for one map character skin. Atomic ANMs are cached once,
    /// and each effective graph clip owns one pose evaluator shared by all of its placements.
    /// </summary>
    internal sealed class MapCharacterAnimationRuntime : IDisposable
    {
        private const string BindKey = "\0bind";

        private sealed record PreparedStep(
            AnimationClipDefinition Clip,
            float Duration,
            float FrameSeconds);

        private sealed record PoseState(
            IAnimationAsset Animation,
            AnimationService Evaluator,
            IReadOnlyList<AnimationClipTimedCue> TimedCues,
            IReadOnlyList<PreparedStep> Steps,
            float? Parameter = null);

        private readonly MapAssetResolver _assetResolver;
        private readonly LogService _logService;
        private readonly Dictionary<string, IAnimationAsset> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, PoseState> _states = new();
        private readonly Dictionary<uint, PoseState> _previewStates = new();
        private readonly Dictionary<string, PoseState> _byRequested = new(StringComparer.OrdinalIgnoreCase);
        private readonly PoseState _bindState;
        private string _projectRoot;
        private bool _disposed;

        internal MapCharacterAnimationRuntime(MapAssetResolver assetResolver, LogService logService)
        {
            _assetResolver = assetResolver;
            _logService = logService;
            _bindState = new PoseState(
                BindPoseAnimationAsset.Instance,
                new AnimationService(logService),
                Array.Empty<AnimationClipTimedCue>(),
                Array.Empty<PreparedStep>());
        }

        internal async Task PrepareAsync(
            MapCharacterAssetData asset,
            IEnumerable<string> requestedAnimations,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(asset);
            _projectRoot = projectRoot;

            string[] requested = (requestedAnimations ?? Enumerable.Empty<string>())
                .Select(NormalizeRequested)
                .Append(BindKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            IReadOnlyList<AnimationClipDefinition> clips = asset.AnimationGraph?.Clips ??
                                                           Array.Empty<AnimationClipDefinition>();
            foreach (string key in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string requestedName = key == BindKey ? null : key;
                AnimationClipDefinition clip = AnimationGraphPlayback.NamedOrOpeningClip(clips, requestedName);
                if (clip == null)
                {
                    _byRequested[key] = _bindState;
                    continue;
                }

                if (_states.TryGetValue(clip.OwnerPathHash, out PoseState existing))
                {
                    _byRequested[key] = existing;
                    continue;
                }

                IReadOnlyList<AnimationClipDefinition> playlist =
                    AnimationGraphPlayback.ResolvePlaylist(clip, clips);
                var steps = new List<IAnimationAsset>(playlist.Count);
                foreach (AnimationClipDefinition step in playlist)
                {
                    IAnimationAsset source = await LoadSourceAsync(
                        step.AnimationFilePath,
                        projectRoot,
                        cancellationToken);
                    if (source == null)
                        continue;
                    steps.Add(AnimationGraphPlayback.RetimeForGraph(source, step.TickDuration));
                }

                if (steps.Count == 0)
                {
                    _byRequested[key] = _bindState;
                    continue;
                }

                IAnimationAsset animation = AnimationGraphPlayback.CreatePlaylist(steps);
                var state = new PoseState(
                    animation,
                    new AnimationService(_logService),
                    Array.Empty<AnimationClipTimedCue>(),
                    Array.Empty<PreparedStep>());
                _states[clip.OwnerPathHash] = state;
                _byRequested[key] = state;
            }
        }

        internal async Task<bool> PrepareClipAsync(
            MapCharacterAssetData asset,
            AnimationClipDefinition clip,
            float? parameter = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(asset);
            if (clip == null) return false;
            IReadOnlyList<float> values = AnimationGraphPlayback.ParameterValues(clip);
            float? effectiveParameter = values.Count > 1
                ? AnimationGraphPlayback.NearestParameter(
                    values,
                    parameter ?? clip.ParametricValues?.FirstOrDefault() ?? values[0])
                : null;
            if (_previewStates.TryGetValue(clip.OwnerPathHash, out PoseState held) &&
                held.Parameter == effectiveParameter)
            {
                return true;
            }

            IReadOnlyList<AnimationClipDefinition> clips = asset.AnimationGraph?.Clips ??
                                                           Array.Empty<AnimationClipDefinition>();
            IReadOnlyList<AnimationClipDefinition> playlist =
                AnimationGraphPlayback.ResolvePlaylist(clip, clips, effectiveParameter);
            var steps = new List<IAnimationAsset>(playlist.Count);
            var preparedSteps = new List<PreparedStep>(playlist.Count);
            foreach (AnimationClipDefinition step in playlist)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAnimationAsset source = await LoadSourceAsync(
                    step.AnimationFilePath,
                    _projectRoot,
                    cancellationToken);
                if (source == null)
                    continue;
                IAnimationAsset timed = AnimationGraphPlayback.RetimeForGraph(source, step.TickDuration);
                steps.Add(timed);
                preparedSteps.Add(new PreparedStep(
                    step,
                    timed.Duration,
                    timed.Fps > 0f && float.IsFinite(timed.Fps) ? 1f / timed.Fps : 1f / 30f));
            }

            if (steps.Count == 0)
                return false;

            IAnimationAsset animation = AnimationGraphPlayback.CreatePlaylist(steps);
            IReadOnlyList<AnimationClipTimedCue> timedCues = BuildTimedCues(playlist, steps);
            var evaluator = new AnimationService(_logService);
            evaluator.SetJointSnapCues(timedCues.OfType<AnimationJointSnapCue>().ToArray());
            if (_previewStates.Remove(clip.OwnerPathHash, out PoseState previous))
            {
                previous.Animation.Dispose();
                previous.Evaluator.Dispose();
            }
            _previewStates[clip.OwnerPathHash] = new PoseState(
                animation,
                evaluator,
                timedCues,
                preparedSteps,
                effectiveParameter);
            return true;
        }

        internal float PreparedClipDuration(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.Animation.Duration
                : 0f;

        internal IReadOnlyList<AnimationClipTimedCue> PreparedClipCues(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.TimedCues
                : Array.Empty<AnimationClipTimedCue>();

        internal IReadOnlyList<AnimationClipDefinition> PreparedClipPlaylist(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.Steps.Select(step => step.Clip).ToArray()
                : Array.Empty<AnimationClipDefinition>();

        internal IReadOnlyList<float> PreparedClipStepDurations(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.Steps.Select(step => step.Duration).ToArray()
                : Array.Empty<float>();

        internal IReadOnlyList<float> PreparedClipFrameSeconds(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.Steps.Select(step => step.FrameSeconds).ToArray()
                : Array.Empty<float>();

        internal bool TryGetPreparedClipBoneTransform(
            AnimationClipDefinition clip,
            string boneName,
            uint boneHash,
            out Matrix4x4 transform)
        {
            transform = Matrix4x4.Identity;
            if (clip == null || !_previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state))
                return false;
            if (!string.IsNullOrWhiteSpace(boneName) && state.Evaluator.TryGetBoneTransform(boneName, out transform))
                return true;
            return boneHash != 0 && state.Evaluator.TryGetBoneTransform(boneHash, out transform);
        }

        internal Matrix4x4[] PreparedClipSkinningMatrices(AnimationClipDefinition clip) =>
            clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState state)
                ? state.Evaluator.FinalBoneTransforms ?? Array.Empty<Matrix4x4>()
                : Array.Empty<Matrix4x4>();

        internal Matrix4x4[] EvaluateClip(
            MapCharacterAssetData asset,
            AnimationClipDefinition clip,
            float timeSeconds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(asset);

            PoseState state = clip != null && _previewStates.TryGetValue(clip.OwnerPathHash, out PoseState prepared)
                ? prepared
                : _bindState;
            return state.Evaluator.EvaluateSkinningTransforms(
                timeSeconds,
                state.Animation,
                asset.Skeleton);
        }

        internal Matrix4x4[] Evaluate(
            MapCharacterAssetData asset,
            string requestedAnimation,
            float timeSeconds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(asset);

            string key = NormalizeRequested(requestedAnimation);
            if (!_byRequested.TryGetValue(key, out PoseState state))
                state = _bindState;
            return state.Evaluator.EvaluateSkinningTransforms(
                timeSeconds,
                state.Animation,
                asset.Skeleton);
        }

        internal static IReadOnlyList<AnimationClipTimedCue> BuildTimedCues(
            IReadOnlyList<AnimationClipDefinition> playlist,
            IReadOnlyList<IAnimationAsset> steps)
        {
            if (playlist == null || steps == null || playlist.Count == 0 || steps.Count == 0)
                return Array.Empty<AnimationClipTimedCue>();

            var cues = new List<AnimationClipTimedCue>();
            double passTime = 0d;
            int count = Math.Min(playlist.Count, steps.Count);
            for (int index = 0; index < count; index++)
            {
                AnimationClipDefinition atomic = playlist[index];
                IAnimationAsset timed = steps[index];
                double tick = timed.Fps > 0f && float.IsFinite(timed.Fps)
                    ? 1d / timed.Fps
                    : 1d / 30d;
                foreach (AnimationClipEventDefinition authoredEvent in atomic.Events ?? Array.Empty<AnimationClipEventDefinition>())
                {
                    double at = passTime + authoredEvent.StartFrame * tick;
                    double? until = authoredEvent.EndFrame >= 0f
                        ? passTime + authoredEvent.EndFrame * tick
                        : null;
                    if (until <= at) until = null;

                    switch (authoredEvent)
                    {
                        case AnimationSubmeshVisibilityEventDefinition visibility:
                            cues.Add(new AnimationSubmeshVisibilityCue(
                                at,
                                until,
                                visibility.ShowSubmeshHashes,
                                visibility.HideSubmeshHashes));
                            break;
                        case AnimationJointSnapEventDefinition snap:
                            cues.Add(new AnimationJointSnapCue(
                                at,
                                until,
                                snap.JointHash,
                                snap.SnapToHash,
                                snap.Offset));
                            break;
                        case AnimationConformToPathEventDefinition conform:
                            cues.Add(new AnimationConformToPathCue(
                                at,
                                until,
                                conform.MaskHash,
                                conform.BlendInSeconds,
                                conform.BlendOutSeconds));
                            break;
                    }
                }

                passTime += timed.Duration;
            }

            return cues.OrderBy(cue => cue.AtSeconds).ToArray();
        }

        internal static MapAssetReference ReferenceFromAnimation(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string trimmed = value.Trim();
            if (!trimmed.Contains('/') && !trimmed.Contains('\\'))
            {
                string stem = Path.GetFileNameWithoutExtension(trimmed);
                if (stem.Length == 16 && ulong.TryParse(
                        stem,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out ulong hash))
                {
                    return new MapAssetReference(null, hash);
                }
            }

            return new MapAssetReference(trimmed, 0);
        }

        private async Task<IAnimationAsset> LoadSourceAsync(
            string animationPath,
            string projectRoot,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(animationPath))
                return null;
            if (_sources.TryGetValue(animationPath, out IAnimationAsset cached))
                return cached;

            MapAssetReference reference = ReferenceFromAnimation(animationPath);
            MapResolvedAsset asset = await _assetResolver.ResolveReferenceAsync(
                reference,
                projectRoot,
                cancellationToken);
            if (asset == null)
                return null;

            try
            {
                await using Stream stream = await _assetResolver.OpenReadAsync(asset, cancellationToken);
                if (stream == null) return null;
                IAnimationAsset animation = AnimationAsset.Load(stream);
                _sources[animationPath] = animation;
                return animation;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogDebug($"MAP structure animation unavailable '{animationPath}': {ex.Message}");
                return null;
            }
        }

        private static string NormalizeRequested(string value) =>
            string.IsNullOrWhiteSpace(value) ? BindKey : value.Trim().ToLowerInvariant();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (PoseState state in _states.Values)
            {
                state.Animation.Dispose();
                state.Evaluator.Dispose();
            }
            _states.Clear();
            foreach (PoseState state in _previewStates.Values)
            {
                state.Animation.Dispose();
                state.Evaluator.Dispose();
            }
            _previewStates.Clear();
            _byRequested.Clear();
            _bindState.Evaluator.Dispose();

            foreach (IAnimationAsset source in _sources.Values)
                source.Dispose();
            _sources.Clear();
        }

        private sealed class BindPoseAnimationAsset : IAnimationAsset
        {
            internal static BindPoseAnimationAsset Instance { get; } = new();
            public float Duration => 0f;
            public float Fps => 30f;
            public bool IsDisposed => false;
            public void Dispose() { }
            public void Evaluate(
                float time,
                IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
            {
                pose?.Clear();
            }
        }
    }
}

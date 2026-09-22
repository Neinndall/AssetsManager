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

        private sealed record PoseState(IAnimationAsset Animation, AnimationService Evaluator);

        private readonly MapAssetResolver _assetResolver;
        private readonly LogService _logService;
        private readonly Dictionary<string, IAnimationAsset> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, PoseState> _states = new();
        private readonly Dictionary<string, PoseState> _byRequested = new(StringComparer.OrdinalIgnoreCase);
        private readonly PoseState _bindState;
        private bool _disposed;

        internal MapCharacterAnimationRuntime(MapAssetResolver assetResolver, LogService logService)
        {
            _assetResolver = assetResolver;
            _logService = logService;
            _bindState = new PoseState(BindPoseAnimationAsset.Instance, new AnimationService(logService));
        }

        internal async Task PrepareAsync(
            MapCharacterAssetData asset,
            IEnumerable<string> requestedAnimations,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(asset);

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
                var state = new PoseState(animation, new AnimationService(_logService));
                _states[clip.OwnerPathHash] = state;
                _byRequested[key] = state;
            }
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

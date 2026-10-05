using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using Pose = System.Collections.Generic.Dictionary<uint, (System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Translation, System.Numerics.Vector3 Scale)>;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Samples sequencer poses with graph-authored time blends, without shifting event clocks.</summary>
internal sealed class AnimationPlaylistAsset : IGraphPoseAnimationAsset
{
    private readonly IAnimationAsset[] _steps;
    private readonly float[] _starts;
    private readonly float[] _blendSeconds;
    private readonly Pose _fromPose = new();
    private readonly Pose _toPose = new();
    private readonly Pose _bindPose = new();
    private RigResource _skeleton;

    internal AnimationPlaylistAsset(IReadOnlyList<IAnimationAsset> steps,
        IReadOnlyList<AnimationClipDefinition> clips, AnimationGraphDefinition graph)
    {
        if (clips != null && clips.Count != steps.Count)
            throw new ArgumentException("Playlist assets and clip definitions must have matching counts.", nameof(clips));
        _steps = steps.ToArray();
        _starts = new float[_steps.Length];
        _blendSeconds = new float[_steps.Length];
        var rules = graph?.Blends?.OfType<AnimationTimeBlendDefinition>()
            .ToDictionary(rule => (rule.FromClipHash, rule.ToClipHash));
        float duration = 0f;
        for (int index = 0; index < _steps.Length; index++)
        {
            _starts[index] = duration;
            duration += _steps[index].Duration;
            if (index > 0 && clips != null && rules != null &&
                clips[index - 1].GraphPathHash == graph.PathHash && clips[index].GraphPathHash == graph.PathHash &&
                rules.TryGetValue((clips[index - 1].OwnerPathHash, clips[index].OwnerPathHash), out var rule) &&
                float.IsFinite(rule.Duration) && rule.Duration > 0f)
                _blendSeconds[index] = rule.Duration;
        }
        Duration = duration;
        Fps = _steps.Length > 0 ? _steps[0].Fps : 30f;
    }

    public float Duration { get; }
    public float Fps { get; }
    public bool IsDisposed { get; private set; }
    // Source assets are owned by the catalog/runtime, which can share them across playlists.
    public void Dispose() => IsDisposed = true;
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        => Evaluate(time, pose, null);

    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose,
        RigResource skeleton)
    {
        if (IsDisposed || _steps.Length == 0) return;
        time = float.IsFinite(time) ? Math.Clamp(time, 0f, Duration) : 0f;
        int index = 0;
        while (index < _steps.Length - 1 && time >= _starts[index + 1]) index++;
        float localTime = Math.Min(time - _starts[index], _steps[index].Duration);
        if (index == 0 || localTime >= _blendSeconds[index])
        {
            AnimationGraphPlayback.Evaluate(_steps[index], localTime, pose, skeleton);
            return;
        }

        if (!ReferenceEquals(_skeleton, skeleton))
        {
            _skeleton = skeleton;
            _bindPose.Clear();
            if (skeleton != null)
                foreach (Joint joint in skeleton.Joints)
                    _bindPose[Elf.HashLower(joint.Name)] = (joint.LocalRotation, joint.LocalTranslation, joint.LocalScale);
        }

        // Reconstruct interrupted blends from their preceding boundaries, so seeking never uses a cached live pose.
        int first = index - 1;
        while (first > 0 && _steps[first].Duration < _blendSeconds[first]) first--;
        Pose previous = _fromPose, current = _toPose;
        for (int step = first; step <= index; step++)
        {
            current.Clear();
            float sampleTime = step == index ? localTime : _steps[step].Duration;
            AnimationGraphPlayback.Evaluate(_steps[step], sampleTime, current, skeleton);
            if (step > first)
                Blend(previous, current, Math.Clamp(sampleTime / _blendSeconds[step], 0f, 1f));
            (previous, current) = (current, previous);
        }
        foreach (var joint in previous) pose[joint.Key] = joint.Value;
    }

    private void Blend(Pose previous, Pose current, float weight)
    {
        foreach (var joint in current)
        {
            if (previous.TryGetValue(joint.Key, out var from) || _bindPose.TryGetValue(joint.Key, out from))
                current[joint.Key] = Interpolate(from, joint.Value, weight);
        }
        foreach (var joint in previous)
        {
            if (!current.ContainsKey(joint.Key))
                current[joint.Key] = _bindPose.TryGetValue(joint.Key, out var to)
                    ? Interpolate(joint.Value, to, weight) : joint.Value;
        }
    }

    private static (Quaternion Rotation, Vector3 Translation, Vector3 Scale) Interpolate(
        (Quaternion Rotation, Vector3 Translation, Vector3 Scale) from,
        (Quaternion Rotation, Vector3 Translation, Vector3 Scale) to, float weight) =>
        (Quaternion.Normalize(Quaternion.Slerp(from.Rotation, to.Rotation, weight)),
         Vector3.Lerp(from.Translation, to.Translation, weight),
         Vector3.Lerp(from.Scale, to.Scale, weight));
}

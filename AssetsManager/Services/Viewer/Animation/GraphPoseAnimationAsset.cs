using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Animation;

internal interface IGraphPoseAnimationAsset : IAnimationAsset
{
    void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose, RigResource skeleton);
}

/// <summary>An additive graph track previews its deltas over the authored idle base, with bind fallback.</summary>
internal sealed class GraphPoseAnimationAsset : IGraphPoseAnimationAsset
{
    private readonly IAnimationAsset _source;
    private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _basis = new();
    private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _deltas = new();
    private readonly float _weight;
    private readonly IReadOnlyList<float> _mask;

    internal GraphPoseAnimationAsset(IAnimationAsset source, IAnimationAsset basis, float weight, IReadOnlyList<float> mask)
    {
        _source = source;
        _weight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 1f;
        _mask = mask;
        if (basis != null) AnimationGraphPlayback.Evaluate(basis, 0f, _basis, null);
    }

    public float Duration => _source.Duration;
    public float Fps => _source.Fps;
    public bool IsDisposed => _source.IsDisposed;
    public void Dispose() { }
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose) => Evaluate(time, pose, null);

    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose, RigResource skeleton)
    {
        _deltas.Clear();
        AnimationGraphPlayback.Evaluate(_source, time, _deltas, skeleton);
        foreach (var entry in _basis) pose[entry.Key] = entry.Value;
        if (skeleton != null)
        {
            for (int index = 0; index < skeleton.Joints.Count; index++)
            {
                var joint = skeleton.Joints[index];
                uint hash = Elf.HashLower(joint.Name);
                if (!_basis.TryGetValue(hash, out var basis))
                {
                    Matrix4x4.Decompose(joint.LocalTransform, out var scale, out var rotation, out var translation);
                    basis = (rotation, translation, scale);
                }
                float maskWeight = _mask == null ? 1f : index < _mask.Count && float.IsFinite(_mask[index])
                    ? Math.Clamp(_mask[index], 0f, 1f) : 0f;
                float weight = _weight * maskWeight;
                pose[hash] = _deltas.TryGetValue(hash, out var delta) ? Blend(basis, delta, weight) : basis;
            }
        }
        else
        {
            foreach (var entry in _deltas)
            {
                var basis = _basis.TryGetValue(entry.Key, out var authored) ? authored : (Quaternion.Identity, Vector3.Zero, Vector3.One);
                pose[entry.Key] = Blend(basis, entry.Value, _weight);
            }
        }
    }

    private static (Quaternion, Vector3, Vector3) Blend(
        (Quaternion Rotation, Vector3 Translation, Vector3 Scale) basis,
        (Quaternion Rotation, Vector3 Translation, Vector3 Scale) delta, float weight) =>
        (Quaternion.Normalize(basis.Rotation * Quaternion.Slerp(Quaternion.Identity, delta.Rotation, weight)),
         basis.Translation + delta.Translation * weight,
         basis.Scale * Vector3.Lerp(Vector3.One, delta.Scale, weight));

    internal static AnimationTrackDefinition AdditiveTrack(AnimationClipDefinition clip, AnimationGraphDefinition graph) =>
        graph?.Tracks?.FirstOrDefault(track => track.Hash == clip?.Track?.Hash && track.BlendMode == 1);

    internal static AnimationClipDefinition BasisClip(AnimationGraphDefinition graph) =>
        graph?.Clips?.FirstOrDefault(clip => clip.OwnerPathHash == Fnv1a.HashLower("Idle1_Base") &&
            !string.IsNullOrWhiteSpace(clip.AnimationFilePath) && AdditiveTrack(clip, graph) == null);

    internal static IAnimationAsset Wrap(IAnimationAsset source, AnimationClipDefinition clip, AnimationGraphDefinition graph, IAnimationAsset basis)
    {
        var track = AdditiveTrack(clip, graph);
        return track == null ? source : new GraphPoseAnimationAsset(source, basis, track.BlendWeight,
            graph.Masks?.FirstOrDefault(mask => mask.Hash == clip.Mask?.Hash)?.Weights);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Immutable native-frame poses for history-independent sampling of compressed ANMs.</summary>
internal sealed class SampledAnimationAsset : IAnimationAsset
{
    private readonly uint[] _joints;
    private readonly int _frameCount;
    private (Quaternion Rotation, Vector3 Translation, Vector3 Scale)[] _poses;

    internal SampledAnimationAsset(IAnimationAsset source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Duration = source.Duration;
        Fps = source.Fps;
        if (!float.IsFinite(Duration) || Duration < 0 || !float.IsFinite(Fps) || Fps <= 0)
            throw new InvalidDataException("Animation has invalid frame timing.");
        _frameCount = checked((int)MathF.Floor(Duration * Fps + 0.5f) + 1);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        cancellationToken.ThrowIfCancellationRequested();
        source.Evaluate(0, pose);
        _joints = pose.Keys.ToArray();
        _poses = new (Quaternion, Vector3, Vector3)[checked(_frameCount * _joints.Length)];
        for (int frame = 0; frame < _frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame > 0)
            {
                pose.Clear();
                source.Evaluate(frame * Duration / (_frameCount - 1), pose);
            }
            for (int joint = 0; joint < _joints.Length; joint++)
                _poses[frame * _joints.Length + joint] = pose[_joints[joint]];
        }
    }

    internal static IAnimationAsset Load(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAnimationAsset source = AnimationAsset.Load(stream);
        if (source is not CompressedAnimationAsset) return source;
        // LTK bakes ANMs at their native frame rate before preview interpolation. A compressed
        // evaluator's jump cache can otherwise give different poses after a backward seek.
        using (source)
            return new SampledAnimationAsset(source, cancellationToken);
    }

    public float Duration { get; }
    public float Fps { get; }
    public bool IsDisposed { get; private set; }

    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        float frame = Duration > 0 && float.IsFinite(time)
            ? Math.Clamp(time / Duration, 0, 1) * (_frameCount - 1) : 0;
        int from = Math.Min((int)frame, _frameCount - 1);
        int to = Math.Min(from + 1, _frameCount - 1);
        float amount = frame - from;
        for (int joint = 0; joint < _joints.Length; joint++)
        {
            var a = _poses[from * _joints.Length + joint];
            var b = _poses[to * _joints.Length + joint];
            pose[_joints[joint]] = (
                Quaternion.Slerp(a.Rotation, b.Rotation, amount),
                Vector3.Lerp(a.Translation, b.Translation, amount),
                Vector3.Lerp(a.Scale, b.Scale, amount));
        }
    }

    public void Dispose()
    {
        IsDisposed = true;
        _poses = Array.Empty<(Quaternion, Vector3, Vector3)>();
    }
}

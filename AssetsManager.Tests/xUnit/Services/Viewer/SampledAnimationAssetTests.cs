using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using AssetsManager.Services.Viewer.Animation;
using LeagueToolkit.Core.Animation;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public class SampledAnimationAssetTests
{
    [Fact]
    public void BackwardSamplingDoesNotRevisitHistorySensitiveSource()
    {
        using var source = new HistorySensitiveAnimation();
        using var sampled = new SampledAnimationAsset(source);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        sampled.Evaluate(0.35f, pose);
        var first = pose[1];
        sampled.Evaluate(0.8f, pose);
        sampled.Evaluate(0.35f, pose);
        Assert.Equal(first, pose[1]);
        Assert.Equal(0.35f, pose[1].Translation.X, 5);
        Assert.Equal(11, source.EvaluationCount);
    }

    [Fact]
    public void SamplingPreservesEndpointsAndInterpolatesRotationAndScale()
    {
        using var source = new HistorySensitiveAnimation();
        using var sampled = new SampledAnimationAsset(source);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        sampled.Evaluate(-1, pose);
        Assert.Equal(Vector3.Zero, pose[1].Translation);
        sampled.Evaluate(2, pose);
        Assert.Equal(Vector3.UnitX, pose[1].Translation);
        Assert.Equal(new Vector3(2), pose[1].Scale);
        sampled.Evaluate(0.55f, pose);
        Assert.Equal(0.55f, pose[1].Translation.X, 5);
        Assert.Equal(1.55f, pose[1].Scale.X, 5);
        Assert.True(Math.Abs(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.55f), pose[1].Rotation)) > 0.99999f);
    }

    [Fact]
    public void BakingHonorsCancellationAndDisposedSamplesRejectReads()
    {
        using var source = new HistorySensitiveAnimation();
        Assert.Throws<OperationCanceledException>(() => new SampledAnimationAsset(source, new CancellationToken(true)));
        var sampled = new SampledAnimationAsset(source);
        sampled.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sampled.Evaluate(0, new Dictionary<uint, (Quaternion, Vector3, Vector3)>()));
    }

    [Fact]
    public void CompressedLoopKeepsItsPoseAcrossForwardAndBackwardSamplingWhenFixtureExists()
    {
        const string path = @"C:\Users\danielpriego\Desktop\Janna.wad.client\2654bec8925af159.anm";
        if (!File.Exists(path)) return;
        using var file = File.OpenRead(path);
        using var sampled = SampledAnimationAsset.Load(file);
        Assert.IsType<SampledAnimationAsset>(sampled);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        var expected = new Dictionary<float, Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>>();
        for (int i = 0; i <= 120; i++)
        {
            float time = i * sampled.Duration / 120;
            pose.Clear(); sampled.Evaluate(time, pose);
            expected[time] = new(pose);
        }
        for (int i = 120; i >= 0; i--)
        {
            float time = i * sampled.Duration / 120;
            pose.Clear(); sampled.Evaluate(time, pose);
            Assert.Equal(expected[time].Count, pose.Count);
            foreach (var joint in expected[time]) Assert.Equal(joint.Value, pose[joint.Key]);
        }
    }

    [Fact]
    public void CompressedNativeFramesMatchForwardDecodedSourceWhenFixtureExists()
    {
        const string path = @"C:\Users\danielpriego\Desktop\Janna.wad.client\2654bec8925af159.anm";
        if (!File.Exists(path)) return;
        using var sourceFile = File.OpenRead(path);
        using var source = AnimationAsset.Load(sourceFile);
        using var sampledFile = File.OpenRead(path);
        using var sampled = SampledAnimationAsset.Load(sampledFile);
        var original = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        var actual = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        int intervals = (int)MathF.Floor(source.Duration * source.Fps + 0.5f);
        for (int frame = 0; frame <= intervals; frame++)
        {
            float time = frame * source.Duration / intervals;
            original.Clear(); actual.Clear();
            source.Evaluate(time, original); sampled.Evaluate(time, actual);
            Assert.Equal(original.Count, actual.Count);
            foreach (var joint in original)
            {
                var expected = joint.Value; var value = actual[joint.Key];
                Assert.True(Vector3.Distance(expected.Translation, value.Translation) < 0.0001f);
                Assert.True(Vector3.Distance(expected.Scale, value.Scale) < 0.0001f);
                Assert.True(Math.Min(
                    Vector4.Distance(new(expected.Rotation.X, expected.Rotation.Y, expected.Rotation.Z, expected.Rotation.W),
                        new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W)),
                    Vector4.Distance(new(expected.Rotation.X, expected.Rotation.Y, expected.Rotation.Z, expected.Rotation.W),
                        new(-value.Rotation.X, -value.Rotation.Y, -value.Rotation.Z, -value.Rotation.W))) < 0.0001f);
            }
        }
    }

    private sealed class HistorySensitiveAnimation : IAnimationAsset
    {
        private float _last = -1;
        public int EvaluationCount { get; private set; }
        public float Duration => 1;
        public float Fps => 10;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            EvaluationCount++;
            float offset = time < _last ? 100 : 0;
            _last = time;
            pose[1] = (Quaternion.CreateFromAxisAngle(Vector3.UnitY, time), new Vector3(time + offset, 0, 0), new Vector3(1 + time));
        }
    }
}

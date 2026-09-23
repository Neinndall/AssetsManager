using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class AnimationGraphPlaybackTests
    {
        private static readonly uint Atomic = Fnv1a.HashLower("AtomicClipData");
        private static readonly uint Sequencer = Fnv1a.HashLower("SequencerClipData");

        [Fact]
        public void OpeningClipIsFirstPlayableIdleCaseInsensitively()
        {
            var clips = new[]
            {
                Clip(1, Atomic, "Run", "run.anm"),
                Clip(2, Atomic, "IDLE_A", "idle_a.anm"),
                Clip(3, Atomic, "idle_b", "idle_b.anm")
            };

            Assert.Same(clips[1], AnimationGraphPlayback.OpeningClip(clips));
            Assert.Same(clips[1], AnimationGraphPlayback.NamedOrOpeningClip(clips, "missing"));
            Assert.Same(clips[0], AnimationGraphPlayback.NamedOrOpeningClip(clips, "RUN"));
        }

        [Fact]
        public void SequencerKeepsEveryResolvedChildInAuthoredOrder()
        {
            var first = Clip(2, Atomic, "A", "a.anm");
            var second = Clip(3, Atomic, "B", "b.anm");
            var sequence = Clip(1, Sequencer, "IdleSequence", null, 2, 3);
            var clips = new[] { sequence, first, second };

            var playlist = AnimationGraphPlayback.ResolvePlaylist(sequence, clips);

            Assert.Equal(new[] { first, second }, playlist);
        }

        [Fact]
        public void MapPreviewCuesFollowRetimedPlaylistStepClocks()
        {
            var first = new AnimationClipDefinition(
                1,
                Atomic,
                1f / 60f,
                0f,
                60f,
                new AnimationClipEventDefinition[]
                {
                    new AnimationSubmeshVisibilityEventDefinition(
                        10,
                        3f,
                        6f,
                        new[] { 0x1111u },
                        new[] { 0x2222u })
                },
                "A",
                "a.anm");
            var second = new AnimationClipDefinition(
                2,
                Atomic,
                1f / 30f,
                0f,
                15f,
                new AnimationClipEventDefinition[]
                {
                    new AnimationJointSnapEventDefinition(
                        11,
                        2f,
                        5f,
                        0x3333u,
                        0x4444u,
                        new Vector3(1f, 2f, 3f))
                },
                "B",
                "b.anm");
            var steps = new IAnimationAsset[]
            {
                new RecordingAnimationAsset(1f, 60f),
                new RecordingAnimationAsset(0.5f, 30f)
            };

            IReadOnlyList<AnimationClipTimedCue> cues =
                MapCharacterAnimationRuntime.BuildTimedCues(new[] { first, second }, steps);

            AnimationSubmeshVisibilityCue visibility = Assert.IsType<AnimationSubmeshVisibilityCue>(cues[0]);
            Assert.Equal(0.05d, visibility.AtSeconds, 5);
            Assert.Equal(0.1d, visibility.UntilSeconds!.Value, 5);
            AnimationJointSnapCue snap = Assert.IsType<AnimationJointSnapCue>(cues[1]);
            Assert.Equal(1d + 2d / 30d, snap.AtSeconds, 5);
            Assert.Equal(1d + 5d / 30d, snap.UntilSeconds!.Value, 5);
        }

        [Fact]
        public void VisibilityKeepsAuthoredNonForwardEndWhileJointSnapNormalizesIt()
        {
            var clip = new AnimationClipDefinition(
                1,
                Atomic,
                1f / 30f,
                0f,
                30f,
                new AnimationClipEventDefinition[]
                {
                    new AnimationSubmeshVisibilityEventDefinition(
                        10,
                        6f,
                        6f,
                        new[] { 0x1111u },
                        new[] { 0x2222u }),
                    new AnimationJointSnapEventDefinition(
                        11,
                        8f,
                        4f,
                        0x3333u,
                        0x4444u,
                        Vector3.Zero)
                },
                "A",
                "a.anm");
            var steps = new IAnimationAsset[]
            {
                new RecordingAnimationAsset(1f, 30f)
            };

            IReadOnlyList<AnimationClipTimedCue> cues =
                MapCharacterAnimationRuntime.BuildTimedCues(new[] { clip }, steps);

            AnimationSubmeshVisibilityCue visibility = Assert.IsType<AnimationSubmeshVisibilityCue>(cues[0]);
            Assert.Equal(0.2d, visibility.AtSeconds, 5);
            Assert.Equal(0.2d, visibility.UntilSeconds!.Value, 5);
            AnimationJointSnapCue snap = Assert.IsType<AnimationJointSnapCue>(cues[1]);
            Assert.Equal(8d / 30d, snap.AtSeconds, 5);
            Assert.Null(snap.UntilSeconds);
        }

        [Theory]
        [InlineData("1234567890abcdef.anm", 0x1234567890abcdeful)]
        [InlineData("1234567890abcdef", 0x1234567890abcdeful)]
        public void HashNamedAnimationBecomesHashReference(string value, ulong expected)
        {
            MapAssetReference reference = MapCharacterAnimationRuntime.ReferenceFromAnimation(value);

            Assert.Null(reference.VirtualPath);
            Assert.Equal(expected, reference.PathHash);
        }

        [Fact]
        public void RealAnimationPathRemainsPathReference()
        {
            const string path = "assets/characters/turret/animations/idle.anm";
            MapAssetReference reference = MapCharacterAnimationRuntime.ReferenceFromAnimation(path);

            Assert.Equal(path, reference.VirtualPath);
            Assert.Equal(0ul, reference.PathHash);
        }

        private sealed class RecordingAnimationAsset(float duration, float fps) : IAnimationAsset
        {
            public float Duration { get; } = duration;
            public float Fps { get; } = fps;
            public bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;
            public void Evaluate(
                float time,
                IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
                => pose?.Clear();
        }

        private static AnimationClipDefinition Clip(
            uint hash,
            uint classHash,
            string name,
            string animation,
            params uint[] children) =>
            new(
                hash,
                classHash,
                1f / 30f,
                0f,
                30f,
                Array.Empty<AnimationClipEventDefinition>(),
                name,
                animation,
                0x1234,
                children);
    }
}

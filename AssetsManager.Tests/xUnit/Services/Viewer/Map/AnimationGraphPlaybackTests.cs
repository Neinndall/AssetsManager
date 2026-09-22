using System;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Views.Models.Viewer;
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

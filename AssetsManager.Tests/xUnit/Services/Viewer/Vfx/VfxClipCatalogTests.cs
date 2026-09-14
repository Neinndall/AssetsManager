using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxClipCatalogTests
    {
        private static readonly uint Sequencer = Fnv1a.HashLower("SequencerClipData");
        private static readonly uint Parametric = Fnv1a.HashLower("ParametricClipData");
        private static readonly uint Selector = Fnv1a.HashLower("SelectorClipData");

        [Fact]
        public void SequencerResolvesEveryPlayableChildInAuthoredOrder()
        {
            AnimationClipDefinition first = Atomic(2, "first.anm");
            AnimationClipDefinition second = Atomic(3, "second.anm");
            AnimationClipDefinition root = Composite(1, Sequencer, new uint[] { 2, 3 });

            var playlist = VfxClipCatalog.ResolvePlaylist(root, new[] { root, second, first });

            Assert.Equal(new uint[] { 2, 3 }, playlist.Select(clip => clip.OwnerPathHash));
        }

        [Fact]
        public void NonSequencerUsesFirstChildThatActuallyResolves()
        {
            AnimationClipDefinition playable = Atomic(3, "playable.anm");
            AnimationClipDefinition root = Composite(1, Selector, new uint[] { 99, 3 });

            AnimationClipDefinition resolved = Assert.Single(
                VfxClipCatalog.ResolvePlaylist(root, new[] { root, playable }));

            Assert.Equal(3u, resolved.OwnerPathHash);
        }

        [Fact]
        public void ParametricWithoutParameterKeepsAuthoredFallbackOrder()
        {
            // The first child is unavailable. LTK then tries child 2 before child 3 even
            // though child 3 is numerically much nearer the first pair's authored value.
            AnimationClipDefinition second = Atomic(2, "second.anm");
            AnimationClipDefinition third = Atomic(3, "third.anm");
            AnimationClipDefinition root = Composite(
                1,
                Parametric,
                new uint[] { 99, 2, 3 },
                new float[] { 0f, 100f, 1f });

            AnimationClipDefinition resolved = Assert.Single(
                VfxClipCatalog.ResolvePlaylist(root, new[] { root, second, third }));

            Assert.Equal(2u, resolved.OwnerPathHash);
        }

        [Fact]
        public void ParametricWithParameterUsesNearestPairAndEarlierTie()
        {
            AnimationClipDefinition first = Atomic(2, "first.anm");
            AnimationClipDefinition second = Atomic(3, "second.anm");
            AnimationClipDefinition root = Composite(
                1,
                Parametric,
                new uint[] { 2, 3 },
                new float[] { 0f, 10f });

            Assert.Equal(
                3u,
                Assert.Single(VfxClipCatalog.ResolvePlaylist(root, new[] { root, first, second }, 9f)).OwnerPathHash);
            Assert.Equal(
                2u,
                Assert.Single(VfxClipCatalog.ResolvePlaylist(root, new[] { root, first, second }, 5f)).OwnerPathHash);
        }

        [Fact]
        public void CompositeCycleIsSkippedAndNextPlayableChildWins()
        {
            AnimationClipDefinition root = Composite(1, Selector, new uint[] { 2, 3 });
            AnimationClipDefinition cycle = Composite(2, Selector, new uint[] { 1 });
            AnimationClipDefinition playable = Atomic(3, "playable.anm");

            AnimationClipDefinition resolved = Assert.Single(
                VfxClipCatalog.ResolvePlaylist(root, new[] { root, cycle, playable }));

            Assert.Equal(3u, resolved.OwnerPathHash);
        }

        private static AnimationClipDefinition Atomic(uint hash, string path) =>
            new(
                hash,
                Fnv1a.HashLower("AtomicClipData"),
                1f / 30f,
                0f,
                -1f,
                Array.Empty<AnimationClipEventDefinition>(),
                AnimationFilePath: path,
                GraphPathHash: 42u);

        private static AnimationClipDefinition Composite(
            uint hash,
            uint classHash,
            uint[] children,
            float[] parameters = null) =>
            new(
                hash,
                classHash,
                1f / 30f,
                0f,
                -1f,
                Array.Empty<AnimationClipEventDefinition>(),
                GraphPathHash: 42u,
                ChildClipHashes: children,
                ChildParameters: parameters ?? Array.Empty<float>());
    }
}

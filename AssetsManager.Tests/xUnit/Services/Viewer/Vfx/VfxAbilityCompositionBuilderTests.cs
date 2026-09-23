using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxAbilityCompositionBuilderTests
    {
        [Fact]
        public void ResolvesEffectKeysThroughResourceMapAndOrdersByStartFrame()
        {
            var firstSystem = new VfxSystemDefinition(100, "First", "first", new VfxEmitterDefinition[0]);
            var secondSystem = new VfxSystemDefinition(200, "Second", "second", new VfxEmitterDefinition[0]);
            var sequence = new AnimationClipDefinition(
                10,
                20,
                1f / 30f,
                0f,
                30f,
                new[]
                {
                    Event(2, 8f, 2),
                    Event(1, 2f, 1)
                });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                new Dictionary<uint, VfxSystemDefinition> { [100] = firstSystem, [200] = secondSystem },
                new Dictionary<uint, uint> { [1] = 100, [2] = 200 });

            Assert.Equal(2, composition.ResolvedCount);
            Assert.Equal(1u, composition.Events[0].Event.EffectKey);
            Assert.Equal(100u, composition.Events[0].ResolvedSystemHash);
            Assert.Equal(2u, composition.Events[1].Event.EffectKey);
        }

        [Fact]
        public void NullResolverMappingSuppressesDirectAndNameFallbacks()
        {
            var direct = new VfxSystemDefinition(10, "Effects/Named", "direct", new VfxEmitterDefinition[0]);
            VfxParticleEventDefinition particleEvent = Event(1, 0f, 10) with
            {
                EffectName = "Effects/Named",
                IsKillEvent = false
            };
            var sequence = new AnimationClipDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { particleEvent });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                new Dictionary<uint, VfxSystemDefinition> { [10] = direct },
                new Dictionary<uint, uint> { [10] = 0 });

            Assert.Equal(0, composition.ResolvedCount);
            Assert.Null(Assert.Single(composition.Events).System);
        }

        [Fact]
        public void SelectsEnemyEffectWithoutChangingTheAuthoredEvent()
        {
            var ally = new VfxSystemDefinition(100, "Ally", "ally", new VfxEmitterDefinition[0]);
            var enemy = new VfxSystemDefinition(200, "Enemy", "enemy", new VfxEmitterDefinition[0]);
            VfxParticleEventDefinition particleEvent = Event(1, 0f, 100) with { EnemyEffectKey = 200 };
            var sequence = new AnimationClipDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { particleEvent });
            var systems = new Dictionary<uint, VfxSystemDefinition> { [100] = ally, [200] = enemy };

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                systems,
                new Dictionary<uint, uint>(),
                useEnemyEffects: true);

            VfxCompositionEvent resolved = Assert.Single(composition.Events);
            Assert.True(resolved.UsesEnemyEffect);
            Assert.Same(enemy, resolved.System);
            Assert.Equal(100u, resolved.Event.EffectKey);
        }

        [Fact]
        public void AnimationClipModeCanRequireExactResolverKeyInsteadOfEffectNameFallback()
        {
            var named = new VfxSystemDefinition(100, "Effects/Named", "named", new VfxEmitterDefinition[0]);
            VfxParticleEventDefinition particleEvent = Event(1, 0f, 999) with { EffectName = "Effects/Named", IsKillEvent = false };
            var sequence = new AnimationClipDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { particleEvent });
            var systems = new Dictionary<uint, VfxSystemDefinition> { [100] = named };

            Assert.Same(
                named,
                Assert.Single(VfxAbilityCompositionBuilder.Build(
                    sequence,
                    systems,
                    new Dictionary<uint, uint>()).Events).System);
            Assert.Null(
                Assert.Single(VfxAbilityCompositionBuilder.Build(
                    sequence,
                    systems,
                    new Dictionary<uint, uint>(),
                    allowEffectNameFallback: false).Events).System);
        }

        [Fact]
        public void ResolverOnlyModeDoesNotTreatASystemHashAsAnEffectKey()
        {
            var direct = new VfxSystemDefinition(100, "Effects/Direct", "direct", new VfxEmitterDefinition[0]);
            var sequence = new AnimationClipDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { Event(1, 0f, 100) });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                new Dictionary<uint, VfxSystemDefinition> { [100] = direct },
                new Dictionary<uint, uint>(),
                allowEffectNameFallback: false,
                resolverOnly: true);

            Assert.Equal(0, composition.ResolvedCount);
            Assert.Null(Assert.Single(composition.Events).System);
        }

        [Fact]
        public void TimedPlaylistPlacesParticleEventsOnTheRetimedSequencerClock()
        {
            var system = new VfxSystemDefinition(100, "Trail", "trail", Array.Empty<VfxEmitterDefinition>());
            var first = new AnimationClipDefinition(
                11,
                1,
                1f / 60f,
                0f,
                60f,
                new[] { Event(1, 30f, 7) with { IsKillEvent = false } },
                "First",
                "first.anm",
                0xAA);
            var second = new AnimationClipDefinition(
                12,
                1,
                1f / 30f,
                0f,
                30f,
                new[] { Event(2, 15f, 7) with { IsKillEvent = false } },
                "Second",
                "second.anm",
                0xAA);
            var root = new AnimationClipDefinition(
                10,
                2,
                1f / 30f,
                0f,
                0f,
                Array.Empty<AnimationClipEventDefinition>(),
                "Sequence",
                null,
                0xAA,
                new[] { 11u, 12u });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.BuildTimedPlaylist(
                root,
                new[] { first, second },
                new[] { 1f, 2f },
                new[] { 1f / 60f, 1f / 30f },
                new Dictionary<uint, VfxSystemDefinition> { [100] = system },
                new Dictionary<uint, uint> { [7] = 100 });

            Assert.Equal(2, composition.ResolvedCount);
            Assert.Equal(0.5f, composition.Events[0].Event.StartFrame, 5);
            Assert.Equal(1.5f, composition.Events[1].Event.StartFrame, 5);
            Assert.Equal(3f, composition.EndFrame, 5);
            Assert.All(composition.Events, cue => Assert.Same(system, cue.System));
        }

        [Fact]
        public void TimedPlaylistKeepsResolverOnlySemanticsForMapClipEvents()
        {
            var direct = new VfxSystemDefinition(100, "Named", "named", Array.Empty<VfxEmitterDefinition>());
            var atomic = new AnimationClipDefinition(
                11,
                1,
                1f / 30f,
                0f,
                30f,
                new[] { Event(1, 3f, 100) with { IsKillEvent = false } },
                "Atomic",
                "atomic.anm");

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.BuildTimedPlaylist(
                atomic,
                new[] { atomic },
                new[] { 1f },
                new[] { 1f / 30f },
                new Dictionary<uint, VfxSystemDefinition> { [100] = direct },
                new Dictionary<uint, uint>());

            Assert.Equal(0, composition.ResolvedCount);
            Assert.Null(Assert.Single(composition.Events).System);
        }

        [Fact]
        public void KeepsUnresolvedEventsVisibleForDiagnostics()
        {
            var sequence = new AnimationClipDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { Event(1, 0f, 999) });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                new Dictionary<uint, VfxSystemDefinition>(),
                new Dictionary<uint, uint>());

            Assert.Equal(0, composition.ResolvedCount);
            Assert.Equal(1, composition.UnresolvedCount);
            Assert.Null(Assert.Single(composition.Events).System);
        }

        [Fact]
        public void FindsOnlyCompositionsWithAnExplicitResolvedSystemReference()
        {
            var selected = new VfxSystemDefinition(100, "Dash", "dash", new VfxEmitterDefinition[0]);
            var sibling = new VfxSystemDefinition(200, "Dash_Trail", "dash_trail", new VfxEmitterDefinition[0]);
            VfxAbilityComposition exact = VfxAbilityCompositionBuilder.Build(
                new AnimationClipDefinition(20, 1, 1f / 30f, 0, 30, new[] { Event(1, 0, 100) }),
                new Dictionary<uint, VfxSystemDefinition> { [100] = selected, [200] = sibling },
                new Dictionary<uint, uint>());
            VfxAbilityComposition nameOnlySibling = VfxAbilityCompositionBuilder.Build(
                new AnimationClipDefinition(10, 1, 1f / 30f, 0, 30, new[] { Event(2, 0, 200) }),
                new Dictionary<uint, VfxSystemDefinition> { [100] = selected, [200] = sibling },
                new Dictionary<uint, uint>());

            IReadOnlyList<VfxAbilityComposition> matches = VfxAbilityCompositionBuilder.FindContainingSystem(
                100,
                new[] { nameOnlySibling, exact });

            Assert.Same(exact, Assert.Single(matches));
        }

        [Fact]
        public void PreservesEveryAuthoredCandidateWhenSystemBelongsToMultipleSequences()
        {
            var selected = new VfxSystemDefinition(100, "Trail", "trail", new VfxEmitterDefinition[0]);
            var systems = new Dictionary<uint, VfxSystemDefinition> { [100] = selected };
            VfxAbilityComposition first = VfxAbilityCompositionBuilder.Build(
                new AnimationClipDefinition(30, 1, 1f / 30f, 0, 30, new[] { Event(1, 2, 100) }),
                systems,
                new Dictionary<uint, uint>());
            VfxAbilityComposition second = VfxAbilityCompositionBuilder.Build(
                new AnimationClipDefinition(10, 1, 1f / 30f, 0, 30, new[] { Event(2, 8, 100) }),
                systems,
                new Dictionary<uint, uint>());

            IReadOnlyList<VfxAbilityComposition> matches = VfxAbilityCompositionBuilder.FindContainingSystem(
                100,
                new[] { first, second });

            Assert.Equal(new[] { 10u, 30u }, System.Linq.Enumerable.Select(matches, match => match.SequencePathHash));
        }

        private static VfxParticleEventDefinition Event(uint eventHash, float startFrame, uint effectKey)
            => new(
                eventHash,
                0,
                startFrame,
                -1f,
                effectKey,
                0,
                string.Empty,
                true,
                true,
                false,
                false,
                false,
                false,
                false,
                1f,
                new VfxParticleEventAttachment[0]);
    }
}

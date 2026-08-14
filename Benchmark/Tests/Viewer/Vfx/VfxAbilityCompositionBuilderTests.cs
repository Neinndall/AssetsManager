using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxAbilityCompositionBuilderTests
    {
        [Fact]
        public void ResolvesEffectKeysThroughResourceMapAndOrdersByStartFrame()
        {
            var firstSystem = new VfxSystemDefinition(100, "First", "first", new VfxEmitterDefinition[0]);
            var secondSystem = new VfxSystemDefinition(200, "Second", "second", new VfxEmitterDefinition[0]);
            var sequence = new VfxEventSequenceDefinition(
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
        public void SelectsEnemyEffectWithoutChangingTheAuthoredEvent()
        {
            var ally = new VfxSystemDefinition(100, "Ally", "ally", new VfxEmitterDefinition[0]);
            var enemy = new VfxSystemDefinition(200, "Enemy", "enemy", new VfxEmitterDefinition[0]);
            VfxParticleEventDefinition particleEvent = Event(1, 0f, 100) with { EnemyEffectKey = 200 };
            var sequence = new VfxEventSequenceDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { particleEvent });
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
        public void KeepsUnresolvedEventsVisibleForDiagnostics()
        {
            var sequence = new VfxEventSequenceDefinition(10, 20, 1f / 30f, 0f, 30f, new[] { Event(1, 0f, 999) });

            VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                sequence,
                new Dictionary<uint, VfxSystemDefinition>(),
                new Dictionary<uint, uint>());

            Assert.Equal(0, composition.ResolvedCount);
            Assert.Equal(1, composition.UnresolvedCount);
            Assert.Null(Assert.Single(composition.Events).System);
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

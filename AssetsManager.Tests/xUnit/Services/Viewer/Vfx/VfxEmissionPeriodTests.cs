using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxEmissionPeriodTests
    {
        [Theory]
        [InlineData(null, 0.5f, null)]
        [InlineData(0f, 0.5f, null)]
        [InlineData(-2f, 0.5f, null)]
        [InlineData(2f, null, 2f)]
        [InlineData(2f, -1f, 0f)]
        [InlineData(2f, 3f, 2f)]
        [InlineData(2f, 0.5f, 0.5f)]
        public void ParserNormalizesAuthoredWindows(float? length, float? active, float? expectedActive)
        {
            var emitter = ParseEmitter(length, active);
            if (expectedActive is null) Assert.Null(emitter.EmissionPeriod);
            else Assert.Equal(new VfxEmissionPeriod(length.Value, expectedActive.Value), emitter.EmissionPeriod);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParserReadsOptionalWindowsForSimpleAndComplexEmitters(bool simple)
        {
            var emitter = ParseEmitter(2f, 0.5f, optional: true, simple: simple);
            Assert.Equal(new VfxEmissionPeriod(2f, 0.5f), emitter.EmissionPeriod);
        }

        [Theory]
        [InlineData(-0.1f, true)]
        [InlineData(0f, true)]
        [InlineData(0.5f, true)]
        [InlineData(0.625f, false)]
        [InlineData(1.875f, false)]
        [InlineData(2f, true)]
        [InlineData(2.5f, true)]
        public void ActiveWindowMatchesLtkInclusiveBoundary(float age, bool active)
            => Assert.Equal(active, new VfxEmissionPeriod(2f, 0.5f).IsActive(age));

        [Fact]
        public void PauseKeepsParticlesMovingAndResumeDoesNotCatchUpMissedBirths()
        {
            var runtime = CreateRuntime(ParseEmitter(2f, 0.5f));
            for (int i = 0; i < 4; i++) runtime.Update(0.125f);
            var state = Assert.Single(runtime.Emitters);
            int count = state.Particles.Count;
            Assert.True(count > 0);
            float before = state.Particles[0].Age;
            Vector3 position = state.Particles[0].Pos;
            for (int i = 0; i < 11; i++) runtime.Update(0.125f);
            Assert.Equal(count, state.Particles.Count);
            Assert.True(state.Particles[0].Age > before);
            Assert.True(state.Particles[0].Pos.X > position.X);
            Assert.Equal(state.Age, state.EmittedThrough);
            runtime.Update(0.125f);
            Assert.Equal(count + 12, state.Particles.Count);
        }

        [Fact]
        public void CycleStartsAtFirstEmissionAndBurstDoesNotRepeat()
        {
            var runtime = CreateRuntime(ParseEmitter(2f, 0.5f) with
            {
                TimeBeforeFirstEmission = 0.75f,
                IsSingleParticle = true
            });
            for (int i = 0; i < 5; i++) runtime.Update(0.125f);
            Assert.Empty(Assert.Single(runtime.Emitters).Particles);
            runtime.Update(0.125f);
            int count = Assert.Single(runtime.Emitters).Particles.Count;
            Assert.Equal(100, count);
            for (int i = 0; i < 20; i++) runtime.Update(0.125f);
            Assert.Equal(count, Assert.Single(runtime.Emitters).Particles.Count);
        }

        [Fact]
        public void MissingActiveWindowKeepsContinuousEmission()
        {
            var runtime = CreateRuntime(ParseEmitter(2f, null));
            for (int i = 0; i < 16; i++) runtime.Update(0.125f);
            Assert.Equal(200, Assert.Single(runtime.Emitters).Particles.Count);
        }

        [Fact]
        public void ResetReplaysTheSamePeriodicBirths()
        {
            var runtime = CreateRuntime(ParseEmitter(2f, 0.5f));
            for (int i = 0; i < 20; i++) runtime.Update(0.125f);
            var before = Assert.Single(runtime.Emitters).Particles.Select(p => (p.Age, p.Pos, p.RangeRandom)).ToArray();
            runtime.Reset();
            for (int i = 0; i < 20; i++) runtime.Update(0.125f);
            Assert.Equal(before, Assert.Single(runtime.Emitters).Particles.Select(p => (p.Age, p.Pos, p.RangeRandom)).ToArray());
        }

        [Fact]
        public void ChildSystemsUseTheirOwnEmissionCycle()
        {
            var child = new VfxSystemDefinition(2, "child", "child", new[] { ParseEmitter(2f, 0.5f) });
            var parentEmitter = ParseEmitter(null, null) with
            {
                Rate = VfxCurveF.Const(1f),
                IsSingleParticle = true,
                ChildParticleSet = new VfxChildParticleSetDefinition(
                    new[] { new VfxChildSystemReference("child", 2, 0) },
                    false, VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0)
            };
            var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { parentEmitter });
            var graph = new VfxPlaybackGraphRuntime(parent, Matrix4x4.Identity, 7,
                new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child },
                new Dictionary<uint, uint>(),
                (definition, transform, seed) =>
                {
                    var runtime = new VfxPlaybackRuntime(seed);
                    runtime.SetSystem(definition, transform);
                    return runtime;
                });
            graph.Update(0.125f);
            for (int i = 0; i < 4; i++) graph.Update(0.125f);
            var state = Assert.Single(Assert.Single(graph.Runtimes.Skip(1)).Emitters);
            int count = state.Particles.Count;
            Assert.True(count > 0);
            for (int i = 0; i < 11; i++) graph.Update(0.125f);
            Assert.Equal(count, state.Particles.Count);
            graph.Update(0.125f);
            Assert.True(state.Particles.Count > count);
        }

        [Fact]
        public void EmitterLifetimeStillEndsPeriodicEmission()
        {
            var runtime = CreateRuntime(ParseEmitter(2f, 0.5f) with { EmitterLifetime = 0.5f });
            for (int i = 0; i < 4; i++) runtime.Update(0.125f);
            int count = Assert.Single(runtime.Emitters).Particles.Count;
            for (int i = 0; i < 20; i++) runtime.Update(0.125f);
            Assert.Equal(count, Assert.Single(runtime.Emitters).Particles.Count);
            Assert.False(runtime.IsComplete);
        }

        private static VfxPlaybackRuntime CreateRuntime(VfxEmitterDefinition emitter)
        {
            var runtime = new VfxPlaybackRuntime(7);
            runtime.SetSystem(new VfxSystemDefinition(1, "periodic", "periodic", new[] { emitter }), Vector3.Zero);
            return runtime;
        }

        private static VfxEmitterDefinition ParseEmitter(float? length, float? active, bool optional = false, bool simple = false)
        {
            var fields = new List<BinTreeProperty>();
            void Add(string name, float? value)
            {
                if (value is null) return;
                uint hash = Fnv1a.HashLower(name);
                BinTreeProperty number = new BinTreeF32(hash, value.Value);
                fields.Add(optional ? new BinTreeOptional(hash, number) : number);
            }
            Add("period", length);
            Add("timeActiveDuringPeriod", active);
            var emitter = new BinTreeStruct(0, Fnv1a.HashLower("VfxEmitterDefinitionData"), fields);
            var system = new BinTreeObject("Effects/Periodic", "VfxSystemDefinitionData", new BinTreeProperty[]
            {
                new BinTreeContainer(Fnv1a.HashLower(simple ? "simpleEmitterDefinitionData" : "complexEmitterDefinitionData"),
                    BinPropertyType.Struct, new BinTreeProperty[] { emitter })
            });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);
            return Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters) with
            {
                Rate = VfxCurveF.Const(100f),
                ParticleLifetime = VfxCurveF.Const(60f),
                EmitterLifetime = null,
                IsSingleParticle = false,
                BirthVelocity = VfxCurve3.Const(Vector3.UnitX)
            };
        }
    }
}

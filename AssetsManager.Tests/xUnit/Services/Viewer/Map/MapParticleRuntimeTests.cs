using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapParticleRuntimeTests
    {
        [Fact]
        public void RuntimeUsesNameSeedRigidPlacementAndDemandSizedRootPool()
        {
            var transform = new Matrix4x4(
                0, 0, -2, 0,
                0, 2, 0, 0,
                2, 0, 0, 0,
                100, 200, 300, 1);
            MapParticleData particle = Particle("Brazier1", 0x30000001, transform);
            VfxSystemDefinition system = System(0x30000001);
            var systems = new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system };

            MapParticleRuntime runtime = MapParticleRuntime.Create(
                particle,
                system,
                systems,
                new Dictionary<uint, uint>());

            Assert.Equal(16, runtime.Capacity);
            Assert.Equal(16, runtime.Graph.Root.ParticleCapacity);
            Assert.Equal(unchecked((int)MapParticleSemantics.Seed("Brazier1")), runtime.Graph.InitialSeed);
            Assert.Equal(MapParticleSemantics.RigidTransform(transform), runtime.Graph.Root.WorldTransform);
            Assert.Same(particle, runtime.Graph.UserTag);
            Assert.Equal(MapOutlineSemantics.ChunkId(particle.ChunkHash), runtime.ChunkId);
            Assert.Equal(MapOutlineSemantics.ItemId(particle.ChunkHash, particle.KeyHash), runtime.ItemId);
        }

        [Fact]
        public void RuntimeAdvancesDirectlyWithoutASessionClock()
        {
            MapParticleData particle = Particle("Fire1", 0x30000001, Matrix4x4.Identity);
            VfxSystemDefinition system = System(0x30000001);
            var systems = new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system };
            MapParticleRuntime runtime = MapParticleRuntime.Create(
                particle,
                system,
                systems,
                new Dictionary<uint, uint>());

            runtime.Advance(0.05f);

            Assert.Equal(0.05f, runtime.Graph.Root.CurrentTime, 5);
        }

        [Fact]
        public void CreateAllSkipsSystemsWithNoEmitters()
        {
            const uint hash = 0x30000002;
            MapParticleData particle = Particle("SoundOnly", hash, Matrix4x4.Identity);
            var soundOnly = new VfxSystemDefinition(
                hash,
                "SoundOnly",
                string.Empty,
                Array.Empty<VfxEmitterDefinition>());
            var catalog = new MapParticleSystemCatalog(
                new Dictionary<uint, VfxSystemDefinition> { [hash] = soundOnly },
                new Dictionary<uint, uint>(),
                new[] { new MapParticleSystemGroupData(hash, soundOnly, new[] { particle }) });

            Assert.Empty(MapParticleRuntime.CreateAll(catalog));
        }

        [Fact]
        public void CreateAllBuildsOneIndependentGraphPerPlacement()
        {
            VfxSystemDefinition system = System(0x30000001);
            MapParticleData first = Particle("Fire1", system.PathHash, Matrix4x4.CreateTranslation(1, 2, 3));
            MapParticleData second = Particle("Fire2", system.PathHash, Matrix4x4.CreateTranslation(4, 5, 6));
            var catalog = new MapParticleSystemCatalog(
                new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system },
                new Dictionary<uint, uint>(),
                new[]
                {
                    new MapParticleSystemGroupData(system.PathHash, system, new[] { first, second })
                });

            IReadOnlyList<MapParticleRuntime> runtimes = MapParticleRuntime.CreateAll(catalog);

            Assert.Equal(2, runtimes.Count);
            Assert.NotSame(runtimes[0].Graph, runtimes[1].Graph);
            Assert.Equal(new Vector3(1, 2, 3), runtimes[0].Graph.Root.WorldTransform.Translation);
            Assert.Equal(new Vector3(4, 5, 6), runtimes[1].Graph.Root.WorldTransform.Translation);
            Assert.NotEqual(runtimes[0].Graph.InitialSeed, runtimes[1].Graph.InitialSeed);
        }

        private static MapParticleData Particle(string name, uint system, Matrix4x4 transform)
        {
            var placed = new MapPlaceableData(
                0x10000001,
                unchecked((uint)name.GetHashCode()),
                MapParticleParser.MapParticleClass,
                name,
                transform,
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            return new MapParticleData(placed, system, false, false);
        }

        private static VfxSystemDefinition System(uint hash) =>
            new(
                hash,
                $"0x{hash:x8}",
                string.Empty,
                new[]
                {
                    new VfxEmitterDefinition(
                        Name: "Emitter",
                        Rate: VfxCurveF.Const(1f),
                        ParticleLifetime: VfxCurveF.Const(1f),
                        EmitterLifetime: null,
                        ParticleLinger: 0f,
                        TimeBeforeFirstEmission: 0f,
                        IsSingleParticle: false,
                        Disabled: false,
                        BlendMode: 4,
                        BirthScale: VfxCurve3.Const(Vector3.One),
                        ScaleOverLife: null,
                        BirthColor: VfxCurve4.Const(Vector4.One),
                        ColorOverLife: null,
                        BirthVelocity: null,
                        Acceleration: null,
                        BirthRotationalVelocity: null,
                        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                        TexturePath: string.Empty,
                        TexDiv: Vector2.One,
                        NumFrames: 1,
                        RandomStartFrame: false,
                        IsMeshPrimitive: false)
                });
    }
}

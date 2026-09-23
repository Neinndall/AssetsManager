using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapParticleSystemParserTests
    {
        [Fact]
        public void ParserKeepsPlacedRootsButDoesNotParseUnrelatedSystems()
        {
            const string rootPath = "Effects/Root";
            const string childPath = "Effects/Child";
            const uint resourceKey = 0x11223344;
            uint rootHash = Fnv1a.HashLower(rootPath);
            uint childHash = Fnv1a.HashLower(childPath);

            BinTree tree = Tree(
                System(rootPath, "Root"),
                System(childPath, "Child"),
                Resolver("Maps/Test/Resolver", resourceKey, childHash));

            var rootParticle = Particle("RootPlacement", rootHash);
            var missingParticle = Particle("MissingPlacement", 0xdeadbeef);
            IReadOnlyList<MapParticleGroupData> groups = new[]
            {
                new MapParticleGroupData(rootHash, new[] { rootParticle }),
                new MapParticleGroupData(0xdeadbeef, new[] { missingParticle })
            };

            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(tree, groups);

            Assert.Single(catalog.Systems);
            Assert.True(catalog.Systems.ContainsKey(rootHash));
            Assert.False(catalog.Systems.ContainsKey(childHash));
            Assert.Equal(childHash, catalog.ResourceMap[resourceKey]);
            Assert.Equal(childHash, catalog.Systems[rootHash].ResourceMap[resourceKey]);

            MapParticleSystemGroupData resolved = Assert.Single(catalog.Groups);
            Assert.Equal(rootHash, resolved.SystemHash);
            Assert.Same(catalog.Systems[rootHash], resolved.System);
            Assert.Equal(new[] { rootParticle }, resolved.Particles);
        }

        [Fact]
        public void ParserKeepsResolverChildrenTransitivelyForPlacedRoots()
        {
            const string rootPath = "Effects/Root";
            const string childPath = "Effects/Child";
            const uint resourceKey = 0x11223344;
            uint rootHash = Fnv1a.HashLower(rootPath);
            uint childHash = Fnv1a.HashLower(childPath);
            BinTree tree = Tree(
                SystemWithChild(rootPath, "Root", resourceKey),
                System(childPath, "Child"),
                Resolver("Maps/Test/Resolver", resourceKey, childHash));

            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(
                tree,
                new[] { new MapParticleGroupData(rootHash, new[] { Particle("RootPlacement", rootHash) }) });

            Assert.Equal(2, catalog.Systems.Count);
            Assert.True(catalog.Systems.ContainsKey(rootHash));
            Assert.True(catalog.Systems.ContainsKey(childHash));
            VfxChildSystemReference child = Assert.Single(
                Assert.Single(catalog.Systems[rootHash].Emitters).ChildParticleSet.Children);
            Assert.Equal(resourceKey, child.EffectKey);
        }

        [Fact]
        public void ParserDoesNotParseSystemsWhenNoRootPlacementIsPlayable()
        {
            const string path = "Effects/ChildOnly";
            BinTree tree = Tree(System(path, "ChildOnly"));

            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(
                tree,
                Array.Empty<MapParticleGroupData>());

            Assert.Empty(catalog.Systems);
            Assert.Empty(catalog.Groups);
        }

        [Fact]
        public void EmptyDocumentProducesEmptyCatalog()
        {
            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(
                Tree(),
                Array.Empty<MapParticleGroupData>());

            Assert.Empty(catalog.Systems);
            Assert.Empty(catalog.ResourceMap);
            Assert.Empty(catalog.Groups);
        }

        private static BinTreeObject System(string path, string name) =>
            new(
                path,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), name),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), path)
                });

        private static BinTreeObject SystemWithChild(string path, string name, uint effectKey)
        {
            var child = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxChildIdentifier"),
                new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("effectKey"), effectKey)
                });
            var childSet = new BinTreeStruct(
                Fnv1a.HashLower("childParticleSetDefinition"),
                Fnv1a.HashLower("VfxChildParticleSetDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("childrenIdentifiers"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { child })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { childSet });
            return new BinTreeObject(
                path,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), name),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), path),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
        }

        private static BinTreeObject Resolver(string path, uint key, uint target) =>
            new(
                path,
                "ResourceResolver",
                new BinTreeProperty[]
                {
                    new BinTreeMap(
                        Fnv1a.HashLower("resourceMap"),
                        BinPropertyType.Hash,
                        BinPropertyType.ObjectLink,
                        new[]
                        {
                            new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                                new BinTreeHash(0, key),
                                new BinTreeObjectLink(0, target))
                        })
                });

        private static MapParticleData Particle(string name, uint system)
        {
            var placed = new MapPlaceableData(
                0x10000001,
                Fnv1a.HashLower(name),
                MapParticleParser.MapParticleClass,
                name,
                Matrix4x4.Identity,
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            return new MapParticleData(placed, system, false, false);
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());
    }
}

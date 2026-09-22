using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Map.Parsing;
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
        public void ParserKeepsWholeSystemCatalogButOnlyResolvesPlacedRoots()
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

            Assert.Equal(2, catalog.Systems.Count);
            Assert.True(catalog.Systems.ContainsKey(rootHash));
            Assert.True(catalog.Systems.ContainsKey(childHash));
            Assert.Equal(childHash, catalog.ResourceMap[resourceKey]);
            Assert.Equal(childHash, catalog.Systems[rootHash].ResourceMap[resourceKey]);

            MapParticleSystemGroupData resolved = Assert.Single(catalog.Groups);
            Assert.Equal(rootHash, resolved.SystemHash);
            Assert.Same(catalog.Systems[rootHash], resolved.System);
            Assert.Equal(new[] { rootParticle }, resolved.Particles);
        }

        [Fact]
        public void ParserReturnsSystemsEvenWhenNoRootPlacementIsPlayable()
        {
            const string path = "Effects/ChildOnly";
            uint hash = Fnv1a.HashLower(path);
            BinTree tree = Tree(System(path, "ChildOnly"));

            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(
                tree,
                Array.Empty<MapParticleGroupData>());

            Assert.True(catalog.Systems.ContainsKey(hash));
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

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
        public void ParserResolvesCustomMaterialForPlacedMapSystemsLikeVfxStudio()
        {
            const ulong customTextureHash = 0x1234567890abcdefUL;
            const string systemPath = "Effects/CustomMaterial";
            const string materialPath = "Effects/Materials/Particle";
            const string customTexturePath = "ASSETS/Effects/MapParticle_TX_CM.tex";
            const string fallbackTexturePath = "ASSETS/Effects/Fallback.tex";
            uint systemHash = Fnv1a.HashLower(systemPath);
            uint materialHash = Fnv1a.HashLower(materialPath);

            var sampler = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("StaticMaterialShaderSamplerDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("textureName"), "Diffuse_Texture"),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), customTextureHash)
                });
            var pass = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("StaticMaterialPassDef"),
                new BinTreeProperty[]
                {
                    new BinTreeBool(Fnv1a.HashLower("blendEnable"), true),
                    new BinTreeU32(Fnv1a.HashLower("srcColorBlendFactor"), 6),
                    new BinTreeU32(Fnv1a.HashLower("dstColorBlendFactor"), 7)
                });
            var technique = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("StaticMaterialTechniqueDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("name"), "normal"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("passes"),
                        BinPropertyType.Embedded,
                        new BinTreeProperty[] { pass })
                });
            var material = new BinTreeObject(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        new BinTreeProperty[] { sampler }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("techniques"),
                        BinPropertyType.Embedded,
                        new BinTreeProperty[] { technique })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("texture"), fallbackTexturePath),
                    new BinTreeStruct(
                        Fnv1a.HashLower("CustomMaterial"),
                        Fnv1a.HashLower("VfxMaterialDefinitionData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(Fnv1a.HashLower("Material"), materialHash)
                        })
                });
            var system = new BinTreeObject(
                systemPath,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });

            MapParticleSystemCatalog catalog = new MapParticleSystemParser().Parse(
                Tree(system, material),
                new[] { new MapParticleGroupData(systemHash, new[] { Particle("Placement", systemHash) }) },
                hash => hash == customTextureHash ? customTexturePath : null);

            VfxEmitterDefinition parsed = Assert.Single(catalog.Systems[systemHash].Emitters);
            Assert.Equal(materialHash, parsed.CustomMaterialPathHash);
            Assert.True(parsed.HasResolvedCustomMaterial);
            Assert.Equal(VfxCustomMaterialBlendFactor.SourceAlpha, parsed.CustomMaterialSourceBlendFactor);
            Assert.Equal(VfxCustomMaterialBlendFactor.OneMinusSourceAlpha, parsed.CustomMaterialDestinationBlendFactor);
            Assert.Equal(customTexturePath.ToLowerInvariant(), parsed.TexturePath);
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

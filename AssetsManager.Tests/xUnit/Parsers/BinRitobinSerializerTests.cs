using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Parsers
{
    public sealed class BinRitobinSerializerTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreviewAndDiffResolveEntryHeadingsWithoutDependingOnHashProperties(bool hasMatchingHashProperty)
        {
            const uint entryHash = 0x6cf1ce8a;
            const string entryPath = "Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef";
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{entryHash:x8} {entryPath}{Environment.NewLine}");
            WriteHashes(bridge, "hashes.bintypes.txt", "StaticMaterialDef");
            WriteHashes(bridge, "hashes.binfields.txt", "name", "objectPath", "texturePath", "target");
            File.AppendAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binfields.txt"),
                $"deadbeef UnrelatedFieldName{Environment.NewLine}");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();
            var serializer = new BinRitobinSerializer(resolver);
            BinTree Create(string texturePath) => new(new[]
            {
                new BinTreeObject(entryHash, Fnv1a.HashLower("StaticMaterialDef"), new BinTreeProperty[]
                {
                    new BinTreeHash64(Fnv1a.HashLower("name"), 0xccdb6584d78a04f6),
                    hasMatchingHashProperty
                        ? new BinTreeHash(Fnv1a.HashLower("objectPath"), entryHash)
                        : new BinTreeString(Fnv1a.HashLower("objectPath"), "no matching generic hash"),
                    new BinTreeString(Fnv1a.HashLower("texturePath"), texturePath),
                    new BinTreeObjectLink(Fnv1a.HashLower("target"), entryHash)
                }),
                new BinTreeObject(0xdeadbeef, Fnv1a.HashLower("StaticMaterialDef"), Array.Empty<BinTreeProperty>())
            }, Array.Empty<string>());
            byte[] before = WriteTree(Create("before.tex"));
            byte[] after = WriteTree(Create("after.tex"));

            Assert.Equal(entryPath, resolver.ResolveBinEntry(entryHash));
            string preview = await serializer.WriteBinTreeAsRitobinAsync(before);
            var diff = await serializer.WriteBinDiffAsRitobinAsync(before, after);

            string heading = $"\"{entryPath}\" = StaticMaterialDef";
            Assert.Contains(heading, preview);
            Assert.Contains(heading, diff.OldRitobin);
            Assert.Contains(heading, diff.NewRitobin);
            Assert.DoesNotContain("0x6cf1ce8a =", preview);
            Assert.Contains("0xdeadbeef = StaticMaterialDef", preview);
            Assert.Contains($"target: link = \"{entryPath}\"", preview);
            Assert.Contains("before.tex", diff.OldRitobin);
            Assert.Contains("after.tex", diff.NewRitobin);
        }

        [Fact]
        public async Task PreviewAndDiffPreserveWideMaterialNamesAndFollowingFields()
        {
            using var bridge = new AssetsManagerTestBridge();
            using HashResolverService resolver = CreateResolver(bridge);
            var serializer = new BinRitobinSerializer(resolver);
            BinTree Create(ulong name) => new(new[]
            {
                new BinTreeObject(1, 0xff9d3409, new BinTreeProperty[]
                {
                    new BinTreeHash64(0x8d39bde6, name),
                    new BinTreeString(Fnv1a.HashLower("nested"), "Following material data")
                })
            }, Array.Empty<string>());
            byte[] before = WriteTree(Create(0xccdb6584d78a04f6));
            byte[] after = WriteTree(Create(0xdddb6584d78a04f6));

            string preview = await serializer.WriteBinTreeAsRitobinAsync(before);
            var diff = await serializer.WriteBinDiffAsRitobinAsync(before, after);

            Assert.Contains("hash = 0xccdb6584d78a04f6", preview);
            Assert.Contains("Following material data", preview);
            Assert.Contains("hash = 0xccdb6584d78a04f6", diff.OldRitobin);
            Assert.Contains("hash = 0xdddb6584d78a04f6", diff.NewRitobin);
            Assert.DoesNotContain("Following material data", diff.OldRitobin);
            Assert.DoesNotContain("Following material data", diff.NewRitobin);
        }

        [Fact]
        public void BinRemainsPreviewableAndDiffableWithJsonHighlighting()
        {
            Assert.True(SupportedFileTypes.IsText("skin0.bin"));
            Assert.True(SupportedFileTypes.IsDiffSupported("skin0.bin"));
            Assert.True(SupportedFileTypes.IsNonImageDiffable("skin0.bin"));
            Assert.True(SupportedFileTypes.UsesJsonHighlighting("skin0.bin"));
        }

        [Fact]
        public async Task SerializerPreservesMetadataLinkedAndNestedPropertyTypes()
        {
            using var bridge = new AssetsManagerTestBridge();
            using HashResolverService resolver = CreateResolver(bridge);
            var serializer = new BinRitobinSerializer(resolver);
            BinTree tree = CreateTypedTree("old-value", "DATA/Characters/Test/Test.bin");

            string ritobin = await serializer.WriteBinTreeAsRitobinAsync(WriteTree(tree));

            Assert.Contains("#PROP_text", ritobin);
            Assert.Contains("type: string = \"PROP\"", ritobin);
            Assert.Contains("version: u32 = 3", ritobin);
            Assert.Contains("linked: list[string]", ritobin);
            Assert.Contains("\"DATA/Characters/Test/Test.bin\"", ritobin);
            Assert.Contains("entries: map[hash,embed]", ritobin);
            Assert.Contains("RootType", ritobin);
            Assert.Contains("embedded: embed = EmbeddedType", ritobin);
            Assert.Contains("pointer: pointer = PointerType", ritobin);
            Assert.Contains("ordered: list[u32]", ritobin);
            Assert.Contains("unordered: list2[string]", ritobin);
            Assert.Contains("optional: option[string]", ritobin);
            Assert.Contains("mapping: map[string,u32]", ritobin);
            Assert.Contains("target: link = \"test/entry\"", ritobin);
            Assert.Contains("enabled: flag = true", ritobin);
            Assert.Contains("0xdeadbeef: u32 = 9", ritobin);
        }

        [Fact]
        public async Task DiffUsesBinTreeAndWritesBothSidesAsRitobin()
        {
            using var bridge = new AssetsManagerTestBridge();
            using HashResolverService resolver = CreateResolver(bridge);
            var serializer = new BinRitobinSerializer(resolver);
            BinTree oldTree = CreateTypedTree("old-value", "DATA/Old.bin");
            BinTree newTree = CreateTypedTree("new-value", "DATA/New.bin");

            (string oldRitobin, string newRitobin) = await serializer.WriteBinDiffAsRitobinAsync(
                WriteTree(oldTree),
                WriteTree(newTree));

            Assert.Contains("#PROP_text", oldRitobin);
            Assert.Contains("#PROP_text", newRitobin);
            Assert.Contains("\"DATA/Old.bin\"", oldRitobin);
            Assert.Contains("\"DATA/New.bin\"", newRitobin);
            Assert.Contains("embedded: embed = EmbeddedType", oldRitobin);
            Assert.Contains("embedded: embed = EmbeddedType", newRitobin);
            Assert.Contains("nested: string = \"old-value\"", oldRitobin);
            Assert.Contains("nested: string = \"new-value\"", newRitobin);
            Assert.DoesNotContain("ordered:", oldRitobin);
            Assert.DoesNotContain("pointer:", oldRitobin);
            Assert.DoesNotContain("old-value", newRitobin);
            Assert.DoesNotContain("new-value", oldRitobin);
        }

        [Fact]
        public async Task SerializerResolvesGenericHashValuesAcrossBinDomains()
        {
            const uint objectPathHash = 0x27f20d91;
            const string objectPath = "Characters/Aatrox/Animations/Skin0";
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"),
                string.Empty);
            File.WriteAllText(
                Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt"),
                $"{objectPathHash:x8} {objectPath}{Environment.NewLine}");
            WriteHashes(bridge, "hashes.bintypes.txt", "RootType");
            WriteHashes(bridge, "hashes.binfields.txt", "objectPath");

            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();
            var serializer = new BinRitobinSerializer(resolver);
            BinTree tree = new BinTree(new[]
            {
                new BinTreeObject(
                    Fnv1a.HashLower("test/entry"),
                    Fnv1a.HashLower("RootType"),
                    new BinTreeProperty[]
                    {
                        new BinTreeHash(Fnv1a.HashLower("objectPath"), objectPathHash)
                    })
            }, Array.Empty<string>());

            string ritobin = await serializer.WriteBinTreeAsRitobinAsync(WriteTree(tree));

            Assert.Contains($"objectPath: hash = \"{objectPath}\"", ritobin);
        }

        [Fact]
        public async Task SerializerResolvesAllHashDomainsInsideNestedPropertyKinds()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            WriteHashes(bridge, "hashes.binentries.txt", "test/entry", "EntryCatalogValue", "Referenced/Entry");
            WriteHashes(bridge, "hashes.binhashes.txt", "HashCatalogValue");
            WriteHashes(bridge, "hashes.bintypes.txt", "RootType", "EmbeddedType", "PointerType", "StaticMaterialDef", "TypeCatalogValue");
            WriteHashes(bridge, "hashes.binfields.txt", "FieldCatalogValue", "direct", "embedded", "pointer", "ordered",
                "unordered", "mapping", "linkedMapping", "optional", "emptyOptional", "name", "nested", "target", "texture", "unknown");
            const ulong gameHash = 0x1234567887654321;
            const ulong lcuHash = 0x2345678998765432;
            const ulong wideHash = 0xccdb6584d78a04f6;
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"), $"{gameHash:x16} assets/known.tex\n");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.lcu.txt"), $"{lcuHash:x16} plugins/known.png\n");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.xxh3.txt"), $"{wideHash:x16} Materials/WideName\n");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            await resolver.LoadAllHashesAsync();
            var serializer = new BinRitobinSerializer(resolver);
            BinTreeEmbedded Embedded(uint name) => new(name, Fnv1a.HashLower("EmbeddedType"), new BinTreeProperty[]
            {
                new BinTreeHash(Fnv1a.HashLower("nested"), Fnv1a.HashLower("EntryCatalogValue")),
                new BinTreeObjectLink(Fnv1a.HashLower("target"), Fnv1a.HashLower("Referenced/Entry")),
                new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), gameHash)
            });
            var emptyOptional = new BinTreeOptional(Fnv1a.HashLower("emptyOptional"), new BinTreeHash(0, 1)) { Value = null };
            var tree = new BinTree(new[]
            {
                new BinTreeObject(Fnv1a.HashLower("test/entry"), Fnv1a.HashLower("RootType"), new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("direct"), Fnv1a.HashLower("HashCatalogValue")),
                    Embedded(Fnv1a.HashLower("embedded")),
                    new BinTreeStruct(Fnv1a.HashLower("pointer"), Fnv1a.HashLower("PointerType"), new BinTreeProperty[]
                    {
                        new BinTreeHash(Fnv1a.HashLower("nested"), Fnv1a.HashLower("FieldCatalogValue")),
                        new BinTreeWadChunkLink(Fnv1a.HashLower("texture"), lcuHash)
                    }),
                    new BinTreeContainer(Fnv1a.HashLower("ordered"), BinPropertyType.Embedded, new[] { Embedded(0) }),
                    new BinTreeUnorderedContainer(Fnv1a.HashLower("unordered"), BinPropertyType.Hash, new BinTreeProperty[]
                    {
                        new BinTreeHash(0, Fnv1a.HashLower("TypeCatalogValue")),
                        new BinTreeHash(0, 0xabcdef01)
                    }),
                    new BinTreeMap(Fnv1a.HashLower("mapping"), BinPropertyType.Hash, BinPropertyType.Embedded, new[]
                    {
                        new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Fnv1a.HashLower("HashCatalogValue")), Embedded(0))
                    }),
                    new BinTreeMap(Fnv1a.HashLower("linkedMapping"), BinPropertyType.ObjectLink, BinPropertyType.WadChunkLink, new[]
                    {
                        new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                            new BinTreeObjectLink(0, Fnv1a.HashLower("Referenced/Entry")), new BinTreeWadChunkLink(0, lcuHash))
                    }),
                    new BinTreeOptional(Fnv1a.HashLower("optional"), new BinTreeEmbedded(0, Fnv1a.HashLower("StaticMaterialDef"), new BinTreeProperty[]
                    {
                        new BinTreeHash64(Fnv1a.HashLower("name"), wideHash),
                        Embedded(Fnv1a.HashLower("embedded"))
                    })),
                    emptyOptional,
                    new BinTreeHash(Fnv1a.HashLower("unknown"), 0xabcdef02),
                    new BinTreeStruct(0xabcdef03, 0xabcdef04, new[] { new BinTreeWadChunkLink(0xabcdef05, 0xabcdef0612345678) })
                })
            }, Array.Empty<string>());

            string text = await serializer.WriteBinTreeAsRitobinAsync(WriteTree(tree));

            foreach (string expected in new[]
            {
                "\"test/entry\" = RootType", "direct: hash = \"HashCatalogValue\"", "embedded: embed = EmbeddedType",
                "nested: hash = \"EntryCatalogValue\"", "target: link = \"Referenced/Entry\"", "texture: file = \"assets/known.tex\"",
                "pointer: pointer = PointerType", "nested: hash = \"FieldCatalogValue\"", "texture: file = \"plugins/known.png\"",
                "ordered: list[embed]", "unordered: list2[hash]", "\"TypeCatalogValue\"", "mapping: map[hash,embed]",
                "\"HashCatalogValue\" = EmbeddedType", "linkedMapping: map[link,file]", "\"Referenced/Entry\" = \"plugins/known.png\"",
                "optional: option[embed]", "StaticMaterialDef", "name: hash = \"Materials/WideName\"", "emptyOptional: option[hash] = {}",
                "0xabcdef01", "unknown: hash = 0xabcdef02", "0xabcdef03: pointer = 0xabcdef04", "0xabcdef05: file = 0xabcdef0612345678"
            }) Assert.Contains(expected, text);
        }

        private static BinTree CreateTypedTree(string nestedValue, string dependency)
        {
            var embedded = new BinTreeEmbedded(
                Fnv1a.HashLower("embedded"),
                Fnv1a.HashLower("EmbeddedType"),
                new BinTreeProperty[] { new BinTreeString(Fnv1a.HashLower("nested"), nestedValue) });
            var pointer = new BinTreeStruct(
                Fnv1a.HashLower("pointer"),
                Fnv1a.HashLower("PointerType"),
                new BinTreeProperty[] { new BinTreeU32(Fnv1a.HashLower("amount"), 3) });
            var unordered = new BinTreeUnorderedContainer(
                Fnv1a.HashLower("unordered"),
                BinPropertyType.String,
                new BinTreeProperty[] { new BinTreeString(0, "first"), new BinTreeString(0, "second") });
            var ordered = new BinTreeContainer(
                Fnv1a.HashLower("ordered"),
                BinPropertyType.U32,
                new BinTreeProperty[] { new BinTreeU32(0, 1), new BinTreeU32(0, 2) });
            var optional = new BinTreeOptional(
                Fnv1a.HashLower("optional"),
                new BinTreeString(0, "present"));
            var mapping = new BinTreeMap(
                Fnv1a.HashLower("mapping"),
                BinPropertyType.String,
                BinPropertyType.U32,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeString(0, "key"),
                        new BinTreeU32(0, 5))
                });
            var treeObject = new BinTreeObject(
                Fnv1a.HashLower("test/entry"),
                Fnv1a.HashLower("RootType"),
                new BinTreeProperty[]
                {
                    embedded,
                    pointer,
                    ordered,
                    unordered,
                    optional,
                    mapping,
                    new BinTreeObjectLink(Fnv1a.HashLower("target"), Fnv1a.HashLower("test/entry")),
                    new BinTreeBitBool(Fnv1a.HashLower("enabled"), true),
                    new BinTreeU32(0xdeadbeef, 9)
                });

            return new BinTree(new[] { treeObject }, new[] { dependency });
        }

        private static HashResolverService CreateResolver(AssetsManagerTestBridge bridge)
        {
            bridge.Directories.CreateHashesDirectories();
            WriteHashes(bridge, "hashes.binentries.txt", "test/entry");
            WriteHashes(bridge, "hashes.bintypes.txt", "RootType", "EmbeddedType", "PointerType");
            WriteHashes(
                bridge,
                "hashes.binfields.txt",
                "embedded",
                "nested",
                "pointer",
                "amount",
                "ordered",
                "unordered",
                "optional",
                "mapping",
                "target",
                "enabled");
            File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.binhashes.txt"), string.Empty);

            var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            resolver.LoadBinHashes();
            return resolver;
        }

        private static void WriteHashes(AssetsManagerTestBridge bridge, string fileName, params string[] values)
        {
            using var writer = new StreamWriter(Path.Combine(bridge.Directories.HashesPath, fileName));
            foreach (string value in values)
                writer.WriteLine($"{Fnv1a.HashLower(value):x8} {value}");
        }

        private static byte[] WriteTree(BinTree tree)
        {
            using var stream = new MemoryStream();
            tree.Write(stream);
            return stream.ToArray();
        }

        [Fact]
        public async Task SerializerFormatsImaaAutoAtlasWithoutError()
        {
            using var bridge = new AssetsManagerTestBridge();
            using HashResolverService resolver = CreateResolver(bridge);
            var serializer = new BinRitobinSerializer(resolver);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(new byte[] { 0x49, 0x4D, 0x41, 0x41 }); // "IMAA"
            writer.Write((uint)2); // Version 2
            writer.Write((ulong)0x1111222233334444); // tex0
            writer.Write((ulong)0x5555666677778888); // tex1
            writer.Write((uint)1); // sprite count = 1
            writer.Write((ulong)0xaabbccddeeff0011); // sprite hash
            writer.Write(0.1f); // uMin
            writer.Write(0.2f); // vMin
            writer.Write(0.3f); // uMax
            writer.Write(0.4f); // vMax
            writer.Write((uint)0); // texIndex = 0

            string ritobin = await serializer.WriteBinTreeAsRitobinAsync(ms.ToArray());

            Assert.Contains("# Image Auto Atlas (IMAA v2)", ritobin);
            Assert.Contains("textures: list[string]", ritobin);
            Assert.Contains("sprites: map[hash, struct]", ritobin);
            Assert.Contains("uvMin: vec2 = [0.1000, 0.2000]", ritobin);
            Assert.Contains("uvMax: vec2 = [0.3000, 0.4000]", ritobin);
        }
    }
}

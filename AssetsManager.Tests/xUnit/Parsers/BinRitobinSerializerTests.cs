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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Parsers
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
    }
}

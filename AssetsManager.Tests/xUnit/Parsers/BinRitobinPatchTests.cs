using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Tests.xUnit.Infrastructure;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Parsers
{
    public sealed class BinRitobinPatchTests
    {
        [Fact]
        public async Task PatchPreviewAndDiffResolveHashesAndPreserveRepeatedRecords()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            void Catalog(string name, string content) => File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, name), content);
            Catalog("hashes.binentries.txt", "6cf1ce8a Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef\n12345678 Referenced/Entry\n");
            Catalog("hashes.binhashes.txt", "10000001 BeforeValue\n10000002 AfterValue\n");
            Catalog("hashes.binfields.txt", $"{Fnv1a.HashLower("name"):x8} name\n{Fnv1a.HashLower("nested"):x8} nested\n{Fnv1a.HashLower("settings"):x8} settings\n20000001 FieldCatalogValue\n");
            Catalog("hashes.bintypes.txt", $"{Fnv1a.HashLower("StaticMaterialDef"):x8} StaticMaterialDef\n{Fnv1a.HashLower("PointerType"):x8} PointerType\n");
            Catalog("hashes.bin.xxh364.txt", "ccdb6584d78a04f6 Materials/WideName\n");
            Catalog("hashes.game.txt", "1234567887654321 assets/known.tex\n");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            var serializer = new BinRitobinSerializer(resolver);
            byte[] Create(uint hash) => WritePatch(
                (0x6cf1ce8a, "settings.value", new BinTreeHash(0, hash)),
                (0x6cf1ce8a, "settings.value", new BinTreeHash(0, 0x20000001)),
                (0xdeadbeef, "unknown", new BinTreeObjectLink(0, 0xdead0001)),
                (0x6cf1ce8a, "Lookup{\"weapon\"}", new BinTreeWadChunkLink(0, 0x1234567887654321)),
                (0x6cf1ce8a, "material", new BinTreeEmbedded(0, Fnv1a.HashLower("StaticMaterialDef"), new BinTreeProperty[]
                {
                    new BinTreeHash64(Fnv1a.HashLower("name"), 0xccdb6584d78a04f6),
                    new BinTreeStruct(Fnv1a.HashLower("settings"), Fnv1a.HashLower("PointerType"), new BinTreeProperty[]
                    {
                        new BinTreeUnorderedContainer(Fnv1a.HashLower("nested"), BinPropertyType.Hash, new BinTreeProperty[]
                        {
                            new BinTreeHash(0, 0x20000001), new BinTreeHash(0, 0xabcdef01)
                        })
                    })
                })),
                (0x6cf1ce8a, "link", new BinTreeObjectLink(0, 0x12345678)),
                (0x6cf1ce8a, "empty", new BinTreeOptional(0, new BinTreeHash(0, 1)) { Value = null }));
            byte[] before = Create(0x10000001);
            byte[] after = Create(0x10000002);
            using (var input = new MemoryStream(before)) Assert.Equal(7, new BinTree(input).DataOverrides.Count);

            string preview = await serializer.WriteBinTreeAsRitobinAsync(before);
            var diff = await serializer.WriteBinDiffAsRitobinAsync(before, after);

            Assert.Equal("BeforeValue", resolver.ResolveBinHash(0x10000001));
            Assert.Contains("value: hash = \"BeforeValue\"", preview);
            Assert.Contains("value: hash = \"BeforeValue\"", diff.OldRitobin);
            Assert.DoesNotContain("BeforeValue", diff.NewRitobin);
            Assert.Contains("value: hash = \"AfterValue\"", diff.NewRitobin);
            foreach (string text in new[] { preview, diff.OldRitobin, diff.NewRitobin })
            {
                Assert.Contains($"{Environment.NewLine}patches: map[hash,embed]", text);
                foreach (string expected in new[]
                {
                    "type: string = \"PTCH\"", "patches: map[hash,embed]", "\"Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef\" = patch",
                    "path: string = \"settings.value\"", "value: hash = \"FieldCatalogValue\"", "0xdeadbeef = patch", "value: link = 0xdead0001",
                    "path: string = \"Lookup{\\\"weapon\\\"}\"", "value: file = \"assets/known.tex\"", "value: embed = StaticMaterialDef",
                    "name: hash = \"Materials/WideName\"", "settings: pointer = PointerType", "nested: list2[hash]", "0xabcdef01",
                    "value: link = \"Referenced/Entry\"", "value: option[hash] = {}"
                }) Assert.Contains(expected, text);
                Assert.Equal(7, text.Split(" = patch", StringSplitOptions.None).Length - 1);
                Assert.Equal(2, text.Split("path: string = \"settings.value\"", StringSplitOptions.None).Length - 1);
                string firstValue = text == diff.NewRitobin ? "AfterValue" : "BeforeValue";
                Assert.True(text.IndexOf($"value: hash = \"{firstValue}\"", StringComparison.Ordinal)
                    < text.IndexOf("value: hash = \"FieldCatalogValue\"", StringComparison.Ordinal));
                Assert.True(text.IndexOf("path: string = \"settings.value\"", StringComparison.Ordinal)
                    < text.IndexOf("value: hash = \"FieldCatalogValue\"", StringComparison.Ordinal));
                Assert.True(text.IndexOf("value: hash = \"FieldCatalogValue\"", StringComparison.Ordinal)
                    < text.IndexOf("0xdeadbeef = patch", StringComparison.Ordinal));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task DiffKeepsPropAndEmptyPatchFileKinds(bool patchFirst)
        {
            using var bridge = new AssetsManagerTestBridge();
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            var serializer = new BinRitobinSerializer(resolver);
            byte[] patch = WritePatch();
            byte[] prop = WriteTree(new BinTree());

            var diff = await serializer.WriteBinDiffAsRitobinAsync(patchFirst ? patch : prop, patchFirst ? prop : patch);

            string patchText = patchFirst ? diff.OldRitobin : diff.NewRitobin;
            string propText = patchFirst ? diff.NewRitobin : diff.OldRitobin;
            Assert.Contains("type: string = \"PTCH\"", patchText);
            Assert.Contains($"{Environment.NewLine}patches: map[hash,embed] = {{}}", patchText);
            Assert.Contains("type: string = \"PROP\"", propText);
            Assert.DoesNotContain("patches:", propText);
        }

        private static byte[] WritePatch(params (uint Target, string Path, BinTreeProperty Value)[] records)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write("PTCH"u8);
            writer.Write(1);
            writer.Write(0);
            writer.Write(WriteTree(new BinTree()));
            writer.Write(records.Length);
            foreach (var record in records)
            {
                byte[] wrapper = WriteTree(new BinTree(new[]
                {
                    new BinTreeObject(1, 1, new[] { record.Value })
                }, Array.Empty<string>()));
                using var input = new MemoryStream(wrapper);
                using var reader = new BinaryReader(input);
                Assert.Equal("PROP", Encoding.ASCII.GetString(reader.ReadBytes(4)));
                Assert.Equal(3, reader.ReadInt32());
                Assert.Equal(0, reader.ReadInt32());
                Assert.Equal(1, reader.ReadInt32());
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                Assert.Equal(1, reader.ReadUInt16());
                reader.ReadUInt32();
                Assert.Equal((byte)record.Value.Type, reader.ReadByte());
                byte[] value = reader.ReadBytes((int)(input.Length - input.Position));
                byte[] path = Encoding.ASCII.GetBytes(record.Path);
                writer.Write(record.Target);
                writer.Write(1 + 2 + path.Length + value.Length);
                writer.Write((byte)record.Value.Type);
                writer.Write((ushort)path.Length);
                writer.Write(path);
                writer.Write(value);
            }
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] WriteTree(BinTree tree)
        {
            using var stream = new MemoryStream();
            tree.Write(stream);
            return stream.ToArray();
        }
    }
}

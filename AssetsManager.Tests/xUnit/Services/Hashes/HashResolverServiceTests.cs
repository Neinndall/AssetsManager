using System;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class HashResolverServiceTests
    {
        [Fact]
        public async Task BinResolutionPreservesDomainsAndGenericFallbackPriority()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            void Catalog(string name, string content) =>
                File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, name), content);
            Catalog("hashes.binhashes.txt", "12345678 GenericValue\n00000001 HashOnly\n");
            Catalog("hashes.binentries.txt", "12345678 EntryValue\n00000002 EntryOnly\n");
            Catalog("hashes.binfields.txt", "12345678 FieldValue\n00000003 FieldOnly\n");
            Catalog("hashes.bintypes.txt", "12345678 TypeValue\n00000004 TypeOnly\n");
            Catalog("hashes.bin.xxh364.txt", "abcdef0112345678 WideValue\n");
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);
            await resolver.LoadAllHashesAsync();

            Assert.Equal("GenericValue", resolver.ResolveBinHash(0x12345678));
            Assert.Equal("EntryValue", resolver.ResolveBinEntry(0x12345678));
            Assert.Equal("FieldValue", resolver.ResolveBinField(0x12345678));
            Assert.Equal("TypeValue", resolver.ResolveBinType(0x12345678));
            Assert.Equal("GenericValue", resolver.ResolveBinHashGeneral(0x12345678));
            Assert.Equal("HashOnly", resolver.ResolveBinHashGeneral(1));
            Assert.Equal("EntryOnly", resolver.ResolveBinHashGeneral(2));
            Assert.Equal("FieldOnly", resolver.ResolveBinHashGeneral(3));
            Assert.Equal("TypeOnly", resolver.ResolveBinHashGeneral(4));
            Assert.Equal("00000003", resolver.ResolveBinEntry(3));
            Assert.Equal("WideValue", resolver.ResolveBinXxh3(0xabcdef0112345678));
            Assert.Equal("0000000012345678", resolver.ResolveBinXxh3(0x12345678));
            Assert.Equal("abcdef0212345678", resolver.ResolveBinXxh3(0xabcdef0212345678));
            Assert.Equal("deadbeef", resolver.ResolveBinHashGeneral(0xdeadbeef));
            Assert.Equal(HashResolutionOrigin.Official, resolver.ResolveBinEntryDetailed(2).Origin);
            Assert.Equal(HashResolutionOrigin.Unknown, resolver.ResolveBinEntryDetailed(3).Origin);
        }

        [Fact]
        public async Task BinCatalogLoadsFromBinaryCacheWhenTextCatalogIsUnavailable()
        {
            using var bridge = new AssetsManagerTestBridge();
            bridge.Directories.CreateHashesDirectories();
            string textPath = Path.Combine(bridge.Directories.HashesPath, "hashes.binentries.txt");
            File.WriteAllText(textPath, $"6cf1ce8a Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef{Environment.NewLine}");
            using (var cache = new BinaryHashCache(textPath, bridge.LogService)) cache.Load();
            File.Delete(textPath);
            using var resolver = new HashResolverService(bridge.Directories, bridge.LogService);

            Assert.True(resolver.HasLocalHashCatalogs);
            await resolver.LoadAllHashesAsync();

            Assert.Equal("Characters/Evelynn/Skins/Skin10/Materials/EvelynnSkin10_staticDef", resolver.ResolveBinEntry(0x6cf1ce8a));
        }
    }
}

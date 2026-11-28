using System.IO;
using System.Text;
using AssetsManager.Services.Parsers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Parsers
{
    public sealed class StringTableParserBoundsTests
    {
        [Fact]
        public void ParseSkipsTruncatedTrencEntryWithoutThrowing()
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RST"));
                writer.Write((byte)3);       // version 3 (40-bit hash, hasTrenc byte)
                writer.Write((uint)1);       // 1 entry
                writer.Write((ulong)0x1234); // offset 0, hash 0x1234
                writer.Write((byte)1);       // hasTrenc = true
                writer.Write((byte)0xFF);    // TRENC marker
                writer.Write((ushort)50);    // claims 50 bytes, but only 2 bytes follow
                writer.Write((byte)0xAA);
                writer.Write((byte)0xBB);
            }

            ms.Position = 0;
            var (entries, _, _) = StringTableParser.Parse(ms);

            Assert.Empty(entries);
        }

        [Fact]
        public void ParseHandlesEmptyDataBlockCleanly()
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RST"));
                writer.Write((byte)5); // version 5
                writer.Write((uint)1); // 1 entry
                writer.Write((ulong)0x5678);
            }

            ms.Position = 0;
            var (entries, _, _) = StringTableParser.Parse(ms);

            Assert.Empty(entries);
        }
    }
}

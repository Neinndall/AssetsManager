using System;
using System.Collections.Generic;
using System.IO;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameMeshLinkContextTests
    {
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        public void MeshReferenceRetainsTextureNamingContext(bool hashed, bool skeleton, bool known)
        {
            const string stem = "assets/characters/example/skins/skin1/unusual_costume";
            string mesh = stem + (skeleton ? ".skl" : ".skn");
            string target = stem + ".tex";
            uint field = skeleton ? 0xb14c976eU : 0xd6a00df6U;
            var tree = new BinTree(new[]
            {
                new BinTreeObject(1, 0x9b67e9f6, new BinTreeProperty[]
                {
                    new BinTreeStruct(0x45ff5904, 2, new BinTreeProperty[]
                    {
                        hashed ? new BinTreeWadChunkLink(field, XxHash64Ext.Hash(mesh)) : new BinTreeString(field, mesh),
                        new BinTreeWadChunkLink(3, XxHash64Ext.Hash(target))
                    })
                })
            }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, known ? new[] { mesh } : Array.Empty<string>()));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
            guesser.GrepWad(engine, new ArraySegment<byte>(stream.ToArray()), "unknown.bin", "example.wad.client", 123);
            if (!known)
            {
                Assert.Empty(engine.Matches);
                Assert.Contains(XxHash64Ext.Hash(target), engine.UnknownHashes);
                return;
            }
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(target, match.Path);
            Assert.Equal("example.wad.client", match.SourceWadPath);
            Assert.Equal(123UL, match.SourceChunkHash);
            Assert.Contains(42UL, engine.UnknownHashes);
        }
    }
}

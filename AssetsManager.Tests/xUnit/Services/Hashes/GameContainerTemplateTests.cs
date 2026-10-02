using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameContainerTemplateTests
    {
        [Fact]
        public void RepeatedContainersAndThemesKeepTheirCorrelation()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/example_skin1_glow.pie_c_11_15.tex",
                "assets/characters/example/skins/skin3/marker.tex",
                "assets/characters/petexample/themes/summer/animations/petexample_summer_idle.anm",
                "data/characters/petexample/themes/winter/root.bin"
            }));
            string[] paths = guesser.GenerateContainerTemplateCandidates().Select(value => value.Path).ToArray();
            Assert.Contains("assets/characters/example/skins/skin3/example_skin3_glow.pie_c_11_15.tex", paths);
            Assert.Contains("assets/characters/petexample/themes/winter/animations/petexample_winter_idle.anm", paths);
            Assert.Contains("data/characters/petexample/themes/summer/root.bin", paths);
            Assert.DoesNotContain(paths, path => path.Contains("example_skin1_glow.skin3"));
        }

        [Fact]
        public void BorrowedSkinsAndPartialThemeTokensRemainLiteral()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/borrowed_skin12_skin2.tex",
                "assets/characters/example/skins/skin3/marker.tex",
                "assets/characters/petexample/themes/base/petexample_database_base.tex",
                "assets/characters/petexample/themes/winter/marker.tex"
            }));
            string[] paths = guesser.GenerateContainerTemplateCandidates().Select(value => value.Path).ToArray();
            Assert.Contains("assets/characters/example/skins/skin3/borrowed_skin12_skin2.tex", paths);
            Assert.Contains("assets/characters/petexample/themes/winter/petexample_database_winter.tex", paths);
        }

        [Fact]
        public void ContainersStayWithinTheirCharacterAndLayout()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/example_skin1.tex",
                "assets/characters/other/skins/skin3/other_skin3.tex",
                "assets/characters/example/themes/winter/marker.tex"
            }));
            string[] paths = guesser.GenerateContainerTemplateCandidates().Select(value => value.Path).ToArray();
            Assert.DoesNotContain(paths, path => path.Contains("example/skins/skin3"));
            Assert.DoesNotContain(paths, path => path.Contains("example/skins/winter"));
            Assert.Equal(paths.Length, paths.Distinct().Count());
        }

        [Fact]
        public void CancelledTemplateBuildStopsImmediately()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/example_skin1.tex"
            }));
            Assert.Throws<OperationCanceledException>(() =>
                guesser.GenerateContainerTemplateCandidates(cancellation.Token).ToArray());
        }


        [Fact]
        public async System.Threading.Tasks.Task ExtendedSelectionAddsCorrelationWithoutRemovingLegacyCombinations()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/example_skin1_unusual.tex",
                "assets/characters/example/skins/skin2/marker.tex"
            }));
            const string correlated = "assets/characters/example/skins/skin2/example_skin2_unusual.tex";
            const string legacy = "assets/characters/example/skins/skin1/example_skin2_unusual.tex";
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong> { XxHash64Ext.Hash(correlated), XxHash64Ext.Hash(legacy), 42 });
            await guesser.RunExtendedAttacksAsync(engine, "", null, CancellationToken.None,
                new HashSet<string> { "game-ext-skinnumbers" });
            Assert.Equal(2, engine.Matches.Count);
            Assert.Contains(XxHash64Ext.Hash(correlated), engine.Matches.Keys);
            Assert.Contains(XxHash64Ext.Hash(legacy), engine.Matches.Keys);
        }

        [Fact]
        public void ContainerProjectionDoesNotRewritePatchDecorations()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/example/skins/skin1/example_skin1.skin1_patch.tex",
                "assets/characters/example/skins/skin3/marker.tex"
            }));
            Assert.Contains(guesser.GenerateContainerTemplateCandidates(),
                value => value.Path == "assets/characters/example/skins/skin3/example_skin3.skin1_patch.tex");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PropertyBinWithInferredExtensionUsesStructuralLinkGuessing(bool patch)
        {
            const string target = "assets/example/images/unlisted_artwork.tex";
            var tree = new BinTree(new[]
            {
                new BinTreeObject(1, 2, new BinTreeProperty[]
                {
                    new BinTreeString(3, "assets/example/images/known.tex"),
                    new BinTreeString(4, "unlisted_artwork"),
                    new BinTreeWadChunkLink(5, XxHash64Ext.Hash(target))
                })
            }, Array.Empty<string>());
            using var stream = new MemoryStream();
            tree.Write(stream);
            byte[] bytes = stream.ToArray();
            if (patch)
            {
                using var wrapper = new MemoryStream();
                using var writer = new BinaryWriter(wrapper);
                writer.Write(new byte[] { (byte)'P', (byte)'T', (byte)'C', (byte)'H' });
                writer.Write(1U);
                writer.Write(0U);
                writer.Write(bytes);
                writer.Write(0U);
                bytes = wrapper.ToArray();
            }
            byte[] padded = new byte[bytes.Length + 10];
            bytes.CopyTo(padded, 5);
            var guesser = new GameHashGuesser();
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
            guesser.GrepWad(engine, new ArraySegment<byte>(padded, 5, bytes.Length),
                "0123456789abcdef.bin", "example.wad.client", 123);
            var match = Assert.Single(engine.Matches).Value;
            Assert.Equal(target, match.Path);
            Assert.Equal("example.wad.client", match.SourceWadPath);
            Assert.Equal(123UL, match.SourceChunkHash);
            Assert.Contains(42UL, engine.UnknownHashes);
        }
    }
}

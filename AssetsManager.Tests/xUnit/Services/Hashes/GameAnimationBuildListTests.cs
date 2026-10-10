using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameAnimationBuildListTests
{
    private const string Target = "assets/characters/petexample/themes/t1/animations/petexample_t1_finisher_c003.anm";
    private const string BinPath = "data/characters/petexample/animations/t1.bin";
    private static string[] Paths => new[]
    {
        BinPath,
        "assets/characters/petexample/themes/t1/animations/petexample_t1_idle01.anm",
        "assets/characters/petexample/themes/base/animations/petexample_base_idle01.anm",
        "assets/characters/teacher/themes/base/animations/teacher_base_finisher_c003.anm"
    };

    [Fact]
    public void UsesCompoundSuffixOnlyInReferencedContainer()
    {
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
        {
            XxHash64Ext.Hash(Target), XxHash64Ext.Hash(Target.Replace("/t1/", "/base/").Replace("_t1_", "_base_"))
        });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Paths));
        guesser.SubstituteReferencedAnimationSuffixes(engine, new HashSet<(string, string)> { ("petexample", "t1") }, CancellationToken.None);
        Assert.Equal(Target, Assert.Single(engine.Matches).Value.Path);
        Assert.Equal(1, engine.RemainingUnknownCount);
    }

    [Fact]
    public void ProjectsLearnedActionsWithoutKnownAnimationInTargetContainer()
    {
        const string target = "assets/characters/petexample/skins/skin05/animations/petexample_skin5_main_idle02.anm";
        string outside = target.Replace("/skin05/", "/skin06/").Replace("_skin5_", "_skin6_");
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
        {
            XxHash64Ext.Hash(target), XxHash64Ext.Hash(outside)
        });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
        {
            "assets/characters/teacher/skins/skin0/animations/teacher_skin0_main_idle02.anm"
        }));
        guesser.SubstituteReferencedAnimationNames(engine,
            new HashSet<(string, string)> { ("petexample", "skin5") }, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
        Assert.Equal(1, engine.RemainingUnknownCount);
    }

    [Fact]
    public void NumericRoundsCombineCountersFromNewlyResolvedAnimations()
    {
        const string prefix = "assets/characters/petexample/skins/skin05/animations/petexample_skin5_main_idle";
        string first = prefix + "08.anm", compound = prefix + "08_2.anm";
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>
        {
            XxHash64Ext.Hash(first), XxHash64Ext.Hash(compound)
        });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { prefix + "02.anm" }));
        guesser.SubstituteReferencedAnimationNames(engine,
            new HashSet<(string, string)> { ("petexample", "skin5") }, CancellationToken.None);
        Assert.Equal(2, engine.Matches.Count);
        Assert.Equal(0, engine.RemainingUnknownCount);
    }

    [Fact]
    public void BudgetAndCancellationApplyToDirectedCandidates()
    {
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 42 });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Paths));
        var containers = new HashSet<(string, string)> { ("petexample", "t1") };
        long reported = -1;
        long attempts = guesser.SubstituteReferencedAnimationSuffixes(engine, containers, CancellationToken.None, 3, n => reported = n);
        Assert.Equal(3L, attempts);
        Assert.Equal(attempts, engine.CheckedCandidates);
        Assert.Equal(attempts, reported);
        Assert.Throws<OperationCanceledException>(() => guesser.SubstituteReferencedAnimationSuffixes(
            engine, containers, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CustomAnimationMethodReadsInstalledBinAndSkipsMalformedBins(bool malformed, bool earlierCopy)
    {
        string directory = Path.Combine(Path.GetTempPath(), "assetsmanager-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var link = new BinTreeWadChunkLink(Fnv1a.HashLower("mAnimationFilePath"), XxHash64Ext.Hash(Target));
            var tree = new BinTree(new[] { new BinTreeObject(1, Fnv1a.HashLower("AnimationGraphData"), new BinTreeProperty[]
            {
                new BinTreeStruct(2, Fnv1a.HashLower("AnimationResourceData"), new BinTreeProperty[]
                {
                    link
                })
            }) }, Array.Empty<string>());
            using var bytes = new MemoryStream();
            tree.Write(bytes);
            byte[] data = bytes.ToArray();
            if (earlierCopy)
            {
                link.Value = 43;
                using var variant = new MemoryStream();
                tree.Write(variant);
                byte[] variantData = variant.ToArray();
                WadBuilder.Bake(new[]
                {
                    new WadBakeEntry(BinPath, () => new MemoryStream(variantData), WadChunkCompression.None),
                    new WadBakeEntry(Target, () => new MemoryStream(new byte[] { 1, 2, 3 }), WadChunkCompression.None)
                }, Path.Combine(directory, "a.wad.client"), new WadBakeSettings());
            }
            WadBuilder.Bake(new[]
            {
                new WadBakeEntry(BinPath, () => new MemoryStream(malformed ? new byte[] { 1, 2 } : data), WadChunkCompression.None),
                new WadBakeEntry(Target, () => new MemoryStream(new byte[] { 1, 2, 3 }), WadChunkCompression.None)
            }, Path.Combine(directory, "test.wad.client"), new WadBakeSettings());
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Paths));
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(Target), 42 });
            long attempts = guesser.SubstituteAnimationBuildListWords(engine, CancellationToken.None, 100, rootDirectory: directory);
            Assert.InRange(attempts, 1, 100);
            if (!malformed)
            {
                var match = Assert.Single(engine.Matches).Value;
                Assert.Equal(Target, match.Path);
                Assert.Equal("GAME Custom: BIN-referenced animation suffixes", match.SourceWadPath);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}

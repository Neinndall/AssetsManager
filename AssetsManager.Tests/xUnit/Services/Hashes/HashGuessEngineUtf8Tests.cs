using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class HashGuessEngineUtf8Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(512)]
    [InlineData(1024)]
    public void EncodedPartsPreserveHashesPathsCountersAndProvenanceAcrossBufferBoundaries(int padding)
    {
        string prefix = "assets/" + new string('x', padding) + "/\u65e5\u672c_";
        const string word = "\u00e9toile", suffix = "_\u03c0.tex";
        string path = prefix + word + suffix;
        ulong hash = XxHash64Ext.Hash(path);
        var original = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { hash, 42 });
        var encoded = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { hash, 42 });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool before = original.CheckNormalizedParts(prefix, word, suffix, HashGuessStrategy.WordlistVariant, "test.wad", 123);
            bool after = encoded.CheckNormalizedUtf8Parts(Encoding.UTF8.GetBytes(prefix), Encoding.UTF8.GetBytes(word),
                Encoding.UTF8.GetBytes(suffix), HashGuessStrategy.WordlistVariant, "test.wad", 123);
            Assert.Equal(before, after);
        }
        Assert.Equal(original.CheckedCandidates, encoded.CheckedCandidates);
        Assert.Equal(original.DiscardedCandidates, encoded.DiscardedCandidates);
        var match = Assert.Single(encoded.Matches).Value;
        Assert.Equal(path, match.Path);
        Assert.Equal(hash, match.Hash);
        Assert.Equal("test.wad", match.SourceWadPath);
        Assert.Equal(123UL, match.SourceChunkHash);
        Assert.Contains(42UL, encoded.UnknownHashes);
    }

    [Fact]
    public void EmptyEncodedCandidateIsDiscardedEvenIfEmptyHashIsPending()
    {
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash("") });
        Assert.False(engine.CheckNormalizedUtf8Parts(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty, HashGuessStrategy.WordlistVariant, "test"));
        Assert.Equal(1L, engine.CheckedCandidates);
        Assert.Equal(1L, engine.DiscardedCandidates);
        Assert.Empty(engine.Matches);
    }

    [Theory]
    [InlineData("example", false)]
    [InlineData("\u00e9toile", false)]
    [InlineData("", true)]
    public void WordSubstitutionRetainsOriginalNonAsciiAndInvalidUtf16PathSpelling(string word, bool invalidUtf16)
    {
        if (invalidUtf16) word = new string((char)0xd800, 1);
        string target = "assets/particles/" + word + ".tex";
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
        var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()));
        guesser.RunFocusedWordlistSubstitution(engine, new[] { "assets/particles/seed.tex" }, new[] { word }, CancellationToken.None);
        Assert.Equal(target, Assert.Single(engine.Matches).Value.Path);
    }
}

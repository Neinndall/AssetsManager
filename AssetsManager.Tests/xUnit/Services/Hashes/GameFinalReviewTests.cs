using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using AssetsManager.Tests.Diagnostics.Hashes;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes;

public sealed class GameFinalReviewTests
{
    [Fact]
    public async Task MethodAuditRestartsFromTheOriginalTargetsAfterEachSuccessfulMethod()
    {
        using var bridge = new AssetsManagerTestBridge();
        bridge.Directories.CreateHashesDirectories();
        string lab = bridge.CreateDirectory("hash_lab");
        const string seed = "data/maps/mapgeometry/test.mapgeo";
        const string target = "data/maps/mapgeometry/test.navregions";
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.game.txt"), $"{XxHash64Ext.Hash(seed):x16} {seed}\n");
        File.WriteAllText(Path.Combine(bridge.Directories.HashesPath, "hashes.lcu.txt"), "");
        File.WriteAllText(Path.Combine(lab, "unknowns.game.txt"), $"{XxHash64Ext.Hash(target):x16}\n");
        string output = Path.Combine(bridge.RootPath, "audit.jsonl");
        await GameMethodCoverageDiagnostic.RunAsync(new[] { "--state=" + bridge.RootPath, "--root=" + bridge.RootPath,
            "--out=" + output, "--methods=game-basic-extensions,game-basic-extensions", "--seconds=10" });
        string[] results = File.ReadAllLines(output);
        Assert.Equal(2, results.Length);
        foreach (string line in results)
        {
            using var report = JsonDocument.Parse(line);
            Assert.Equal(1, report.RootElement.GetProperty("Initial").GetInt32());
            Assert.Equal("returned", report.RootElement.GetProperty("Completion").GetString());
            Assert.Equal(target, report.RootElement.GetProperty("Matches")[0].GetProperty("Path").GetString());
        }
        Assert.Single(File.ReadAllLines(Path.Combine(lab, "unknowns.game.txt")));
    }

    [Fact]
    public void AnimationHashingObservesCancellationEvenWhenNoCandidateMatches()
    {
        using var cancellation = new CancellationTokenSource();
        var guesser = new GameHashGuesser();
        var remaining = new HashSet<ulong> { 42 };
        Assert.Throws<OperationCanceledException>(() => guesser.MatchAnimationVariants("run", "example", "skin100", remaining,
            prefixes: new CancellingPrefixes(cancellation), suffixes: new[] { "" },
            cancellationToken: cancellation.Token).ToArray());
        Assert.Equal(new[] { 42UL }, remaining);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void LegacySkinCombinationsKeepExactOrderAndBudget(int budget)
    {
        const string root = "assets/characters/example/skins/";
        var guesser = Guesser(root + "base/a.tex", root + "skin1/example_skin1.tex", root + "skin2/z.tex",
            root + "skin2/base_mix_skin1.tex", "assets/characters/sightward/skins/skin2/excluded.tex");
        string[] expected =
        {
            root + "base/a.tex", root + "skin1/a.tex", root + "skin2/a.tex",
            root + "base/example_skin1.tex", root + "base/example_skin2.tex", root + "skin1/example_skin2.tex",
            root + "base/z.tex", root + "skin1/z.tex", root + "skin2/z.tex",
            root + "base/skin1_mix_skin2.tex"
        };
        var actual = guesser.GenerateSkinNumberCandidates(budget).ToArray();
        Assert.Equal(expected.Take(budget), actual.Select(candidate => candidate.Path));
        Assert.All(actual, candidate => Assert.Equal(HashGuessStrategy.SkinNumberVariant, candidate.Strategy));
    }

    [Fact]
    public void CorrelatedTemplatesKeepFormatThenContainerOrder()
    {
        const string root = "assets/characters/example/skins/";
        var guesser = Guesser(root + "skin2/example_skin2_a.tex", root + "skin1/z.tex");
        Assert.Equal(new[] { root + "skin1/example_skin1_a.tex", root + "skin2/example_skin2_a.tex",
            root + "skin1/z.tex", root + "skin2/z.tex" },
            guesser.GenerateContainerTemplateCandidates().Select(candidate => candidate.Path));
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("skins")]
    [InlineData("suffixes")]
    public void ExtendedPreparationObservesCancellation(string method)
    {
        var guesser = Guesser("assets/characters/example/skins/skin1/example_skin1.tex");
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => (method switch
        {
            "characters" => guesser.SubstituteCharacter(100, token),
            "skins" => guesser.SubstituteSkinNumbers(100, token),
            _ => guesser.SubstituteSuffixes(100, token)
        }).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmCorpusAndReleasePreserveSpecializedSearchResults(bool shaders)
    {
        using var bridge = new AssetsManagerTestBridge();
        string target = shaders ? "assets/shaders/test_alpha_beta.ps" : "assets/characters/example/skins/base/animations/cast_loop.anm";
        var guesser = shaders
            ? Guesser("assets/shaders/test_alpha_gamma.ps", "assets/shaders/test_beta_delta.ps", "data/shaders/teacher.ps")
            : Guesser("assets/characters/example/skins/base/animations/idle.anm", "assets/characters/teacher/skins/base/animations/teacher_cast_loop.anm");
        string catalog = Path.Combine(bridge.RootPath, "hashes.game.txt");
        File.WriteAllLines(catalog, guesser.KnownPaths.Select(path => $"{XxHash64Ext.Hash(path):x16} {path}"));
        guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, catalog));
        var cold = Run();
        var warm = Run();
        guesser.ReleaseMemory();
        var released = Run();
        Assert.Equal(cold.Checks, warm.Checks);
        Assert.Equal(cold.Checks, released.Checks);
        Assert.Equal(cold.Matches, warm.Matches);
        Assert.Equal(cold.Matches, released.Matches);
        Assert.Contains(target, cold.Matches);

        (long Checks, string[] Matches) Run()
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { XxHash64Ext.Hash(target), 42 });
            if (shaders) guesser.SubstituteShaderVocabWords(engine, CancellationToken.None);
            else guesser.SubstituteAnimationBuildListWords(engine, CancellationToken.None, candidateBudget: 200_000);
            return (engine.CheckedCandidates, engine.Matches.Values.Select(match => match.Path).ToArray());
        }
    }

    private static GameHashGuesser Guesser(params string[] paths) => new(new HashFile(HashGuessDomain.Game, paths));

    private sealed class CancellingPrefixes(CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 2;
        public string this[int index] => index == 0 ? "" : "second_";
        public IEnumerator<string> GetEnumerator()
        {
            yield return "";
            cancellation.Cancel();
            yield return "second_";
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

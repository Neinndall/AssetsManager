using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameContainerTemplateDiagnostic
    {
        public static void Run(string[] args)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
            string input = args.FirstOrDefault(value => value.StartsWith("--data="))?[7..] ?? root;
            var unknown = File.ReadLines(Path.Combine(input, "hash_lab", "unknowns.game.txt"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => ulong.Parse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToHashSet();
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                Path.Combine(input, "hashes", "hashes.game.txt")));
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknown);
            Console.WriteLine($"Input: {input}; {unknown.Count} unknowns; persistence disabled");
            Run("Correlated containers", guesser.GenerateContainerTemplateCandidates());
            Run("Animation decorations", guesser.GenerateAnimationDecorationCandidates());
            var proposed = engine.Matches.Values.ToList();
            foreach (var match in proposed.OrderBy(value => value.Hash))
                Console.WriteLine($"PROPOSED {match.Hash:x16} {match.Path}");

            if (args.Contains("--compare-grep"))
            {
                var known = new HashFile(HashGuessDomain.Game, Path.Combine(input, "hashes", "hashes.game.txt")).Load();
                var names = new Dictionary<uint, string>();
                foreach (string catalog in new[] { "hashes.binhashes.txt", "hashes.binentries.txt", "hashes.binfields.txt", "hashes.bintypes.txt" })
                {
                    string file = Path.Combine(input, "hashes", catalog);
                    if (!File.Exists(file)) continue;
                    foreach (string line in File.ReadLines(file))
                    {
                        int separator = line.IndexOf(' ');
                        if (separator > 0 && uint.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out uint hash))
                            names.TryAdd(hash, line[(separator + 1)..]);
                    }
                }
                var grepGuesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game,
                    Path.Combine(input, "hashes", "hashes.game.txt")),
                    resolveBinHash: hash => names.GetValueOrDefault(hash, hash.ToString("x8")));
                var targets = proposed.Select(value => value.Hash).ToHashSet();
                var grep = new HashGuessEngine(HashGuessDomain.Game, targets.Append(42UL).ToHashSet());
                string install = args.FirstOrDefault(value => value.StartsWith("--root="))?[7..]
                    ?? @"C:\Riot Games\League of Legends (PBE)";
                foreach (string wadPath in grepGuesser.FindWads(install))
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    if (!args.Contains("--grep-all-wads") && !wad.Chunks.Keys.Any(targets.Contains)) continue;
                    Console.WriteLine($"Grep comparison WAD: {Path.GetFileName(wadPath)}");
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        if (chunk.Compression == LeagueToolkit.Core.Wad.WadChunkCompression.Satellite) continue;
                        string source = known.GetValueOrDefault(chunk.PathHash, chunk.PathHash.ToString("x16"));
                        string extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
                        if (extension.Length > 0 && !grepGuesser.ShouldGrepExtension(extension)) continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            var bytes = owner.DangerousGetArray();
                            if (extension.Length == 0)
                            {
                                extension = HashGuessingService.InferChunkExtension(bytes, false);
                                if (extension.Length > 0) source += "." + extension;
                            }
                            grepGuesser.GrepWad(grep, bytes, source, wadPath, chunk.PathHash, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            Console.WriteLine($"Grep comparison skipped {chunk.PathHash:x16}: {exception.Message}");
                        }
                    }
                }
                foreach (var match in proposed.Where(value => grep.Matches.ContainsKey(value.Hash)))
                    Console.WriteLine($"GREP_EXISTING {match.Hash:x16} {match.Path}");
                foreach (var match in proposed.Where(value => !grep.Matches.ContainsKey(value.Hash)))
                    Console.WriteLine($"GREP_ADDITIONAL {match.Hash:x16} {match.Path}");
            }

            if (!args.Contains("--compare-legacy")) return;

            // Existing capped generators keep exactly their production budgets and ordering.
            var legacy = new HashGuessEngine(HashGuessDomain.Game, proposed.Select(value => value.Hash).Append(42UL).ToHashSet());
            Legacy("Skin combinations", guesser.SubstituteSkinNumbers());
            Legacy("Suffixes", guesser.SubstituteSuffixes());
            Legacy("Character substitution", guesser.SubstituteCharacter());
            long start = Stopwatch.GetTimestamp();
            guesser.SubstituteAnimationBuildListWords(legacy, CancellationToken.None);
            Console.WriteLine($"Legacy animation build-list: {legacy.Matches.Count} cumulative matches in {Stopwatch.GetElapsedTime(start)}");
            start = Stopwatch.GetTimestamp();
            guesser.SubstituteTextureBuildListWords(legacy, CancellationToken.None);
            Console.WriteLine($"Legacy texture build-list: {legacy.Matches.Count} cumulative matches in {Stopwatch.GetElapsedTime(start)}");
            foreach (var match in proposed.Where(value => !legacy.Matches.ContainsKey(value.Hash)).OrderBy(value => value.Hash))
                Console.WriteLine($"ADDITIONAL {match.Hash:x16} {match.Path}");
            Console.WriteLine($"Additional to compared legacy passes: {proposed.Count(value => !legacy.Matches.ContainsKey(value.Hash))}");

            void Run(string name, IEnumerable<HashGuessCandidate> candidates)
            {
                long start = Stopwatch.GetTimestamp();
                long count = 0;
                int before = engine.Matches.Count;
                foreach (var candidate in candidates)
                {
                    engine.Check(candidate.Path, candidate.Strategy, name);
                    count++;
                }
                Console.WriteLine($"{name}: {count:N0} candidates, {engine.Matches.Count - before} matches in {Stopwatch.GetElapsedTime(start)}");
            }
            void Legacy(string name, IEnumerable<HashGuessCandidate> candidates)
            {
                long start = Stopwatch.GetTimestamp();
                foreach (var candidate in candidates) legacy.Check(candidate.Path, candidate.Strategy, name);
                Console.WriteLine($"Legacy {name}: {legacy.Matches.Count} cumulative matches in {Stopwatch.GetElapsedTime(start)}");
            }
        }
    }
}

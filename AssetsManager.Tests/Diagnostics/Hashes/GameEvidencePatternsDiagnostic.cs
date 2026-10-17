using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Views.Models.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Bounded experiments against an immutable snapshot; only exact XXH64 matches are reported.
internal static class GameEvidencePatternsDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        string stage = Option("--stage") ?? "roles";
        string filter = Option("--wad");
        int seconds = int.Parse(Option("--seconds") ?? "90", CultureInfo.InvariantCulture);
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var hashFile = new HashFile(HashGuessDomain.Game, Path.Combine(state, "hashes", "hashes.game.txt"));
        var known = hashFile.Load();
        var pending = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        var engine = new HashGuessEngine(HashGuessDomain.Game, pending,
            match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path} [{match.SourceWadPath}]"));
        var guesser = new GameHashGuesser(hashFile);
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int errors = 0;
        var visited = new HashSet<(ulong, ulong)>();
        bool complete = false;
        Console.WriteLine($"Evidence patterns {stage}: {pending.Count} targets; time budget {seconds}s; no persistence");
        try
        {
            if (stage == "basic")
            {
                var lcu = new HashFile(HashGuessDomain.Lcu, Path.Combine(state, "hashes", "hashes.lcu.txt"));
                Measure("Cross domain", () => guesser.GuessFromLcuHashes(engine, new LcuHashGuesser(lcu, null), cancellation.Token));
                Measure("Characters", () => guesser.GuessCharactersFiles(engine, cancellation.Token));
                Measure("Regalia", () => guesser.GuessRegaliaAssets(engine, cancellation.Token));
                Measure("Shaders", () => guesser.GuessShaderVariants(engine, cancellation.Token, rootDirectory: root));
                Measure("Copies", () => guesser.GuessIdenticalCopies(engine, root, cancellation.Token));
                Measure("Locales", () => guesser.SubstituteLang(engine, cancellation.Token));
                Measure("Extensions", () => guesser.SubstituteExtensions(engine, cancellation.Token));
                Measure("Prefixes", () => guesser.CheckBasenamePrefixes(engine, cancellation.Token));
                Measure("Token removal", () => guesser.RemoveBasenameTokens(engine, cancellation.Token));
                Measure("Numbers", () => guesser.SubstituteNumbers(engine, cancellation.Token, maximum: 200));
                Measure("Padded numbers", () => guesser.SubstituteBasicPaddedNumbers(engine, cancellation.Token));
            }
            else if (stage == "custom")
            {
                guesser.RunCustomAttacks(engine, null, cancellation.Token,
                    Option("--method") is string method ? new HashSet<string> { method } : null, root);
            }
            else if (stage == "numbers")
            {
                Measure("Numbers", () => guesser.SubstituteNumbers(engine, cancellation.Token, maximum: 200));
                Measure("Padded numbers", () => guesser.SubstituteBasicPaddedNumbers(engine, cancellation.Token));
            }
            else if (stage == "roles" && filter == null)
            {
                Measure("Material roles", () => guesser.GuessMaterialTextureRoles(engine, root, cancellation.Token));
            }
            else if (stage == "roles")
            {
                var index = new GameMaterialTextureIndex(known.Values, cancellation.Token);
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (chunk.Compression == WadChunkCompression.Satellite || !known.TryGetValue(chunk.PathHash, out string path) ||
                            !path.EndsWith(".bin", StringComparison.Ordinal) || !visited.Add((chunk.PathHash, chunk.Checksum))) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(chunk);
                            if (!FileTypeDetector.IsPropertyBin(data.Span)) continue;
                            var bytes = data.DangerousGetArray();
                            using var stream = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, false);
                            var tree = new BinTree(stream);
                            index.Guess(engine, tree, path, file, chunk.PathHash, cancellation.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            errors++;
                            if (errors <= 5) Console.WriteLine($"READ ERROR {path}: {ex.Message}");
                        }
                    }
                    Console.WriteLine($"WAD {Path.GetFileName(file)}: {engine.CheckedCandidates:N0} checks; {engine.Matches.Count} hits; {watch.Elapsed.TotalSeconds:F1}s");
                }
            }
            else if (stage == "typos")
            {
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (string file in guesser.FindWads(root))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    using var wad = new WadFile(file);
                    if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                    foreach (ulong hash in wad.Chunks.Keys)
                        if (known.TryGetValue(hash, out string path) && (path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".anm", StringComparison.Ordinal))) paths.Add(path);
                }
                foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int start = path.LastIndexOf('/') + 1, end = path.Length - 4;
                    for (int at = start; at < end; at++)
                    {
                        Check(path.AsSpan(0, at), "", path.AsSpan(at + 1));
                        if (at + 1 < end) Check(path.AsSpan(0, at), string.Concat(path[at + 1], path[at]), path.AsSpan(at + 2));
                        foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-")
                            if (letter != path[at]) Check(path.AsSpan(0, at), letter.ToString(), path.AsSpan(at + 1));
                        foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-")
                            Check(path.AsSpan(0, at), letter.ToString(), path.AsSpan(at));
                    }
                    foreach (char letter in "abcdefghijklmnopqrstuvwxyz0123456789_-") Check(path.AsSpan(0, end), letter.ToString(), path.AsSpan(end));
                }
            }
            else throw new ArgumentException("Stage must be basic, numbers, custom, roles or typos.");
            complete = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Console.WriteLine($"RESULT {stage}: {engine.Matches.Count} hits; {engine.CheckedCandidates:N0} checks; {engine.RemainingUnknownCount} remaining; {watch.Elapsed.TotalSeconds:F2}s; complete={complete}; errors={errors}; allocated={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1_000_000:N0} MB; no persistence");
        if (Option("--out") is string output)
            File.WriteAllLines(output, engine.Matches.Values.OrderBy(m => m.Path, StringComparer.Ordinal).Select(m => $"{m.Hash:x16} {m.Path}"));
        void Check(ReadOnlySpan<char> prefix, string middle, ReadOnlySpan<char> suffix)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            engine.CheckNormalizedParts(prefix, middle, suffix, HashGuessStrategy.WordlistVariant, "GAME filename edit");
        }
        void Measure(string name, Action action)
        {
            long before = engine.CheckedCandidates;
            int found = engine.Matches.Count;
            var timer = Stopwatch.StartNew();
            action();
            Console.WriteLine($"STAGE {name}: {engine.Matches.Count - found} hits; {engine.CheckedCandidates - before:N0} checks; {timer.Elapsed.TotalSeconds:F2}s");
        }
    }
}

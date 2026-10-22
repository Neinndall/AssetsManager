using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Every method starts from the same immutable snapshot; discovery and persistence are separate.
internal static class GameMethodCoverageDiagnostic
{
    internal static async Task RunAsync(string[] args)
    {
        string Option(string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        string state = Option("--state") ?? throw new ArgumentException("Provide --state=<snapshot directory>.");
        string output = Option("--out") ?? throw new ArgumentException("Provide --out=<JSONL report>.");
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        double seconds = double.Parse(Option("--seconds") ?? "30", CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        string[] all = { "scan", "game-banner", "game-basic-crossdomain", "game-basic-characters", "game-basic-regalia", "game-basic-shaders",
            "game-basic-copies", "game-basic-locales", "game-basic-extensions", "game-basic-prefixes", "game-basic-tokenremoval", "game-basic-numbers",
            "game-ext-skinnumbers", "game-ext-skingroups", "game-ext-chromas", "game-ext-suffixes", "game-ext-characters", "game-ext-wordaddition",
            "game-custom-shaders", "game-custom-animation-bin", "game-custom-animations", "game-custom-textures", "grep" };
        string[] selected = (Option("--methods") ?? string.Join(',', all)).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (selected.Any(id => !all.Contains(id, StringComparer.Ordinal))) throw new ArgumentException("Unknown method ID.");
        var paths = new HashFile(HashGuessDomain.Game, Path.Combine(state, "hashes", "hashes.game.txt"));
        var known = paths.Load();
        var pending = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => ulong.Parse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        var log = new LogService(Log.Logger);
        using var resolver = new HashResolverService(new DirectoriesCreator(state), log);
        // Only snapshot caches are generated. Resolver callbacks match production GrepWad.
        resolver.LoadHashes();
        resolver.LoadBinHashes();
        var game = new GameHashGuesser(paths, log, resolver.ResolveBinHashGeneral, resolver.ResolveBinXxh3);
        var lcu = new LcuHashGuesser(new HashFile(HashGuessDomain.Lcu, Path.Combine(state, "hashes", "hashes.lcu.txt")), log);
        using var report = new StreamWriter(output);
        foreach (string method in selected)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(pending));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var watch = Stopwatch.StartNew();
            long allocated = GC.GetTotalAllocatedBytes();
            string completion = "returned";
            string failure = null;
            int unreadable = 0, visitedWads = 0, visitedChunks = 0;
            var scanStats = new Dictionary<string, int>();
            Console.WriteLine($"START {method}: {pending.Count} targets, {seconds}s diagnostic limit");
            try
            {
                switch (method)
                {
                    case "scan":
                        var inventory = game.FromWads(game.FindWads(root), cancellation.Token, (_, _) => unreadable++);
                        var current = inventory.Hashes.Where(h => !resolver.IsKnownHash(h)).ToHashSet();
                        scanStats["CurrentUnknowns"] = current.Count;
                        scanStats["CurrentOutsidePending"] = current.Count(h => !pending.Contains(h));
                        scanStats["PendingAbsentFromWads"] = pending.Count(h => !inventory.Hashes.Contains(h));
                        visitedWads = inventory.WadPaths.Count;
                        break;
                    case "game-banner": game.GuessEsportsBanners(engine, null, cancellation.Token); break;
                    case "game-basic-crossdomain": game.GuessFromLcuHashes(engine, lcu, cancellation.Token); break;
                    case "game-basic-characters": game.GuessScriptFamilyAliases(engine, cancellation.Token); game.GuessCharactersFiles(engine, cancellation.Token); break;
                    case "game-basic-regalia": game.GuessRegaliaAssets(engine, cancellation.Token); break;
                    case "game-basic-shaders": game.GuessShaderVariants(engine, cancellation.Token, rootDirectory: root); break;
                    case "game-basic-copies": game.GuessIdenticalCopies(engine, root, cancellation.Token); break;
                    case "game-basic-locales": game.SubstituteLang(engine, cancellation.Token); break;
                    case "game-basic-extensions": game.GuessMapGeometrySiblings(engine, cancellation.Token); game.SubstituteExtensions(engine, cancellation.Token); break;
                    case "game-basic-prefixes": game.CheckBasenamePrefixes(engine, cancellation.Token); break;
                    case "game-basic-tokenremoval": game.RemoveBasenameTokens(engine, cancellation.Token); break;
                    case "game-basic-numbers":
                        game.SubstituteNumbers(engine, cancellation.Token, maximum: 200);
                        game.SubstituteBasicPaddedNumbers(engine, cancellation.Token);
                        game.GuessLearnedPaddedNumbers(engine, cancellation.Token);
                        break;
                    case "grep":
                        var seen = new HashSet<(ulong, ulong)>();
                        foreach (string file in game.FindWads(root))
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            using var wad = new WadFile(file);
                            visitedWads++;
                            if ((visitedWads & 31) == 0)
                                Console.WriteLine($"GREP progress: {visitedWads} WADs, {visitedChunks} chunks, {engine.Matches.Count} matches");
                            foreach (var chunk in wad.Chunks.Values)
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                if (chunk.Compression == WadChunkCompression.Satellite || !seen.Add((chunk.PathHash, chunk.Checksum))) continue;
                                bool named = known.TryGetValue(chunk.PathHash, out string path);
                                if (named && !game.ShouldGrepExtension(Path.GetExtension(path).TrimStart('.'))) continue;
                                try
                                {
                                    using var data = wad.LoadChunkDecompressed(chunk);
                                    if (!named) path = chunk.PathHash.ToString("x16") + "." + HashGuessingService.InferChunkExtension(data.DangerousGetArray(), false);
                                    visitedChunks++;
                                    game.GrepWad(engine, data.DangerousGetArray(), path, file, chunk.PathHash, cancellation.Token);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    unreadable++;
                                    if (unreadable <= 5) Console.WriteLine($"READ ERROR {path}: {ex.Message}");
                                }
                            }
                        }
                        break;
                    default:
                        if (method.StartsWith("game-ext-", StringComparison.Ordinal))
                            await game.RunExtendedAttacksAsync(engine, root, null, cancellation.Token, new HashSet<string> { method });
                        else game.RunCustomAttacks(engine, null, cancellation.Token, new HashSet<string> { method }, root);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completion = "diagnostic-time-limit"; }
            catch (Exception ex) { completion = "error"; failure = ex.ToString(); }
            long bytes = GC.GetTotalAllocatedBytes() - allocated;
            var matches = engine.Matches.Values.OrderBy(m => m.Path, StringComparer.Ordinal).Select(m => new { m.HashText, m.Path, m.SourceWadPath, m.SourceChunkHash }).ToArray();
            report.WriteLine(JsonSerializer.Serialize(new { Method = method, Initial = pending.Count, Completion = completion,
                Seconds = watch.Elapsed.TotalSeconds, Candidates = engine.CheckedCandidates, AllocatedBytes = bytes,
                Wads = visitedWads, Chunks = visitedChunks, Unreadable = unreadable, Scan = scanStats, Matches = matches, Failure = failure }));
            report.Flush();
            Console.WriteLine($"RESULT {method}: {matches.Length} matches, {engine.CheckedCandidates:N0} checks, {watch.Elapsed.TotalSeconds:F2}s, {completion}, unreadable={unreadable}");
        }
    }
}

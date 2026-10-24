using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameGrepFullProfileDiagnostic
    {
        public static async Task Run(string[] args)
        {
            var startup = Stopwatch.StartNew();
            string root = GetOption(args, "--root") ?? @"C:\Riot Games\League of Legends (PBE)";
            string state = GetOption(args, "--state");
            string hashesPath = GetOption(args, "--hashes") ?? (state == null ? FindInput("hashes", "hashes.game.txt") : Path.Combine(state, "hashes", "hashes.game.txt"));
            string unknownsPath = GetOption(args, "--unknowns") ?? (state == null ? FindInput("hash_lab", "unknowns.game.txt") : Path.Combine(state, "hash_lab", "unknowns.game.txt"));
            string wadFilter = GetOption(args, "--wad");
            bool traceChunks = args.Contains("--trace-chunks", StringComparer.OrdinalIgnoreCase);
            using var cancellation = new CancellationTokenSource();
            if (int.TryParse(GetOption(args, "--seconds"), out int seconds) && seconds > 0)
                cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
            if (!Directory.Exists(root) || !File.Exists(hashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("Usage: game-grep-full-profile [--root <PBE>] [--hashes <hashes.game.txt>] [--unknowns <unknowns.game.txt>] [--wad <filter>] [--seconds <limit>]");
                return;
            }

            // Resolver caches may be rebuilt: always keep those writes in an isolated state.
            string resolverState = Path.Combine(Path.GetTempPath(), "assetsmanager-grep-profile-" + Guid.NewGuid().ToString("N"));
            string resolverHashes = Path.Combine(resolverState, "hashes");
            Directory.CreateDirectory(resolverHashes);
            foreach (string source in Directory.EnumerateFiles(Path.GetDirectoryName(hashesPath), "hashes.*"))
                File.Copy(source, Path.Combine(resolverHashes, Path.GetFileName(source)));
            using var resolver = new HashResolverService(new DirectoriesCreator(resolverState),
                new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger()));
            await resolver.LoadAllHashesAsync();
            var hashFile = new HashFile(HashGuessDomain.Game, hashesPath);
            IReadOnlyDictionary<ulong, string> knownPaths = hashFile.Load();
            var unknowns = File.ReadLines(unknownsPath)
                .Select(line => line.Trim())
                .Where(line => ulong.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                .Select(line => ulong.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToHashSet();
            int initialUnknowns = unknowns.Count;
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknowns,
                match => Console.WriteLine($"\nMATCH {match.Hash:x16} {match.Path}"));
            var guesser = new GameHashGuesser(hashFile, null, resolver.ResolveBinHashGeneral, resolver.ResolveBinXxh3);
            string[] wads = guesser.FindWads(root);
            if (!string.IsNullOrWhiteSpace(wadFilter))
                wads = wads.Where(path => Path.GetFileName(path).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            var extensions = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
            var wadStats = new List<Aggregate>();
            var slowest = new List<ChunkStat>();
            var total = Stopwatch.StartNew();
            long chunkCount = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long skippedMedia = 0;
            long readTicks = 0, lookupTicks = 0, inferTicks = 0;
            int errors = 0;
            Console.WriteLine($"Frozen input: {knownPaths.Count:N0} known paths; {initialUnknowns:N0} targets; {wads.Length} WADs; startup {startup.Elapsed.TotalSeconds:F2}s; no persistence");

            for (int wadIndex = 0; wadIndex < wads.Length && engine.RemainingUnknownCount > 0 && !cancellation.IsCancellationRequested; wadIndex++)
            {
                string wadPath = wads[wadIndex];
                var wadTimer = Stopwatch.StartNew();
                long wadCandidates = engine.CheckedCandidates;
                int wadMatches = engine.Matches.Count;
                long wadChunks = 0;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        long lookupStart = Stopwatch.GetTimestamp();
                        string sourcePath = resolver.ResolveHash(hash);
                        string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                        // Match production's pre-decompression filter for known media.
                        bool skip = resolver.IsKnownHash(hash) && extension.Length > 0 && !guesser.ShouldGrepExtension(extension);
                        lookupTicks += Stopwatch.GetTimestamp() - lookupStart;
                        if (skip)
                        {
                            skippedMedia++;
                            continue;
                        }
                        if (traceChunks)
                            Console.WriteLine($"CHUNK {hash:x16} {sourcePath}: {chunk.CompressedSize:N0} compressed / {chunk.UncompressedSize:N0} decompressed bytes; managed {GC.GetTotalMemory(false) / 1_000_000:N0} MB");
                        try
                        {
                            long readStart = Stopwatch.GetTimestamp();
                            using var owner = wad.LoadChunkDecompressed(chunk);
                            readTicks += Stopwatch.GetTimestamp() - readStart;
                            ArraySegment<byte> data = owner.DangerousGetArray();
                            if (extension.Length == 0)
                            {
                                long inferStart = Stopwatch.GetTimestamp();
                                extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                                inferTicks += Stopwatch.GetTimestamp() - inferStart;
                                if (extension.Length > 0) sourcePath += "." + extension;
                            }

                            long beforeCandidates = engine.CheckedCandidates;
                            long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
                            int beforeMatches = engine.Matches.Count;
                            var timer = Stopwatch.StartNew();
                            guesser.GrepWad(engine, data, sourcePath, wadPath, hash, cancellation.Token);
                            timer.Stop();
                            if (timer.Elapsed.TotalSeconds >= 1)
                                Console.WriteLine($"SLOW {timer.Elapsed.TotalSeconds:F2}s; {engine.CheckedCandidates - beforeCandidates:N0} checks; {Path.GetFileName(wadPath)}; {sourcePath}");
                            if (traceChunks)
                                Console.WriteLine($"DONE {hash:x16}: {timer.Elapsed.TotalMilliseconds:N0} ms; allocated {(GC.GetAllocatedBytesForCurrentThread() - beforeAllocated) / 1_000_000:N0} MB; managed {GC.GetTotalMemory(false) / 1_000_000:N0} MB");
                            var stat = new ChunkStat(sourcePath, Path.GetFileName(wadPath), extension, timer.Elapsed,
                                engine.CheckedCandidates - beforeCandidates, engine.Matches.Count - beforeMatches);
                            AddAggregate(extensions, extension.Length == 0 ? "<none>" : extension, stat.Elapsed, stat.Candidates, stat.Matches, 1);
                            AddSlowest(slowest, stat);
                            chunkCount++;
                            wadChunks++;
                            if (engine.RemainingUnknownCount == 0) break;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            errors++;
                            Console.WriteLine($"SKIP CHUNK {sourcePath}: {exception.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Console.WriteLine("\nProfile time limit reached; no hash files were modified.");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"\nSkipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
                wadTimer.Stop();
                wadStats.Add(new Aggregate(Path.GetFileName(wadPath), wadTimer.Elapsed, wadChunks,
                    engine.CheckedCandidates - wadCandidates, engine.Matches.Count - wadMatches));
                if ((wadIndex + 1) % 5 == 0 || wadIndex + 1 == wads.Length)
                    Console.WriteLine($"{wadIndex + 1:N0}/{wads.Length:N0} WADs | {chunkCount:N0} chunks | {engine.Matches.Count:N0} matches | {total.Elapsed:hh\\:mm\\:ss}");
            }
            total.Stop();
            Console.WriteLine();

            Console.WriteLine($"\n{"Extension",-14} {"Time",12} {"Chunks",10} {"Candidates",14} {"Matches",8}");
            foreach (Aggregate stat in extensions.Values.OrderByDescending(value => value.Elapsed)) Print(stat);
            Console.WriteLine("\nSlowest WADs:");
            foreach (Aggregate stat in wadStats.OrderByDescending(value => value.Elapsed).Take(20)) Print(stat);
            Console.WriteLine("\nSlowest chunks:");
            foreach (ChunkStat stat in slowest.OrderByDescending(value => value.Elapsed))
                Console.WriteLine($"{stat.Elapsed,12:hh\\:mm\\:ss\\.fff} | {stat.Candidates,12:N0} | {stat.Matches,4:N0} | {stat.Wad} | {stat.Path}");
            Console.WriteLine($"\nTotal: {total.Elapsed:hh\\:mm\\:ss\\.fff} | Chunks: {chunkCount:N0} | Matches: {engine.Matches.Count:N0} | Remaining: {engine.RemainingUnknownCount:N0}");
            Console.WriteLine($"Lookup {lookupTicks / (double)Stopwatch.Frequency:F2}s; read/decompress {readTicks / (double)Stopwatch.Frequency:F2}s; infer {inferTicks / (double)Stopwatch.Frequency:F2}s; errors {errors}");
            Console.WriteLine($"Allocated: {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1_000_000:N0} MB cumulative; skipped known media: {skippedMedia:N0}");
            using var process = Process.GetCurrentProcess();
            Console.WriteLine($"Peak working set: {process.PeakWorkingSet64 / 1_000_000:N0} MB; private memory at end: {process.PrivateMemorySize64 / 1_000_000:N0} MB.");
            Console.WriteLine($"Managed memory before collection: {GC.GetTotalMemory(false) / 1_000_000:N0} MB; retained after collection: {GC.GetTotalMemory(true) / 1_000_000:N0} MB.");
            GC.KeepAlive(guesser);
            GC.KeepAlive(knownPaths);
            if (GetOption(args, "--out") is string output)
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    Seconds = total.Elapsed.TotalSeconds, InitialUnknowns = initialUnknowns,
                    engine.CheckedCandidates, engine.RemainingUnknownCount, Chunks = chunkCount, Errors = errors,
                    LookupSeconds = lookupTicks / (double)Stopwatch.Frequency,
                    ReadSeconds = readTicks / (double)Stopwatch.Frequency,
                    InferSeconds = inferTicks / (double)Stopwatch.Frequency,
                    Matches = engine.Matches.Values.OrderBy(match => match.Hash).Select(match => new { Hash = match.Hash.ToString("x16"), match.Path }),
                    Extensions = extensions.Values, Wads = wadStats, Slowest = slowest
                }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void AddAggregate(Dictionary<string, Aggregate> values, string name, TimeSpan elapsed, long candidates, int matches, long calls)
        {
            values.TryGetValue(name, out Aggregate current);
            values[name] = new Aggregate(name, current.Elapsed + elapsed, current.Calls + calls,
                current.Candidates + candidates, current.Matches + matches);
        }

        private static void AddSlowest(List<ChunkStat> values, ChunkStat candidate)
        {
            values.Add(candidate);
            if (values.Count <= 40) return;
            values.Remove(values.MinBy(value => value.Elapsed));
        }

        private static void Print(Aggregate value) =>
            Console.WriteLine($"{value.Name,-40} {value.Elapsed,12:hh\\:mm\\:ss\\.fff} {value.Calls,10:N0} {value.Candidates,14:N0} {value.Matches,8:N0}");

        private static string GetOption(string[] args, string option)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static string FindInput(string directory, string fileName)
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", directory, fileName);
            if (File.Exists(local)) return local;
            return Directory.EnumerateDirectories(Path.GetTempPath(), "assetsmanager-game-baseline-*")
                .Select(path => Path.Combine(path, directory, fileName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private readonly record struct Aggregate(string Name, TimeSpan Elapsed, long Calls, long Candidates, int Matches);
        private readonly record struct ChunkStat(string Path, string Wad, string Extension, TimeSpan Elapsed, long Candidates, int Matches);
    }
}

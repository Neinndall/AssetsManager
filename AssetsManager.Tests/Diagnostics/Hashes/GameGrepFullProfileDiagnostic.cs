using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameGrepFullProfileDiagnostic
    {
        public static void Run(string[] args)
        {
            string root = GetOption(args, "--root") ?? @"C:\Riot Games\League of Legends (PBE)";
            string hashesPath = GetOption(args, "--hashes") ?? FindInput("hashes", "hashes.game.txt");
            string unknownsPath = GetOption(args, "--unknowns") ?? FindInput("hash_lab", "unknowns.game.txt");
            string wadFilter = GetOption(args, "--wad");
            if (!Directory.Exists(root) || !File.Exists(hashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("Usage: game-grep-full-profile [--root <PBE>] [--hashes <hashes.game.txt>] [--unknowns <unknowns.game.txt>]");
                return;
            }

            var hashFile = new HashFile(HashGuessDomain.Game, hashesPath);
            IReadOnlyDictionary<ulong, string> knownPaths = hashFile.Load();
            var unknowns = File.ReadLines(unknownsPath)
                .Select(line => line.Trim())
                .Where(line => ulong.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                .Select(line => ulong.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToHashSet();
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknowns);
            var guesser = new GameHashGuesser(hashFile);
            string[] wads = guesser.FindWads(root);
            if (!string.IsNullOrWhiteSpace(wadFilter))
                wads = wads.Where(path => Path.GetFileName(path).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            var extensions = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
            var wadStats = new List<Aggregate>();
            var slowest = new List<ChunkStat>();
            var total = Stopwatch.StartNew();
            long chunkCount = 0;

            for (int wadIndex = 0; wadIndex < wads.Length && engine.RemainingUnknownCount > 0; wadIndex++)
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
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        using var owner = wad.LoadChunkDecompressed(chunk);
                        ArraySegment<byte> data = owner.DangerousGetArray();
                        string sourcePath = knownPaths.TryGetValue(hash, out string path) ? path : hash.ToString("x16");
                        string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                        if (extension.Length == 0)
                        {
                            extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                            if (extension.Length > 0) sourcePath += "." + extension;
                        }

                        long beforeCandidates = engine.CheckedCandidates;
                        int beforeMatches = engine.Matches.Count;
                        var timer = Stopwatch.StartNew();
                        guesser.GrepWad(engine, data, sourcePath, wadPath, hash, CancellationToken.None);
                        timer.Stop();
                        var stat = new ChunkStat(sourcePath, Path.GetFileName(wadPath), extension, timer.Elapsed,
                            engine.CheckedCandidates - beforeCandidates, engine.Matches.Count - beforeMatches);
                        AddAggregate(extensions, extension.Length == 0 ? "<none>" : extension, stat.Elapsed, stat.Candidates, stat.Matches, 1);
                        AddSlowest(slowest, stat);
                        chunkCount++;
                        wadChunks++;
                        if (engine.RemainingUnknownCount == 0) break;
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"\nSkipped {Path.GetFileName(wadPath)}: {exception.Message}");
                }
                wadTimer.Stop();
                wadStats.Add(new Aggregate(Path.GetFileName(wadPath), wadTimer.Elapsed, wadChunks,
                    engine.CheckedCandidates - wadCandidates, engine.Matches.Count - wadMatches));
                Console.Write($"\r{wadIndex + 1:N0}/{wads.Length:N0} WADs | {chunkCount:N0} chunks | {engine.Matches.Count:N0} matches | {total.Elapsed:hh\\:mm\\:ss}");
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

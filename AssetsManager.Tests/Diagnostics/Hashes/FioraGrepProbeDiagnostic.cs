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
    internal static class FioraGrepProbeDiagnostic
    {
        public static void Run(string[] args)
        {
            string wadPath = args.Length > 0 && !args[0].StartsWith("--", StringComparison.OrdinalIgnoreCase)
                ? args[0]
                : @"C:\Riot Games\League of Legends (PBE)\Game\DATA\FINAL\Champions\Fiora.wad.client";
            bool grep = args.Any(value => value.Equals("--grep", StringComparison.OrdinalIgnoreCase));
            int maxChunks = ParseInt(args, "--max-chunks", int.MaxValue);
            int timeoutMilliseconds = ParseInt(args, "--timeout-ms", 2_000);

            if (!File.Exists(wadPath))
            {
                Console.WriteLine($"WAD not found: {wadPath}");
                return;
            }

            using var wad = new WadFile(wadPath);
            Console.WriteLine($"WAD: {wadPath}");
            Console.WriteLine($"Chunks: {wad.Chunks.Count:N0}");

            if (!grep)
            {
                PrintMetadata(wad, maxChunks);
                return;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string hashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            if (!File.Exists(hashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("GAME hash catalog or unknowns file not found.");
                return;
            }

            var hashFile = new HashFile(HashGuessDomain.Game, hashesPath);
            IReadOnlyDictionary<ulong, string> knownPaths = hashFile.Load();
            var unknowns = File.ReadLines(unknownsPath)
                .Where(line => ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                .Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToHashSet();
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknowns);
            var guesser = new GameHashGuesser(hashFile);
            var stats = new List<ChunkStat>();
            int processed = 0;

            foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
            {
                if (processed++ >= maxChunks) break;
                if (chunk.Compression == WadChunkCompression.Satellite) continue;

                string sourcePath = knownPaths.TryGetValue(hash, out string path) ? path : string.Empty;
                long beforeCandidates = engine.CheckedCandidates;
                var decompressTimer = Stopwatch.StartNew();
                try
                {
                    using var owner = wad.LoadChunkDecompressed(chunk);
                    ArraySegment<byte> data = owner.DangerousGetArray();
                    decompressTimer.Stop();

                    if (sourcePath.Length == 0)
                    {
                        string extension = HashGuessingService.InferChunkExtension(data, detectJson: false);
                        sourcePath = extension.Length == 0 ? hash.ToString("x16") : hash.ToString("x16") + "." + extension;
                    }

                    Console.WriteLine($"  START {processed:N0}: {sourcePath} ({data.Count:N0} bytes)");
                    Console.Out.Flush();
                    var grepTimer = Stopwatch.StartNew();
                    using var timeout = new CancellationTokenSource(timeoutMilliseconds);
                    guesser.GrepWad(engine, data, sourcePath, wadPath, hash, timeout.Token);
                    grepTimer.Stop();
                    stats.Add(new ChunkStat(sourcePath, chunk.UncompressedSize, decompressTimer.Elapsed, grepTimer.Elapsed, engine.CheckedCandidates - beforeCandidates));
                }
                catch (OperationCanceledException)
                {
                    decompressTimer.Stop();
                    stats.Add(new ChunkStat(sourcePath, chunk.UncompressedSize, decompressTimer.Elapsed, TimeSpan.Zero, engine.CheckedCandidates - beforeCandidates, "timeout"));
                    Console.WriteLine($"  TIMEOUT {processed:N0}: {sourcePath}");
                }
                catch (Exception exception)
                {
                    decompressTimer.Stop();
                    stats.Add(new ChunkStat(sourcePath, chunk.UncompressedSize, decompressTimer.Elapsed, TimeSpan.Zero, engine.CheckedCandidates - beforeCandidates, exception.GetType().Name));
                }

                if (processed % 100 == 0)
                    Console.WriteLine($"  {processed:N0}/{wad.Chunks.Count:N0} chunks, {engine.CheckedCandidates:N0} candidates, {engine.Matches.Count:N0} matches");
            }

            Console.WriteLine($"Processed: {processed:N0}");
            Console.WriteLine($"Candidates: {engine.CheckedCandidates:N0}");
            Console.WriteLine($"Matches: {engine.Matches.Count:N0}");
            Console.WriteLine("Slowest GrepWad chunks:");
            foreach (ChunkStat stat in stats.OrderByDescending(value => value.GrepElapsed).Take(20))
                Console.WriteLine($"  {stat.GrepElapsed.TotalMilliseconds,10:N1} ms | {stat.Candidates,10:N0} candidates | {stat.Size,10:N0} bytes | {stat.Path} {stat.Error}");
        }

        private static void PrintMetadata(WadFile wad, int maxChunks)
        {
            var rows = wad.Chunks
                .Take(maxChunks)
                .Select(pair => (Hash: pair.Key, Chunk: pair.Value))
                .OrderByDescending(row => row.Chunk.UncompressedSize)
                .ToList();
            Console.WriteLine($"Inspected: {rows.Count:N0}");
            Console.WriteLine($"Uncompressed bytes: {rows.Sum(row => (long)row.Chunk.UncompressedSize):N0}");
            Console.WriteLine("Largest chunks:");
            foreach (var row in rows.Take(20))
                Console.WriteLine($"  {row.Chunk.UncompressedSize,10:N0} bytes | {row.Chunk.Compression,-10} | {row.Hash:x16}");
        }

        private static int ParseInt(string[] args, string name, int fallback)
        {
            int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
                ? Math.Max(1, value)
                : fallback;
        }

        private readonly record struct ChunkStat(
            string Path,
            int Size,
            TimeSpan DecompressElapsed,
            TimeSpan GrepElapsed,
            long Candidates,
            string Error = "");
    }
}

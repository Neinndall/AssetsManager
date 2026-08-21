using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameBaselineDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";
            bool withCustom = args.Any(arg => string.Equals(arg, "--with-custom", StringComparison.OrdinalIgnoreCase));
            bool withGrep = args.Any(arg => string.Equals(arg, "--with-grep", StringComparison.OrdinalIgnoreCase));

            if (!Directory.Exists(pbeRoot))
            {
                Console.WriteLine($"Error: game directory not found at: {pbeRoot}");
                return;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string realHashesPath = Path.Combine(localAppData, "AssetsManager", "hashes");
            string realHashLabPath = Path.Combine(localAppData, "AssetsManager", "hash_lab");
            if (!Directory.Exists(realHashesPath) || !Directory.Exists(realHashLabPath))
            {
                Console.WriteLine("Error: real hashes/hash_lab directories not found in AppData.");
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"assetsmanager-game-baseline-{Guid.NewGuid():N}");
            try
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("    GAME UNKNOWN RESOLUTION BASELINE (NON-DESTRUCTIVE)");
                Console.WriteLine("==================================================");
                Console.WriteLine($"Root: {pbeRoot}");
                Console.WriteLine("Preparing isolated temp workspace...");

                string tempHashesPath = Path.Combine(tempDir, "hashes");
                string tempHashLabPath = Path.Combine(tempDir, "hash_lab");
                Directory.CreateDirectory(tempHashesPath);
                Directory.CreateDirectory(tempHashLabPath);
                CopyDirectory(realHashesPath, tempHashesPath);
                CopyDirectory(realHashLabPath, tempHashLabPath);

                int initialUnknowns = File.ReadAllLines(Path.Combine(tempHashLabPath, "unknowns.game.txt"))
                    .Count(line => !string.IsNullOrWhiteSpace(line.Trim()));
                Console.WriteLine($"Unknowns at start: {initialUnknowns}");

                var directories = new DirectoriesCreator(tempDir);
                var serilogLogger = new LoggerConfiguration()
                    .MinimumLevel.Warning()
                    .WriteTo.Console()
                    .CreateLogger();
                var log = new LogService(serilogLogger);
                var pathStore = new HashGuessingStore(directories);
                var binRstStore = new BinRstHashGuessingStore(directories);
                var persistence = new HashGuessPersistenceService(pathStore, binRstStore);
                var resolver = new HashResolverService(directories, log);
                var service = new HashGuessingService(resolver, pathStore, persistence, log, directories);

                var summary = new List<(string Suite, TimeSpan Elapsed, int Candidates, int Resolved)>();
                var resolvedPaths = new List<HashGuessMatch>();
                var totalStopwatch = Stopwatch.StartNew();

                await RunSuiteAsync("GAME Basic", () => service.RunGameBasicGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);
                await RunSuiteAsync("GAME Extended", () => service.RunGameExtendedGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);
                await RunSuiteAsync("GAME Banners", () => service.RunGameBannerGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);
                await RunSuiteAsync("GAME Prefixes", () => service.RunGamePrefixGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);
                await RunSuiteAsync("GAME Shaders", () => service.RunGameShaderGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);

                if (withGrep)
                {
                    await RunSuiteAsync(
                        "GAME Embedded Grep",
                        () => service.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, pbeRoot, null, CancellationToken.None),
                        summary,
                        resolvedPaths);
                }

                if (withCustom)
                {
                    await RunSuiteAsync("GAME Custom", () => service.RunGameCustomGuessingAsync(pbeRoot, null, CancellationToken.None), summary, resolvedPaths);
                }

                totalStopwatch.Stop();

                Console.WriteLine();
                Console.WriteLine("==================================================");
                Console.WriteLine("    PER-SUITE RESULTS");
                Console.WriteLine("==================================================");
                Console.WriteLine($"{"Suite",-22} {"Elapsed",10} {"Candidates",14} {"Resolved",10}");
                foreach (var row in summary)
                {
                    Console.WriteLine($"{row.Suite,-22} {row.Elapsed.ToString(@"hh\:mm\:ss"),10} {row.Candidates,14:N0} {row.Resolved,10}");
                }

                int totalResolved = summary.Sum(row => row.Resolved);
                Console.WriteLine();
                Console.WriteLine("==================================================");
                Console.WriteLine($"    SUMMARY: {totalResolved}/{initialUnknowns} resolved " +
                    $"({(initialUnknowns == 0 ? 0 : 100.0 * totalResolved / initialUnknowns):F1}%) " +
                    $"in {totalStopwatch.Elapsed:hh\\:mm\\:ss}");
                Console.WriteLine("==================================================");

                if (resolvedPaths.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Resolved paths:");
                    foreach (HashGuessMatch match in resolvedPaths.OrderBy(match => match.Hash))
                    {
                        Console.WriteLine($"  {match.Hash:x16} = {match.Path}");
                    }
                }

                HashSet<ulong> remaining = await pathStore.LoadUnknownHashesAsync(HashGuessDomain.Game, CancellationToken.None);
                if (remaining.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Remaining unknowns ({remaining.Count}):");
                    foreach (ulong hash in remaining.OrderBy(hash => hash))
                    {
                        Console.WriteLine($"  {hash:x16}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task RunSuiteAsync(
            string suiteName,
            Func<Task<HashGuessRunResult>> run,
            List<(string Suite, TimeSpan Elapsed, int Candidates, int Resolved)> summary,
            List<HashGuessMatch> resolvedPaths)
        {
            Console.WriteLine();
            Console.Write($"Running {suiteName}... ");
            Console.Out.Flush();
            var stopwatch = Stopwatch.StartNew();
            HashGuessRunResult result = await run();
            stopwatch.Stop();
            summary.Add((suiteName, stopwatch.Elapsed, result.ScannedChunks, result.Matches.Count));
            resolvedPaths.AddRange(result.Matches);
            Console.WriteLine($"done. Candidates: {result.ScannedChunks:N0}, resolved: {result.Matches.Count}, elapsed: {stopwatch.Elapsed:hh\\:mm\\:ss}");
            foreach (HashGuessMatch match in result.Matches)
            {
                Console.WriteLine($"    {match.Hash:x16} = {match.Path}");
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (string filePath in Directory.EnumerateFiles(sourceDir))
            {
                File.Copy(filePath, Path.Combine(targetDir, Path.GetFileName(filePath)), overwrite: true);
            }
            foreach (string dirPath in Directory.EnumerateDirectories(sourceDir))
            {
                CopyDirectory(dirPath, Path.Combine(targetDir, Path.GetFileName(dirPath)));
            }
        }
    }
}

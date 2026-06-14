using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        private static readonly string OldDirectory = @"C:\Riot Games\League of Legends (PBE)_old_20260614_0716";
        private static readonly string NewDirectory = @"C:\Riot Games\League of Legends (PBE)";

        public static async Task RunSampleTestAsync(LogService logService)
        {
            if (!Directory.Exists(OldDirectory) || !Directory.Exists(NewDirectory))
            {
                Console.WriteLine("[BENCHMARK] One or both comparison directories do not exist.");
                Console.WriteLine($"  Old: {OldDirectory}");
                Console.WriteLine($"  New: {NewDirectory}");
                return;
            }

            Console.WriteLine("[BENCHMARK] Starting real WAD comparison benchmark...");
            Console.WriteLine($"  Old: {OldDirectory}");
            Console.WriteLine($"  New: {NewDirectory}");

            var directoriesCreator = new DirectoriesCreator();
            var hashResolver = new HashResolverService(directoriesCreator, logService);
            var comparator = new WadComparatorService(hashResolver, logService);

            // Load hashes to mirror real app conditions
            Console.WriteLine("[BENCHMARK] Loading hashes...");
            var hashLoadSw = Stopwatch.StartNew();
            await hashResolver.LoadAllHashesAsync();
            hashLoadSw.Stop();
            Console.WriteLine($"[BENCHMARK] Hashes loaded in {hashLoadSw.Elapsed.TotalSeconds:F2}s");

            int totalFiles = 0;
            int lastCompleted = 0;
            int diffCount = 0;
            var progressSw = Stopwatch.StartNew();
            var comparisonTcs = new TaskCompletionSource<bool>();

            comparator.ComparisonStarted += (total) =>
            {
                totalFiles = total;
                Console.WriteLine($"[BENCHMARK] Comparison started. Total chunks: {totalFiles}");
            };

            comparator.ComparisonProgressChanged += (completed, currentFile, success, error) =>
            {
                if (completed > lastCompleted)
                {
                    lastCompleted = completed;
                    if (completed == totalFiles || completed % 5000 == 0)
                    {
                        double pct = totalFiles > 0 ? (double)completed / totalFiles * 100 : 0;
                        Console.WriteLine($"[BENCHMARK] Progress: {completed}/{totalFiles} ({pct:F1}%) - {currentFile}");
                    }
                }
            };

            comparator.ComparisonCompleted += (diffs, oldPath, newPath, version) =>
            {
                diffCount = diffs?.Count ?? 0;
                comparisonTcs.TrySetResult(true);
            };

            var comparisonSw = Stopwatch.StartNew();
            await comparator.CompareWadsAsync(OldDirectory, NewDirectory, null, CancellationToken.None);
            await comparisonTcs.Task;
            comparisonSw.Stop();
            double totalSeconds = comparisonSw.Elapsed.TotalSeconds;

            Console.WriteLine("=====================================");
            Console.WriteLine("[BENCHMARK RESULTS]");
            Console.WriteLine($"  Total comparison time: {totalSeconds:F3}s");
            Console.WriteLine($"  Differences found: {diffCount}");
            Console.WriteLine($"  Hash load time: {hashLoadSw.Elapsed.TotalSeconds:F3}s");
            Console.WriteLine();
            Console.WriteLine("[DELAY IMPACT ANALYSIS]");
            Console.WriteLine($"  50ms delay represents: {(50.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time");
            Console.WriteLine($"  100ms delay represents: {(100.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time");
            Console.WriteLine($"  Difference between 50ms and 100ms: {(50.0 / 1000.0):F3}s ({(50.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time)");
            Console.WriteLine("=====================================");

            // Write results to a log file in the benchmark output directory
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark_results.log");
            var logContent = $@"AssetsManager Comparison Benchmark
Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
Old: {OldDirectory}
New: {NewDirectory}

Results:
  Total comparison time: {totalSeconds:F3}s
  Differences found: {diffCount}
  Hash load time: {hashLoadSw.Elapsed.TotalSeconds:F3}s

Delay Impact Analysis:
  50ms delay represents: {(50.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time
  100ms delay represents: {(100.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time
  Difference between 50ms and 100ms: {(50.0 / 1000.0):F3}s ({(50.0 / 1000.0 / totalSeconds * 100.0):F4}% of total time)
";
            File.WriteAllText(logPath, logContent);
            Console.WriteLine($"[BENCHMARK] Results written to: {logPath}");
        }
    }
}

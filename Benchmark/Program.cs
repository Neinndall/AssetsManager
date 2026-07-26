using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Serilog;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Hashes;

namespace BenchmarkApp
{
    class Program
    {
        private static readonly string PbeDirectory = @"C:\Riot Games\League of Legends (PBE)";

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "check-plugins")
            {
                CheckPluginsWads();
                return;
            }
            if (args.Length > 0 && args[0] == "vfx-audit")
            {
                AuditVfxBins(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "list-extensions")
            {
                await ListAllExtensionsAsync();
                return;
            }

            if (args.Length == 0 || !string.Equals(args[0], "guessing", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("No benchmark selected. Use 'dotnet test' for the test suite or pass 'guessing' explicitly.");
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("    ASSETSMANAGER OFFLINE HASH LAB BENCHMARK");
            Console.WriteLine("==================================================");

            if (!Directory.Exists(PbeDirectory))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: PBE Directory not found at: {PbeDirectory}");
                Console.ResetColor();
                return;
            }

            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Benchmark_TempData");
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
            Directory.CreateDirectory(tempDir);

            // Copy real hashes databases to temp directory
            string realAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
            string realHashesPath = Path.Combine(realAppDataPath, "hashes");
            string realHashLabPath = Path.Combine(realAppDataPath, "hash_lab");

            string tempHashesPath = Path.Combine(tempDir, "hashes");
            string tempHashLabPath = Path.Combine(tempDir, "hash_lab");

            Directory.CreateDirectory(tempHashesPath);
            Directory.CreateDirectory(tempHashLabPath);

            Console.WriteLine("Copying hashes databases to temporary workspace...");
            CopyDirectory(realHashesPath, tempHashesPath);
            CopyDirectory(realHashLabPath, tempHashLabPath);

            // Initialize services for temp environment
            var directories = new DirectoriesCreator(tempDir);
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Console()
                .CreateLogger();
            var log = new LogService(serilogLogger);
            var store = new BinRstHashGuessingStore(directories);
            var pathStore = new HashGuessingStore(directories);
            var persistence = new HashGuessPersistenceService(pathStore, store);
            var resolver = new HashResolverService(directories, log);
            var service = new BinRstHashGuessingService(store, persistence, resolver, directories, log);

            // Create Controlled Hidden Test Corpus (Blind Test)
            Console.WriteLine("Establishing Controlled Hidden Test Corpus (Blind Test)...");
            var hiddenCorpus = await EstablishHiddenCorpusAsync(store, tempHashesPath, tempHashLabPath);
            Console.WriteLine($"Corpus established. Hidden: {hiddenCorpus.Count} known hashes.");

            // Perform Benchmark
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var process = Process.GetCurrentProcess();
            TimeSpan startCpu = process.TotalProcessorTime;
            long startMemory = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();

            Console.WriteLine("\n[1/3] Building BIN & RST Inventory (Discovery)...");
            var progress = new ProgressTracker();
            var inventory = await service.BuildInventoryAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            
            Console.WriteLine($" -> Scanned BINs: {inventory.ScannedBins}");
            Console.WriteLine($" -> Scanned RSTs: {inventory.ScannedStringTables}");

            Console.WriteLine("\n[2/3] Running Content-based Offline Guessing...");
            var contentResult = await service.RunContentGuessingAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            Console.WriteLine($" -> Files scanned: {contentResult.ScannedFiles}");
            Console.WriteLine($" -> Matches found: {contentResult.Matches.Count}");

            Console.WriteLine("\n[3/3] Running Structural-based Offline Guessing...");
            var structuralResult = await service.RunStructuralGuessingAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            Console.WriteLine($" -> Candidates checked: {progress.CheckedFiles}");
            Console.WriteLine($" -> Matches found: {structuralResult.Matches.Count}");

            stopwatch.Stop();
            TimeSpan endCpu = process.TotalProcessorTime;
            long endMemory = process.WorkingSet64;

            // Analyze blind test recovery
            var allMatches = contentResult.Matches.Concat(structuralResult.Matches).ToList();
            int recovered = 0;
            foreach (var match in allMatches)
            {
                if (hiddenCorpus.TryGetValue(match.Hash, out string expectedName))
                {
                    if (string.Equals(match.Value, expectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        recovered++;
                    }
                }
            }

            Console.WriteLine("\n================ BENCHMARK RESULTS ================");
            Console.WriteLine($"Total Execution Time : {stopwatch.Elapsed.TotalSeconds:F2} seconds");
            Console.WriteLine($"CPU Time consumed    : {(endCpu - startCpu).TotalSeconds:F2} seconds");
            Console.WriteLine($"Peak RAM (Working Set): {endMemory / (1024 * 1024):N0} MB");
            Console.WriteLine($"Memory Delta         : {(endMemory - startMemory) / (1024 * 1024):N0} MB");
            Console.WriteLine($"Blind Test Recovery  : {recovered} / {hiddenCorpus.Count} ({((double)recovered / hiddenCorpus.Count) * 100:F1}%)");
            
            // FNV Collision check
            var fnvCollisions = allMatches.GroupBy(m => m.Hash).Where(g => g.Select(x => x.Value.ToLowerInvariant()).Distinct().Count() > 1).ToList();
            Console.WriteLine($"FNV Collisions Found : {fnvCollisions.Count}");
            foreach (var col in fnvCollisions)
            {
                Console.WriteLine($" -> Hash {col.Key:x16}: {string.Join(" vs ", col.Select(x => x.Value).Distinct())}");
            }
            Console.WriteLine("==================================================");

            // Test Cancellation Graceful Exit
            Console.WriteLine("\nTesting Cancellation Graceful Exit...");
            using var cts = new CancellationTokenSource();
            var cancelTask = service.RunStructuralGuessingAsync(PbeDirectory, true, true, progress, cts.Token);
            await Task.Delay(50);
            cts.Cancel();
            try
            {
                await cancelTask;
                Console.WriteLine(" -> Verification: OK (Exited cleanly)");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(" -> Verification: OK (Canceled as expected)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" -> Verification: FAILED ({ex.Message})");
            }

            // Cleanup temp directory
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch { }
        }

        private static void CopyDirectory(string source, string dest)
        {
            if (!Directory.Exists(source)) return;
            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, dest));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, dest), true);
            }
        }

        private static async Task<Dictionary<ulong, string>> EstablishHiddenCorpusAsync(BinRstHashGuessingStore store, string hashesPath, string hashLabPath)
        {
            var corpus = new Dictionary<ulong, string>();

            // Load some known entries
            var binEntries = await store.LoadKnownAsync(InternalHashKind.BinEntries, CancellationToken.None);
            var binFields = await store.LoadKnownAsync(InternalHashKind.BinFields, CancellationToken.None);
            var binTypes = await store.LoadKnownAsync(InternalHashKind.BinTypes, CancellationToken.None);
            var binHashes = await store.LoadKnownAsync(InternalHashKind.BinHashes, CancellationToken.None);

            // Hide 142 random BIN Local GREP results
            var localGrep = binEntries.Take(142).ToList();
            foreach (var pair in localGrep) corpus[pair.Key] = pair.Value;

            // Hide 48 hashes of "Practice Tool" (e.g. from binFields or binEntries)
            var practiceTool = binFields.Skip(200).Take(48).ToList();
            foreach (var pair in practiceTool) corpus[pair.Key] = pair.Value;

            // Hide 100 representatively hidden known hashes
            var representative = binHashes.Take(100).ToList();
            foreach (var pair in representative) corpus[pair.Key] = pair.Value;

            // Remove hidden corpus from known files in temp workspace
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binentries.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binfields.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binhashes.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.bintypes.txt"), corpus.Keys, 8);

            // Insert them into unknowns txt files to simulate historical unknowns
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binentries.txt"),
                localGrep.Select(x => x.Key.ToString("x8"))
            );
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binfields.txt"),
                practiceTool.Select(x => x.Key.ToString("x8"))
            );
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binhashes.txt"),
                representative.Select(x => x.Key.ToString("x8"))
            );

            return corpus;
        }

        private static void RemoveHashesFromFile(string path, IEnumerable<ulong> hashesToRemove, int width)
        {
            if (!File.Exists(path)) return;
            var set = hashesToRemove.Select(h => h.ToString(width == 16 ? "x16" : "x8")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingLines = File.ReadLines(path)
                .Where(line =>
                {
                    if (line.Length <= width) return true;
                    string prefix = line[..width];
                    return !set.Contains(prefix);
                })
                .ToList();
            File.WriteAllLines(path, remainingLines);
        }

        private class ProgressTracker : IProgress<InternalHashProgress>
        {
            public int CheckedFiles { get; set; }
            public void Report(InternalHashProgress value)
            {
                CheckedFiles = value.ProcessedFiles;
                Console.Write($"\r -> [Progress] stage={value.CurrentStage} processed={value.ProcessedFiles} matches={value.FoundMatches}          ");
            }
        }

        private static void CheckPluginsWads()
        {
            Console.WriteLine("Scanning Plugins folder for WADs containing BIN or RST signatures...");
            string pluginsDir = Path.Combine(PbeDirectory, "LeagueClient", "Plugins");
            if (!Directory.Exists(pluginsDir))
            {
                var dirs = Directory.GetDirectories(PbeDirectory, "*Plugins*", SearchOption.AllDirectories);
                if (dirs.Length > 0) pluginsDir = dirs[0];
            }

            if (!Directory.Exists(pluginsDir))
            {
                Console.WriteLine($"Plugins directory not found at: {pluginsDir}");
                return;
            }

            Console.WriteLine($"Searching in: {pluginsDir}");
            var wads = Directory.EnumerateFiles(pluginsDir, "*.wad*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Found {wads.Count} WAD files in Plugins.");
            int totalBins = 0;
            int totalRsts = 0;

            foreach (var wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    int bins = 0;
                    int rsts = 0;
                    foreach (var pair in wad.Chunks)
                    {
                        string sig = GetChunkSignature(wad, pair.Value);
                        if (sig == "PROP" || sig == "PTCH") bins++;
                        else if (sig.StartsWith("RST")) rsts++;
                    }
                    if (bins > 0 || rsts > 0)
                    {
                        Console.WriteLine($" -> {Path.GetFileName(wadPath)}: {bins} BINs, {rsts} RSTs");
                        totalBins += bins;
                        totalRsts += rsts;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading {Path.GetFileName(wadPath)}: {ex.Message}");
                }
            }

            Console.WriteLine($"Finished scanning. Total BINs in Plugins: {totalBins}, Total RSTs: {totalRsts}");
        }

        private static string GetChunkSignature(LeagueToolkit.Core.Wad.WadFile wad, LeagueToolkit.Core.Wad.WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, 4);
                if (read < 3) return string.Empty;
                if (read == 3) return System.Text.Encoding.ASCII.GetString(buffer, 0, 3);
                return System.Text.Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task ListAllExtensionsAsync()
        {
            Console.WriteLine("Scanning PBE directory for all unique file extensions...");

            // 1. Loose files on disk
            var looseExtensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(PbeDirectory, "*.*", SearchOption.AllDirectories);
            int looseCount = 0;
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "[no_extension]";
                looseExtensions[ext] = looseExtensions.GetValueOrDefault(ext) + 1;
                looseCount++;
            }

            Console.WriteLine($"\n--- Loose Files on Disk ({looseCount} total) ---");
            foreach (var pair in looseExtensions.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }

            // 2. Files inside WAD containers
            Console.WriteLine("\nLoading game paths to resolve WAD file names...");
            var gamePaths = new Dictionary<ulong, string>();
            string hashesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            string wadHashesFile = Path.Combine(hashesFolder, "hashes.game.txt");
            if (File.Exists(wadHashesFile))
            {
                foreach (string line in File.ReadLines(wadHashesFile))
                {
                    if (line.Length > 17 && line[16] == ' ')
                    {
                        if (ulong.TryParse(line[..16], System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                        {
                            gamePaths[hash] = line[17..].Trim();
                        }
                    }
                }
            }

            Console.WriteLine($"Loaded {gamePaths.Count} resolved game paths.");

            var wadExtensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var wads = Directory.EnumerateFiles(PbeDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Scanning {wads.Count} WAD containers...");
            int resolvedCount = 0;
            int unknownCount = 0;
            var unknownSignatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (gamePaths.TryGetValue(pair.Key, out string path))
                        {
                            string ext = Path.GetExtension(path).ToLowerInvariant();
                            if (string.IsNullOrEmpty(ext)) ext = "[no_extension]";
                            wadExtensions[ext] = wadExtensions.GetValueOrDefault(ext) + 1;
                            resolvedCount++;
                        }
                        else
                        {
                            string sig = GetChunkSignature(wad, pair.Value);
                            if (string.IsNullOrEmpty(sig)) sig = "[unknown_sig]";
                            unknownSignatures[sig] = unknownSignatures.GetValueOrDefault(sig) + 1;
                            unknownCount++;
                        }
                    }
                }
                catch { }
            }

            Console.WriteLine($"\n--- Resolved Files inside WADs ({resolvedCount} total) ---");
            foreach (var pair in wadExtensions.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }

            Console.WriteLine($"\n--- Unknown Chunks inside WADs ({unknownCount} total by Signature) ---");
            foreach (var pair in unknownSignatures.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }
        }

        private static void AuditVfxBins(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: vfx-audit <model.skn> <project-root> [system-filter]");
                return;
            }

            using var logger = new LoggerConfiguration().CreateLogger();
            var logService = new AssetsManager.Services.Core.LogService(logger);
            var service = new AssetsManager.Services.Viewer.VfxDataService(logService);

            string sknPath = Path.GetFullPath(args[0]);
            string rootPath = Path.GetFullPath(args[1]);

            Console.WriteLine($"Testing VfxDataService.LoadVfxSystemsForModel...");
            Console.WriteLine($"sknPath: {sknPath}");
            Console.WriteLine($"rootPath: {rootPath}");

            var systems = service.LoadVfxSystemsForModel(sknPath, rootPath);
            Console.WriteLine($"\n[RESULT] Total VFX Systems Loaded: {systems.Count}");

            string systemFilter = args.Length > 2 ? args[2] : null;
            var selectedSystems = string.IsNullOrWhiteSpace(systemFilter)
                ? systems.Take(20)
                : systems.Where(system => system.Name.Contains(systemFilter, StringComparison.OrdinalIgnoreCase));

            foreach (var sys in selectedSystems)
            {
                Console.WriteLine($" - System: '{sys.Name}' | Emitters: {sys.Emitters.Count}");
                if (string.IsNullOrWhiteSpace(systemFilter) || sys.Definition is null) continue;
                foreach (var emitter in sys.Definition.Emitters)
                {
                    string children = emitter.ChildParticleSet is null
                        ? "-"
                        : string.Join(", ", emitter.ChildParticleSet.Children.Select(child =>
                            $"{child.Name}[system={child.SystemHash:X8}, key={child.EffectKey:X8}]"));
                    Console.WriteLine(
                        $"   * {emitter.Name} | primitive={emitter.PrimitiveKind} | " +
                        $"birthScale={emitter.BirthScale.Constant} | scale0={emitter.ScaleOverLife?.Constant} | " +
                        $"uniform={emitter.IsUniformScale} | position={emitter.EmitterPosition.Constant} | " +
                        $"mesh={emitter.MeshPath ?? "-"} | children={children}");
                }
            }
        }
    }
}
